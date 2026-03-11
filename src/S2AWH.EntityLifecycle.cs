using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;

namespace S2AWH;

public partial class S2AWH
{
    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
        {
            int slot = player.Slot;
            if ((uint)slot < VisibilitySlotCapacity)
            {
                int stabilizeUntilTick = Server.TickCount + SnapshotStabilizeGraceTicks;
                if (_snapshotStabilizeUntilTickBySlot[slot] < stabilizeUntilTick)
                {
                    _snapshotStabilizeUntilTickBySlot[slot] = stabilizeUntilTick;
                }
            }

            TrackKnownLivePlayerEntities(player, scheduleRescan: true);
        }

        return HookResult.Continue;
    }

    private void OnClientDisconnect(int playerSlot)
    {
        if ((uint)playerSlot < VisibilitySlotCapacity)
        {
            _visibilityCache[playerSlot] = null;
            _revealHoldRows[playerSlot] = null;
            _stableDecisionRows[playerSlot] = null;
            _visibleConfirmRows[playerSlot] = null;
            _targetTransmitEntitiesCache[playerSlot] = null;
            SnapshotTransforms[playerSlot] = default;
            SnapshotPawns[playerSlot] = null;
            _liveSlotFlags[playerSlot] = false;
            _snapshotStabilizeUntilTickBySlot[playerSlot] = 0;
            ClearViewerRayCountSlotState(playerSlot);
            RemoveViewerRayCountOverlay(playerSlot);

            for (int i = 0; i < VisibilitySlotCapacity; i++)
            {
                var viewerVisibility = _visibilityCache[i];
                if (viewerVisibility != null)
                {
                    viewerVisibility.Known[playerSlot] = false;
                    viewerVisibility.Decisions[playerSlot] = false;
                    viewerVisibility.PawnHandles[playerSlot] = 0;
                    viewerVisibility.EvalTicks[playerSlot] = 0;
                }

                var revealHold = _revealHoldRows[i];
                if (revealHold != null && revealHold.Known[playerSlot])
                {
                    revealHold.Known[playerSlot] = false;
                    revealHold.HoldUntilTick[playerSlot] = 0;
                    revealHold.ActiveCount--;
                    if (revealHold.ActiveCount <= 0) _revealHoldRows[i] = null;
                }

                var stableDecision = _stableDecisionRows[i];
                if (stableDecision != null && stableDecision.Known[playerSlot])
                {
                    stableDecision.Known[playerSlot] = false;
                    stableDecision.Decisions[playerSlot] = false;
                    stableDecision.Ticks[playerSlot] = 0;
                    stableDecision.ActiveCount--;
                    if (stableDecision.ActiveCount <= 0) _stableDecisionRows[i] = null;
                }

                var visibleConfirm = _visibleConfirmRows[i];
                if (visibleConfirm != null && visibleConfirm.Known[playerSlot])
                {
                    visibleConfirm.Known[playerSlot] = false;
                    visibleConfirm.FirstVisibleTick[playerSlot] = 0;
                    visibleConfirm.ActiveCount--;
                    if (visibleConfirm.ActiveCount <= 0) _visibleConfirmRows[i] = null;
                }
            }
        }

        _predictor?.InvalidateTargetSlot(playerSlot);

        InvalidateLivePlayersCache();

        DebugLog(
            "Player left the server.",
            $"Slot {playerSlot} was freed and old data for this player was removed.",
            "No stale data remains."
        );
    }

    private void OnEntityCreated(CEntityInstance entity)
    {
        TrackKnownEntityHandle(entity);
        MarkOwnedEntityDirty(entity, scheduleRescan: true);
    }

    private void OnEntitySpawned(CEntityInstance entity)
    {
        MarkOwnedEntityDirty(entity, scheduleRescan: true);
    }

    private void OnEntityDeleted(CEntityInstance entity)
    {
        if (!TryGetTrackedEntityHandleRaw(entity, out uint entityHandleRaw))
        {
            return;
        }

        UntrackKnownEntityHandle(entityHandleRaw);
        RemoveOwnedEntityRelationsForHandle(entityHandleRaw);
        _dirtyOwnedEntityHandles.Remove(entityHandleRaw);
        _pendingOwnedEntityRescanUntilTick.Remove(entityHandleRaw);
        _ownedEntityBucketsTick = -1;
    }

    private void OnEntityParentChanged(CEntityInstance entity, CEntityInstance newParent)
    {
        MarkOwnedEntityDirty(entity, scheduleRescan: true);
    }

    private void PrimeKnownLivePlayerHandles()
    {
        for (int slot = 0; slot < VisibilitySlotCapacity; slot++)
        {
            CCSPlayerController? player = Utilities.GetPlayerFromSlot(slot);
            if (!IsLivePlayer(player))
            {
                continue;
            }

            TrackKnownLivePlayerEntities(player!, scheduleRescan: false);
        }
    }

    private void TrackKnownLivePlayerEntities(CCSPlayerController player, bool scheduleRescan)
    {
        if (!player.IsValid)
        {
            return;
        }

        TrackKnownEntityHandle(player);
        MarkOwnedEntityDirty(player, scheduleRescan);

        CBasePlayerPawn? pawn;
        try
        {
            pawn = player.PlayerPawn.Value ?? player.Pawn.Value;
        }
        catch
        {
            return;
        }

        if (pawn == null || !pawn.IsValid)
        {
            return;
        }

        TrackKnownEntityHandle(pawn);
        MarkOwnedEntityDirty(pawn, scheduleRescan);
    }
}
