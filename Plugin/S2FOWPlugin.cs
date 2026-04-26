using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using S2FOW.Config;
using S2FOW.Core;
using S2FOW.Util;

namespace S2FOW;

/// <summary>
/// S2FOW — Server-Side Fog of War for Counter-Strike 2.
///
/// This plugin prevents wallhacking by controlling which enemy players the server
/// sends to each client. If an enemy is behind a wall and cannot be seen, the server
/// simply never tells the client that the enemy exists — so no wallhack can reveal them.
///
/// The plugin is split across multiple files (called "partial classes") to keep each
/// concern in its own file. This file holds the shared fields and small helpers that
/// all the other files need.
///
/// File breakdown:
///   S2FOWPlugin.cs           → This file. Shared fields and tiny helpers.
///   S2FOWPlugin.Lifecycle.cs → Plugin loading, unloading, and RayTrace connection.
///   S2FOWPlugin.Events.cs    → Handlers for game events (death, spawn, smoke, bomb, etc.).
///   S2FOWPlugin.Transmit.cs  → The hot path: runs every frame to decide who sees whom.
///   S2FOWPlugin.Config.cs    → Configuration loading, migration, and change logging.
///   S2FOWPlugin.Commands.cs  → Admin console commands (css_fow_stats, css_fow_toggle).
///   S2FOWPlugin.Debug.cs     → Debug HUD overlay and startup banner.
///   S2FOWPlugin.Helpers.cs   → Round phase tracking, state resets, and utility methods.
/// </summary>
[MinimumApiVersion(MinimumApiVersionRequired)]
public partial class S2FOWPlugin : BasePlugin, IPluginConfig<S2FOWConfig>
{
    // ────────────────────────────────────────────────────────────────────────
    //  Plugin identity constants
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Lowest CounterStrikeSharp API version this plugin will run on.</summary>
    private const int MinimumApiVersionRequired = 276;

    /// <summary>
    /// Engine bit flag for "no interpolation". When set on a player's effects,
    /// the client snaps the player to their current position instead of smoothly
    /// blending from the old position. We use this to avoid teleport-like visuals
    /// when a player transitions from hidden to visible.
    /// </summary>
    private const uint EffectNoInterp = 1u << 3;

    /// <summary>Author's Steam profile link, shown in the startup banner.</summary>
    private const string AuthorSteamProfile = "https://steamcommunity.com/profiles/76561198353131845/";

    /// <summary>Author's Discord handle, shown in the startup banner.</summary>
    private const string AuthorDiscord = "karola3vax";

    // ────────────────────────────────────────────────────────────────────────
    //  Per-player state
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tracks when to remove the NOINTERP flag for each player slot.
    /// When a hidden player becomes visible, we set NOINTERP so the client
    /// does not show them "teleporting" from their last known position.
    /// After 2 ticks we clear it so normal smooth movement resumes.
    /// </summary>
    private readonly int[] _clearNoInterpAfterTick = new int[FowConstants.MaxSlots];

    /// <summary>
    /// The current phase of the round (Warmup, FreezeTime, Live, PostPlant, RoundEnd).
    /// During non-live phases (warmup, freeze, round end) we skip visibility checks
    /// because all players should be visible.
    /// </summary>
    private RoundPhase _currentRoundPhase = RoundPhase.Live;

    // ────────────────────────────────────────────────────────────────────────
    //  Plugin metadata (shown in the server plugin list)
    // ────────────────────────────────────────────────────────────────────────

    public override string ModuleName => "S2FOW";
    public override string ModuleVersion => "1.0.3";
    public override string ModuleAuthor => "karola3vax";
    public override string ModuleDescription => "Server-side anti-wallhack plugin for CS2 that hides enemy player entities using smoke-aware RayTrace visibility checks and crash-safe transmit closure handling.";

    /// <summary>The active configuration. Parsed from JSON on load and updated on reload.</summary>
    public S2FOWConfig Config { get; set; } = new();

    // ────────────────────────────────────────────────────────────────────────
    //  Core engine components
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The capability string that the RayTrace plugin registers.
    /// We use this to look up the ray tracing service at startup.
    /// </summary>
    /// <summary>The external ray tracing API provided by the RayTraceImpl plugin.</summary>
    private IRayTraceService? _rayTrace;

    /// <summary>Wraps the ray trace API with budget tracking, reusable vectors, and hit interpretation.</summary>
    private RaycastEngine? _raycastEngine;

    /// <summary>The decision engine: determines if a target should be visible to an observer.</summary>
    private VisibilityManager? _visibilityManager;

    /// <summary>Tracks active smoke grenades and checks if they block line of sight.</summary>
    private SmokeTracker? _smokeTracker;

    /// <summary>Builds per-frame snapshots of every player's position, speed, team, etc.</summary>
    private PlayerStateCache? _playerStateCache;

    /// <summary>Measures how long each frame takes and how many rays are cast. Shown in css_fow_stats.</summary>
    private PerformanceMonitor? _perfMonitor;

    /// <summary>Creates in-world beam entities to visualize debug points and rays (debug mode only).</summary>
    private DebugAabbRenderer? _debugAabbRenderer;

    /// <summary>Set to true once the RayTrace API is connected and all components are ready.</summary>
    private bool _initialized;

    // ────────────────────────────────────────────────────────────────────────
    //  Diagnostic counters
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Counts how many times reading game rules threw an exception that we intentionally
    /// swallowed. Exposed via css_fow_stats so server operators can spot recurring issues.
    /// </summary>
    private long _suppressedGameRulesErrors;

    // ────────────────────────────────────────────────────────────────────────
    //  Small utility helpers used by multiple partial-class files
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>Writes a cyan "[S2FOW] ..." message to the server console.</summary>
    private static void Log(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(PluginOutput.Prefix(message));
        Console.ResetColor();
    }

    /// <summary>Sends a "[S2FOW] ..." reply to whoever ran an admin command.</summary>
    private static void Reply(CommandInfo command, string message)
    {
        command.ReplyToCommand(PluginOutput.Prefix(message));
    }

    /// <summary>Sends multiple lines of text as a reply to an admin command.</summary>
    private static void ReplyMany(CommandInfo command, IEnumerable<string> lines)
    {
        foreach (string line in lines)
            command.ReplyToCommand(line);
    }
}
