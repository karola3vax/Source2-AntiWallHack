using CounterStrikeSharp.API.Modules.Utils;

namespace S2FOW.Models;

/// <summary>
/// Describes what type of weapon a player is holding, so S2FOW can choose the
/// right weapon-tip body point.
///
/// Different weapons have different visual profiles — a player holding a
/// sniper rifle sticks out further than one holding a pistol, so we use
/// different check points for each weapon class.
/// </summary>
public enum WeaponLosClass
{
    /// <summary>No weapon or unrecognized weapon — use the default body points only.</summary>
    None = 0,

    /// <summary>Pistol — compact weapon, uses pistol-specific muzzle point.</summary>
    Pistol = 1,

    /// <summary>Rifle — medium-sized weapon, uses rifle-specific muzzle point.</summary>
    Rifle = 2,

    /// <summary>Sniper — long weapon (AWP, Scout, etc.), uses sniper-specific muzzle point.</summary>
    Sniper = 3
}

/// <summary>
/// A one-frame read of a single player's state.
///
/// Every network frame, S2FOW captures each player's position, speed, team,
/// weapon, body size, and connected-object status into this struct. Visibility
/// checks then use this fixed picture for the rest of the frame.
///
/// Using a struct (value type) instead of a class means no garbage collection
/// pressure in the hot path, which is critical for performance at 64 frames/second.
/// </summary>
public struct PlayerSnapshot
{
    /// <summary>The player's slot number (0–63). Unique identifier for this player on the server.</summary>
    public int Slot;

    /// <summary>The engine object index of the player's in-world body. 0 means no body.</summary>
    public uint PawnEntityIndex;

    /// <summary>True if the player is currently alive.</summary>
    public bool IsAlive;

    /// <summary>Which team the player is on (Terrorist, CounterTerrorist, Spectator, or None).</summary>
    public CsTeam Team;

    /// <summary>True if this player is a bot (AI-controlled).</summary>
    public bool IsBot;

    /// <summary>The world position of the player's eyes (where they look from). X/Y are horizontal, Z is vertical.</summary>
    public float EyePosX, EyePosY, EyePosZ;

    /// <summary>How far above the player's feet their eyes are (changes when crouching).</summary>
    public float ViewOffsetZ;

    /// <summary>The player's view pitch angle in degrees (-90 = looking straight up, +90 = looking straight down).</summary>
    public float Pitch;

    /// <summary>The player's view yaw angle in degrees (0 = East, 90 = North, etc.).</summary>
    public float Yaw;

    /// <summary>The world position of the player's feet (base position). X/Y are horizontal, Z is vertical.</summary>
    public float PosX, PosY, PosZ;

    /// <summary>The player's velocity (speed and direction). VelZ is vertical (positive = moving up).</summary>
    public float VelX, VelY, VelZ;

    /// <summary>The maximum movement speed allowed by the player's current weapon (e.g., knife = 250, AWP = 200).</summary>
    public float WeaponMaxSpeed;

    /// <summary>What class of weapon the player is currently holding (None, Pistol, Rifle, Sniper).</summary>
    public WeaponLosClass ActiveWeaponLosClass;

    /// <summary>True if the player is standing on the ground (not jumping or falling).</summary>
    public bool IsOnGround;

    /// <summary>The minimum corner of the player's collision box (bounding box), relative to their position.</summary>
    public float MinsX, MinsY, MinsZ;

    /// <summary>The maximum corner of the player's collision box (bounding box), relative to their position.</summary>
    public float MaxsX, MaxsY, MaxsZ;

    /// <summary>True if this snapshot contains valid data (the player exists and is connected).</summary>
    public bool IsValid;

    /// <summary>
    /// How many connected object indexes are recorded for this player. These are
    /// all the objects that should be hidden together with the player body.
    /// </summary>
    public int AssociatedEntityCount;

    /// <summary>
    /// True when S2FOW could not read every connected object for this player.
    /// A live controlled player with this set must stay visible.
    /// </summary>
    public bool DependentEntityCollectionFailed;

    /// <summary>
    /// True when the connected-object list filled before all known objects were recorded.
    /// A live controlled player with this set must stay visible.
    /// </summary>
    public bool AssociatedEntityCapExceeded;

    /// <summary>True when the player body still has a valid engine controller.</summary>
    public bool HasValidPawnController;

    /// <summary>How many attached scene objects were added to the connected-object list.</summary>
    public int SceneChildEntityCount;

    /// <summary>True when S2FOW knows enough connected objects to hide this live player safely.</summary>
    public readonly bool CanHideControlledLivePawn =>
        HasValidPawnController &&
        AssociatedEntityCount > 0 &&
        !DependentEntityCollectionFailed &&
        !AssociatedEntityCapExceeded;

    /// <summary>
    /// Maximum number of connected objects tracked per player.
    /// Normal gameplay uses far fewer than 128, but this leaves room for attached
    /// scene objects and unusual equipment combinations.
    /// </summary>
    public const int MaxAssociatedEntities = 128;
}
