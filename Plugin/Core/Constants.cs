namespace S2FOW.Core;

public static class FowConstants
{
    public const int MaxSlots = 64;

    /// <summary>
    /// Source 2 engine maximum entity index. Entity indices above this value
    /// are invalid and must never be passed to TransmitEntities.Remove().
    /// </summary>
    public const int MaxEntityIndex = 16384;

    public static bool IsValidSlot(int slot)
    {
        return (uint)slot < MaxSlots;
    }

    /// <summary>
    /// Returns true if the entity index is within the safe transmission range.
    /// Indices &lt;= 0 or &gt;= MaxEntityIndex must never be removed from the
    /// transmit list, as they may refer to world/engine entities or stale handles.
    /// </summary>
    public static bool IsValidEntityIndex(int entityIndex)
    {
        return entityIndex > 0 && entityIndex < MaxEntityIndex;
    }
}
