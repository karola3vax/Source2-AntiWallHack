using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Config;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Extensions.Logging;
using RayTraceAPI;
using Vector = CounterStrikeSharp.API.Modules.Utils.Vector;

namespace Source2AntiWallHack;

public sealed class Source2AntiWallHackConfig : BasePluginConfig
{
    [JsonPropertyName("Enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("HideTeammates")] public bool HideTeammates { get; set; } = true;
    [JsonPropertyName("IgnoreBots")] public bool IgnoreBots { get; set; } = false;

    [JsonPropertyName("VisibleGraceTicks")] public int VisibleGraceTicks { get; set; } = 1;
    [JsonPropertyName("PreloadLookaheadTicks")] public int PreloadLookaheadTicks { get; set; } = 12;
    [JsonPropertyName("PreloadHoldTicks")] public int PreloadHoldTicks { get; set; } = 22;
    [JsonPropertyName("PreloadVelocityScale")] public float PreloadVelocityScale { get; set; } = 1.3f;
    [JsonPropertyName("PreloadMinSpeed")] public float PreloadMinSpeed { get; set; } = 0.0f;

    [JsonPropertyName("CombatGraceTicks")] public int CombatGraceTicks { get; set; } = 32;
    [JsonPropertyName("DeathGraceTicks")] public int DeathGraceTicks { get; set; } = 64;

    [JsonPropertyName("MaxTraceDistance")] public float MaxTraceDistance { get; set; } = 4096.0f;
    [JsonPropertyName("SideProbeUnits")] public float SideProbeUnits { get; set; } = 24.0f;
    [JsonPropertyName("TargetPaddingUnits")] public float TargetPaddingUnits { get; set; } = 30.0f;
    [JsonPropertyName("HorizontalPeekBonusUnits")] public float HorizontalPeekBonusUnits { get; set; } = 24.0f;
    [JsonPropertyName("HorizontalPeekSpeedForMaxBonus")] public float HorizontalPeekSpeedForMaxBonus { get; set; } = 100.0f;
    [JsonPropertyName("DebugTraceBeams")] public bool DebugTraceBeams { get; set; } = false;
    [JsonPropertyName("AllowLosThroughPlayerClip")] public bool AllowLosThroughPlayerClip { get; set; } = true;
}

[MinimumApiVersion(276)]
public sealed class Source2AntiWallHackPlugin : BasePlugin, IPluginConfig<Source2AntiWallHackConfig>
{
    private const int MaxSlotCount = 128;
    private const int PairStateTableSize = MaxSlotCount * MaxSlotCount;
    private const string PluginTag = "[Source2-AntiWallHack]";
    private const float LosCachePositionTolerance = 2.5f;
    private const float LosCacheVelocityTolerance = 22.0f;
    private const int LosCacheMovingTicks = 1;
    private const int LosCacheStaticVisibleTicks = 2;
    private const int LosCacheStaticHiddenTicks = 3;

    private static readonly float LosCachePositionToleranceSqr = LosCachePositionTolerance * LosCachePositionTolerance;
    private static readonly float LosCacheVelocityToleranceSqr = LosCacheVelocityTolerance * LosCacheVelocityTolerance;

    private readonly struct FrameTuning
    {
        public readonly float MaxTraceDistanceSquared;
        public readonly float PreloadMinSpeedSquared;
        public readonly float PreloadStepTime;
        public readonly int VisibleGraceTicks;
        public readonly int PreloadHoldTicks;
        public readonly int PreloadLookaheadTicks;
        public readonly float TargetPaddingUnits;
        public readonly float SideProbeUnits;
        public readonly float HorizontalPeekBonusUnits;
        public readonly float HorizontalPeekSpeedInverse;

        public FrameTuning(
            float maxTraceDistanceSquared,
            float preloadMinSpeedSquared,
            float preloadStepTime,
            int visibleGraceTicks,
            int preloadHoldTicks,
            int preloadLookaheadTicks,
            float targetPaddingUnits,
            float sideProbeUnits,
            float horizontalPeekBonusUnits,
            float horizontalPeekSpeedInverse)
        {
            MaxTraceDistanceSquared = maxTraceDistanceSquared;
            PreloadMinSpeedSquared = preloadMinSpeedSquared;
            PreloadStepTime = preloadStepTime;
            VisibleGraceTicks = visibleGraceTicks;
            PreloadHoldTicks = preloadHoldTicks;
            PreloadLookaheadTicks = preloadLookaheadTicks;
            TargetPaddingUnits = targetPaddingUnits;
            SideProbeUnits = sideProbeUnits;
            HorizontalPeekBonusUnits = horizontalPeekBonusUnits;
            HorizontalPeekSpeedInverse = horizontalPeekSpeedInverse;
        }
    }

    private sealed class PairState
    {
        public int LastVisibleTick = int.MinValue / 2;
        public int PreloadUntilTick;
        public int ForceUntilTick;
        public int LastDecisionTick = int.MinValue / 2;
        public bool LastDecision;

        public int LosCacheUntilTick = int.MinValue / 2;
        public Vector3 LosCacheViewerEye;
        public Vector3 LosCacheViewerVelocity;
        public Vector3 LosCacheTargetOrigin;
        public Vector3 LosCacheTargetVelocity;
        public bool LosCacheValue;
        public sbyte LastExpandedHitSampleIndex = -1;
        public sbyte LastCompactHitSampleIndex = -1;
    }

    private sealed class PlayerSnapshot
    {
        public CCSPlayerPawn Pawn = null!;
        public int Slot;
        public CsTeam Team;
        public bool IsBot;
        public Vector3 Origin;
        public Vector3 ViewOffset;
        public Vector3 EyePosition;
        public Vector3 Velocity;
        public float VelocityLengthSquared;
        public float SideProbe;
        public bool LinkedIndicesBuilt;
        public int LinkedIndicesCount;
        public uint[] LinkedIndices = new uint[8];

        public void Populate(
            int slot,
            CsTeam team,
            bool isBot,
            CCSPlayerPawn pawn,
            Vector3 origin,
            Vector3 viewOffset,
            Vector3 velocity,
            float sideProbe)
        {
            Pawn = pawn;
            Slot = slot;
            Team = team;
            IsBot = isBot;
            Origin = origin;
            ViewOffset = viewOffset;
            EyePosition = origin + viewOffset;
            Velocity = velocity;
            VelocityLengthSquared = velocity.LengthSquared();
            SideProbe = sideProbe;
            LinkedIndicesBuilt = false;
            LinkedIndicesCount = 0;
        }
    }

    private static readonly PluginCapability<CRayTraceInterface> RayTraceCapability = new("raytrace:craytraceinterface");
    private static readonly InteractionLayers OcclusionTraceMask =
        InteractionLayers.MASK_SHOT_PHYSICS |
        InteractionLayers.BlockLOS |
        InteractionLayers.WorldGeometry |
        InteractionLayers.csgo_opaque;
    private static readonly InteractionLayers OcclusionTraceMaskWithoutPlayerClip =
        (OcclusionTraceMask & ~InteractionLayers.PlayerClip);

    private readonly PairState?[] _pairStateTable = new PairState?[PairStateTableSize];
    private readonly int[] _forceTargetTransmitUntilTick = new int[MaxSlotCount];
    private readonly List<PlayerSnapshot> _targetsAll = new(MaxSlotCount);
    private readonly List<PlayerSnapshot> _targetsAllNonBot = new(MaxSlotCount);
    private readonly List<PlayerSnapshot> _targetsT = new(MaxSlotCount);
    private readonly List<PlayerSnapshot> _targetsCT = new(MaxSlotCount);
    private readonly List<PlayerSnapshot> _targetsTNonBot = new(MaxSlotCount);
    private readonly List<PlayerSnapshot> _targetsCTNonBot = new(MaxSlotCount);
    private readonly PlayerSnapshot?[] _snapshotBySlot = new PlayerSnapshot?[MaxSlotCount];
    private readonly PlayerSnapshot?[] _snapshotCacheBySlot = new PlayerSnapshot?[MaxSlotCount];
    private readonly Vector _traceStart = new();
    private readonly Vector _traceEnd = new();

    private CRayTraceInterface? _rayTrace;
    private TraceOptions _segmentTraceOptions;
    private bool _missingRayTraceLogged;
    private bool _traceFailureLogged;
    private bool _traceDisabledByFailure;
    private int _lastSnapshotCount;
    private int _lastBotSnapshotCount;
    private int _lastViewerCount;
    private int _lastHiddenPawnRemovals;
    private int _snapshotCacheTick = int.MinValue;

    public override string ModuleName => "Source2-AntiWallHack";
    public override string ModuleVersion => "Alpha Release 1.0";
    public override string ModuleAuthor => "karola3vax";
    public override string ModuleDescription => "Source2-AntiWallHack: LOS-based antiwallhack with predictive preload (Ray-Trace)";

    public Source2AntiWallHackConfig Config { get; set; } = new();

    public void OnConfigParsed(Source2AntiWallHackConfig config)
    {
        config.VisibleGraceTicks = Math.Clamp(config.VisibleGraceTicks, 0, 256);
        config.PreloadLookaheadTicks = Math.Clamp(config.PreloadLookaheadTicks, 0, 128);
        config.PreloadHoldTicks = Math.Clamp(config.PreloadHoldTicks, 0, 256);
        config.PreloadVelocityScale = Math.Clamp(config.PreloadVelocityScale, 0.1f, 4.0f);
        config.PreloadMinSpeed = Math.Clamp(config.PreloadMinSpeed, 0.0f, 1000.0f);
        config.CombatGraceTicks = Math.Clamp(config.CombatGraceTicks, 0, 512);
        config.DeathGraceTicks = Math.Clamp(config.DeathGraceTicks, 0, 512);
        config.MaxTraceDistance = Math.Clamp(config.MaxTraceDistance, 128.0f, 10000.0f);
        config.SideProbeUnits = Math.Clamp(config.SideProbeUnits, 2.0f, 40.0f);
        config.TargetPaddingUnits = Math.Clamp(config.TargetPaddingUnits, 0.0f, 64.0f);
        config.HorizontalPeekBonusUnits = Math.Clamp(config.HorizontalPeekBonusUnits, 0.0f, 64.0f);
        config.HorizontalPeekSpeedForMaxBonus = Math.Clamp(config.HorizontalPeekSpeedForMaxBonus, 40.0f, 600.0f);

        Config = config;
        RefreshSegmentTraceOptions();
    }

    public override void Load(bool hotReload)
    {
        RefreshSegmentTraceOptions();

        RegisterListener<Listeners.OnMetamodAllPluginsLoaded>(OnMetamodAllPluginsLoaded);
        RegisterListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RegisterListener<Listeners.OnMapStart>(OnMapStart);
        RegisterListener<Listeners.OnClientDisconnect>(OnClientDisconnect);

        RegisterEventHandler<EventPlayerHurt>(OnPlayerHurt);
        RegisterEventHandler<EventPlayerDeath>(OnPlayerDeath);

        AddCommand("css_source2awh_status", "Shows Source2-AntiWallHack status", OnStatusCommand);
        AddCommand("css_source2awh_reset", "Resets Source2-AntiWallHack runtime data", OnResetCommand);

        RefreshRayTrace(logIfAvailable: hotReload);
    }

    public override void Unload(bool hotReload)
    {
        RemoveListener<Listeners.OnMetamodAllPluginsLoaded>(OnMetamodAllPluginsLoaded);
        RemoveListener<Listeners.CheckTransmit>(OnCheckTransmit);
        RemoveListener<Listeners.OnMapStart>(OnMapStart);
        RemoveListener<Listeners.OnClientDisconnect>(OnClientDisconnect);

        ClearRuntimeState(resetSnapshotTick: true);
    }

    private void OnMetamodAllPluginsLoaded()
    {
        RefreshRayTrace(logIfAvailable: true);
    }

    private void OnMapStart(string _)
    {
        ClearRuntimeState(resetSnapshotTick: true);
    }

    private void OnClientDisconnect(int slot)
    {
        RemoveStateForSlot(slot);
        _snapshotCacheTick = int.MinValue;
    }

    private HookResult OnPlayerHurt(EventPlayerHurt @event, GameEventInfo _)
    {
        if (!Config.Enabled)
            return HookResult.Continue;

        int now = Server.TickCount;
        CCSPlayerController? victim = @event.Userid;
        CCSPlayerController? attacker = @event.Attacker;

        if (victim != null && victim.IsValid)
            ExtendTargetForce(victim.Slot, Config.CombatGraceTicks, now);

        if (attacker != null && attacker.IsValid)
            ExtendTargetForce(attacker.Slot, Config.CombatGraceTicks, now);

        if (victim != null && victim.IsValid && attacker != null && attacker.IsValid && victim.Slot != attacker.Slot)
        {
            ExtendPairForce(attacker.Slot, victim.Slot, Config.CombatGraceTicks, now);
            ExtendPairForce(victim.Slot, attacker.Slot, Config.CombatGraceTicks, now);
        }

        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo _)
    {
        if (!Config.Enabled)
            return HookResult.Continue;

        int now = Server.TickCount;
        CCSPlayerController? victim = @event.Userid;
        CCSPlayerController? attacker = @event.Attacker;

        if (victim != null && victim.IsValid)
            ExtendTargetForce(victim.Slot, Config.DeathGraceTicks, now);

        if (attacker != null && attacker.IsValid)
            ExtendTargetForce(attacker.Slot, Math.Max(1, Config.DeathGraceTicks / 2), now);

        if (victim != null && victim.IsValid && attacker != null && attacker.IsValid && victim.Slot != attacker.Slot)
        {
            ExtendPairForce(attacker.Slot, victim.Slot, Config.DeathGraceTicks, now);
            ExtendPairForce(victim.Slot, attacker.Slot, Math.Max(1, Config.DeathGraceTicks / 2), now);
        }

        return HookResult.Continue;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (!Config.Enabled)
            return;

        CRayTraceInterface? rayTrace = EnsureRayTrace();
        if (rayTrace == null)
            return;

        int currentTick = Server.TickCount;
        BuildPlayerSnapshots(currentTick);
        _lastViewerCount = 0;
        _lastHiddenPawnRemovals = 0;

        if (_targetsAll.Count == 0)
            return;

        bool hideTeammates = Config.HideTeammates;
        bool ignoreBots = Config.IgnoreBots;
        FrameTuning frameTuning = BuildFrameTuning();
        List<PlayerSnapshot> sharedTargets = ignoreBots ? _targetsAllNonBot : _targetsAll;
        List<PlayerSnapshot> targetsForTViewer = ignoreBots ? _targetsCTNonBot : _targetsCT;
        List<PlayerSnapshot> targetsForCtViewer = ignoreBots ? _targetsTNonBot : _targetsT;

        foreach ((CCheckTransmitInfo info, CCSPlayerController? viewerController) in infoList)
        {
            if (viewerController == null || !viewerController.IsValid)
                continue;

            int viewerSlot = viewerController.Slot;
            if (!IsSlotInRange(viewerSlot))
                continue;

            bool viewerIsBot = viewerController.IsBot;
            if (ignoreBots && viewerIsBot)
                continue;

            CsTeam viewerTeam = viewerController.Team;
            if (viewerTeam is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
                continue;

            PlayerSnapshot? viewer = _snapshotBySlot[viewerSlot];
            if (viewer == null)
            {
                PlayerSnapshot cache = _snapshotCacheBySlot[viewerSlot] ??= new PlayerSnapshot();
                if (!TryPopulateSnapshot(viewerController, cache, viewerSlot, viewerTeam, viewerIsBot))
                    continue;

                viewer = cache;
                _snapshotBySlot[viewerSlot] = viewer;
            }

            List<PlayerSnapshot> targets = hideTeammates
                ? sharedTargets
                : (viewerTeam == CsTeam.Terrorist ? targetsForTViewer : targetsForCtViewer);
            int targetCount = targets.Count;
            if (targetCount == 0)
                continue;

            _lastViewerCount++;
            var transmitEntities = info.TransmitEntities;
            int viewerPairBase = viewerSlot * MaxSlotCount;

            for (int i = 0; i < targetCount; i++)
            {
                PlayerSnapshot target = targets[i];

                if (target.Slot == viewer.Slot)
                    continue;

                uint targetIndex = target.Pawn.Index;
                if (!transmitEntities.Contains(targetIndex))
                    continue;

                if (!ShouldTransmit(viewer, target, viewerPairBase, currentTick, rayTrace, frameTuning))
                {
                    transmitEntities.Remove(targetIndex);
                    RemoveLinkedWeaponEntities(info, target);
                    _lastHiddenPawnRemovals++;
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool ShouldTransmit(PlayerSnapshot viewer, PlayerSnapshot target, int viewerPairBase, int currentTick, CRayTraceInterface rayTrace, in FrameTuning frameTuning)
    {
        PairState state = GetOrCreatePairStateByIndex(viewerPairBase + target.Slot);

        if (state.LastDecisionTick == currentTick)
            return state.LastDecision;

        bool shouldTransmit = EvaluateShouldTransmit(viewer, target, state, currentTick, rayTrace, frameTuning);

        state.LastDecisionTick = currentTick;
        state.LastDecision = shouldTransmit;
        return shouldTransmit;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool EvaluateShouldTransmit(PlayerSnapshot viewer, PlayerSnapshot target, PairState state, int currentTick, CRayTraceInterface rayTrace, in FrameTuning frameTuning)
    {
        if (state.ForceUntilTick >= currentTick)
            return true;

        if (_forceTargetTransmitUntilTick[target.Slot] >= currentTick)
            return true;

        if (DistanceSquared(viewer.EyePosition, target.EyePosition) > frameTuning.MaxTraceDistanceSquared)
            return true;

        if (HasLineOfSightCached(rayTrace, viewer, target, state, currentTick, frameTuning))
        {
            state.LastVisibleTick = currentTick;
            state.PreloadUntilTick = Math.Max(state.PreloadUntilTick, currentTick + frameTuning.PreloadHoldTicks);
            return true;
        }

        if (currentTick - state.LastVisibleTick <= frameTuning.VisibleGraceTicks)
            return true;

        if (state.PreloadUntilTick >= currentTick)
            return true;

        if (frameTuning.PreloadLookaheadTicks <= 0)
            return false;

        if (viewer.VelocityLengthSquared < frameTuning.PreloadMinSpeedSquared &&
            target.VelocityLengthSquared < frameTuning.PreloadMinSpeedSquared)
            return false;

        if (WillLikelyBeVisibleSoon(rayTrace, viewer, target, frameTuning))
        {
            state.PreloadUntilTick = currentTick + frameTuning.PreloadHoldTicks;
            return true;
        }

        return false;
    }

    private bool WillLikelyBeVisibleSoon(CRayTraceInterface rayTrace, PlayerSnapshot viewer, PlayerSnapshot target, in FrameTuning frameTuning)
    {
        float stepTime = frameTuning.PreloadStepTime;
        Vector3 viewerStep = viewer.Velocity * stepTime;
        Vector3 targetStep = target.Velocity * stepTime;
        Vector3 predictedViewerEye = viewer.EyePosition;
        Vector3 predictedTargetOrigin = target.Origin;

        for (int step = 1; step <= frameTuning.PreloadLookaheadTicks; step++)
        {
            predictedViewerEye += viewerStep;
            predictedTargetOrigin += targetStep;
            Vector3 predictedTargetEye = predictedTargetOrigin + target.ViewOffset;

            if (DistanceSquared(predictedViewerEye, predictedTargetEye) > frameTuning.MaxTraceDistanceSquared)
                continue;

            if (HasLineOfSight(
                    rayTrace,
                    viewer.Pawn,
                    predictedViewerEye,
                    viewer.Velocity,
                    target,
                    predictedTargetOrigin,
                    target.Velocity,
                    expandedSampling: false,
                    pairState: null,
                    frameTuning: frameTuning))
                return true;
        }

        return false;
    }

    private bool HasLineOfSightCached(CRayTraceInterface rayTrace, PlayerSnapshot viewer, PlayerSnapshot target, PairState state, int currentTick, in FrameTuning frameTuning)
    {
        if (currentTick <= state.LosCacheUntilTick)
        {
            if (DistanceSquared(viewer.EyePosition, state.LosCacheViewerEye) <= LosCachePositionToleranceSqr &&
                DistanceSquared(target.Origin, state.LosCacheTargetOrigin) <= LosCachePositionToleranceSqr &&
                DistanceSquared(viewer.Velocity, state.LosCacheViewerVelocity) <= LosCacheVelocityToleranceSqr &&
                DistanceSquared(target.Velocity, state.LosCacheTargetVelocity) <= LosCacheVelocityToleranceSqr)
            {
                return state.LosCacheValue;
            }
        }

        bool hasLos = HasLineOfSight(
            rayTrace,
            viewer.Pawn,
            viewer.EyePosition,
            viewer.Velocity,
            target,
            target.Origin,
            target.Velocity,
            expandedSampling: true,
            pairState: state,
            frameTuning: frameTuning);

        state.LosCacheViewerEye = viewer.EyePosition;
        state.LosCacheViewerVelocity = viewer.Velocity;
        state.LosCacheTargetOrigin = target.Origin;
        state.LosCacheTargetVelocity = target.Velocity;
        state.LosCacheValue = hasLos;

        float motionSqr = viewer.VelocityLengthSquared + target.VelocityLengthSquared;
        if (motionSqr > (120.0f * 120.0f))
        {
            state.LosCacheUntilTick = currentTick + LosCacheMovingTicks;
        }
        else
        {
            state.LosCacheUntilTick = currentTick + (hasLos ? LosCacheStaticVisibleTicks : LosCacheStaticHiddenTicks);
        }

        return hasLos;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private bool HasLineOfSight(
        CRayTraceInterface rayTrace,
        CCSPlayerPawn viewerPawn,
        Vector3 viewerEye,
        Vector3 viewerVelocity,
        PlayerSnapshot target,
        Vector3 targetOrigin,
        Vector3 targetVelocity,
        bool expandedSampling,
        PairState? pairState,
        in FrameTuning frameTuning)
    {
        if (expandedSampling)
        {
            Span<Vector3> samplePoints = stackalloc Vector3[11];
            BuildSamplePoints(
                viewerEye,
                viewerVelocity,
                targetOrigin,
                targetVelocity,
                target.ViewOffset,
                target.SideProbe,
                samplePoints,
                expandedSampling: true,
                frameTuning: frameTuning);

            int preferredIndex = pairState?.LastExpandedHitSampleIndex ?? -1;
            if ((uint)preferredIndex < (uint)samplePoints.Length &&
                IsSegmentVisible(rayTrace, viewerPawn, viewerEye, samplePoints[preferredIndex]))
            {
                return true;
            }

            for (int i = 0; i < samplePoints.Length; i++)
            {
                if (i == preferredIndex)
                    continue;

                if (IsSegmentVisible(rayTrace, viewerPawn, viewerEye, samplePoints[i]))
                {
                    if (pairState != null)
                        pairState.LastExpandedHitSampleIndex = (sbyte)i;
                    return true;
                }
            }

            return false;
        }

        {
            Span<Vector3> samplePoints = stackalloc Vector3[5];
            BuildSamplePoints(
                viewerEye,
                viewerVelocity,
                targetOrigin,
                targetVelocity,
                target.ViewOffset,
                target.SideProbe,
                samplePoints,
                expandedSampling: false,
                frameTuning: frameTuning);

            int preferredIndex = pairState?.LastCompactHitSampleIndex ?? -1;
            if ((uint)preferredIndex < (uint)samplePoints.Length &&
                IsSegmentVisible(rayTrace, viewerPawn, viewerEye, samplePoints[preferredIndex]))
            {
                return true;
            }

            for (int i = 0; i < samplePoints.Length; i++)
            {
                if (i == preferredIndex)
                    continue;

                if (IsSegmentVisible(rayTrace, viewerPawn, viewerEye, samplePoints[i]))
                {
                    if (pairState != null)
                        pairState.LastCompactHitSampleIndex = (sbyte)i;
                    return true;
                }
            }

            return false;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void BuildSamplePoints(
        Vector3 viewerEye,
        Vector3 viewerVelocity,
        Vector3 targetOrigin,
        Vector3 targetVelocity,
        Vector3 targetViewOffset,
        float targetSideProbe,
        Span<Vector3> output,
        bool expandedSampling,
        in FrameTuning frameTuning)
    {
        float padding = frameTuning.TargetPaddingUnits;
        float headZ = targetOrigin.Z + (targetViewOffset.Z * 0.92f) + (padding * (expandedSampling ? 0.55f : 0.40f));
        float chestZ = targetOrigin.Z + (targetViewOffset.Z * 0.62f);
        float pelvisZ = targetOrigin.Z + (targetViewOffset.Z * 0.35f) - (padding * (expandedSampling ? 0.35f : 0.20f));

        float toTargetX = targetOrigin.X - viewerEye.X;
        float toTargetY = targetOrigin.Y - viewerEye.Y;
        float toTargetLenSqr = (toTargetX * toTargetX) + (toTargetY * toTargetY);
        float forwardX;
        float forwardY;
        if (toTargetLenSqr > 0.001f)
        {
            float invLen = 1.0f / MathF.Sqrt(toTargetLenSqr);
            forwardX = toTargetX * invLen;
            forwardY = toTargetY * invLen;
        }
        else
        {
            forwardX = 0.0f;
            forwardY = 1.0f;
        }

        float rightX = forwardY;
        float rightY = -forwardX;

        float relativeVelocityX = targetVelocity.X - viewerVelocity.X;
        float relativeVelocityY = targetVelocity.Y - viewerVelocity.Y;
        float lateralSpeed = MathF.Abs((relativeVelocityX * rightX) + (relativeVelocityY * rightY));
        float peekRatio = Math.Clamp(lateralSpeed * frameTuning.HorizontalPeekSpeedInverse, 0.0f, 1.0f);
        float peekBonus = frameTuning.HorizontalPeekBonusUnits * peekRatio;

        float sideProbeBase = Math.Clamp(
            MathF.Min((targetSideProbe * 0.5f) + padding + peekBonus, frameTuning.SideProbeUnits + padding + peekBonus),
            2.0f,
            110.0f);
        float forwardProbeBase = Math.Clamp((targetSideProbe * 0.35f) + padding + (peekBonus * 0.5f), 2.0f, 110.0f);
        float sideProbe = expandedSampling ? sideProbeBase : sideProbeBase * 0.85f;
        float forwardProbe = expandedSampling ? forwardProbeBase : forwardProbeBase * 0.75f;

        output[0] = new Vector3(targetOrigin.X, targetOrigin.Y, headZ);
        output[1] = new Vector3(targetOrigin.X, targetOrigin.Y, chestZ);
        output[2] = new Vector3(targetOrigin.X, targetOrigin.Y, pelvisZ);
        output[3] = new Vector3(targetOrigin.X + (rightX * sideProbe), targetOrigin.Y + (rightY * sideProbe), chestZ);
        output[4] = new Vector3(targetOrigin.X - (rightX * sideProbe), targetOrigin.Y - (rightY * sideProbe), chestZ);
        if (!expandedSampling)
            return;

        output[5] = new Vector3(targetOrigin.X + (forwardX * forwardProbe), targetOrigin.Y + (forwardY * forwardProbe), chestZ);
        output[6] = new Vector3(targetOrigin.X - (forwardX * forwardProbe), targetOrigin.Y - (forwardY * forwardProbe), chestZ);
        output[7] = new Vector3(targetOrigin.X + (rightX * sideProbe), targetOrigin.Y + (rightY * sideProbe), headZ);
        output[8] = new Vector3(targetOrigin.X - (rightX * sideProbe), targetOrigin.Y - (rightY * sideProbe), headZ);
        output[9] = new Vector3(targetOrigin.X + (forwardX * forwardProbe), targetOrigin.Y + (forwardY * forwardProbe), headZ);
        output[10] = new Vector3(targetOrigin.X - (forwardX * forwardProbe), targetOrigin.Y - (forwardY * forwardProbe), headZ);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsSegmentVisible(CRayTraceInterface rayTrace, CCSPlayerPawn viewerPawn, Vector3 start, Vector3 end)
    {
        if (_traceDisabledByFailure)
            return true;

        try
        {
            return IsSegmentClear(rayTrace, viewerPawn, start, end, _segmentTraceOptions);
        }
        catch (Exception ex)
        {
            _traceDisabledByFailure = true;
            if (!_traceFailureLogged)
            {
                _traceFailureLogged = true;
                Logger.LogError(ex, "{PluginTag} RayTrace call failed, falling back to transmit.", PluginTag);
            }

            return true;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private bool IsSegmentClear(CRayTraceInterface rayTrace, CCSPlayerPawn viewerPawn, Vector3 start, Vector3 end, TraceOptions options)
    {
        _traceStart.X = start.X;
        _traceStart.Y = start.Y;
        _traceStart.Z = start.Z;

        _traceEnd.X = end.X;
        _traceEnd.Y = end.Y;
        _traceEnd.Z = end.Z;

        bool didHit = rayTrace.TraceEndShape(_traceStart, _traceEnd, viewerPawn, options, out TraceResult result);
        return !didHit || result.Fraction >= 0.99f;
    }

    private static void RemoveLinkedWeaponEntities(CCheckTransmitInfo info, PlayerSnapshot target)
    {
        EnsureLinkedWeaponIndices(target);
        if (target.LinkedIndicesCount == 0)
            return;

        for (int i = 0; i < target.LinkedIndicesCount; i++)
        {
            info.TransmitEntities.Remove(target.LinkedIndices[i]);
        }
    }

    private static void EnsureLinkedWeaponIndices(PlayerSnapshot snapshot)
    {
        if (snapshot.LinkedIndicesBuilt)
            return;

        snapshot.LinkedIndicesBuilt = true;
        snapshot.LinkedIndicesCount = 0;

        CPlayer_WeaponServices? weaponServices = snapshot.Pawn.WeaponServices;
        if (weaponServices == null)
            return;

        AddWeaponHandleIndex(snapshot, weaponServices.ActiveWeapon);
        AddWeaponHandleIndex(snapshot, weaponServices.LastWeapon);

        if (weaponServices is CCSPlayer_WeaponServices cssWeaponServices)
            AddWeaponHandleIndex(snapshot, cssWeaponServices.SavedWeapon);

        NetworkedVector<CHandle<CBasePlayerWeapon>> myWeapons = weaponServices.MyWeapons;
        int myWeaponCount = myWeapons.Count;
        for (int i = 0; i < myWeaponCount; i++)
        {
            AddWeaponHandleIndex(snapshot, myWeapons[i]);
        }
    }

    private static void AddWeaponHandleIndex(PlayerSnapshot snapshot, CHandle<CBasePlayerWeapon> weaponHandle)
    {
        if (!weaponHandle.IsValid)
            return;

        uint index = weaponHandle.Index;
        if (index == 0 || index >= (Utilities.MaxEdicts - 1))
            return;

        for (int i = 0; i < snapshot.LinkedIndicesCount; i++)
        {
            if (snapshot.LinkedIndices[i] == index)
                return;
        }

        if (snapshot.LinkedIndicesCount >= snapshot.LinkedIndices.Length)
        {
            uint[] newBuffer = new uint[snapshot.LinkedIndices.Length * 2];
            Array.Copy(snapshot.LinkedIndices, newBuffer, snapshot.LinkedIndices.Length);
            snapshot.LinkedIndices = newBuffer;
        }

        snapshot.LinkedIndices[snapshot.LinkedIndicesCount++] = index;
    }

    private void BuildPlayerSnapshots(int currentTick)
    {
        if (_snapshotCacheTick == currentTick)
            return;

        _targetsAll.Clear();
        _targetsAllNonBot.Clear();
        _targetsT.Clear();
        _targetsCT.Clear();
        _targetsTNonBot.Clear();
        _targetsCTNonBot.Clear();
        Array.Clear(_snapshotBySlot);
        _lastSnapshotCount = 0;
        _lastBotSnapshotCount = 0;
        int snapshotCount = 0;

        foreach (CCSPlayerController player in Utilities.GetPlayers())
        {
            int slot = player.Slot;
            if (!IsSlotInRange(slot))
                continue;

            CsTeam team = player.Team;
            if (team is not (CsTeam.Terrorist or CsTeam.CounterTerrorist))
                continue;

            bool isBot = player.IsBot;
            PlayerSnapshot snapshot = _snapshotCacheBySlot[slot] ??= new PlayerSnapshot();
            if (!TryPopulateSnapshot(player, snapshot, slot, team, isBot))
                continue;

            snapshotCount++;
            _snapshotBySlot[slot] = snapshot;
            _targetsAll.Add(snapshot);
            if (!isBot)
                _targetsAllNonBot.Add(snapshot);

            if (team == CsTeam.Terrorist)
            {
                _targetsT.Add(snapshot);
                if (!isBot)
                    _targetsTNonBot.Add(snapshot);
            }
            else
            {
                _targetsCT.Add(snapshot);
                if (!isBot)
                    _targetsCTNonBot.Add(snapshot);
            }

            if (isBot)
                _lastBotSnapshotCount++;
        }

        _lastSnapshotCount = snapshotCount;
        _snapshotCacheTick = currentTick;
    }

    private static bool TryPopulateSnapshot(CCSPlayerController player, PlayerSnapshot snapshot, int slot, CsTeam team, bool isBot)
    {
        try
        {
            if (!player.IsValid)
                return false;

            if (player.IsHLTV)
                return false;

            if (!isBot && player.Connected != PlayerConnectedState.PlayerConnected)
                return false;

            CCSPlayerPawn? pawn = null;
            if (player.Pawn.IsValid)
            {
                CBasePlayerPawn? basePawn = player.Pawn.Value;
                if (basePawn == null || !basePawn.IsValid)
                    return false;

                pawn = basePawn.As<CCSPlayerPawn>();
            }
            else if (player.PlayerPawn.IsValid)
            {
                pawn = player.PlayerPawn.Value;
            }

            if (pawn == null || !pawn.IsValid)
                return false;

            if (!player.PawnIsAlive && pawn.LifeState != (byte)LifeState_t.LIFE_ALIVE)
                return false;

            CounterStrikeSharp.API.Modules.Utils.Vector? absOrigin = pawn.AbsOrigin;
            if (absOrigin == null)
                return false;

            Vector3 viewOffsetVector = new(0.0f, 0.0f, 64.0f);
            try
            {
                var viewOffset = pawn.ViewOffset;
                viewOffsetVector = new Vector3(viewOffset.X, viewOffset.Y, viewOffset.Z);
            }
            catch
            {
                // Fallback keeps LOS functional even if schema offsets fail for this field.
            }

            Vector3 velocityVector = Vector3.Zero;
            try
            {
                var velocity = pawn.AbsVelocity;
                velocityVector = new Vector3(velocity.X, velocity.Y, velocity.Z);
            }
            catch
            {
                // No velocity means preload still works from static grace logic.
            }

            float sideProbe = 14.0f;
            try
            {
                sideProbe = Math.Clamp(pawn.Collision.BoundingRadius, 6.0f, 40.0f);
            }
            catch
            {
                // Keep default probe width.
            }

            snapshot.Populate(
                slot,
                team,
                isBot,
                pawn,
                new Vector3(absOrigin.X, absOrigin.Y, absOrigin.Z),
                viewOffsetVector,
                velocityVector,
                sideProbe);

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ExtendTargetForce(int targetSlot, int ticks, int currentTick)
    {
        if (!IsSlotInRange(targetSlot))
            return;

        if (ticks <= 0)
            return;

        int untilTick = currentTick + ticks;
        if (_forceTargetTransmitUntilTick[targetSlot] >= untilTick)
            return;

        _forceTargetTransmitUntilTick[targetSlot] = untilTick;
    }

    private void ExtendPairForce(int viewerSlot, int targetSlot, int ticks, int currentTick)
    {
        if (!IsSlotInRange(viewerSlot) || !IsSlotInRange(targetSlot))
            return;

        if (ticks <= 0)
            return;

        PairState state = GetOrCreatePairStateByIndex(PairIndex(viewerSlot, targetSlot));
        state.ForceUntilTick = Math.Max(state.ForceUntilTick, currentTick + ticks);
    }

    private void RemoveStateForSlot(int slot)
    {
        if (!IsSlotInRange(slot))
            return;

        _forceTargetTransmitUntilTick[slot] = 0;
        _snapshotBySlot[slot] = null;
        _snapshotCacheBySlot[slot] = null;

        int rowStart = slot * MaxSlotCount;
        Array.Clear(_pairStateTable, rowStart, MaxSlotCount);
        for (int row = 0; row < MaxSlotCount; row++)
        {
            _pairStateTable[(row * MaxSlotCount) + slot] = null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PairState GetOrCreatePairStateByIndex(int idx)
    {
        PairState? state = _pairStateTable[idx];
        if (state == null)
        {
            state = new PairState();
            _pairStateTable[idx] = state;
        }

        return state;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int PairIndex(int viewerSlot, int targetSlot)
    {
        return (viewerSlot * MaxSlotCount) + targetSlot;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsSlotInRange(int slot) => slot >= 0 && slot < MaxSlotCount;

    private FrameTuning BuildFrameTuning()
    {
        float maxTraceDistance = Config.MaxTraceDistance;
        float preloadMinSpeed = Config.PreloadMinSpeed;
        float peekSpeed = MathF.Max(Config.HorizontalPeekSpeedForMaxBonus, 1.0f);

        return new FrameTuning(
            maxTraceDistanceSquared: maxTraceDistance * maxTraceDistance,
            preloadMinSpeedSquared: preloadMinSpeed * preloadMinSpeed,
            preloadStepTime: Server.TickInterval * Config.PreloadVelocityScale,
            visibleGraceTicks: Config.VisibleGraceTicks,
            preloadHoldTicks: Config.PreloadHoldTicks,
            preloadLookaheadTicks: Config.PreloadLookaheadTicks,
            targetPaddingUnits: Config.TargetPaddingUnits,
            sideProbeUnits: Config.SideProbeUnits,
            horizontalPeekBonusUnits: Config.HorizontalPeekBonusUnits,
            horizontalPeekSpeedInverse: 1.0f / peekSpeed);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float DistanceSquared(Vector3 a, Vector3 b)
    {
        float dx = a.X - b.X;
        float dy = a.Y - b.Y;
        float dz = a.Z - b.Z;
        return (dx * dx) + (dy * dy) + (dz * dz);
    }

    private void RefreshSegmentTraceOptions()
    {
        _segmentTraceOptions = BuildTraceOptions(includePlayerClip: !Config.AllowLosThroughPlayerClip);
    }

    private void ClearRuntimeState(bool resetSnapshotTick)
    {
        _targetsAll.Clear();
        _targetsAllNonBot.Clear();
        _targetsT.Clear();
        _targetsCT.Clear();
        _targetsTNonBot.Clear();
        _targetsCTNonBot.Clear();
        Array.Clear(_snapshotBySlot);
        Array.Clear(_snapshotCacheBySlot);
        Array.Clear(_pairStateTable);
        Array.Clear(_forceTargetTransmitUntilTick);

        _lastSnapshotCount = 0;
        _lastBotSnapshotCount = 0;
        _lastViewerCount = 0;
        _lastHiddenPawnRemovals = 0;

        if (resetSnapshotTick)
            _snapshotCacheTick = int.MinValue;
    }

    private TraceOptions BuildTraceOptions(bool includePlayerClip = true)
    {
        InteractionLayers mask = includePlayerClip ? OcclusionTraceMask : OcclusionTraceMaskWithoutPlayerClip;

        return new TraceOptions(
            (InteractionLayers)0,
            mask,
            InteractionLayers.Player | InteractionLayers.NPC,
            Config.DebugTraceBeams
        );
    }

    private void RefreshRayTrace(bool logIfAvailable)
    {
        _rayTrace = TryGetRayTraceCapability();

        if (_rayTrace != null)
        {
            _missingRayTraceLogged = false;
            _traceFailureLogged = false;
            _traceDisabledByFailure = false;
            if (logIfAvailable)
                Logger.LogInformation("{PluginTag} RayTrace capability ready.", PluginTag);
            return;
        }

        if (!_missingRayTraceLogged)
        {
            _missingRayTraceLogged = true;
            Logger.LogWarning("{PluginTag} RayTrace capability missing. Ensure RayTraceImpl is loaded.", PluginTag);
        }
    }

    private CRayTraceInterface? EnsureRayTrace()
    {
        if (_rayTrace != null)
            return _rayTrace;

        _rayTrace = TryGetRayTraceCapability();
        if (_rayTrace == null)
        {
            if (!_missingRayTraceLogged)
            {
                _missingRayTraceLogged = true;
                Logger.LogWarning("{PluginTag} RayTrace capability missing. Ensure RayTraceImpl is loaded.", PluginTag);
            }
            return null;
        }

        _missingRayTraceLogged = false;
        return _rayTrace;
    }

    private static CRayTraceInterface? TryGetRayTraceCapability()
    {
        try
        {
            return RayTraceCapability.Get();
        }
        catch (KeyNotFoundException)
        {
            return null;
        }
    }

    private void OnStatusCommand(CCSPlayerController? _, CommandInfo info)
    {
        bool hasRayTrace = EnsureRayTrace() != null;
        string enabledText = Config.Enabled ? "On" : "Off";
        string rayTraceText = hasRayTrace ? "Ready" : "Missing";
        string playerClipRuleText = Config.AllowLosThroughPlayerClip ? "Ignore PlayerClip: On" : "Ignore PlayerClip: Off";
        int currentTick = Server.TickCount;
        int forcedTargets = 0;
        for (int i = 0; i < MaxSlotCount; i++)
        {
            if (_forceTargetTransmitUntilTick[i] >= currentTick)
                forcedTargets++;
        }

        info.ReplyToCommand(
            $"{PluginTag} Status -> Enabled: {enabledText}, RayTrace: {rayTraceText}, Players: {_lastSnapshotCount}, Bots: {_lastBotSnapshotCount}, Viewers Checked: {_lastViewerCount}, Hidden Players: {_lastHiddenPawnRemovals}, Force-Visible Players: {forcedTargets}, {playerClipRuleText}");
    }

    private void OnResetCommand(CCSPlayerController? _, CommandInfo info)
    {
        ClearRuntimeState(resetSnapshotTick: true);
        info.ReplyToCommand($"{PluginTag} Runtime data reset.");
    }
}
