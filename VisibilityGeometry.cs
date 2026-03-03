using System.Drawing;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using RayTraceAPI;

namespace S2AWH;

internal enum DebugAabbKind : byte
{
    None = 0,
    Los = 1,
    PredictorCurrent = 2,
    PredictorPredicted = 3
}

internal enum DebugTraceKind : byte
{
    AimRay = 0,
    MicroHull = 1,
    LosSurface = 2
}

internal static class VisibilityGeometry
{
    private static readonly QAngle BeamRotationZero = new(0.0f, 0.0f, 0.0f);
    private static readonly Vector BeamVelocityZero = new(0.0f, 0.0f, 0.0f);
    private static readonly Color LosSurfaceDebugBeamColor = Color.FromArgb(255, 190, 80, 255);
    private static readonly Color AimRayDebugBeamColor = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color MicroHullDebugBeamColor = Color.FromArgb(255, 255, 96, 96);
    private static readonly Color LosDebugAabbColor = Color.FromArgb(255, 255, 170, 0);
    private static readonly Color PredictorCurrentDebugAabbColor = Color.FromArgb(255, 0, 225, 120);
    private static readonly Color PredictorFutureDebugAabbColor = Color.FromArgb(255, 225, 80, 255);
    private const float DebugBeamWidth = 1.5f;
    private const float DebugBeamLifetimeSeconds = 0.08f;
    private const float DebugAabbLineWidth = 1.2f;
    private const float DebugAabbLifetimeSeconds = 0.08f;
    private const int MaxDebugBeamEntitiesPerTick = 256;
    private static readonly (int Start, int End)[] DebugAabbEdges = new[]
    {
        (0, 1), (1, 3), (3, 2), (2, 0), // lower ring
        (4, 5), (5, 7), (7, 6), (6, 4), // upper ring
        (0, 4), (1, 5), (2, 6), (3, 7)  // vertical edges
    };
    // LOS should be blocked by world geometry, not by other player models standing in front.
    private static readonly TraceOptions VisibilityTraceOptions = new(
        (InteractionLayers)0,
        InteractionLayers.MASK_WORLD_ONLY
    );
    private static int _debugBudgetTick = -1;
    private static int _debugBeamEntitiesUsedThisTick;

    /// <summary>
    /// Returns shared trace options used by LOS checks.
    /// </summary>
    public static TraceOptions GetVisibilityTraceOptions()
    {
        return VisibilityTraceOptions;
    }

    /// <summary>
    /// Returns whether debug LOS beams should be rendered for the viewer type.
    /// </summary>
    public static bool ShouldDrawDebugTraceBeam(bool viewerIsBot)
    {
        var diagnostics = S2AWHState.Current.Diagnostics;
        if (!diagnostics.DrawDebugTraceBeams)
        {
            return false;
        }

        return viewerIsBot
            ? diagnostics.DrawDebugTraceBeamsForBots
            : diagnostics.DrawDebugTraceBeamsForHumans;
    }

    /// <summary>
    /// Returns whether debug AABB boxes should be rendered.
    /// </summary>
    public static bool ShouldDrawDebugAabbBox()
    {
        return S2AWHState.Current.Diagnostics.DrawDebugAabbBoxes;
    }

    /// <summary>
    /// Draws a short-lived debug beam for a single trace.
    /// </summary>
    public static void DrawDebugTraceBeam(
        Vector start,
        Vector intendedEnd,
        in TraceResult traceResult,
        DebugTraceKind traceKind)
    {
        if (!TryConsumeDebugBeamBudget(1))
        {
            return;
        }

        CBeam? beam = Utilities.CreateEntityByName<CBeam>("env_beam");
        if (beam == null || !beam.IsValid)
        {
            return;
        }

        beam.Render = ResolveDebugTraceColor(traceKind);
        beam.Width = DebugBeamWidth;
        beam.RenderMode = RenderMode_t.kRenderNormal;
        beam.RenderFX = RenderFx_t.kRenderFxNone;

        beam.Teleport(start, BeamRotationZero, BeamVelocityZero);

        if (traceResult.DidHit)
        {
            beam.EndPos.X = traceResult.EndPosX;
            beam.EndPos.Y = traceResult.EndPosY;
            beam.EndPos.Z = traceResult.EndPosZ;
        }
        else
        {
            beam.EndPos.X = intendedEnd.X;
            beam.EndPos.Y = intendedEnd.Y;
            beam.EndPos.Z = intendedEnd.Z;
        }

        beam.DispatchSpawn();
        beam.AddEntityIOEvent("Kill", beam, beam, delay: DebugBeamLifetimeSeconds);
    }

    /// <summary>
    /// Draws a short-lived wireframe AABB using 12 beam edges.
    /// </summary>
    public static void DrawDebugAabbBox(
        float minX,
        float minY,
        float minZ,
        float maxX,
        float maxY,
        float maxZ,
        DebugAabbKind kind)
    {
        if (kind == DebugAabbKind.None || !ShouldDrawDebugAabbBox())
        {
            return;
        }

        if (!TryConsumeDebugBeamBudget(DebugAabbEdges.Length))
        {
            return;
        }

        Color color = ResolveDebugAabbColor(kind);
        Vector[] cornerBuffer = CreateDebugAabbCornerBuffer();

        SetPoint(cornerBuffer, 0, minX, minY, minZ);
        SetPoint(cornerBuffer, 1, maxX, minY, minZ);
        SetPoint(cornerBuffer, 2, minX, maxY, minZ);
        SetPoint(cornerBuffer, 3, maxX, maxY, minZ);
        SetPoint(cornerBuffer, 4, minX, minY, maxZ);
        SetPoint(cornerBuffer, 5, maxX, minY, maxZ);
        SetPoint(cornerBuffer, 6, minX, maxY, maxZ);
        SetPoint(cornerBuffer, 7, maxX, maxY, maxZ);

        for (int i = 0; i < DebugAabbEdges.Length; i++)
        {
            var edge = DebugAabbEdges[i];
            DrawDebugLine(cornerBuffer[edge.Start], cornerBuffer[edge.End], color, DebugAabbLineWidth, DebugAabbLifetimeSeconds);
        }
    }

    private static void SetPoint(Vector[] pointBuffer, int index, float x, float y, float z)
    {
        Vector point = pointBuffer[index];
        point.X = x;
        point.Y = y;
        point.Z = z;
    }

    private static Vector[] CreateDebugAabbCornerBuffer()
    {
        Vector[] corners = new Vector[8];
        for (int i = 0; i < corners.Length; i++)
        {
            corners[i] = new Vector(0.0f, 0.0f, 0.0f);
        }

        return corners;
    }

    private static void DrawDebugLine(Vector start, Vector end, Color color, float width, float lifetime)
    {
        CBeam? beam = Utilities.CreateEntityByName<CBeam>("env_beam");
        if (beam == null || !beam.IsValid)
        {
            return;
        }

        beam.Render = color;
        beam.Width = width;
        beam.RenderMode = RenderMode_t.kRenderNormal;
        beam.RenderFX = RenderFx_t.kRenderFxNone;

        beam.Teleport(start, BeamRotationZero, BeamVelocityZero);
        beam.EndPos.X = end.X;
        beam.EndPos.Y = end.Y;
        beam.EndPos.Z = end.Z;

        beam.DispatchSpawn();
        beam.AddEntityIOEvent("Kill", beam, beam, delay: lifetime);
    }

    private static bool TryConsumeDebugBeamBudget(int amount)
    {
        int nowTick = Server.TickCount;
        if (_debugBudgetTick != nowTick)
        {
            _debugBudgetTick = nowTick;
            _debugBeamEntitiesUsedThisTick = 0;
        }

        if ((_debugBeamEntitiesUsedThisTick + amount) > MaxDebugBeamEntitiesPerTick)
        {
            return false;
        }

        _debugBeamEntitiesUsedThisTick += amount;
        return true;
    }

    private static Color ResolveDebugAabbColor(DebugAabbKind kind)
    {
        return kind switch
        {
            DebugAabbKind.Los => LosDebugAabbColor,
            DebugAabbKind.PredictorCurrent => PredictorCurrentDebugAabbColor,
            DebugAabbKind.PredictorPredicted => PredictorFutureDebugAabbColor,
            _ => LosDebugAabbColor
        };
    }

    private static Color ResolveDebugTraceColor(DebugTraceKind traceKind)
    {
        return traceKind switch
        {
            DebugTraceKind.LosSurface => LosSurfaceDebugBeamColor,
            DebugTraceKind.AimRay => AimRayDebugBeamColor,
            DebugTraceKind.MicroHull => MicroHullDebugBeamColor,
            _ => throw new ArgumentOutOfRangeException(nameof(traceKind), traceKind, "Unknown debug trace kind.")
        };
    }
}
