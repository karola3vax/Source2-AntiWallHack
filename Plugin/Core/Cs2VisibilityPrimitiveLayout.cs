using System.Numerics;
using S2FOW.Models;

namespace S2FOW.Core;

/// <summary>
/// One body or weapon spot S2FOW can check for visibility.
///
/// Each entry is a specific spot on the enemy being checked, such as the
/// head, shoulder, knee, foot, or weapon muzzle.
/// LocalPoint uses coordinates relative to the player model. At runtime,
/// S2FOW moves that point to the enemy's real position and facing direction.
/// </summary>
internal readonly struct VisibilityPrimitive
{
    /// <summary>The point's position relative to the center of the player model.</summary>
    public required Vector3 LocalPoint { get; init; }

    /// <summary>
    /// If true, S2FOW checks this point from the viewer's current eye position
    /// instead of the movement-predicted eye position. This keeps head checks stable.
    /// </summary>
    public bool UseFixedHeadOrigin { get; init; }

    /// <summary>
    /// If set, this point is used only when the enemy is holding that weapon type.
    /// WeaponLosClass.None means the point is always checked.
    /// </summary>
    public WeaponLosClass RequiredWeaponClass { get; init; }
}

/// <summary>
/// The body and weapon points S2FOW checks when deciding if an enemy is visible.
///
/// These 35 points come from the actual CS2 player model data. They cover the
/// head, neck, shoulders, spine, hips, knees, feet, elbows, and weapon muzzle
/// positions for each weapon class.
///
/// S2FOW can also check 8 backup box corners around the enemy. Those backup points
/// help catch edge cases where only a small visible sliver of the model can be seen.
///
/// Together, the 35 model points plus 8 backup box points give broad body coverage
/// while keeping the amount of per-frame work predictable.
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
