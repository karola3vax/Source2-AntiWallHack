using System.Numerics;
using S2FOW.Models;

namespace S2FOW.Core;

/// <summary>
/// One visibility check point on the player model.
///
/// Each primitive represents a specific spot on the CS2 player skeleton
/// (e.g., top of head, left shoulder, right knee, weapon muzzle).
/// The LocalPoint is in model-local coordinates — it gets transformed to
/// world-space based on the player's position and facing direction.
/// </summary>
internal readonly struct VisibilityPrimitive
{
    /// <summary>The position of this point in the player's local coordinate space.</summary>
    public required Vector3 LocalPoint { get; init; }

    /// <summary>
    /// If true, rays to this point originate from the observer's non-predicted eye
    /// position (more stable for head-level checks).
    /// </summary>
    public bool UseFixedHeadOrigin { get; init; }

    /// <summary>
    /// If set to a specific weapon class, this point only exists when the target
    /// is holding that type of weapon (e.g., sniper rifle muzzle extends further).
    /// WeaponLosClass.None means this point applies to all weapons.
    /// </summary>
    public WeaponLosClass RequiredWeaponClass { get; init; }
}

/// <summary>
/// The canonical set of visibility check points extracted from CS2's player model hitboxes.
///
/// These 35 skeleton points were extracted from the actual CS2 player model using the
/// tools in the Tools/ directory. They cover the full body: head, neck, shoulders, spine,
/// hips, knees, feet, elbows, and weapon muzzle positions for each weapon class.
///
/// Additionally, 8 AABB (axis-aligned bounding box) corner points serve as a fallback
/// safety net — if all 35 skeleton rays hit walls, the 8 corners are checked to catch
/// extreme edge cases (e.g., only a sliver of the player model is visible).
///
/// Together these 43 points per target provide comprehensive body coverage while keeping
/// the raycast count manageable for real-time performance.
/// </summary>
internal static class Cs2VisibilityPrimitiveLayout
{
    public const int PrimitiveCount = 35;
    public const int AabbPointCount = 8;
    public const int MaxVisibilityTestPoints = PrimitiveCount + AabbPointCount;

    private static readonly VisibilityPrimitive[] _primitives =
    [
        new()
        {
            LocalPoint = new Vector3(-6.992f, -2.912f, 64.51f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(-6.842f, -3.302f, 57.078f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(-5.178f, -2.45f, 54.506f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(-3.112f, -0.79f, 50.789f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(0.214f, 1.964f, 47.735f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(1.239f, 2.516f, 42.095f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(1.368f, 2.574f, 37.481f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(-13.748f, 8.339f, 1.183f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(11.336f, -4.611f, 1.186f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(-12.706f, 6.395f, 13.386f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(6.25f, -7.008f, 10.219f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(-9.662f, 5.599f, 58.888f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(-2.229f, -5.48f, 58.269f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(-14.146f, 5.486f, 56.11f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(1.17f, -7.81f, 55.162f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(-21.768f, -2.639f, 54.429f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(-7.527f, -7.862f, 53.768f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(-3.829f, 5.905f, 29.617f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(4.136f, -3.275f, 28.942f),
            UseFixedHeadOrigin = true
        },
        new()
        {
            LocalPoint = new Vector3(-35.0f, -3.695f, 60.443f),
            RequiredWeaponClass = WeaponLosClass.Pistol
        },
        new()
        {
            LocalPoint = new Vector3(-56.0f, -3.604f, 61.383f),
            RequiredWeaponClass = WeaponLosClass.Sniper
        },
        new()
        {
            LocalPoint = new Vector3(-46.0f, -3.665f, 61.443f),
            RequiredWeaponClass = WeaponLosClass.Rifle
        },
        new()
        {
            LocalPoint = new Vector3(1.239f, -6.4f, 42.095f)
        },
        new()
        {
            LocalPoint = new Vector3(1.239f, 13.62f, 42.095f)
        },
        new()
        {
            LocalPoint = new Vector3(1.368f, 12.67f, 37.481f)
        },
        new()
        {
            LocalPoint = new Vector3(1.367f, -4.843f, 37.481f)
        },
        new()
        {
            LocalPoint = new Vector3(0.214f, -5.355f, 47.735f)
        },
        new()
        {
            LocalPoint = new Vector3(0.214f, 13.381f, 47.735f)
        },
        new()
        {
            LocalPoint = new Vector3(-3.112f, 11.901f, 50.789f)
        },
        new()
        {
            LocalPoint = new Vector3(-3.112f, -5.552f, 50.789f)
        },
        new()
        {
            LocalPoint = new Vector3(-5.178f, 10.984f, 54.506f)
        },
        new()
        {
            LocalPoint = new Vector3(-5.178f, -7.886f, 54.506f)
        },
        new()
        {
            LocalPoint = new Vector3(-1.543f, 10.403f, 72.019f)
        },
        new()
        {
            LocalPoint = new Vector3(8.677f, 10.403f, 56.313f)
        },
        new()
        {
            LocalPoint = new Vector3(11.08f, 12.403f, 44.349f)
        }
    ];

    public static ReadOnlySpan<VisibilityPrimitive> Primitives => _primitives;
}
