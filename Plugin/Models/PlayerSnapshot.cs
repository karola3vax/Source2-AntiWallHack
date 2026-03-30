using CounterStrikeSharp.API.Modules.Utils;

namespace S2FOW.Models;

public struct PlayerSnapshot
{
    public int Slot;
    public int ControllerEntityIndex;
    public bool HasControllerEntity;
    public uint PawnEntityIndex;
    public bool IsAlive;
    public CsTeam Team;
    public bool IsBot;
    public float EyePosX, EyePosY, EyePosZ;
    public float ViewOffsetZ;
    public float Pitch;
    public float Yaw;
    public float PosX, PosY, PosZ;
    public float VelX, VelY, VelZ;
    public float WeaponMaxSpeed;
    public float DuckAmount;
    public bool IsOnGround;
    public bool IsScoped;
    public float MinsX, MinsY, MinsZ;
    public float MaxsX, MaxsY, MaxsZ;
    public bool IsValid;

    /// <summary>
    /// Number of entity indices associated with this player that should be hidden together.
    /// This includes the pawn, weapons, wearables, effect entities, owner-linked entities,
    /// and scene-parented child entities.
    /// </summary>
    public int AssociatedEntityCount;

    public const int MaxAssociatedEntities = 512;
}
