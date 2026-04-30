using System.Reflection;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;

namespace S2FOW.Core;

/// <summary>
/// World layers S2FOW asks RayTrace to check. The plugin only wants map/world
/// blockers, not players or props.
/// </summary>
[Flags]
internal enum InteractionLayers : ulong
{
    None = 0,
    Solid = 0x1,
    Window = 0x1000,
    PassBullets = 0x2000,
    MaskWorldOnly = Solid | Window | PassBullets
}

/// <summary>Options passed to RayTrace for one straight-line wall check.</summary>
internal readonly struct TraceOptions
{
    public readonly ulong InteractsWith;
    public readonly ulong InteractsExclude;
    public readonly int DrawBeam;

    public TraceOptions(InteractionLayers interactsWith, InteractionLayers interactsExclude = InteractionLayers.None, bool drawBeam = false)
    {
        InteractsWith = (ulong)interactsWith;
        InteractsExclude = (ulong)interactsExclude;
        DrawBeam = drawBeam ? 1 : 0;
    }
}

/// <summary>RayTrace result reduced to only the data S2FOW needs.</summary>
internal readonly struct TraceResult
{
    public readonly float EndPosX;
    public readonly float EndPosY;
    public readonly float EndPosZ;
    public readonly float Fraction;

    public TraceResult(float endPosX, float endPosY, float endPosZ, float fraction)
    {
        EndPosX = endPosX;
        EndPosY = endPosY;
        EndPosZ = endPosZ;
        Fraction = fraction;
    }

    public bool DidHit => Fraction < 1.0f;
}

/// <summary>
/// Small local interface around the external RayTrace plugin. Keeping this interface
/// narrow makes the rest of S2FOW read as "check this line" instead of reflection code.
/// </summary>
internal interface IRayTraceService
{
    bool TraceEndShape(Vector start, Vector end, CEntityInstance? ignore, TraceOptions options, out TraceResult result);
}

/// <summary>
/// Finds the external RayTrace plugin after all plugins load. If RayTrace is missing
/// or incompatible, S2FOW stays idle instead of crashing the server.
/// </summary>
internal static class RayTraceCapabilityResolver
{
    private const string CapabilityName = "raytrace:craytraceinterface";
    private const string RayTraceInterfaceTypeName = "RayTraceAPI.CRayTraceInterface";

    public static bool TryGet(out IRayTraceService? service, out string error)
    {
        service = null;
        error = string.Empty;

        Type? rayTraceInterfaceType = FindLoadedType(RayTraceInterfaceTypeName);
        if (rayTraceInterfaceType == null)
        {
            error = "RayTraceApi was not loaded. Install RayTraceApi as a CounterStrikeSharp shared assembly and load RayTraceImpl first.";
            return false;
        }

        Type capabilityType = typeof(PluginCapability<>).MakeGenericType(rayTraceInterfaceType);
        object capability = Activator.CreateInstance(capabilityType, CapabilityName)
            ?? throw new InvalidOperationException("Could not create RayTrace plugin capability.");

        object? nativeService;
        try
        {
            nativeService = capabilityType.GetMethod(nameof(PluginCapability<object>.Get))?.Invoke(capability, null);
        }
        catch (TargetInvocationException ex)
        {
            error = ex.InnerException?.Message ?? ex.Message;
            return false;
        }

        if (nativeService == null)
        {
            error = "RayTraceImpl did not provide the raytrace capability.";
            return false;
        }

        return ReflectiveRayTraceService.TryCreate(nativeService, rayTraceInterfaceType, out service, out error);
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type = assembly.GetType(fullName, throwOnError: false);
            if (type != null)
                return type;
        }

        return null;
    }
}

/// <summary>
/// Adapter around RayTraceApi. It caches the reflected fields and method so each
/// visibility check does not rediscover the same metadata repeatedly.
/// </summary>
internal sealed class ReflectiveRayTraceService : IRayTraceService
{
    private readonly object _nativeService;
    private readonly MethodInfo _traceEndShapeMethod;
    private readonly Type _traceOptionsType;
    private readonly Type _traceResultType;
    private readonly FieldInfo _interactsWithField;
    private readonly FieldInfo _interactsExcludeField;
    private readonly FieldInfo _drawBeamField;
    private readonly FieldInfo _endPosXField;
    private readonly FieldInfo _endPosYField;
    private readonly FieldInfo _endPosZField;
    private readonly FieldInfo _fractionField;
    private readonly object?[] _traceEndArgs = new object?[5];
    private readonly object _externalOptions;
    private object _externalResult;
    private TraceOptions _lastOptions;
    private bool _hasLastOptions;

    private ReflectiveRayTraceService(
        object nativeService,
        MethodInfo traceEndShapeMethod,
        Type traceOptionsType,
        Type traceResultType)
    {
        _nativeService = nativeService;
        _traceEndShapeMethod = traceEndShapeMethod;
        _traceOptionsType = traceOptionsType;
        _traceResultType = traceResultType;

        _interactsWithField = RequiredField(traceOptionsType, "InteractsWith");
        _interactsExcludeField = RequiredField(traceOptionsType, "InteractsExclude");
        _drawBeamField = RequiredField(traceOptionsType, "DrawBeam");
        _endPosXField = RequiredField(traceResultType, "EndPosX");
        _endPosYField = RequiredField(traceResultType, "EndPosY");
        _endPosZField = RequiredField(traceResultType, "EndPosZ");
        _fractionField = RequiredField(traceResultType, "Fraction");
        _externalOptions = Activator.CreateInstance(_traceOptionsType)!;
        _externalResult = Activator.CreateInstance(_traceResultType)!;
    }

    public static bool TryCreate(
        object nativeService,
        Type rayTraceInterfaceType,
        out IRayTraceService? service,
        out string error)
    {
        service = null;
        error = string.Empty;

        MethodInfo? traceEndShape = rayTraceInterfaceType.GetMethod("TraceEndShape");
        if (traceEndShape == null)
        {
            error = "RayTraceApi is missing TraceEndShape.";
            return false;
        }

        ParameterInfo[] parameters = traceEndShape.GetParameters();
        if (parameters.Length != 5 || !parameters[4].ParameterType.IsByRef)
        {
            error = "RayTraceApi TraceEndShape signature is not compatible.";
            return false;
        }

        service = new ReflectiveRayTraceService(
            nativeService,
            traceEndShape,
            parameters[3].ParameterType,
            parameters[4].ParameterType.GetElementType()!);
        return true;
    }

    public bool TraceEndShape(Vector start, Vector end, CEntityInstance? ignore, TraceOptions options, out TraceResult result)
    {
        if (!_hasLastOptions || !_lastOptions.Equals(options))
        {
            _interactsWithField.SetValue(_externalOptions, options.InteractsWith);
            _interactsExcludeField.SetValue(_externalOptions, options.InteractsExclude);
            _drawBeamField.SetValue(_externalOptions, options.DrawBeam);
            _lastOptions = options;
            _hasLastOptions = true;
        }

        _traceEndArgs[0] = start;
        _traceEndArgs[1] = end;
        _traceEndArgs[2] = ignore;
        _traceEndArgs[3] = _externalOptions;
        _traceEndArgs[4] = _externalResult;

        bool success = (bool)(_traceEndShapeMethod.Invoke(_nativeService, _traceEndArgs) ?? false);
        _externalResult = _traceEndArgs[4]!;
        result = new TraceResult(
            Convert.ToSingle(_endPosXField.GetValue(_externalResult)),
            Convert.ToSingle(_endPosYField.GetValue(_externalResult)),
            Convert.ToSingle(_endPosZField.GetValue(_externalResult)),
            Convert.ToSingle(_fractionField.GetValue(_externalResult)));
        return success;
    }

    private static FieldInfo RequiredField(Type type, string name)
    {
        return type.GetField(name)
            ?? throw new MissingFieldException(type.FullName, name);
    }
}
