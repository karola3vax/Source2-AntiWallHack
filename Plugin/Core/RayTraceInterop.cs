using System.Reflection;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Utils;

namespace S2FOW.Core;

[Flags]
internal enum InteractionLayers : ulong
{
    None = 0,
    Solid = 0x1,
    Window = 0x1000,
    PassBullets = 0x2000,
    MaskWorldOnly = Solid | Window | PassBullets
}

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

internal interface IRayTraceService
{
    bool TraceEndShape(Vector start, Vector end, CEntityInstance? ignore, TraceOptions options, out TraceResult result);
}

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
        object externalOptions = Activator.CreateInstance(_traceOptionsType)!;
        _interactsWithField.SetValue(externalOptions, options.InteractsWith);
        _interactsExcludeField.SetValue(externalOptions, options.InteractsExclude);
        _drawBeamField.SetValue(externalOptions, options.DrawBeam);

        _traceEndArgs[0] = start;
        _traceEndArgs[1] = end;
        _traceEndArgs[2] = ignore;
        _traceEndArgs[3] = externalOptions;
        _traceEndArgs[4] = Activator.CreateInstance(_traceResultType);

        bool success = (bool)(_traceEndShapeMethod.Invoke(_nativeService, _traceEndArgs) ?? false);
        object externalResult = _traceEndArgs[4]!;
        result = new TraceResult(
            Convert.ToSingle(_endPosXField.GetValue(externalResult)),
            Convert.ToSingle(_endPosYField.GetValue(externalResult)),
            Convert.ToSingle(_endPosZField.GetValue(externalResult)),
            Convert.ToSingle(_fractionField.GetValue(externalResult)));
        return success;
    }

    private static FieldInfo RequiredField(Type type, string name)
    {
        return type.GetField(name)
            ?? throw new MissingFieldException(type.FullName, name);
    }
}
