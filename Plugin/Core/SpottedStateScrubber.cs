using System;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using S2FOW.Config;
using S2FOW.Models;

namespace S2FOW.Core;

/// <summary>
/// Scrubs the m_entitySpottedState.m_bSpottedByMask networked field on player pawns
/// to prevent radar/minimap ESP from revealing hidden players.
///
/// Even though S2FOW hides pawn entities from transmission, the spotted mask can leak
/// information through grace period frames, cache reuse windows, or client-side caching.
/// This scrubber proactively clears spotted bits for observer-target pairs where the
/// target is hidden from the observer.
/// </summary>
public class SpottedStateScrubber
{
    private readonly S2FOWConfig _config;

    // Pawns touched this frame so state changes can be flushed in one pass.
    private readonly bool[] _pawnDirty = new bool[FowConstants.MaxSlots];

    // Reused pawn lookup array to avoid per-frame GC allocation.
    private readonly CCSPlayerPawn?[] _pawnsBySlot = new CCSPlayerPawn?[FowConstants.MaxSlots];

    // Tracks the planted C4 entity for bomb spotted-state scrubbing.
    private int _plantedC4EntityIndex;
    private bool _hasPlantedC4;
    private long _suppressedPlantedC4StateErrors;
    private long _suppressedPawnStateErrors;
    private long _suppressedBombBitErrors;

    public long SuppressedPlantedC4StateErrors => _suppressedPlantedC4StateErrors;
    public long SuppressedPawnStateErrors => _suppressedPawnStateErrors;
    public long SuppressedBombBitErrors => _suppressedBombBitErrors;

    public SpottedStateScrubber(S2FOWConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Scrubs spotted bits for all hidden observer-target pairs.
    /// Call after visibility decisions are made in CheckTransmit, before ForceFlexStateResync.
    /// </summary>
    /// <param name="players">Active player controllers</param>
    /// <param name="snapshots">Player snapshots for this frame</param>
    /// <param name="terroristSlots">Active terrorist player slots</param>
    /// <param name="counterTerroristSlots">Active CT player slots</param>
    /// <param name="isHiddenFromObserver">
    /// Callback that returns true if targetSlot is hidden from observerSlot this frame.
    /// </param>
    public void ScrubPlayerSpottedState(
        List<CCSPlayerController> players,
        ReadOnlySpan<PlayerSnapshot> snapshots,
        ReadOnlySpan<int> terroristSlots,
        ReadOnlySpan<int> counterTerroristSlots,
        Func<int, int, bool> isHiddenFromObserver)
    {
        if (!_config.AntiWallhack.BlockRadarESP)
            return;

        Array.Clear(_pawnDirty);
        Array.Clear(_pawnsBySlot);
        for (int p = 0; p < players.Count; p++)
        {
            var controller = players[p];
            int slot = controller.Slot;
            if (!FowConstants.IsValidSlot(slot))
                continue;

            var pawn = controller.PlayerPawn.Value;
            if (pawn != null && pawn.IsValid)
                _pawnsBySlot[slot] = pawn;
        }

        // Walk each observer against the enemy team.
        for (int p = 0; p < players.Count; p++)
        {
            var controller = players[p];
            int observerSlot = controller.Slot;
            if (!FowConstants.IsValidSlot(observerSlot))
                continue;

            ref readonly var observer = ref snapshots[observerSlot];
            if (!observer.IsValid || !observer.IsAlive)
                continue;

            if (observer.Team != CsTeam.Terrorist && observer.Team != CsTeam.CounterTerrorist)
                continue;

            if (observer.IsBot)
                continue;

            // Use the player slot (0-63) as the bit index in the spotted mask.
            int controllerIndex = observerSlot;
            if (controllerIndex < 0 || controllerIndex >= 64)
                continue;

            ReadOnlySpan<int> enemySlots = observer.Team == CsTeam.Terrorist
                ? counterTerroristSlots
                : terroristSlots;

            for (int i = 0; i < enemySlots.Length; i++)
            {
                int targetSlot = enemySlots[i];
                ref readonly var target = ref snapshots[targetSlot];

                if (!target.IsValid || !target.IsAlive || targetSlot == observerSlot)
                    continue;

                // Skip targets that are not hidden from this observer.
                if (!isHiddenFromObserver(observerSlot, targetSlot))
                    continue;

                // Clear the observer bit on the target pawn.
                ClearSpottedBitForTarget(targetSlot, controllerIndex);
            }
        }

        // Flush state changes for every modified pawn.
        FlushDirtyPawns(players);
    }

    /// <summary>
    /// Registers a planted C4 entity for spotted state scrubbing.
    /// </summary>
    public void OnC4Planted(int entityIndex)
    {
        _plantedC4EntityIndex = entityIndex;
        _hasPlantedC4 = true;
    }

    /// <summary>
    /// Clears planted C4 tracking (defused, exploded, or round end).
    /// </summary>
    public void OnC4Removed()
    {
        _hasPlantedC4 = false;
        _plantedC4EntityIndex = 0;
    }

    /// <summary>
    /// Scrubs spotted state on the planted C4 for CTs who cannot see it.
    /// Only active in Strict security profile or when explicitly enabled.
    /// </summary>
    public void BlockBombRadarESP(
        List<CCSPlayerController> players,
        ReadOnlySpan<PlayerSnapshot> snapshots,
        Func<int, CPlantedC4, bool> canCtSeeC4)
    {
        if (!_hasPlantedC4 || _plantedC4EntityIndex <= 0)
            return;

        try
        {
            var c4Entity = Utilities.GetEntityFromIndex<CPlantedC4>(_plantedC4EntityIndex);
            if (c4Entity == null || !c4Entity.IsValid)
            {
                _hasPlantedC4 = false;
                return;
            }

            for (int p = 0; p < players.Count; p++)
            {
                var controller = players[p];
                int slot = controller.Slot;
                if (!FowConstants.IsValidSlot(slot))
                    continue;

                ref readonly var snap = ref snapshots[slot];
                if (!snap.IsValid || !snap.IsAlive || snap.Team != CsTeam.CounterTerrorist)
                    continue;

                if (snap.IsBot)
                    continue;

                int controllerIndex = slot;
                if (controllerIndex < 0 || controllerIndex >= 64)
                    continue;

                if (!canCtSeeC4(slot, c4Entity))
                {
                    // Clear this CT's spotted bit on the planted bomb.
                    ClearEntitySpottedBit(c4Entity, controllerIndex);
                }
            }
        }
        catch
        {
            _suppressedPlantedC4StateErrors++;
            // If the planted bomb can no longer be read, stop tracking it.
            _hasPlantedC4 = false;
        }
    }

    public void Clear()
    {
        Array.Clear(_pawnDirty);
        _hasPlantedC4 = false;
        _plantedC4EntityIndex = 0;
    }

    private void ClearSpottedBitForTarget(int targetSlot, int observerControllerIndex)
    {
        var pawn = FowConstants.IsValidSlot(targetSlot) ? _pawnsBySlot[targetSlot] : null;
        if (pawn == null || !pawn.IsValid)
            return;

        try
        {
            var spottedState = pawn.EntitySpottedState;
            if (spottedState == null)
                return;

            // m_bSpottedByMask stores 64 observer bits across two UInt32 values.
            var mask = spottedState.SpottedByMask;
            if (mask.Length < 2)
                return;

            int arrayIndex = observerControllerIndex / 32;
            int bitIndex = observerControllerIndex % 32;
            uint currentMask = mask[arrayIndex];
            uint clearedMask = currentMask & ~(1u << bitIndex);

            if (currentMask != clearedMask)
            {
                mask[arrayIndex] = clearedMask;
                _pawnDirty[targetSlot] = true;

                // If nobody spots this target anymore, also clear the overall spotted flag.
                if (mask[0] == 0 && mask[1] == 0)
                {
                    spottedState.Spotted = false;
                }
            }
        }
        catch
        {
            _suppressedPawnStateErrors++;
            // Ignore transient entity-state failures here.
        }
    }

    private void ClearEntitySpottedBit(CPlantedC4 entity, int controllerIndex)
    {
        try
        {
            var spottedState = entity.EntitySpottedState;
            if (spottedState == null)
                return;

            var mask = spottedState.SpottedByMask;
            if (mask.Length < 2)
                return;

            int arrayIndex = controllerIndex / 32;
            int bitIndex = controllerIndex % 32;
            uint currentMask = mask[arrayIndex];
            uint clearedMask = currentMask & ~(1u << bitIndex);

            if (currentMask != clearedMask)
            {
                mask[arrayIndex] = clearedMask;
                Utilities.SetStateChanged(entity, "CPlantedC4", "m_entitySpottedState");

                if (mask[0] == 0 && mask[1] == 0)
                {
                    spottedState.Spotted = false;
                }
            }
        }
        catch
        {
            _suppressedBombBitErrors++;
            // Ignore transient entity-state failures here.
        }
    }

    private void FlushDirtyPawns(List<CCSPlayerController> players)
    {
        for (int p = 0; p < players.Count; p++)
        {
            var controller = players[p];
            int slot = controller.Slot;
            if (!FowConstants.IsValidSlot(slot) || !_pawnDirty[slot])
                continue;

            var pawn = controller.PlayerPawn.Value;
            if (pawn != null && pawn.IsValid)
            {
                Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_entitySpottedState");
            }
        }
    }
}
