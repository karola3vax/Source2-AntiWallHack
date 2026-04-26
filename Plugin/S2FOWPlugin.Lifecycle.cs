using CounterStrikeSharp.API.Core;
using S2FOW.Config;
using S2FOW.Core;
using S2FOW.Util;

namespace S2FOW;

/// <summary>
/// Plugin lifecycle — handles loading, unloading, and connecting to the RayTrace API.
///
/// When the server starts:
///   1. Load() is called → we create helper objects and register all event listeners.
///   2. OnConfigParsed() is called → we read the JSON config and apply any needed migrations.
///   3. OnAllPluginsLoaded() is called → we connect to the RayTrace plugin (it must load first).
///   4. Once RayTrace is ready, we build the engine components and set _initialized = true.
///
/// When the server shuts down:
///   1. Unload() is called → we clean up debug visuals and reset any modified player state.
/// </summary>
public partial class S2FOWPlugin
{
    /// <summary>
    /// Called by CounterStrikeSharp when this plugin is loaded.
    /// Sets up all event listeners, creates helper objects, and waits for RayTrace.
    /// </summary>
    public override void Load(bool hotReload)
    {
        // Show the ASCII art banner in the server console.
        PrintStartupBanner();

        // Create the helper objects that do not depend on RayTrace.
        _smokeTracker = new SmokeTracker();
        _playerStateCache = new PlayerStateCache();
        _perfMonitor = new PerformanceMonitor();

        // Listen for map changes so we can reset state between maps.
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        // This is the critical listener — it fires every network frame and lets us
        // control which entities each client receives.
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);

        // Register handlers for game events that affect visibility decisions.
        RegisterEventHandler<EventPlayerDisconnect>(OnPlayerDisconnect);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);
        RegisterEventHandler<EventPlayerSpawn>(OnPlayerSpawn);
        RegisterEventHandler<EventRoundStart>(OnRoundStart);
        RegisterEventHandler<EventRoundFreezeEnd>(OnRoundFreezeEnd);
        RegisterEventHandler<EventRoundEnd>(OnRoundEnd);
        RegisterEventHandler<EventWarmupEnd>(OnWarmupEnd);
        RegisterEventHandler<EventSmokegrenadeDetonate>(OnSmokeDetonate);
        RegisterEventHandler<EventSmokegrenadeExpired>(OnSmokeExpired);
        RegisterEventHandler<EventBombPlanted>(OnBombPlanted);
        RegisterEventHandler<EventBombDefused>(OnBombDefused);
        RegisterEventHandler<EventBombExploded>(OnBombExploded);

        // Register admin commands for server operators.
        AddCommand("css_fow_stats", PluginText.StatsCommandDescription, OnFowStats);
        AddCommand("css_fow_toggle", PluginText.ToggleCommandDescription, OnFowToggle);
        Log("Loaded. Waiting for ray tracing support...");

        // If this is a hot reload (plugin reloaded while server is running),
        // try to connect to RayTrace immediately since it may already be loaded.
        if (hotReload)
            InitializeRayTrace();
    }

    /// <summary>
    /// Called by CounterStrikeSharp when this plugin is unloaded.
    /// Cleans up debug visuals and resets any modified player state.
    /// </summary>
    public override void Unload(bool hotReload)
    {
        // Remove any debug beam entities we created in the world.
        _debugAabbRenderer?.Clear();

        // Make sure no players are stuck with the NOINTERP flag.
        ResetNoInterpState();

        // Clear the player tracking cache.
        _playerStateCache?.ResetTracking();

        _initialized = false;
        Log("Stopped cleanly.");
    }

    /// <summary>
    /// Called by CounterStrikeSharp after it parses the plugin's JSON config file.
    /// We apply migrations for older config versions and rebuild internal state.
    /// </summary>
    public void OnConfigParsed(S2FOWConfig config)
    {
        // Keep a snapshot of the previous config so we can log what changed.
        S2FOWConfig previousConfig = CloneConfig(Config);
        Config = CloneConfig(config);

        // If this config file was from an older version, update its values
        // to match the current defaults where appropriate.
        ApplyConfigMigrations(Config);

        // Write the guided JSON config (with inline comments) back to disk.
        S2FOWConfigWriter.EnsureGuidedJsonFiles(Config);

        // Rebuild all runtime components that depend on config values.
        RebuildRuntimeConfig(resetRuntimeState: _initialized);

        // Log any changed settings so server operators can verify what happened.
        LogConfigDiff(previousConfig, Config);
        Log(BuildStartupConfigLine());
        Log(BuildStartupCoverageLine());
    }

    /// <summary>
    /// Called once all plugins (including metamod/RayTrace) are loaded.
    /// This is our first safe opportunity to connect to the RayTrace API.
    /// </summary>
    public override void OnAllPluginsLoaded(bool hotReload)
    {
        InitializeRayTrace();
    }

    /// <summary>
    /// Attempts to connect to the RayTrace plugin's API.
    /// If successful, creates the engine components and marks the plugin as ready.
    /// If RayTrace is not loaded, the plugin stays inactive (but does not crash).
    /// </summary>
    private void InitializeRayTrace()
    {
        // Do not re-initialize if already connected.
        if (_initialized && _rayTrace != null)
            return;

        if (!RayTraceCapabilityResolver.TryGet(out _rayTrace, out string error) || _rayTrace == null)
        {
            Log($"Ray tracing support was not found. {error}");
            return;
        }

        // Build the core engine components now that we have ray tracing.
        _raycastEngine = new RaycastEngine(_rayTrace, Config);
        _visibilityManager = new VisibilityManager(
            _raycastEngine, _smokeTracker!,
            Config, _perfMonitor);
        _visibilityManager.SetRoundPhase(_currentRoundPhase);

        // Create the debug beam renderer if debug visuals are enabled.
        RebuildDebugRenderer();

        _initialized = true;
        Log("Ray tracing ready. Protection is live.");
    }
}
