using System.Drawing;
using System.Numerics;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using S2FOW.Config;
using S2FOW.Models;

namespace S2FOW.Core;

internal sealed class DebugAabbRenderer : IDisposable
{
    private const float RayBeamWidth = 0.1f;
    private const float PointBeamWidth = 0.4f;
    private const float FallbackPointBeamWidth = 0.45f;
    private const float LineBeamWidth = 0.2f;
    private const float FallbackLineBeamWidth = 0.225f;
    private const float PointHalfHeight = 0.5f;
    private const float FallbackPointHalfHeight = 0.58f;
    private const float PointBeamHdrColorScale = 2.4f;
    private const float LineBeamHdrColorScale = 1.6f;
    private const float BeamBoundsPadding = 256.0f;
    private const int VisualUpdateIntervalTicks = 1;
    private const float PointUpdateDistanceSqr = 1.0f;

    private readonly RaycastEngine _raycastEngine;
    private readonly S2FOWConfig _config;
    private readonly Dictionary<int, DebugVisualState> _states = new();
    private readonly CEnvBeam?[] _rayBeams = new CEnvBeam[RaycastEngine.MaxDebugRays];
    private readonly bool[] _touchedObservers = new bool[FowConstants.MaxSlots];
    private readonly List<int> _observersToRemove = new(16);
    private readonly Vector3[] _points = new Vector3[RaycastEngine.MaxDebugPointsPerObserver];
    private readonly bool[] _aabbFallbackPoints = new bool[RaycastEngine.MaxDebugPointsPerObserver];
    private readonly Vector3[] _lineStarts = new Vector3[RaycastEngine.MaxDebugLinesPerObserver];
    private readonly Vector3[] _lineEnds = new Vector3[RaycastEngine.MaxDebugLinesPerObserver];

    public DebugAabbRenderer(RaycastEngine raycastEngine, S2FOWConfig config)
    {
        _raycastEngine = raycastEngine;
        _config = config;
    }

    public void Update(ReadOnlySpan<PlayerSnapshot> snapshots, VisibilityManager visibilityManager, int currentTick)
    {
        Array.Clear(_touchedObservers);

        if (_config.Debug.ShowTargetPoints)
        {
            int count = Math.Min(snapshots.Length, FowConstants.MaxSlots);
            for (int i = 0; i < count; i++)
            {
                ref readonly var observer = ref snapshots[i];
                if (!ShouldDrawForObserver(in observer))
                    continue;

                _touchedObservers[observer.Slot] = true;
                UpdateObserverVisual(in observer, visibilityManager, currentTick);
            }
        }

        _observersToRemove.Clear();
        foreach ((int observerSlot, _) in _states)
        {
            if (!FowConstants.IsValidSlot(observerSlot) || !_touchedObservers[observerSlot])
                _observersToRemove.Add(observerSlot);
        }

        for (int i = 0; i < _observersToRemove.Count; i++)
            DisableObserverVisual(_observersToRemove[i]);

        if (_config.Debug.ShowRayLines)
            UpdateRayBeams();
        else
            ClearRayBeams();
    }

    public void Clear()
    {
        _observersToRemove.Clear();
        foreach ((int observerSlot, _) in _states)
            _observersToRemove.Add(observerSlot);

        for (int i = 0; i < _observersToRemove.Count; i++)
            RemoveObserverVisual(_observersToRemove[i]);

        RemoveRayBeams();
    }

    public void RemoveOtherObserverPointEntities(CCheckTransmitInfo info, int observerSlot)
    {
        foreach ((int ownerSlot, DebugVisualState state) in _states)
        {
            if (ownerSlot == observerSlot)
                continue;

            for (int i = 0; i < state.PointBeams.Length; i++)
            {
                var beam = state.PointBeams[i];
                if (beam != null && beam.IsValid && beam.Index > 0)
                    info.TransmitEntities.Remove((int)beam.Index);
            }

            for (int i = 0; i < state.LineBeams.Length; i++)
            {
                var beam = state.LineBeams[i];
                if (beam != null && beam.IsValid && beam.Index > 0)
                    info.TransmitEntities.Remove((int)beam.Index);
            }
        }
    }

    private static bool ShouldDrawForObserver(in PlayerSnapshot snapshot)
    {
        if (!snapshot.IsValid || !snapshot.IsAlive || snapshot.IsBot)
            return false;

        if (snapshot.PawnEntityIndex == 0)
            return false;

        return snapshot.Team == CsTeam.Terrorist || snapshot.Team == CsTeam.CounterTerrorist;
    }

    private void UpdateObserverVisual(in PlayerSnapshot observer, VisibilityManager visibilityManager, int currentTick)
    {
        if (!_states.TryGetValue(observer.Slot, out DebugVisualState? state))
        {
            state = new DebugVisualState();
            _states[observer.Slot] = state;
        }

        if (state.NextVisualUpdateTick > currentTick)
            return;

        state.NextVisualUpdateTick = currentTick + VisualUpdateIntervalTicks;

        int pointCount = visibilityManager.FillObserverDebugPoints(observer.Slot, _points, _aabbFallbackPoints);
        EnsurePointSet(state, pointCount);

        for (int i = 0; i < pointCount; i++)
            UpdatePointBeam(state, i, _points[i], GetPointColor(_aabbFallbackPoints[i]), _aabbFallbackPoints[i]);

        for (int i = pointCount; i < RaycastEngine.MaxDebugPointsPerObserver; i++)
            DisablePointBeam(state, i);

        int lineCount = visibilityManager.FillObserverDebugLines(observer.Slot, _lineStarts, _lineEnds);
        EnsureLineSet(state, lineCount);
        for (int i = 0; i < lineCount; i++)
            UpdateLineBeam(state, i, _lineStarts[i], _lineEnds[i], GetLineColor(false), false);
    }

    private void EnsurePointSet(DebugVisualState state, int pointCount)
    {
        for (int i = 0; i < pointCount; i++)
            EnsurePointBeam(state, i, _aabbFallbackPoints[i]);

        for (int i = pointCount; i < RaycastEngine.MaxDebugPointsPerObserver; i++)
            DisablePointBeam(state, i);
    }

    private void EnsureLineSet(DebugVisualState state, int lineCount)
    {
        for (int i = 0; i < lineCount; i++)
            EnsureLineBeam(state, i, isAabbFallback: false);

        for (int i = lineCount; i < state.LineBeams.Length; i++)
            DisableLineBeam(state, i);
    }

    private void UpdateRayBeams()
    {
        ReadOnlySpan<DebugRay> rays = _raycastEngine.DebugRays;
        for (int i = 0; i < rays.Length; i++)
        {
            if (_rayBeams[i] == null || !_rayBeams[i]!.IsValid)
                _rayBeams[i] = CreateBeam(RayBeamWidth, 1.2f);

            var beam = _rayBeams[i];
            if (beam == null || !beam.IsValid)
                continue;

            UpdateBeam(beam, rays[i].Start, rays[i].End, GetRayColor(rays[i].Visible, rays[i].Elevated, rays[i].Aim));
        }

        for (int i = rays.Length; i < _rayBeams.Length; i++)
            DisableBeam(_rayBeams[i]);
    }

    private void ClearRayBeams()
    {
        for (int i = 0; i < _rayBeams.Length; i++)
            DisableBeam(_rayBeams[i]);
    }

    private void RemoveRayBeams()
    {
        for (int i = 0; i < _rayBeams.Length; i++)
            RemoveEntity(ref _rayBeams[i]);
    }

    private void DisableObserverVisual(int observerSlot)
    {
        if (!_states.TryGetValue(observerSlot, out DebugVisualState? state))
            return;

        for (int i = 0; i < RaycastEngine.MaxDebugPointsPerObserver; i++)
            DisablePointBeam(state, i);

        for (int i = 0; i < state.LineBeams.Length; i++)
            DisableLineBeam(state, i);
    }

    private void RemoveObserverVisual(int observerSlot)
    {
        if (!_states.TryGetValue(observerSlot, out DebugVisualState? state))
            return;

        for (int i = 0; i < RaycastEngine.MaxDebugPointsPerObserver; i++)
            RemovePointBeam(state, i);

        for (int i = 0; i < state.LineBeams.Length; i++)
            RemoveLineBeam(state, i);

        _states.Remove(observerSlot);
    }

    private static Color GetPointColor(bool isAabbFallback)
    {
        return isAabbFallback
            ? Color.FromArgb(255, 70, 110, 170)
            : Color.FromArgb(255, 255, 255, 255);
    }

    private static Color GetLineColor(bool isAabbFallback)
    {
        return isAabbFallback
            ? Color.FromArgb(215, 85, 130, 190)
            : Color.FromArgb(215, 235, 235, 235);
    }

    private static Color GetRayColor(bool visible, bool elevated, bool aim)
    {
        if (aim)
        {
            return visible
                ? Color.FromArgb(255, 255, 210, 90)
                : Color.FromArgb(255, 255, 150, 90);
        }

        if (elevated)
        {
            return visible
                ? Color.FromArgb(255, 120, 255, 220)
                : Color.FromArgb(255, 120, 170, 255);
        }

        return visible
            ? Color.FromArgb(255, 255, 220, 70)
            : Color.FromArgb(255, 70, 130, 255);
    }

    private static CEnvBeam? CreateBeam(float width, float hdrColorScale)
    {
        var beam = Utilities.CreateEntityByName<CEnvBeam>("env_beam");
        if (beam == null || !beam.IsValid)
            return null;

        beam.BeamType = BeamType_t.BEAM_POINTS;
        beam.NumBeamEnts = 2;
        beam.Width = width;
        beam.EndWidth = width;
        beam.BoltWidth = width;
        beam.NoiseAmplitude = 0.0f;
        beam.Amplitude = 0.0f;
        beam.Life = 0.0f;
        beam.FadeLength = 0.0f;
        beam.FrameRate = 0.0f;
        beam.StartFrame = 0.0f;
        beam.Frame = 0.0f;
        beam.HDRColorScale = hdrColorScale;
        beam.HaloScale = 0.0f;
        beam.Speed = 0;
        beam.ClipStyle = BeamClipStyle_t.kNOCLIP;
        beam.RenderMode = RenderMode_t.kRenderTransAlpha;
        beam.RenderFX = RenderFx_t.kRenderFxNone;
        beam.AllowFadeInView = false;
        beam.FadeMinDist = -1.0f;
        beam.FadeMaxDist = 100000.0f;
        beam.FadeScale = 1.0f;
        beam.ObjectCulling = 0;
        beam.Active = 1;
        beam.TurnedOff = false;
        beam.Render = Color.White;
        beam.NoInterpolate = true;
        beam.Collision.SurroundType = SurroundingBoundsType_t.USE_SPECIFIED_BOUNDS;
        beam.DispatchSpawn();
        beam.AcceptInput("TurnOn");
        return beam;
    }

    private void EnsurePointBeam(DebugVisualState state, int pointIndex, bool isAabbFallback)
    {
        if (state.PointBeams[pointIndex] != null && state.PointBeams[pointIndex]!.IsValid)
        {
            ApplyPointBeamVisualSettings(state.PointBeams[pointIndex], isAabbFallback);
            return;
        }

        GetPointVisualSettings(isAabbFallback, out float width, out _);
        state.PointBeams[pointIndex] = CreateBeam(width, PointBeamHdrColorScale);
    }

    private void EnsureLineBeam(DebugVisualState state, int lineIndex, bool isAabbFallback)
    {
        if (state.LineBeams[lineIndex] != null && state.LineBeams[lineIndex]!.IsValid)
        {
            ApplyLineBeamVisualSettings(state.LineBeams[lineIndex], isAabbFallback);
            return;
        }

        GetLineVisualSettings(isAabbFallback, out float width);
        state.LineBeams[lineIndex] = CreateBeam(width, LineBeamHdrColorScale);
    }

    private static void UpdateBeam(CEnvBeam beam, Vector3 start, Vector3 end, Color color)
    {
        beam.Teleport(start, null, null);
        beam.EndPointWorld.X = end.X;
        beam.EndPointWorld.Y = end.Y;
        beam.EndPointWorld.Z = end.Z;
        beam.EndPos.X = end.X;
        beam.EndPos.Y = end.Y;
        beam.EndPos.Z = end.Z;
        beam.Render = color;
        bool needsTurnOn = beam.Active == 0 || beam.TurnedOff;
        beam.Active = 1;
        beam.TurnedOff = false;
        if (needsTurnOn)
            beam.AcceptInput("TurnOn");
        UpdateBeamBounds(beam, start, end);

        TrySetStateChanged(beam, "CEnvBeam", "m_vEndPointWorld");
        TrySetStateChanged(beam, "CEnvBeam", "m_active");
        TrySetStateChanged(beam, "CBeam", "m_vecEndPos");
        TrySetStateChanged(beam, "CBeam", "m_bTurnedOff");
        TrySetStateChanged(beam, "CBaseModelEntity", "m_clrRender");
    }

    private static void UpdateBeamBounds(CEnvBeam beam, Vector3 start, Vector3 end)
    {
        float minX = MathF.Min(0.0f, end.X - start.X) - BeamBoundsPadding;
        float maxX = MathF.Max(0.0f, end.X - start.X) + BeamBoundsPadding;
        float minY = MathF.Min(0.0f, end.Y - start.Y) - BeamBoundsPadding;
        float maxY = MathF.Max(0.0f, end.Y - start.Y) + BeamBoundsPadding;
        float minZ = MathF.Min(0.0f, end.Z - start.Z) - BeamBoundsPadding;
        float maxZ = MathF.Max(0.0f, end.Z - start.Z) + BeamBoundsPadding;

        var collision = beam.Collision;
        collision.SpecifiedSurroundingMins.X = minX;
        collision.SpecifiedSurroundingMins.Y = minY;
        collision.SpecifiedSurroundingMins.Z = minZ;
        collision.SpecifiedSurroundingMaxs.X = maxX;
        collision.SpecifiedSurroundingMaxs.Y = maxY;
        collision.SpecifiedSurroundingMaxs.Z = maxZ;
        collision.BoundingRadius = MathF.Sqrt(maxX * maxX + maxY * maxY + maxZ * maxZ);
    }

    private static void UpdatePointBeam(DebugVisualState state, int pointIndex, Vector3 center, Color color, bool isAabbFallback)
    {
        if (state.PointInitialized[pointIndex] &&
            Vector3.DistanceSquared(state.LastPointCenters[pointIndex], center) <= PointUpdateDistanceSqr)
        {
            return;
        }

        GetPointVisualSettings(isAabbFallback, out _, out float halfHeight);
        ApplyPointBeamVisualSettings(state.PointBeams[pointIndex], isAabbFallback);
        UpdatePointBeamEntity(state.PointBeams[pointIndex], center, color, halfHeight);
        state.LastPointCenters[pointIndex] = center;
        state.PointInitialized[pointIndex] = true;
    }

    private static void DisablePointBeam(DebugVisualState state, int pointIndex)
    {
        state.PointInitialized[pointIndex] = false;
        DisableBeam(state.PointBeams[pointIndex]);
    }

    private static void RemovePointBeam(DebugVisualState state, int pointIndex)
    {
        state.PointInitialized[pointIndex] = false;
        RemoveEntity(ref state.PointBeams[pointIndex]);
    }

    private static void UpdateLineBeam(DebugVisualState state, int lineIndex, Vector3 start, Vector3 end, Color color, bool isAabbFallback)
    {
        ApplyLineBeamVisualSettings(state.LineBeams[lineIndex], isAabbFallback);
        if (state.LineBeams[lineIndex] == null || !state.LineBeams[lineIndex]!.IsValid)
            return;

        UpdateBeam(state.LineBeams[lineIndex]!, start, end, color);
    }

    private static void DisableLineBeam(DebugVisualState state, int lineIndex)
    {
        DisableBeam(state.LineBeams[lineIndex]);
    }

    private static void RemoveLineBeam(DebugVisualState state, int lineIndex)
    {
        RemoveEntity(ref state.LineBeams[lineIndex]);
    }

    private static void UpdatePointBeamEntity(CEnvBeam? beam, Vector3 center, Color color, float halfHeight)
    {
        if (beam == null || !beam.IsValid)
            return;

        Vector3 start = new(center.X, center.Y, center.Z - halfHeight);
        Vector3 end = new(center.X, center.Y, center.Z + halfHeight);
        UpdateBeam(beam, start, end, color);
    }

    private static void GetPointVisualSettings(bool isAabbFallback, out float beamWidth, out float halfHeight)
    {
        if (isAabbFallback)
        {
            beamWidth = FallbackPointBeamWidth;
            halfHeight = FallbackPointHalfHeight;
            return;
        }

        beamWidth = PointBeamWidth;
        halfHeight = PointHalfHeight;
    }

    private static void GetLineVisualSettings(bool isAabbFallback, out float beamWidth)
    {
        beamWidth = isAabbFallback
            ? FallbackLineBeamWidth
            : LineBeamWidth;
    }

    private static void ApplyPointBeamVisualSettings(CEnvBeam? beam, bool isAabbFallback)
    {
        if (beam == null || !beam.IsValid)
            return;

        GetPointVisualSettings(isAabbFallback, out float width, out _);
        beam.Width = width;
        beam.EndWidth = width;
        beam.BoltWidth = width;
        TrySetStateChanged(beam, "CBeam", "m_fWidth");
        TrySetStateChanged(beam, "CBeam", "m_fEndWidth");
        TrySetStateChanged(beam, "CBeam", "m_fBoltWidth");
    }

    private static void ApplyLineBeamVisualSettings(CEnvBeam? beam, bool isAabbFallback)
    {
        if (beam == null || !beam.IsValid)
            return;

        GetLineVisualSettings(isAabbFallback, out float width);
        beam.Width = width;
        beam.EndWidth = width;
        beam.BoltWidth = width;
        TrySetStateChanged(beam, "CBeam", "m_fWidth");
        TrySetStateChanged(beam, "CBeam", "m_fEndWidth");
        TrySetStateChanged(beam, "CBeam", "m_fBoltWidth");
    }

    private static void TrySetStateChanged(CBaseEntity entity, string className, string fieldName)
    {
        if (!Schema.IsSchemaFieldNetworked(className, fieldName))
            return;

        Utilities.SetStateChanged(entity, className, fieldName);
    }

    private static void DisableBeam(CEnvBeam? beam)
    {
        if (beam == null || !beam.IsValid)
            return;

        if (beam.Active == 0 && beam.TurnedOff)
            return;

        beam.Active = 0;
        beam.TurnedOff = true;
        beam.AcceptInput("TurnOff");
        TrySetStateChanged(beam, "CEnvBeam", "m_active");
        TrySetStateChanged(beam, "CBeam", "m_bTurnedOff");
    }

    private static void RemoveEntity<T>(ref T? entity) where T : CEntityInstance
    {
        if (entity != null && entity.IsValid)
            entity.Remove();

        entity = null;
    }

    public void Dispose()
    {
        Clear();
    }
}
