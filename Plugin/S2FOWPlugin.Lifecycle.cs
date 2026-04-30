using CounterStrikeSharp.API.Core;
using S2FOW.Config;
using S2FOW.Core;
using S2FOW.Util;

namespace S2FOW;

/// <summary>
/// Plugin lifecycle: handles startup, shutdown, config loading, and the RayTrace connection.
///
/// When the server starts:
///   1. Load() creates the helper objects and registers the game events S2FOW needs.
///   2. OnConfigParsed() reads the JSON config, accepts old config names, and writes the current format.
///   3. OnAllPluginsLoaded() tries to connect to RayTrace, the separate plugin that checks walls.
///   4. Once RayTrace is ready, S2FOW can decide whether each viewer should receive each enemy.
///
/// When the server shuts down:
///   1. Unload() removes debug visuals and clears short-lived visual-refresh flags.
///
/// Safety rule: if setup is incomplete, S2FOW stays idle instead of hiding players blindly.
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

        // Create the helper objects that can run before RayTrace is connected.
        _smokeTracker = new SmokeTracker();
        _playerStateCache = new PlayerStateCache();
        _perfMonitor = new PerformanceMonitor();
        if (!NetworkFullUpdateService.TryCreate(out _networkFullUpdateService, out string fullUpdateError))
            Log($"Crash recovery full-update support is unavailable: {fullUpdateError}");
        else
            Log("Crash recovery full-update support is active.");

        // Listen for map changes so we can reset state between maps.
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnMapEnd>(OnMapEnd);

        // CheckTransmit runs every network frame. It is where S2FOW removes hidden
        // enemy bodies and their connected objects from one viewer's update list.
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
        AddCommand("css_s2fow_status", PluginText.StatsCommandDescription, OnFowStats);
        AddCommand("css_fow_toggle", PluginText.ToggleCommandDescription, OnFowToggle);
        AddCommand("css_s2fow_toggle", PluginText.ToggleCommandDescription, OnFowToggle);
        Log("Loaded. Waiting for RayTrace before checking player visibility.");

        // During a hot reload, RayTrace may already be loaded, so try connecting now.
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

        // Clear the short visual-refresh flag used after hiding or showing a player.
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
        if (previousConfig.General.Enabled && !Config.General.Enabled)
            ForceFullUpdateAllObserversNow(ObserverFullUpdateReason.Unhide | ObserverFullUpdateReason.Toggle);

        // Log any changed settings so server operators can verify what happened.
        LogConfigDiff(previousConfig, Config);
        Log(BuildStartupConfigLine());
        Log(BuildStartupCoverageLine());
    }

    /// <summary>
    /// Called once all plugins are loaded.
    /// This is the first safe time to connect to RayTrace, the plugin that checks walls.
    /// </summary>
    public override void OnAllPluginsLoaded(bool hotReload)
    {
        InitializeRayTrace();
    }

    /// <summary>
    /// Attempts to connect to RayTrace.
    /// If successful, creates the visibility-checking components and marks S2FOW ready.
    /// If RayTrace is missing, S2FOW stays inactive so players are shown normally.
    /// </summary>
    private void InitializeRayTrace()
    {
        // Do not re-initialize if already connected.
        if (_initialized && _rayTrace != null)
            return;

        if (!RayTraceCapabilityResolver.TryGet(out _rayTrace, out string error) || _rayTrace == null)
        {
            Log($"RayTrace is not connected, so S2FOW is idle. Install and load RayTraceImpl and RayTraceApi first. Details: {error}");
            return;
        }

        // Build the core engine components now that we have ray tracing.
        _raycastEngine = new RaycastEngine(_rayTrace, Config, _perfMonitor);
        _visibilityManager = new VisibilityManager(
            _raycastEngine, _smokeTracker!,
            Config, _perfMonitor);
        _visibilityManager.SetRoundPhase(_currentRoundPhase);

        // Create the debug beam renderer if debug visuals are enabled.
        RebuildDebugRenderer();

        _initialized = true;
        Log("RayTrace connected. S2FOW is now checking player visibility.");
    }
}
