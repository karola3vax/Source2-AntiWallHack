namespace S2FOW.Core;

/// <summary>
/// Global constants for the S2FOW plugin.
///
/// These values represent hard limits from the Source 2 engine that the plugin
/// must respect. Using values outside these ranges could cause crashes or
/// undefined behavior.
/// </summary>
public static class FowConstants
{
    /// <summary>
    /// Maximum number of player slots supported by the engine.
    /// CS2 supports up to 64 players in a server. Player slots are numbered 0–63.
    /// </summary>
    public const int MaxSlots = 64;

    /// <summary>
    /// Maximum valid entity index in the Source 2 engine.
    /// Every object in the game world (players, weapons, doors, etc.) has a unique
    /// index. Indices at or above this value are invalid and must never be used
    /// with TransmitEntities.Remove() — doing so could corrupt the transmit list.
    /// </summary>
    public const int MaxEntityIndex = 16384;

    /// <summary>
    /// Checks if a player slot number is within the valid range (0 to 63).
    /// Invalid slots can come from stale entity references or spectators.
    /// </summary>
    public static bool IsValidSlot(int slot)
    {
        return (uint)slot < MaxSlots;
    }

    /// <summary>
    /// Checks if an entity index is safe to use for transmission removal.
    /// Index 0 is the world entity (must never be removed).
    /// Negative indices and those >= 16384 are stale handles or engine-reserved.
    /// </summary>
    public static bool IsValidEntityIndex(int entityIndex)
    {
        return entityIndex > 0 && entityIndex < MaxEntityIndex;
    }
}
