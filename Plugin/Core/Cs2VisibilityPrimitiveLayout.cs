using System.Numerics;

namespace S2FOW.Core;

internal enum VisibilityPrimitiveKind
{
    Capsule = 0,
    Sphere = 1
}

internal enum VisibilityPrimitiveSampling
{
    SupportAndEndpoints = 0,
    SupportMidAndDistal = 1
}

internal readonly struct VisibilityPrimitive
{
    public required string Name { get; init; }
    public required string BoneName { get; init; }
    public required VisibilityPrimitiveKind Kind { get; init; }
    public required VisibilityPrimitiveSampling Sampling { get; init; }
    public required Vector3 LocalPoint0 { get; init; }
    public required Vector3 LocalPoint1 { get; init; }
    public required float Radius { get; init; }
    public required bool DistalEndpointIsPoint1 { get; init; }
    public bool UseFixedHeadOrigin { get; init; }
}

internal static class Cs2VisibilityPrimitiveLayout
{
    public const int PrimitiveCount = 19;
    public const int AabbPointCount = 8;
    public const int MaxVisibilityTestPoints = (PrimitiveCount * 3) + AabbPointCount;

    private static readonly VisibilityPrimitive[] _primitives =
    [
        new()
        {
            Name = "head_0",
            BoneName = "head_0",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportAndEndpoints,
            LocalPoint0 = new Vector3(0.44f, 0.00f, 69.78f),
            LocalPoint1 = new Vector3(-1.16f, 0.00f, 74.28f),
            Radius = 4.30f,
            DistalEndpointIsPoint1 = true,
            UseFixedHeadOrigin = true
        },
        new()
        {
            Name = "neck_0",
            BoneName = "neck_0",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportAndEndpoints,
            LocalPoint0 = new Vector3(-3.47f, 0.00f, 65.40f),
            LocalPoint1 = new Vector3(-2.86f, 0.00f, 66.68f),
            Radius = 3.50f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "spine_3",
            BoneName = "spine_3",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportAndEndpoints,
            LocalPoint0 = new Vector3(-3.92f, -6.00f, 61.31f),
            LocalPoint1 = new Vector3(-3.92f, 6.00f, 61.31f),
            Radius = 5.00f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "spine_2",
            BoneName = "spine_2",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportAndEndpoints,
            LocalPoint0 = new Vector3(-3.04f, -4.10f, 57.29f),
            LocalPoint1 = new Vector3(-3.04f, 4.10f, 57.29f),
            Radius = 6.20f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "spine_1",
            BoneName = "spine_1",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportAndEndpoints,
            LocalPoint0 = new Vector3(-1.50f, -2.40f, 51.60f),
            LocalPoint1 = new Vector3(-1.90f, 2.40f, 51.60f),
            Radius = 6.50f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "spine_0",
            BoneName = "spine_0",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportAndEndpoints,
            LocalPoint0 = new Vector3(-1.81f, 3.10f, 45.06f),
            LocalPoint1 = new Vector3(-1.81f, -3.10f, 45.06f),
            Radius = 6.00f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "pelvis",
            BoneName = "pelvis",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportAndEndpoints,
            LocalPoint0 = new Vector3(-1.75f, -3.20f, 40.12f),
            LocalPoint1 = new Vector3(-1.75f, 3.20f, 40.12f),
            Radius = 6.00f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "ankle_l",
            BoneName = "ankle_l",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportAndEndpoints,
            LocalPoint0 = new Vector3(-5.26f, 4.67f, 1.33f),
            LocalPoint1 = new Vector3(3.71f, 5.91f, 1.14f),
            Radius = 2.60f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "ankle_r",
            BoneName = "ankle_r",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportAndEndpoints,
            LocalPoint0 = new Vector3(3.69f, -5.96f, 1.16f),
            LocalPoint1 = new Vector3(-5.27f, -4.73f, 1.31f),
            Radius = 2.60f,
            DistalEndpointIsPoint1 = false
        },
        new()
        {
            Name = "leg_lower_l",
            BoneName = "leg_lower_l",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportMidAndDistal,
            LocalPoint0 = new Vector3(-1.36f, 4.70f, 21.03f),
            LocalPoint1 = new Vector3(-3.34f, 5.29f, 4.25f),
            Radius = 4.00f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "leg_lower_r",
            BoneName = "leg_lower_r",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportMidAndDistal,
            LocalPoint0 = new Vector3(-1.74f, -4.60f, 21.07f),
            LocalPoint1 = new Vector3(-3.34f, -5.29f, 4.25f),
            Radius = 4.00f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "arm_upper_l",
            BoneName = "arm_upper_l",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportMidAndDistal,
            LocalPoint0 = new Vector3(-3.34f, 7.18f, 61.66f),
            LocalPoint1 = new Vector3(-4.44f, 14.33f, 53.10f),
            Radius = 3.30f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "arm_upper_r",
            BoneName = "arm_upper_r",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportMidAndDistal,
            LocalPoint0 = new Vector3(-3.34f, -7.18f, 61.66f),
            LocalPoint1 = new Vector3(-4.44f, -14.33f, 53.10f),
            Radius = 3.30f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "arm_lower_l",
            BoneName = "arm_lower_l",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportMidAndDistal,
            LocalPoint0 = new Vector3(-4.50f, 14.70f, 52.65f),
            LocalPoint1 = new Vector3(-1.55f, 20.35f, 44.95f),
            Radius = 3.00f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "arm_lower_r",
            BoneName = "arm_lower_r",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportMidAndDistal,
            LocalPoint0 = new Vector3(-4.50f, -14.70f, 52.65f),
            LocalPoint1 = new Vector3(-1.63f, -19.97f, 44.64f),
            Radius = 3.00f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "hand_l",
            BoneName = "hand_l",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportAndEndpoints,
            LocalPoint0 = new Vector3(-1.28f, 20.72f, 43.95f),
            LocalPoint1 = new Vector3(-0.70f, 21.88f, 40.49f),
            Radius = 2.30f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "hand_r",
            BoneName = "hand_r",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportAndEndpoints,
            LocalPoint0 = new Vector3(-1.26f, -20.71f, 43.95f),
            LocalPoint1 = new Vector3(-0.72f, -21.78f, 40.61f),
            Radius = 2.30f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "leg_upper_l",
            BoneName = "leg_upper_l",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportMidAndDistal,
            LocalPoint0 = new Vector3(-1.73f, 3.80f, 37.76f),
            LocalPoint1 = new Vector3(-1.03f, 4.53f, 22.59f),
            Radius = 5.00f,
            DistalEndpointIsPoint1 = true
        },
        new()
        {
            Name = "leg_upper_r",
            BoneName = "leg_upper_r",
            Kind = VisibilityPrimitiveKind.Capsule,
            Sampling = VisibilityPrimitiveSampling.SupportMidAndDistal,
            LocalPoint0 = new Vector3(-2.08f, -4.32f, 37.78f),
            LocalPoint1 = new Vector3(-1.88f, -5.02f, 22.59f),
            Radius = 5.00f,
            DistalEndpointIsPoint1 = true
        }
    ];

    public static ReadOnlySpan<VisibilityPrimitive> Primitives => _primitives;
}
