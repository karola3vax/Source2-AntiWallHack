using System.Reflection;
using CounterStrikeSharp.API.Core;
using RayTraceAPI;

namespace S2AWH;

public partial class S2AWH
{
    private static readonly bool HasRequiredTraceResultSurface = DetectRequiredTraceResultSurface();
    private static readonly Version MinimumCounterStrikeSharpApiVersion = new(1, 0, 362, 0);
    private static readonly Version UnknownAssemblyVersion = new(1, 0, 0, 0);

    private void LogRuntimeValidationSummary()
    {
        if (_hasLoggedRuntimeValidationSummary)
        {
            return;
        }

        string bitVecMode = CanUseDirectTransmitBitVecAccess
            ? "direct bitset fast-path"
            : "safe wrapper fallback";
        string cssVersion = GetAssemblyVersionString(typeof(BasePlugin).Assembly);
        string rayTraceApiVersion = GetAssemblyVersionString(typeof(TraceResult).Assembly);
        string traceResultSurface = HasRequiredTraceResultSurface
            ? "TraceResult exposes the required hit/all-solid members."
            : "TraceResult surface is missing required hit/all-solid members.";

        var lines = new List<string>(6)
        {
            "Runtime validation complete.",
            $"Transmit bitset mode: {bitVecMode}.",
            $"CounterStrikeSharp.API assembly: {cssVersion}. RayTraceApi assembly: {rayTraceApiVersion}.",
            traceResultSurface,
            "Visibility traces block against world geometry only.",
            "Critical safety gates are ready."
        };

        WriteLevelBox(LevelDebug, lines);
        _hasLoggedRuntimeValidationSummary = true;
    }

    private void RunRuntimeSelfValidation(int nowTick)
    {
        if (_lastRuntimeSelfValidationTick != int.MinValue &&
            (nowTick - _lastRuntimeSelfValidationTick) < RuntimeSelfValidationIntervalTicks)
        {
            return;
        }

        _lastRuntimeSelfValidationTick = nowTick;
        ValidateDependencySurface();
        ValidateKnownEntityHandleState();
    }

    private void ValidateDependencySurface()
    {
        if (!HasRequiredTraceResultSurface && !_hasLoggedDependencySurfaceWarning)
        {
            WarnLog(
                "RayTraceApi surface check failed.",
                "TraceResult is missing one or more required hit/all-solid members expected by S2AWH.",
                "Update Ray-Trace and RayTraceApi.dll to the same 1.0.6+ line."
            );
            _hasLoggedDependencySurfaceWarning = true;
        }

        Version? cssVersion = typeof(BasePlugin).Assembly.GetName().Version;
        if (cssVersion != null &&
            cssVersion != UnknownAssemblyVersion &&
            cssVersion < MinimumCounterStrikeSharpApiVersion &&
            !_hasLoggedDependencySurfaceWarning)
        {
            WarnLog(
                "CounterStrikeSharp version is too old.",
                $"Detected {cssVersion}, but S2AWH expects {MinimumCounterStrikeSharpApiVersion} or newer.",
                "Update CounterStrikeSharp before relying on the fast transmit path."
            );
            _hasLoggedDependencySurfaceWarning = true;
        }

        if (!_hasLoggedVisibilityScopeNote)
        {
            DebugLog(
                "Visibility scope note.",
                "S2AWH blocks LOS using world geometry only.",
                "Smoke and other gameplay occluders are intentionally outside this trace mask."
            );
            _hasLoggedVisibilityScopeNote = true;
        }
    }

    private void ValidateKnownEntityHandleState()
    {
        if (_knownEntityHandles.Count <= 0 && !_knownEntityHandlesInitialized)
        {
            return;
        }

        List<uint> rebuiltHandles = _knownEntityHandleBootstrapScratch;
        Dictionary<uint, int> rebuiltIndices = _knownEntityHandleBootstrapIndicesScratch;
        rebuiltHandles.Clear();
        rebuiltIndices.Clear();

        bool requiresRepair = _knownEntityHandles.Count != _knownEntityHandleIndices.Count;
        int handleCount = _knownEntityHandles.Count;
        for (int i = 0; i < handleCount; i++)
        {
            uint entityHandleRaw = _knownEntityHandles[i];
            if (!IsValidTrackedEntityHandle(entityHandleRaw) ||
                rebuiltIndices.ContainsKey(entityHandleRaw))
            {
                requiresRepair = true;
                continue;
            }

            rebuiltIndices[entityHandleRaw] = rebuiltHandles.Count;
            rebuiltHandles.Add(entityHandleRaw);

            if (!_knownEntityHandleIndices.TryGetValue(entityHandleRaw, out int actualIndex) || actualIndex != i)
            {
                requiresRepair = true;
            }
        }

        if (!requiresRepair)
        {
            rebuiltHandles.Clear();
            rebuiltIndices.Clear();
            return;
        }

        _knownEntityHandles.Clear();
        _knownEntityHandles.AddRange(rebuiltHandles);
        _knownEntityHandleIndices.Clear();
        foreach ((uint entityHandleRaw, int index) in rebuiltIndices)
        {
            _knownEntityHandleIndices[entityHandleRaw] = index;
        }

        _knownEntityHandlesInitialized = _knownEntityHandles.Count > 0 || _knownEntityHandlesInitialized;
        _knownEntityBootstrapRetryUntilTick = -1;
        DebugLog(
            "Known entity index map repaired.",
            $"Recovered {_knownEntityHandles.Count} tracked handles after a runtime consistency check.",
            "Transmit ownership lookups remain synchronized."
        );

        rebuiltHandles.Clear();
        rebuiltIndices.Clear();
    }

    private static bool DetectRequiredTraceResultSurface()
    {
        Type traceResultType = typeof(TraceResult);
        return HasPublicInstanceMember(traceResultType, nameof(TraceResult.HasExactHit)) &&
               HasPublicInstanceMember(traceResultType, nameof(TraceResult.HitPointX)) &&
               HasPublicInstanceMember(traceResultType, nameof(TraceResult.HitPointY)) &&
               HasPublicInstanceMember(traceResultType, nameof(TraceResult.HitPointZ)) &&
               HasPublicInstanceMember(traceResultType, nameof(TraceResult.IsAllSolid));
    }

    private static bool HasPublicInstanceMember(Type type, string name)
    {
        const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public;
        return type.GetProperty(name, Flags) != null || type.GetField(name, Flags) != null;
    }

    private static string GetAssemblyVersionString(Assembly assembly)
    {
        AssemblyName name = assembly.GetName();
        return name.Version?.ToString() ?? "unknown";
    }
}
