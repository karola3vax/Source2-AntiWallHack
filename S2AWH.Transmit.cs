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

        _liveSlotSet.Clear();
        int liveTargetCount = eligibleTargets.Count;
        for (int i = 0; i < liveTargetCount; i++)
        {
            _liveSlotSet.Add(eligibleTargets[i].Slot);
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
                    _eligibleTargetsWithEntities.Add((target, targetEntities, target.Slot, target.IsBot, target.TeamNum));
                }
            }
            _eligibleTargetsWithEntitiesTick = nowTick;
        }

        foreach ((CCheckTransmitInfo info, CCSPlayerController? viewer) in infoList)
        {
            if (viewer == null || !_liveSlotSet.Contains(viewer.Slot))
            {
                continue; // Dead/invalid viewers see everything
            }
             
            // If it's a bot and we don't calculate LOS for bots, don't block anything
            if (viewer.IsBot && !config.Visibility.BotsDoLOS) continue;

            int viewerSlot = viewer.Slot;
            bool hasViewerCache = _visibilityCache.TryGetValue(viewerSlot, out var targetVisibilityBySlot);

            int targetEntryCount = _eligibleTargetsWithEntities.Count;
            for (int targetEntryIndex = 0; targetEntryIndex < targetEntryCount; targetEntryIndex++)
            {
                var targetEntry = _eligibleTargetsWithEntities[targetEntryIndex];
                var target = targetEntry.Target;
                int targetSlot = targetEntry.TargetSlot;
                if (targetSlot == viewerSlot)
                {
                    continue;
                }
                var targetEntities = targetEntry.Entities;

                // Always-transmit fast paths.
                if (!config.Visibility.IncludeTeammates && targetEntry.TargetTeam == viewer.TeamNum)
                {
                    continue;
                }
                if (!config.Visibility.IncludeBots && targetEntry.TargetIsBot)
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
                }
                else
                {
                    if (_collectDebugCounters)
                    {
                        _transmitFallbackChecksInWindow++;
                    }
                    VisibilityEval visibilityEval = EvaluateVisibilitySafe(viewer, target, config, nowTick, "transmit fallback");
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
                    }
                }

                if (shouldTransmit)
                {
                    // Visible decisions are fail-open: do not force-add entities.
                    // This avoids overriding removals made by other plugins in the same callback.
                    continue;
                }

                bool removedAny = RemoveTargetPlayerAndWeapons(info, targetEntities, nowTick);
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
    }

    private bool RemoveTargetPlayerAndWeapons(CCheckTransmitInfo info, TargetTransmitEntities targetEntities, int nowTick)
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
                if (!TryResolveEntityHandleIndexForTransmit(entityHandleRaw, nowTick, out int entityIndex))
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
            if (!TryResolveEntityHandleIndexForTransmit(entityHandleRaw, nowTick, out int entityIndex))
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
        var targetPawnEntity = target.PlayerPawn.Value ?? target.Pawn.Value;
        if (targetPawnEntity == null || !targetPawnEntity.IsValid)
        {
            return false;
        }

        int targetSlot = target.Slot;
        uint pawnHandleRaw = targetPawnEntity.EntityHandle.Raw;
        int targetPawnIndex = (int)targetPawnEntity.Index;
        int targetControllerIndex = (int)target.Index;

        if (!_targetTransmitEntitiesCache.TryGetValue(targetSlot, out var cachedEntities) || cachedEntities == null)
        {
            cachedEntities = new TargetTransmitEntities();
            _targetTransmitEntitiesCache[targetSlot] = cachedEntities;
        }
        targetEntities = cachedEntities;

        if (targetEntities.Tick == nowTick && targetEntities.PawnHandleRaw == pawnHandleRaw
            && targetEntities.SanitizeTick == nowTick)
        {
            return true;
        }

        targetEntities.Tick = nowTick;
        targetEntities.PawnHandleRaw = pawnHandleRaw;
        targetEntities.Count = 0;
        AddUniqueEntityHandle(targetEntities, pawnHandleRaw);

        try
        {
            var weaponServices = targetPawnEntity.WeaponServices;
            if (weaponServices == null)
            {
                SanitizeTargetEntityList(targetEntities, nowTick);
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
                var weaponHandle = myWeapons[i];
                if (!TryResolveLiveWeaponEntityHandle(weaponHandle, targetPawnIndex, targetControllerIndex, out uint weaponHandleRaw))
                {
                    continue;
                }

                AddUniqueEntityHandle(targetEntities, weaponHandleRaw);
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

        SanitizeTargetEntityList(targetEntities, nowTick);
        targetEntities.SanitizeTick = nowTick;
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
            return; // Safety cap - 16 handles covers all realistic weapon inventories.
        }

        targetEntities.RawHandles[count] = entityHandleRaw;
        targetEntities.Count = count + 1;
    }

    private bool TryResolveLiveWeaponEntityHandle(CHandle<CBasePlayerWeapon> weaponHandle, int targetPawnIndex, int targetControllerIndex, out uint entityHandleRaw)
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

        // Owner can be transiently invalid during rapid inventory/model updates.
        // Accept unresolved-owner states, but keep strict mismatch reject when owner is resolved.
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
                    // Ignore transient owner resolution errors and use weapon handle from player weapon list.
                }
            }
        }

        entityHandleRaw = weaponEntity.EntityHandle.Raw;
        int entityIndex = (int)(entityHandleRaw & (Utilities.MaxEdicts - 1));
        return entityIndex > 0 && entityIndex < Utilities.MaxEdicts;
    }

    private void SanitizeTargetEntityList(TargetTransmitEntities targetEntities, int nowTick)
    {
        int writeIndex = 0;
        int count = targetEntities.Count;
        for (int i = 0; i < count; i++)
        {
            uint entityHandleRaw = targetEntities.RawHandles[i];
            if (!TryResolveEntityHandleIndexForTransmit(entityHandleRaw, nowTick, out _))
            {
                continue;
            }

            targetEntities.RawHandles[writeIndex++] = entityHandleRaw;
        }

        targetEntities.Count = writeIndex;
    }

    private bool TryResolveEntityHandleIndexForTransmit(uint entityHandleRaw, int nowTick, out int entityIndex)
    {
        entityIndex = 0;

        var handle = new CEntityHandle(entityHandleRaw);
        if (!handle.IsValid)
        {
            return false;
        }

        int index = (int)handle.Index;
        if (index <= 0 || index >= Utilities.MaxEdicts)
        {
            return false;
        }

        entityIndex = index;

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
