using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Modules.Commands;
using S2FOW.Config;
using S2FOW.Core;
using S2FOW.Util;

namespace S2FOW;

/// <summary>
/// S2FOW - Server-Side Fog of War for Counter-Strike 2.
///
/// Plain-English reading guide:
///   1. Read every player's current body position, view direction, team, weapon, and
///      connected objects once per network frame.
///   2. For each human viewer, decide whether each enemy should be visible.
///   3. If an enemy should be hidden, remove that enemy's body and connected objects
///      from only that viewer's update list.
///   4. After hide/show changes, ask the client to refresh its state so it does not
///      crash or keep stale player data.
///   5. If S2FOW is unsure, show the enemy instead of hiding them.
///
/// Engine terms used in this source:
///   - CheckTransmit: the CS2 callback that gives plugins the per-viewer list of
///     networked objects the server is about to send.
///   - RayTrace: an external helper plugin that tells S2FOW whether a straight line
///     from a viewer to an enemy hits world geometry such as a wall.
///   - Pawn: Source 2's name for a player's in-world body.
///   - Entity: Source 2's name for a networked object such as a player body,
///     weapon, wearable, or hostage prop.
///   - Connected child entities: objects attached to or owned by a player body that
///     must be hidden together with the body.
///   - NOINTERP: a client flag that makes a reappearing player snap to the correct
///     position instead of blending from an old hidden position.
///   - Full update: a forced client refresh used after hide/show changes.
///   - AABB: a simple backup box around a player, checked after detailed body points.
///
/// The plugin is split across multiple files (called "partial classes") to keep each
/// concern in its own file. This file holds the shared fields and small helpers that
/// all the other files need.
///
/// File breakdown:
///   S2FOWPlugin.cs           -> This file. Shared fields and tiny helpers.
///   S2FOWPlugin.Lifecycle.cs -> Plugin loading, unloading, and RayTrace connection.
///   S2FOWPlugin.Events.cs    -> Handlers for game events (death, spawn, smoke, bomb, etc.).
///   S2FOWPlugin.Transmit.cs  -> Per-frame player hiding.
///   S2FOWPlugin.Config.cs    -> Configuration loading, migration, and change logging.
///   S2FOWPlugin.Commands.cs  -> Admin console commands.
///   S2FOWPlugin.Debug.cs     -> Debug HUD overlay and startup banner.
///   S2FOWPlugin.Helpers.cs   -> Round state tracking, state resets, and utility methods.
/// </summary>
[MinimumApiVersion(MinimumApiVersionRequired)]
public partial class S2FOWPlugin : BasePlugin, IPluginConfig<S2FOWConfig>
{
    // Plugin identity constants

    /// <summary>Lowest CounterStrikeSharp API version this plugin will run on.</summary>
    private const int MinimumApiVersionRequired = 276;

    /// <summary>
    /// Engine bit flag for "no interpolation" (NOINTERP).
    /// When a player reappears after being hidden, the client may otherwise try to
    /// animate from the last old position it knew. This flag tells the client to use
    /// the current position immediately.
    /// </summary>
    private const uint EffectNoInterp = 1u << 3;

    /// <summary>
    /// Minimum time between forced refreshes for one viewer. 32 ticks is about
    /// half a second on a 64 tick server.
    /// </summary>
    private const int FullUpdateThrottleTicks = 32;

    /// <summary>Author's Steam profile link, shown in the startup banner.</summary>
    private const string AuthorSteamProfile = "https://steamcommunity.com/profiles/76561198353131845/";

    /// <summary>Author's Discord handle, shown in the startup banner.</summary>
    private const string AuthorDiscord = "karola3vax";

    // Per-player state

    /// <summary>
    /// Tracks when to remove the NOINTERP flag for each player slot.
    /// S2FOW sets NOINTERP briefly when a hidden player becomes visible, then clears
    /// it after two ticks so normal smooth movement resumes.
    /// </summary>
    private readonly int[] _clearNoInterpAfterTick = new int[FowConstants.MaxSlots];

    /// <summary>Per-frame forced-refresh queue, one entry per viewer.</summary>
    private readonly bool[] _observerFullUpdateQueued = new bool[FowConstants.MaxSlots];

    /// <summary>Why each viewer needs a forced refresh this frame.</summary>
    private readonly ObserverFullUpdateReason[] _observerFullUpdateReasons = new ObserverFullUpdateReason[FowConstants.MaxSlots];

    /// <summary>Next server tick when each viewer may receive another forced refresh.</summary>
    private readonly int[] _nextObserverFullUpdateTick = new int[FowConstants.MaxSlots];

    /// <summary>
    /// The current phase of the round. During warmup, freeze time, and round end,
    /// S2FOW shows everyone because hiding is not needed for live play.
    /// </summary>
    private RoundPhase _currentRoundPhase = RoundPhase.Live;

    // Plugin metadata shown in the server plugin list

    public override string ModuleName => "S2FOW";
    public override string ModuleVersion => "1.0.5";
    public override string ModuleAuthor => "karola3vax";
    public override string ModuleDescription => "Server-side CS2 player visibility plugin that hides enemies a viewer cannot see, supports smoke hiding, and sends crash-recovery full updates after hide/show changes.";

    /// <summary>The active configuration. Parsed from JSON on load and updated on reload.</summary>
    public S2FOWConfig Config { get; set; } = new();

    // Core engine components

    /// <summary>
    /// The RayTrace connection. RayTrace is the external plugin that answers:
    /// "does this straight line hit a wall before it reaches the enemy?"
    /// </summary>
    private IRayTraceService? _rayTrace;

    /// <summary>Runs wall checks and keeps their per-frame cost under control.</summary>
    private RaycastEngine? _raycastEngine;

    /// <summary>Decides whether each enemy being checked should be visible to each viewer.</summary>
    private VisibilityManager? _visibilityManager;

    /// <summary>Tracks active smoke grenades and checks if smoke blocks sight.</summary>
    private SmokeTracker? _smokeTracker;

    /// <summary>Reads player state once per frame so all visibility decisions use the same data.</summary>
    private PlayerStateCache? _playerStateCache;

    /// <summary>Measures plugin work and safety counters shown by the status command.</summary>
    private PerformanceMonitor? _perfMonitor;

    /// <summary>Draws optional debug beams in the world when debug settings are enabled.</summary>
    private DebugAabbRenderer? _debugAabbRenderer;

    /// <summary>Bridge used to force a viewer refresh after hide/show changes.</summary>
    private NetworkFullUpdateService? _networkFullUpdateService;

    /// <summary>Set to true once the RayTrace API is connected and all components are ready.</summary>
    private bool _initialized;

    /// <summary>Prevents repeated console spam if forced refresh support is unavailable.</summary>
    private bool _loggedFullUpdateFailure;

    // Diagnostic counters

    /// <summary>
    /// Counts how many times reading round-state information failed. Shown in the
    /// status command so server owners can notice repeated engine-read problems.
    /// </summary>
    private long _suppressedGameRulesErrors;

    // Small utility helpers used by multiple partial-class files

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
