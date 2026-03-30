using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using RayTraceAPI;
using S2FOW.Config;
using S2FOW.Core;
using S2FOW.Util;

namespace S2FOW;

[MinimumApiVersion(276)]
public partial class S2FOWPlugin : BasePlugin, IPluginConfig<S2FOWConfig>
{
    private const int MinimumApiVersionRequired = 276;
    private const uint EffectNoInterp = 1u << 3;
    private const string AuthorSteamProfile = "https://steamcommunity.com/profiles/76561198353131845/";
    private const string AuthorDiscord = "karola3vax";
    private readonly int[] _clearNoInterpAfterTick = new int[FowConstants.MaxSlots];
    private readonly int[] _nextTraceOverlayUpdateTick = new int[FowConstants.MaxSlots];
    private readonly List<int> _unresolvedEntitiesToHide = new(64);
    private int _nextServerAvgOverlayRefreshTick;
    private double _displayedServerAvgRaycasts;
    private int _trackedPlantedC4EntityIndex;
    private int _lastPlantedC4LookupTick = int.MinValue;
    private S2FOWConfig _baseConfig = new();
    private GameModeProfile _activeProfile = GameModeProfile.Custom;
    private RoundPhase _currentRoundPhase = RoundPhase.Live;

    public override string ModuleName => "S2FOW";
    public override string ModuleVersion => "1.0.0";
    public override string ModuleAuthor => "karola3vax";
    public override string ModuleDescription => "Server-side anti-wallhack plugin for CS2 that hides players and related leak-prone entities using visibility checks, ray tracing, and transmit filtering.";

    public S2FOWConfig Config { get; set; } = new();

    private static readonly PluginCapability<CRayTraceInterface> RayTraceCapability = new("raytrace:craytraceinterface");

    private CRayTraceInterface? _rayTrace;
    private RaycastEngine? _raycastEngine;
    private VisibilityManager? _visibilityManager;
    private VisibilityCache? _visibilityCache;
    private SmokeTracker? _smokeTracker;
    private PlayerStateCache? _playerStateCache;
    private PerformanceMonitor? _perfMonitor;
    private DebugAabbRenderer? _debugAabbRenderer;
    private ProjectileTracker? _projectileTracker;
    private SpottedStateScrubber? _spottedStateScrubber;
    private ImpactTracker? _impactTracker;

    // Per-frame tracking: which targets are hidden from which observers (for spotted state scrubbing)
    private readonly bool[] _hiddenPairs = new bool[FowConstants.MaxSlots * FowConstants.MaxSlots];

    private bool _initialized;
    private bool _legacyConfigDetected;

    // Suppressed error counters for catch blocks that intentionally swallow exceptions.
    // These are exposed via css_fow_stats so operators can spot recurring entity-access failures.
    private long _suppressedEntityLookupErrors;
    private long _suppressedGameRulesErrors;

    // Utility helpers.
    private static void Log(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(PluginOutput.Prefix(message));
        Console.ResetColor();
    }

    private static void Reply(CommandInfo command, string message)
    {
        command.ReplyToCommand(PluginOutput.Prefix(message));
    }

    private static void ReplyMany(CommandInfo command, IEnumerable<string> lines)
    {
        foreach (string line in lines)
            command.ReplyToCommand(line);
    }
}
