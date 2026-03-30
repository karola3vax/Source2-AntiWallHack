using CounterStrikeSharp.API.Core;
using RayTraceAPI;
using S2FOW.Config;
using S2FOW.Core;
using S2FOW.Util;

namespace S2FOW;

public partial class S2FOWPlugin
{
    public override void Load(bool hotReload)
    {
        PrintStartupBanner();

        _visibilityCache = new VisibilityCache();
        _smokeTracker = new SmokeTracker();
        _playerStateCache = new PlayerStateCache(Config);
        _perfMonitor = new PerformanceMonitor();
        _projectileTracker = new ProjectileTracker();
        _spottedStateScrubber = new SpottedStateScrubber(Config);
        _impactTracker = new ImpactTracker();

        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);
        RegisterListener<Listeners.OnEntityCreated>(OnEntityCreated);
        RegisterListener<Listeners.OnEntitySpawned>(OnEntitySpawned);
        RegisterListener<Listeners.OnEntityDeleted>(OnEntityDeleted);

        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);

        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
        RegisterEventHandler<EventSmokegrenadeDetonate>(OnSmokeDetonate);
        RegisterEventHandler<EventSmokegrenadeExpired>(OnSmokeExpired);
        RegisterEventHandler<EventBulletImpact>(OnBulletImpact);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventBombExploded>(OnBombExploded);

        AddCommand("css_fow_stats", PluginText.StatsCommandDescription, OnFowStats);
        AddCommand("css_fow_toggle", PluginText.ToggleCommandDescription, OnFowToggle);
        AddCommand("css_fow_profile", PluginText.ProfileCommandDescription, OnFowProfile);

        Log("Loaded. Waiting for ray tracing support...");

        if (hotReload)
            InitializeRayTrace();
    }

    public override void Unload(bool hotReload)
    {
        _debugAabbRenderer?.Clear();
        ResetNoInterpState();
        _playerStateCache?.ResetTracking();
        _initialized = false;
        Log("Stopped cleanly.");
    }

    public void OnConfigParsed(S2FOWConfig config)
    {
        S2FOWConfig previousConfig = CloneConfig(Config);
        _legacyConfigDetected = DetectLegacyConfig(config);
        if (_legacyConfigDetected)
        {
            Config = CloneConfig(config);
            Config.General.Enabled = false;
            LogLegacyConfigWarning();
        }
        else
        {
            _baseConfig = CloneConfig(config);
            ApplyConfigMigrations(_baseConfig);
            S2FOWConfigWriter.EnsureGuidedJsonFiles(_baseConfig);
            ApplyRuntimeProfile(resetRuntimeState: _initialized, logProfileChange: true);
        }

        _playerStateCache?.Configure(Config);
        _spottedStateScrubber = new SpottedStateScrubber(Config);

        LogConfigDiff(previousConfig, Config);
        Log(BuildStartupProfileLine());
        Log(BuildStartupCoverageLine());
    }

    public override void OnAllPluginsLoaded(bool hotReload)
    {
        InitializeRayTrace();
    }

    private void InitializeRayTrace()
    {
        if (_initialized && _rayTrace != null)
            return;

        try
        {
            _rayTrace = RayTraceCapability.Get();
        }
        catch (Exception ex)
        {
            Log($"Could not connect to ray tracing support: {ex.Message}");
            return;
        }

        if (_rayTrace == null)
        {
            Log("Ray tracing support was not found. Load RayTraceImpl first.");
            return;
        }

        _raycastEngine = new RaycastEngine(_rayTrace, Config);
        _visibilityManager = new VisibilityManager(
            _raycastEngine, _visibilityCache!, _smokeTracker!,
            Config, _perfMonitor, Log);
        _visibilityManager.SetRoundPhase(_currentRoundPhase);
        RebuildDebugRenderer();
        _initialized = true;

        Log("Ray tracing ready. Protection is live.");
    }
}
