using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;

namespace S2AWH;

public partial class S2AWH
{
    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        var config = S2AWHState.Current;
        if (!config.Core.Enabled || _transmitFilter == null) return;

        int nowTick = Server.TickCount;
        BeginViewerRayCountTick(nowTick);
        if (IsRoundStartGraceActive(nowTick))
        {
            return;
        }

        if (_entityHandleIndexCacheTick != nowTick)
        {
            _entityHandleIndexCacheTick = nowTick;
            _entityHandleIndexCache.Clear();
        }
        if (!TryGetLivePlayers(nowTick, out var eligibleTargets))
        {
            return;
        }

        if (_collectDebugCounters)
        {
            _transmitCallbacksInWindow++;
        }

        if (_eligibleTargetsWithEntitiesTick != nowTick)
        {
            _eligibleTargetsWithEntities.Clear();
            int eligibleTargetCount = eligibleTargets.Count;
            for (int i = 0; i < eligibleTargetCount; i++)
            {
                var target = eligibleTargets[i];
                if (!config.Visibility.IncludeBots && target.IsBot)
                {
                    continue;
                }

                if (TryGetTargetTransmitEntities(target, nowTick, out var targetEntities))
                {
                    _eligibleTargetsWithEntities.Add((targetEntities, target.Slot, target.TeamNum));
                }
            }
            _eligibleTargetsWithEntitiesTick = nowTick;
        }

        bool skipTeammates = !config.Visibility.IncludeTeammates;

        int infoCount = infoList.Count;
        for (int infoIndex = 0; infoIndex < infoCount; infoIndex++)
        {
            (CCheckTransmitInfo info, CCSPlayerController? viewer) = infoList[infoIndex];
            if (viewer == null)
            {
                continue;
            }

            int viewerSlot = viewer.Slot;
            if ((uint)viewerSlot >= _liveSlotFlags.Length || !_liveSlotFlags[viewerSlot])
            {
                continue; // Dead/invalid viewers see everything
            }

            // If it's a bot and we don't calculate LOS for bots, don't block anything
            bool viewerIsBot = viewer.IsBot;
            if (viewerIsBot && !config.Visibility.BotsDoLOS) continue;

            int viewerTeam = viewer.TeamNum;
            ViewerVisibilityRow? targetVisibilityBySlot = _visibilityCache[viewerSlot];
            bool hasViewerCache = targetVisibilityBySlot != null;

            int targetEntryCount = _eligibleTargetsWithEntities.Count;
            for (int targetEntryIndex = 0; targetEntryIndex < targetEntryCount; targetEntryIndex++)
            {
                var targetEntry = _eligibleTargetsWithEntities[targetEntryIndex];
                int targetSlot = targetEntry.TargetSlot;
                if (targetSlot == viewerSlot)
                {
                    continue;
                }
                TargetTransmitEntities targetEntities = targetEntry.Entities;

                // Always-transmit fast paths.
                if (skipTeammates && targetEntry.TargetTeam == viewerTeam)
                {
                    continue;
                }

                bool shouldTransmit;
                if (hasViewerCache &&
                    targetVisibilityBySlot != null &&
                    (uint)targetSlot < (uint)targetVisibilityBySlot.Known.Length &&
                    targetVisibilityBySlot.Known[targetSlot] &&
                    targetVisibilityBySlot.PawnHandles[targetSlot] == targetEntities.PawnHandleRaw)
                {
                    shouldTransmit = targetVisibilityBySlot.Decisions[targetSlot];

                    // Staggered snapshot rebuild can leave a hidden decision one or more ticks old.
                    // Recheck only stale hidden pairs so newly exposed targets do not remain popped-out
                    // until the viewer's next scheduled cache batch.
                    if (!shouldTransmit && targetVisibilityBySlot.EvalTicks[targetSlot] != nowTick)
                    {
                        if (_collectDebugCounters)
                        {
                            _transmitFallbackChecksInWindow++;
                        }

                        VisibilityEval visibilityEval = EvaluateVisibilitySafe(
                            viewerSlot,
                            targetSlot,
                            viewerIsBot,
                            config,
                            nowTick,
                            "stale hidden recheck");
                        shouldTransmit = ResolveTransmitWithMemory(viewerSlot, targetSlot, visibilityEval, nowTick);
                        targetVisibilityBySlot.Decisions[targetSlot] = shouldTransmit;
                        targetVisibilityBySlot.EvalTicks[targetSlot] = nowTick;
                    }
                }
                else
                {
                    if (_collectDebugCounters)
                    {
                        _transmitFallbackChecksInWindow++;
                    }
                    VisibilityEval visibilityEval = EvaluateVisibilitySafe(
                        viewerSlot,
                        targetSlot,
                        viewerIsBot,
                        config,
                        nowTick,
                        "transmit fallback");
                    shouldTransmit = ResolveTransmitWithMemory(viewerSlot, targetSlot, visibilityEval, nowTick);

                    // Keep fallback decisions in the snapshot to avoid repeating work in the same tick window.
                    if ((uint)targetSlot < VisibilitySlotCapacity)
                    {
                        if (!hasViewerCache || targetVisibilityBySlot == null)
                        {
                            targetVisibilityBySlot = new ViewerVisibilityRow();
                            _visibilityCache[viewerSlot] = targetVisibilityBySlot;
                            hasViewerCache = true;
                        }

                        targetVisibilityBySlot.Decisions[targetSlot] = shouldTransmit;
                        targetVisibilityBySlot.Known[targetSlot] = true;
                        targetVisibilityBySlot.PawnHandles[targetSlot] = targetEntities.PawnHandleRaw;
                        targetVisibilityBySlot.EvalTicks[targetSlot] = nowTick;
                    }
                }

                if (shouldTransmit)
                {
                    // Visible decisions are fail-open: do not force-add entities.
                    // This avoids overriding removals made by other plugins in the same callback.
                    continue;
                }

                bool removedAny = RemoveTargetPlayerAndWeapons(info, targetEntities);
                if (_collectDebugCounters)
                {
                    if (removedAny)
                    {
                        _transmitHiddenEntitiesInWindow++;
                    }
                    else
                    {
                        _transmitRemovalNoEffectInWindow++;
                    }
                }
            }
        }

        UpdateViewerRayCountOverlays();
    }

    private bool RemoveTargetPlayerAndWeapons(CCheckTransmitInfo info, TargetTransmitEntities targetEntities)
    {
        int entityCount = targetEntities.Count;
        if (entityCount <= 0)
        {
            return false;
        }

        if (!_collectDebugCounters)
        {
            bool removed = false;
            for (int i = 0; i < entityCount; i++)
            {
                uint entityHandleRaw = targetEntities.RawHandles[i];
                if (!TryResolveEntityHandleIndexForTransmit(entityHandleRaw, out int entityIndex))
                {
                    continue;
                }

                info.TransmitEntities.Remove(entityIndex);
                removed = true;
            }
            return removed;
        }

        bool removedAny = false;
        for (int i = 0; i < entityCount; i++)
        {
            uint entityHandleRaw = targetEntities.RawHandles[i];
            if (!TryResolveEntityHandleIndexForTransmit(entityHandleRaw, out int entityIndex))
            {
                continue;
            }

            if (info.TransmitEntities.Contains(entityIndex))
            {
                info.TransmitEntities.Remove(entityIndex);
                removedAny = true;
            }

        }

        return removedAny;
    }

    private bool TryGetTargetTransmitEntities(CCSPlayerController target, int nowTick, out TargetTransmitEntities targetEntities)
    {
        targetEntities = null!;
        int targetSlot = target.Slot;
        if ((uint)targetSlot >= VisibilitySlotCapacity)
        {
            return false;
        }

        // Prefer per-tick snapshot pawn to avoid repeated interop reads in tight transmit loops.
        var targetPawnEntity = SnapshotPawns[targetSlot];
        if (targetPawnEntity == null || !targetPawnEntity.IsValid)
        {
            targetPawnEntity = target.PlayerPawn.Value ?? target.Pawn.Value;
        }

        if (targetPawnEntity == null || !targetPawnEntity.IsValid)
        {
            return false;
        }

        uint pawnHandleRaw = targetPawnEntity.EntityHandle.Raw;

        TargetTransmitEntities? cachedEntities = _targetTransmitEntitiesCache[targetSlot];
        if (cachedEntities == null)
        {
            cachedEntities = new TargetTransmitEntities();
            _targetTransmitEntitiesCache[targetSlot] = cachedEntities;
        }
        targetEntities = cachedEntities;

        int targetPawnIndex = (int)targetPawnEntity.Index;
        int targetControllerIndex = (int)target.Index;
        bool mustRefreshFullList = targetEntities.PawnHandleRaw != pawnHandleRaw || targetEntities.LastFullRefreshTick != nowTick;

        if (mustRefreshFullList)
        {
            targetEntities.LastFullRefreshTick = nowTick;
            targetEntities.PawnHandleRaw = pawnHandleRaw;
            targetEntities.Count = 0;
            AddUniqueEntityHandle(targetEntities, pawnHandleRaw);

            try
            {
                var weaponServices = targetPawnEntity.WeaponServices;
                if (weaponServices == null)
                {
                    SanitizeTargetEntityList(targetEntities);
                    targetEntities.SanitizeTick = nowTick;
                    return true;
                }

                var activeWeapon = weaponServices.ActiveWeapon;
                if (TryResolveLiveWeaponEntityHandle(activeWeapon, targetPawnIndex, targetControllerIndex, out uint activeWeaponHandleRaw))
                {
                    AddUniqueEntityHandle(targetEntities, activeWeaponHandleRaw);
                }

                var lastWeapon = weaponServices.LastWeapon;
                if (TryResolveLiveWeaponEntityHandle(lastWeapon, targetPawnIndex, targetControllerIndex, out uint lastWeaponHandleRaw))
                {
                    AddUniqueEntityHandle(targetEntities, lastWeaponHandleRaw);
                }

                var csWeaponServices = weaponServices.As<CCSPlayer_WeaponServices>();
                if (csWeaponServices != null)
                {
                    var savedWeapon = csWeaponServices.SavedWeapon;
                    if (TryResolveLiveWeaponEntityHandle(savedWeapon, targetPawnIndex, targetControllerIndex, out uint savedWeaponHandleRaw))
                    {
                        AddUniqueEntityHandle(targetEntities, savedWeaponHandleRaw);
                    }
                }

                var myWeapons = weaponServices.MyWeapons;
                int myWeaponCount = myWeapons.Count;
                for (int i = 0; i < myWeaponCount; i++)
                {
                    if (TryResolveLiveWeaponEntityHandle(myWeapons[i], targetPawnIndex, targetControllerIndex, out uint weaponHandleRaw))
                    {
                        AddUniqueEntityHandle(targetEntities, weaponHandleRaw);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_hasLoggedWeaponSyncError)
                {
                    WarnLog(
                        "Weapon sync hiccup.",
                        "There was a brief issue reading a player's weapon data.",
                        "S2AWH handled it safely and will retry next tick."
                    );
                    DebugLog(
                        "Weapon sync error detail.",
                        $"Error: {ex.Message}",
                        "This message only shows once."
                    );
                    _hasLoggedWeaponSyncError = true;
                }
            }
        }

        if (targetEntities.SanitizeTick != nowTick)
        {
            SanitizeTargetEntityList(targetEntities);
            targetEntities.SanitizeTick = nowTick;
        }
        return true;
    }

    private static void AddUniqueEntityHandle(TargetTransmitEntities targetEntities, uint entityHandleRaw)
    {
        int entityIndex = (int)(entityHandleRaw & (Utilities.MaxEdicts - 1));
        if (entityIndex <= 0 || entityIndex >= Utilities.MaxEdicts)
        {
            return;
        }

        int count = targetEntities.Count;
        for (int i = 0; i < count; i++)
        {
            if (targetEntities.RawHandles[i] == entityHandleRaw)
            {
                return;
            }
        }

        if (count >= targetEntities.RawHandles.Length)
        {
            return; // Safety cap reached for this target.
        }

        targetEntities.RawHandles[count] = entityHandleRaw;
        targetEntities.Count = count + 1;
    }

    private static bool TryResolveLiveWeaponEntityHandle(CHandle<CBasePlayerWeapon> weaponHandle, int targetPawnIndex, int targetControllerIndex, out uint entityHandleRaw)
    {
        entityHandleRaw = 0;

        if (!weaponHandle.IsValid)
        {
            return false;
        }

        uint rawHandle = weaponHandle.Raw;
        IntPtr? weaponPointer = EntitySystem.GetEntityByHandle(rawHandle);
        if (!weaponPointer.HasValue || weaponPointer.Value == IntPtr.Zero)
        {
            return false;
        }

        CBasePlayerWeapon weaponEntity;
        try
        {
            weaponEntity = new CBasePlayerWeapon(weaponPointer.Value);
        }
        catch
        {
            return false;
        }

        if (!weaponEntity.IsValid)
        {
            return false;
        }

        // Weapon service arrays can be transient during inventory/model updates.
        // Reject resolved owner mismatches so we never hide another player's entity.
        var ownerHandle = weaponEntity.OwnerEntity;
        if (ownerHandle.IsValid)
        {
            IntPtr? ownerPointer = EntitySystem.GetEntityByHandle(ownerHandle.Raw);
            if (ownerPointer.HasValue && ownerPointer.Value != IntPtr.Zero)
            {
                try
                {
                    var ownerEntity = new CEntityInstance(ownerPointer.Value);
                    if (ownerEntity.IsValid)
                    {
                        int ownerIndex = (int)ownerEntity.Index;
                        if (ownerIndex != targetPawnIndex && ownerIndex != targetControllerIndex)
                        {
                            return false;
                        }
                    }
                }
                catch
                {
                    // Ignore transient owner resolution failures and trust the player weapon list.
                }
            }
        }

        entityHandleRaw = weaponEntity.EntityHandle.Raw;
        int entityIndex = (int)(entityHandleRaw & (Utilities.MaxEdicts - 1));
        if (entityIndex <= 0 || entityIndex >= Utilities.MaxEdicts)
        {
            return false;
        }

        return true;
    }

    private void SanitizeTargetEntityList(TargetTransmitEntities targetEntities)
    {
        int writeIndex = 0;
        int count = targetEntities.Count;
        for (int i = 0; i < count; i++)
        {
            uint entityHandleRaw = targetEntities.RawHandles[i];
            if (!TryResolveEntityHandleIndexForTransmit(entityHandleRaw, out _))
            {
                continue;
            }

            targetEntities.RawHandles[writeIndex++] = entityHandleRaw;
        }

        targetEntities.Count = writeIndex;
    }

    private bool TryResolveEntityHandleIndexForTransmit(uint entityHandleRaw, out int entityIndex)
    {
        entityIndex = 0;

        int index = (int)(entityHandleRaw & (Utilities.MaxEdicts - 1));
        if (index <= 0 || index >= Utilities.MaxEdicts)
        {
            return false;
        }

        if (_entityHandleIndexCache.TryGetValue(entityHandleRaw, out int cachedIndex))
        {
            if (cachedIndex <= 0)
            {
                return false;
            }

            entityIndex = cachedIndex;
            return true;
        }

        IntPtr? entityPointer = EntitySystem.GetEntityByHandle(entityHandleRaw);
        bool isValid = entityPointer.HasValue && entityPointer.Value != IntPtr.Zero;
        _entityHandleIndexCache[entityHandleRaw] = isValid ? index : -1;
        if (!isValid)
        {
            return false;
        }

        entityIndex = index;
        return true;
    }
}
