namespace S2FOW.Core;

public class VisibilityCache
{
    private struct CacheEntry
    {
        public int Tick;
        public bool IsVisible;
        public bool IsValid;
        // Position at evaluation time for velocity-aware cache extension.
        public float ObsPosX, ObsPosY;
        public float TgtPosX, TgtPosY;
    }

    private readonly CacheEntry[] _cache = new CacheEntry[FowConstants.MaxSlots * FowConstants.MaxSlots];

    private bool IsValidSlot(int observerSlot, int targetSlot)
    {
        return (uint)observerSlot < FowConstants.MaxSlots && (uint)targetSlot < FowConstants.MaxSlots;
    }

    /// <summary>
    /// Reads a cached visibility result if it is still within its time-to-live window.
    /// Accepts a stagger offset that extends the TTL for this specific pair.
    /// </summary>
    public bool TryGet(int observerSlot, int targetSlot, int currentTick, int visibleTTL, int hiddenTTL,
        int staggerOffset,
        out bool isVisible, out int cachedTick)
    {
        if (!IsValidSlot(observerSlot, targetSlot))
        {
            isVisible = false;
            cachedTick = 0;
            return false;
        }

        ref var entry = ref _cache[observerSlot * FowConstants.MaxSlots + targetSlot];
        if (!entry.IsValid)
        {
            isVisible = false;
            cachedTick = 0;
            return false;
        }

        int ttl = (entry.IsVisible ? visibleTTL : hiddenTTL) + staggerOffset;
        if (currentTick - entry.Tick >= ttl)
        {
            isVisible = entry.IsVisible;
            cachedTick = entry.Tick;
            return false;
        }

        isVisible = entry.IsVisible;
        cachedTick = entry.Tick;
        return true;
    }

    /// <summary>
    /// Returns the raw cache state without applying TTL rules.
    /// This is used by grace-period logic and other fallback checks.
    /// </summary>
    public bool TryGetRaw(int observerSlot, int targetSlot, out bool wasVisible, out int cachedTick)
    {
        if (!IsValidSlot(observerSlot, targetSlot))
        {
            wasVisible = false;
            cachedTick = 0;
            return false;
        }

        ref var entry = ref _cache[observerSlot * FowConstants.MaxSlots + targetSlot];
        if (!entry.IsValid)
        {
            wasVisible = false;
            cachedTick = 0;
            return false;
        }
        wasVisible = entry.IsVisible;
        cachedTick = entry.Tick;
        return true;
    }

    /// <summary>
    /// Stores the latest visibility decision for an observer-target pair,
    /// including observer and target positions for velocity-aware cache extension.
    /// </summary>
    public void Set(int observerSlot, int targetSlot, bool isVisible, int tick,
        float obsPosX, float obsPosY, float tgtPosX, float tgtPosY)
    {
        if (!IsValidSlot(observerSlot, targetSlot))
            return;

        ref var entry = ref _cache[observerSlot * FowConstants.MaxSlots + targetSlot];
        entry.Tick = tick;
        entry.IsVisible = isVisible;
        entry.IsValid = true;
        entry.ObsPosX = obsPosX;
        entry.ObsPosY = obsPosY;
        entry.TgtPosX = tgtPosX;
        entry.TgtPosY = tgtPosY;
    }

    /// <summary>
    /// Stores visibility without position tracking (used by smoke blocking
    /// and other early-out paths where positions are not yet relevant).
    /// </summary>
    public void SetSimple(int observerSlot, int targetSlot, bool isVisible, int tick)
    {
        if (!IsValidSlot(observerSlot, targetSlot))
            return;

        ref var entry = ref _cache[observerSlot * FowConstants.MaxSlots + targetSlot];
        entry.Tick = tick;
        entry.IsVisible = isVisible;
        entry.IsValid = true;
    }

    /// <summary>
    /// Computes how far the observer and target have each moved (2D, squared)
    /// since the last time this pair was evaluated. Returns false if there
    /// is no prior evaluation to compare against.
    /// </summary>
    public bool TryGetMovementSinceLastEval(int observerSlot, int targetSlot,
        float currentObsX, float currentObsY,
        float currentTgtX, float currentTgtY,
        out float obsMoveSqr, out float tgtMoveSqr)
    {
        obsMoveSqr = 0f;
        tgtMoveSqr = 0f;

        if (!IsValidSlot(observerSlot, targetSlot))
            return false;

        ref var entry = ref _cache[observerSlot * FowConstants.MaxSlots + targetSlot];
        if (!entry.IsValid)
            return false;

        float odx = currentObsX - entry.ObsPosX;
        float ody = currentObsY - entry.ObsPosY;
        obsMoveSqr = odx * odx + ody * ody;

        float tdx = currentTgtX - entry.TgtPosX;
        float tdy = currentTgtY - entry.TgtPosY;
        tgtMoveSqr = tdx * tdx + tdy * tdy;
        return true;
    }

    /// <summary>
    /// Clears the full visibility cache.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_cache);
    }

    /// <summary>
    /// Clears all cache entries that involve the given player slot.
    /// </summary>
    public void ClearForPlayer(int slot)
    {
        if ((uint)slot >= FowConstants.MaxSlots)
            return;

        for (int i = 0; i < FowConstants.MaxSlots; i++)
        {
            _cache[slot * FowConstants.MaxSlots + i] = default;
            _cache[i * FowConstants.MaxSlots + slot] = default;
        }
    }
}
