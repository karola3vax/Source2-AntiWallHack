using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Memory;
using CounterStrikeSharp.API.Modules.Utils;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

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
                    _eligibleTargetsWithEntities.Add((targetEntities, target.Slot, target.TeamNum, false));
                }
            }

            for (int slot = 0; slot < VisibilitySlotCapacity; slot++)
            {
                if (_liveSlotFlags[slot])
                {
                    continue;
                }

                TargetTransmitEntities? cachedTargetEntities = _targetTransmitEntitiesCache[slot];
                if (cachedTargetEntities == null ||
                    cachedTargetEntities.Count <= 0 ||
                    cachedTargetEntities.RetainUntilTick < nowTick ||
                    (!config.Visibility.IncludeBots && cachedTargetEntities.LastKnownIsBot))
                {
                    continue;
                }

                _eligibleTargetsWithEntities.Add((cachedTargetEntities, slot, cachedTargetEntities.LastKnownTeam, true));
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
                if (targetEntities.ForceVisibleUntilTick >= nowTick)
                {
                    continue;
                }

                // Always-transmit fast paths.
                if (skipTeammates && targetEntry.TargetTeam == viewerTeam)
                {
                    continue;
                }

                bool shouldTransmit;
                bool hasMatchingCachedDecision =
                    hasViewerCache &&
                    targetVisibilityBySlot != null &&
                    (uint)targetSlot < (uint)targetVisibilityBySlot.Known.Length &&
                    targetVisibilityBySlot.Known[targetSlot] &&
                    targetVisibilityBySlot.PawnHandles[targetSlot] == targetEntities.PawnHandleRaw;

                if (hasMatchingCachedDecision)
                {
                    ViewerVisibilityRow cachedVisibilityRow = targetVisibilityBySlot!;
                    shouldTransmit = cachedVisibilityRow.Decisions[targetSlot];

                    // Staggered snapshot rebuild can leave a hidden decision one or more ticks old.
                    // Recheck only stale hidden pairs so newly exposed targets do not remain popped-out
                    // until the viewer's next scheduled cache batch.
                    if (!targetEntry.UseCachedDecisionOnly &&
                        !shouldTransmit &&
                        cachedVisibilityRow.EvalTicks[targetSlot] != nowTick)
                    {
                        if (_collectDebugCounters)
                        {
                            _transmitFallbackChecksInWindow++;
                        }

                        VisibilityDecision visibilityDecision = EvaluateVisibilitySafe(
                            viewerSlot,
                            targetSlot,
                            viewerIsBot,
                            config,
                            nowTick,
                            "stale hidden recheck");
                        shouldTransmit = ResolveTransmitWithMemory(viewerSlot, targetSlot, visibilityDecision, nowTick);
                        cachedVisibilityRow.Decisions[targetSlot] = shouldTransmit;
                        cachedVisibilityRow.EvalTicks[targetSlot] = nowTick;
                    }
                }
                else
                {
                    if (targetEntry.UseCachedDecisionOnly)
                    {
                        continue;
                    }

                    if (_collectDebugCounters)
                    {
                        _transmitFallbackChecksInWindow++;
                    }
                    VisibilityDecision visibilityDecision = EvaluateVisibilitySafe(
                        viewerSlot,
                        targetSlot,
                        viewerIsBot,
                        config,
                        nowTick,
                        "transmit fallback");
                    shouldTransmit = ResolveTransmitWithMemory(viewerSlot, targetSlot, visibilityDecision, nowTick);

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

                bool removedAny = RemoveTargetTransmitEntities(info, targetEntities);
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

    private bool RemoveTargetTransmitEntities(CCheckTransmitInfo info, TargetTransmitEntities targetEntities)
    {
        int nowTick = Server.TickCount;
        if (!PrepareTargetTransmitEntitiesForRemoval(targetEntities, nowTick))
        {
            ApplyFailOpenVisibleQuarantine(targetEntities, nowTick);
            RecordTargetClosureOffenderSample(targetEntities, 6);

            if (_collectDebugCounters)
            {
                _transmitFailOpenOwnedClosureInWindow++;
                if (targetEntities.BaseHitEntityCap || targetEntities.HitEntityCap || targetEntities.HitSceneClosureBudget)
                {
                    _transmitFailOpenEntityClosureCapInWindow++;
                }
            }

            if ((targetEntities.BaseHitEntityCap || targetEntities.HitEntityCap || targetEntities.HitSceneClosureBudget) &&
                !_hasLoggedEntityClosureCapError)
            {
                string targetEntitySample = DescribeTargetEntitySample(targetEntities, 6);
                WarnLog(
                    "Transmit closure hit a safety cap.",
                    $"A target had more linked entities than the configured closure budget. Sample: {targetEntitySample}.",
                    "Increase entity closure limits or reduce attachment-heavy entities to avoid fail-open."
                );
                _hasLoggedEntityClosureCapError = true;
            }
            return false;
        }

        int entityCount = targetEntities.Count;
        if (entityCount <= 0)
        {
            return false;
        }

        if (HasUnsafeReverseTransmitReferences(info, targetEntities, nowTick))
        {
            ApplyFailOpenVisibleQuarantine(targetEntities, nowTick);
            if (_collectDebugCounters)
            {
                _transmitFailOpenReverseAuditInWindow++;
            }

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

    private bool HasUnsafeReverseTransmitReferences(CCheckTransmitInfo info, TargetTransmitEntities targetEntities, int nowTick)
    {
        if (!TryEnsureOwnedEntityBuckets(nowTick))
        {
            return true;
        }

        int targetEntityCount = targetEntities.Count;
        _targetTransmitHandleMembership.Clear();
        for (int i = 0; i < targetEntityCount; i++)
        {
            _targetTransmitHandleMembership.Add(targetEntities.RawHandles[i]);
        }

        for (int i = 0; i < targetEntityCount; i++)
        {
            uint referencedHandleRaw = targetEntities.RawHandles[i];
            if (!_ownedEntityBuckets.TryGetValue(referencedHandleRaw, out OwnedEntityBucket? bucket) || bucket.Count <= 0)
            {
                continue;
            }

            for (int bucketIndex = 0; bucketIndex < bucket.Count; bucketIndex++)
            {
                uint referencingHandleRaw = bucket.RawHandles[bucketIndex];
                if (_targetTransmitHandleMembership.Contains(referencingHandleRaw))
                {
                    continue;
                }

                if (!TryResolveEntityHandleIndexForTransmit(referencingHandleRaw, out int referencingEntityIndex))
                {
                    continue;
                }

                if (!info.TransmitEntities.Contains(referencingEntityIndex))
                {
                    continue;
                }

                RecordClosureOffenderHandle(referencingHandleRaw);
                RecordClosureOffenderHandle(referencedHandleRaw);

                if (!_hasLoggedReverseReferenceAuditError)
                {
                    WarnLog(
                        "Reverse transmit audit blocked a hide.",
                        $"A still-transmitted entity references a soon-to-hide entity: {DescribeOwnedRelationSample(referencedHandleRaw, referencingHandleRaw)}.",
                        "S2AWH failed open for safety and will retry after quarantine."
                    );
                    _hasLoggedReverseReferenceAuditError = true;
                }

                return true;
            }
        }

        return false;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Weapon service resolution is external/native-facing and must remain resilient.")]
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
        uint controllerHandleRaw = target.EntityHandle.Raw;

        TargetTransmitEntities? cachedEntities = _targetTransmitEntitiesCache[targetSlot];
        if (cachedEntities == null)
        {
            cachedEntities = new TargetTransmitEntities();
            _targetTransmitEntitiesCache[targetSlot] = cachedEntities;
        }
        targetEntities = cachedEntities;
        targetEntities.LastKnownTeam = target.TeamNum;
        targetEntities.LastKnownIsBot = target.IsBot;
        targetEntities.RetainUntilTick = nowTick + HiddenEntityTransitionGraceTicks;
        targetEntities.ControllerHandleRaw = controllerHandleRaw;

        int targetPawnIndex = (int)targetPawnEntity.Index;
        int targetControllerIndex = (int)target.Index;
        bool mustRefreshFullList = targetEntities.PawnHandleRaw != pawnHandleRaw || targetEntities.LastFullRefreshTick != nowTick;

        if (mustRefreshFullList)
        {
            targetEntities.LastFullRefreshTick = nowTick;
            if (targetEntities.PawnHandleRaw != pawnHandleRaw)
            {
                targetEntities.ForceVisibleUntilTick = -1;
            }
            targetEntities.PawnHandleRaw = pawnHandleRaw;
            targetEntities.Count = 0;
            targetEntities.BaseCount = 0;
            targetEntities.OwnedClosureTick = -1;
            targetEntities.BaseHitEntityCap = false;
            targetEntities.HitEntityCap = false;
            targetEntities.HitSceneClosureBudget = false;
            AddUniqueEntityHandle(targetEntities, pawnHandleRaw);

            try
            {
                var weaponServices = targetPawnEntity.WeaponServices;
                if (weaponServices != null)
                {
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

                var myWearables = targetPawnEntity.MyWearables;
                int myWearableCount = myWearables.Count;
                for (int i = 0; i < myWearableCount; i++)
                {
                    if (TryResolveLiveOwnedEntityHandle(myWearables[i], targetPawnIndex, targetControllerIndex, out uint wearableHandleRaw))
                    {
                        AddUniqueEntityHandle(targetEntities, wearableHandleRaw);
                    }
                }
            }
            catch (Exception ex)
            {
                if (!_hasLoggedWeaponSyncError)
                {
                    WarnLog(
                        "Owned entity sync hiccup.",
                        "There was a brief issue reading a player's weapon or wearable data.",
                        "S2AWH handled it safely and will retry next tick."
                    );
                    DebugLog(
                        "Owned entity sync error detail.",
                        $"Error: {ex.Message}",
                        "This message only shows once."
                    );
                    _hasLoggedWeaponSyncError = true;
                }
            }

            targetEntities.BaseHitEntityCap = targetEntities.HitEntityCap;
            targetEntities.HitEntityCap = false;
            targetEntities.BaseCount = targetEntities.Count;
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
            if (targetEntities.RawHandles.Length >= MaxTrackedTransmitEntitiesPerTarget)
            {
                targetEntities.HitEntityCap = true;
                return;
            }

            int newLength = Math.Min(MaxTrackedTransmitEntitiesPerTarget, targetEntities.RawHandles.Length * 2);
            Array.Resize(ref targetEntities.RawHandles, newLength);
        }

        targetEntities.RawHandles[count] = entityHandleRaw;
        targetEntities.Count = count + 1;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Weapon entity resolution crosses unstable native boundaries and must stay non-fatal inside transmit filtering.")]
    private static bool TryResolveLiveWeaponEntityHandle(CHandle<CBasePlayerWeapon> weaponHandle, int targetPawnIndex, int targetControllerIndex, out uint entityHandleRaw)
    {
        entityHandleRaw = 0;

        if (!weaponHandle.IsValid)
        {
            return false;
        }

        return TryResolveLiveOwnedEntityHandle(weaponHandle, targetPawnIndex, targetControllerIndex, out entityHandleRaw);
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Owned entity resolution crosses unstable native boundaries and must stay non-fatal inside transmit filtering.")]
    private static bool TryResolveLiveOwnedEntityHandle<T>(CHandle<T> entityHandle, int targetPawnIndex, int targetControllerIndex, out uint entityHandleRaw)
        where T : NativeEntity
    {
        entityHandleRaw = 0;

        if (!entityHandle.IsValid)
        {
            return false;
        }

        uint rawHandle = entityHandle.Raw;
        IntPtr? entityPointer = EntitySystem.GetEntityByHandle(rawHandle);
        if (!entityPointer.HasValue || entityPointer.Value == IntPtr.Zero)
        {
            return false;
        }

        CBaseEntity entity;
        try
        {
            entity = new CBaseEntity(entityPointer.Value);
        }
        catch
        {
            return false;
        }

        if (!entity.IsValid)
        {
            return false;
        }

        // Weapon and wearable service arrays can be transient during inventory/model updates.
        // Reject resolved owner mismatches so we never hide another player's entity.
        var ownerHandle = entity.OwnerEntity;
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

        entityHandleRaw = entity.EntityHandle.Raw;
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
        int writeBaseCount = 0;
        int count = targetEntities.Count;
        for (int i = 0; i < count; i++)
        {
            uint entityHandleRaw = targetEntities.RawHandles[i];
            bool isBaseHandle = i < targetEntities.BaseCount;
            bool shouldKeep = isBaseHandle
                ? TryResolveEntityHandleIndexForTransmit(entityHandleRaw, out _)
                : ShouldKeepTargetEntityHandle(targetEntities, entityHandleRaw);
            if (!shouldKeep)
            {
                continue;
            }

            targetEntities.RawHandles[writeIndex++] = entityHandleRaw;
            if (isBaseHandle)
            {
                writeBaseCount++;
            }
        }

        targetEntities.Count = writeIndex;
        targetEntities.BaseCount = Math.Min(writeBaseCount, writeIndex);
    }

    private bool PrepareTargetTransmitEntitiesForRemoval(TargetTransmitEntities targetEntities, int nowTick)
    {
        bool appendedOwnedClosure = false;
        if (targetEntities.OwnedClosureTick != nowTick)
        {
            // Rebuild transient closure from the stable per-target base set every tick.
            // Carrying previous tick's attachments forward can compound stale handles and hit caps early.
            targetEntities.Count = Math.Min(targetEntities.BaseCount, targetEntities.Count);
            targetEntities.HitEntityCap = false;
            targetEntities.HitSceneClosureBudget = false;
            if (!AppendOwnedEntityHandleClosure(targetEntities, targetEntities.ControllerHandleRaw, nowTick))
            {
                return false;
            }
            targetEntities.OwnedClosureTick = nowTick;
            appendedOwnedClosure = true;
        }

        if (appendedOwnedClosure || targetEntities.SanitizeTick != nowTick)
        {
            SanitizeTargetEntityList(targetEntities);
            targetEntities.SanitizeTick = nowTick;
        }

        if (targetEntities.BaseHitEntityCap || targetEntities.HitEntityCap || targetEntities.HitSceneClosureBudget)
        {
            return false;
        }

        return true;
    }

    private bool ShouldKeepTargetEntityHandle(TargetTransmitEntities targetEntities, uint entityHandleRaw)
    {
        if (!TryResolveEntityHandleIndexForTransmit(entityHandleRaw, out _))
        {
            return false;
        }

        if (entityHandleRaw == targetEntities.PawnHandleRaw ||
            entityHandleRaw == targetEntities.ControllerHandleRaw)
        {
            return true;
        }

        return TryResolveOwnerChainToTarget(targetEntities, entityHandleRaw) ||
               TryResolveEffectChainToTarget(targetEntities, entityHandleRaw) ||
               TryResolveParentChainToTarget(targetEntities, entityHandleRaw);
    }

    private static bool TryResolveOwnerChainToTarget(TargetTransmitEntities targetEntities, uint entityHandleRaw)
    {
        uint currentHandleRaw = entityHandleRaw;
        for (int depth = 0; depth < 8; depth++)
        {
            IntPtr? entityPointer = EntitySystem.GetEntityByHandle(currentHandleRaw);
            if (!entityPointer.HasValue || entityPointer.Value == IntPtr.Zero)
            {
                return false;
            }

            var entity = new CBaseEntity(entityPointer.Value);
            if (!entity.IsValid)
            {
                return false;
            }

            var ownerHandle = entity.OwnerEntity;
            if (!ownerHandle.IsValid)
            {
                return false;
            }

            uint ownerHandleRaw = ownerHandle.Raw;
            if (ownerHandleRaw == targetEntities.PawnHandleRaw ||
                ownerHandleRaw == targetEntities.ControllerHandleRaw)
            {
                return true;
            }

            if (ownerHandleRaw == 0 || ownerHandleRaw == currentHandleRaw)
            {
                return false;
            }

            currentHandleRaw = ownerHandleRaw;
        }

        return false;
    }

    private static bool TryResolveParentChainToTarget(TargetTransmitEntities targetEntities, uint entityHandleRaw)
    {
        uint currentHandleRaw = entityHandleRaw;
        for (int depth = 0; depth < 8; depth++)
        {
            if (!TryResolveSceneParentEntityHandle(currentHandleRaw, out uint parentHandleRaw))
            {
                return false;
            }

            if (parentHandleRaw == targetEntities.PawnHandleRaw ||
                parentHandleRaw == targetEntities.ControllerHandleRaw)
            {
                return true;
            }

            if (parentHandleRaw == 0 || parentHandleRaw == currentHandleRaw)
            {
                return false;
            }

            currentHandleRaw = parentHandleRaw;
        }

        return false;
    }

    private static bool TryResolveEffectChainToTarget(TargetTransmitEntities targetEntities, uint entityHandleRaw)
    {
        uint currentHandleRaw = entityHandleRaw;
        for (int depth = 0; depth < 8; depth++)
        {
            IntPtr? entityPointer = EntitySystem.GetEntityByHandle(currentHandleRaw);
            if (!entityPointer.HasValue || entityPointer.Value == IntPtr.Zero)
            {
                return false;
            }

            var entity = new CBaseEntity(entityPointer.Value);
            if (!entity.IsValid)
            {
                return false;
            }

            var effectHandle = entity.EffectEntity;
            if (!effectHandle.IsValid)
            {
                return false;
            }

            uint effectHandleRaw = effectHandle.Raw;
            if (effectHandleRaw == targetEntities.PawnHandleRaw ||
                effectHandleRaw == targetEntities.ControllerHandleRaw)
            {
                return true;
            }

            if (effectHandleRaw == 0 || effectHandleRaw == currentHandleRaw)
            {
                return false;
            }

            currentHandleRaw = effectHandleRaw;
        }

        return false;
    }

    /// <summary>
    /// Resolves the scene-graph parent of an entity by walking
    /// CBaseEntity -> CBodyComponent -> SceneNode -> PParent -> Owner.
    /// Falls back to the networked CGameSceneNode::m_hParent owner handle when the
    /// wrapper's parent pointer is transiently unavailable. Returns false if every
    /// path is null or invalid.
    /// </summary>
    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Scene-graph parent lookup crosses native entity memory and must fail open on transient read faults.")]
    private static bool TryResolveSceneParentEntityHandle(uint childEntityHandleRaw, out uint parentEntityHandleRaw)
    {
        parentEntityHandleRaw = 0;

        try
        {
            IntPtr? childPointer = EntitySystem.GetEntityByHandle(childEntityHandleRaw);
            if (!childPointer.HasValue || childPointer.Value == IntPtr.Zero)
            {
                return false;
            }

            var childEntity = new CBaseEntity(childPointer.Value);
            if (!childEntity.IsValid)
            {
                return false;
            }

            var sceneNode = childEntity.CBodyComponent?.SceneNode;
            if (sceneNode == null)
            {
                return false;
            }

            if (TryResolveSceneParentEntityHandleFromPointer(sceneNode, out parentEntityHandleRaw))
            {
                return true;
            }

            return TryResolveSceneParentEntityHandleFromNetwork(sceneNode, out parentEntityHandleRaw);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryResolveSceneParentEntityHandleFromPointer(CGameSceneNode sceneNode, out uint parentEntityHandleRaw)
    {
        parentEntityHandleRaw = 0;

        var parentSceneNode = sceneNode.PParent;
        if (parentSceneNode == null)
        {
            return false;
        }

        var parentOwner = parentSceneNode.Owner;
        if (parentOwner == null || !parentOwner.IsValid)
        {
            return false;
        }

        return TryResolveLiveEntityHandleRaw(parentOwner.EntityHandle.Raw, out parentEntityHandleRaw);
    }

    private static bool TryResolveSceneParentEntityHandleFromNetwork(CGameSceneNode sceneNode, out uint parentEntityHandleRaw)
    {
        parentEntityHandleRaw = 0;

        if (sceneNode.Handle == IntPtr.Zero)
        {
            return false;
        }

        short parentHandleOffset = Schema.GetSchemaOffset("CGameSceneNode", "m_hParent");
        IntPtr parentOwnerHandlePointer = sceneNode.Handle + parentHandleOffset + 0x8;
        uint parentHandleRaw = unchecked((uint)Marshal.ReadInt32(parentOwnerHandlePointer));
        return TryResolveLiveEntityHandleRaw(parentHandleRaw, out parentEntityHandleRaw);
    }

    private static bool TryResolveLiveEntityHandleRaw(uint entityHandleRaw, out uint liveEntityHandleRaw)
    {
        liveEntityHandleRaw = 0;

        int entityIndex = (int)(entityHandleRaw & (Utilities.MaxEdicts - 1));
        if (entityIndex <= 0 || entityIndex >= Utilities.MaxEdicts)
        {
            return false;
        }

        IntPtr? entityPointer = EntitySystem.GetEntityByHandle(entityHandleRaw);
        if (!entityPointer.HasValue || entityPointer.Value == IntPtr.Zero)
        {
            return false;
        }

        liveEntityHandleRaw = entityHandleRaw;
        return true;
    }

    private void ApplyFailOpenVisibleQuarantine(TargetTransmitEntities targetEntities, int nowTick)
    {
        int quarantineUntilTick = nowTick + FailOpenVisibleQuarantineTicks;
        if (targetEntities.ForceVisibleUntilTick >= quarantineUntilTick)
        {
            return;
        }

        targetEntities.ForceVisibleUntilTick = quarantineUntilTick;
        if (_collectDebugCounters)
        {
            _transmitFailOpenQuarantineInWindow++;
        }
    }

    private bool AppendOwnedEntityHandleClosure(TargetTransmitEntities targetEntities, uint extraOwnerHandleRaw, int nowTick)
    {
        if (!TryEnsureOwnedEntityBuckets(nowTick))
        {
            return false;
        }

        if (extraOwnerHandleRaw != 0 && extraOwnerHandleRaw != uint.MaxValue)
        {
            AppendOwnedChildren(targetEntities, extraOwnerHandleRaw);
        }

        int readIndex = 0;
        while (readIndex < targetEntities.Count)
        {
            AppendOwnedChildren(targetEntities, targetEntities.RawHandles[readIndex++]);
            if (targetEntities.HitEntityCap)
            {
                return false;
            }
        }

        AppendSceneDescendantHandles(targetEntities, targetEntities.PawnHandleRaw);
        if (extraOwnerHandleRaw != 0 && extraOwnerHandleRaw != uint.MaxValue)
        {
            AppendSceneDescendantHandles(targetEntities, extraOwnerHandleRaw);
        }

        if (targetEntities.HitEntityCap || targetEntities.HitSceneClosureBudget)
        {
            return false;
        }

        return true;
    }

    private void AppendOwnedChildren(TargetTransmitEntities targetEntities, uint ownerHandleRaw)
    {
        if (!_ownedEntityBuckets.TryGetValue(ownerHandleRaw, out OwnedEntityBucket? bucket) || bucket.Count <= 0)
        {
            return;
        }

        for (int i = 0; i < bucket.Count; i++)
        {
            AddUniqueEntityHandle(targetEntities, bucket.RawHandles[i]);
            if (targetEntities.HitEntityCap)
            {
                return;
            }
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Scene graph traversal reads native pointers that can be transiently invalid; treat faults as bounded fail-open.")]
    private void AppendSceneDescendantHandles(TargetTransmitEntities targetEntities, uint rootEntityHandleRaw)
    {
        if (rootEntityHandleRaw == 0 || rootEntityHandleRaw == uint.MaxValue)
        {
            return;
        }

        try
        {
            IntPtr? rootPointer = EntitySystem.GetEntityByHandle(rootEntityHandleRaw);
            if (!rootPointer.HasValue || rootPointer.Value == IntPtr.Zero)
            {
                return;
            }

            var rootEntity = new CBaseEntity(rootPointer.Value);
            if (!rootEntity.IsValid)
            {
                return;
            }

            CGameSceneNode? firstChild = rootEntity.CBodyComponent?.SceneNode?.Child;
            if (firstChild == null)
            {
                return;
            }

            _sceneClosureVisitedNodes.Clear();
            int visitedNodeCount = 0;
            AppendSceneNodeBranch(targetEntities, firstChild, 0, ref visitedNodeCount);
        }
        catch
        {
            targetEntities.HitSceneClosureBudget = true;
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Scene node child/sibling traversal can cross transient native state while entities spawn/despawn.")]
    private void AppendSceneNodeBranch(TargetTransmitEntities targetEntities, CGameSceneNode? branchNode, int depth, ref int visitedNodeCount)
    {
        if (branchNode == null)
        {
            return;
        }

        if (depth > MaxSceneNodeClosureDepth)
        {
            targetEntities.HitSceneClosureBudget = true;
            return;
        }

        CGameSceneNode? currentNode = branchNode;
        while (currentNode != null)
        {
            if (visitedNodeCount >= MaxSceneNodeClosureNodesPerTarget)
            {
                targetEntities.HitSceneClosureBudget = true;
                return;
            }

            CGameSceneNode? childNode;
            CGameSceneNode? nextSiblingNode;
            try
            {
                nint nodeHandle = currentNode.Handle;
                if (nodeHandle == nint.Zero)
                {
                    currentNode = currentNode.NextSibling;
                    continue;
                }

                if (!_sceneClosureVisitedNodes.Add(nodeHandle))
                {
                    currentNode = currentNode.NextSibling;
                    continue;
                }

                visitedNodeCount++;

                var ownerEntity = currentNode.Owner;
                if (ownerEntity != null && ownerEntity.IsValid)
                {
                    AddUniqueEntityHandle(targetEntities, ownerEntity.EntityHandle.Raw);
                }

                childNode = currentNode.Child;
                nextSiblingNode = currentNode.NextSibling;
            }
            catch
            {
                targetEntities.HitSceneClosureBudget = true;
                return;
            }

            if (childNode != null)
            {
                AppendSceneNodeBranch(targetEntities, childNode, depth + 1, ref visitedNodeCount);
                if (targetEntities.HitSceneClosureBudget || targetEntities.HitEntityCap)
                {
                    return;
                }
            }

            currentNode = nextSiblingNode;
        }
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Entity ownership scans cross native entity state and must fail open on transient issues.")]
    private bool TryEnsureOwnedEntityBuckets(int nowTick)
    {
        if (_ownedEntityBucketsTick == nowTick)
        {
            return true;
        }

        try
        {
            _lastOwnedEntityBucketOverflowOwnerHandleRaw = 0;
            _lastOwnedEntityBucketOverflowEntityHandleRaw = 0;
            bool bucketOverflowed = false;
            int syncPass = 0;

            do
            {
                PrimePendingOwnedEntityRescans(nowTick);

                bool shouldFullResync =
                    !_ownedEntityBucketsInitialized ||
                    _dirtyOwnedEntityHandles.Count >= MaxDirtyOwnedEntityHandlesBeforeFullResync ||
                    _ownedEntityLastFullResyncTick < 0 ||
                    (nowTick - _ownedEntityLastFullResyncTick) >= OwnedEntityFullResyncIntervalTicks;

                if (shouldFullResync)
                {
                    FullRebuildOwnedEntityBuckets(ref bucketOverflowed);
                    _ownedEntityLastFullResyncTick = nowTick;
                    _ownedEntityBucketsInitialized = true;
                    if (_collectDebugCounters)
                    {
                        _ownedEntityFullResyncsInWindow++;
                    }
                }
                else if (_dirtyOwnedEntityHandles.Count > 0)
                {
                    ProcessDirtyOwnedEntities(ref bucketOverflowed);
                }

                syncPass++;
            }
            while (!bucketOverflowed &&
                   _dirtyOwnedEntityHandles.Count > 0 &&
                   syncPass < MaxOwnedEntityDirtySyncPassesPerTick);

            if (!bucketOverflowed && _dirtyOwnedEntityHandles.Count > 0)
            {
                InvalidateOwnedEntityBucketsForFullResync();
                return false;
            }

            if (bucketOverflowed)
            {
                InvalidateOwnedEntityBucketsForFullResync();
                if (!_hasLoggedEntityClosureCapError)
                {
                    RecordClosureOffenderHandle(_lastOwnedEntityBucketOverflowOwnerHandleRaw);
                    RecordClosureOffenderHandle(_lastOwnedEntityBucketOverflowEntityHandleRaw);
                    string relationSample = DescribeOwnedRelationSample(
                        _lastOwnedEntityBucketOverflowOwnerHandleRaw,
                        _lastOwnedEntityBucketOverflowEntityHandleRaw);
                    WarnLog(
                        "Entity closure scan hit a safety cap.",
                        $"At least one owner bucket exceeded the maximum tracked linked entities. Sample: {relationSample}.",
                        "S2AWH failed open for safety and will retry next tick."
                    );
                    _hasLoggedEntityClosureCapError = true;
                }

                return false;
            }

            _hasLoggedOwnedEntityScanError = false;
            _ownedEntityBucketsTick = nowTick;
            return true;
        }
        catch (Exception ex)
        {
            InvalidateOwnedEntityBucketsForFullResync();

            if (!_hasLoggedOwnedEntityScanError)
            {
                WarnLog(
                    "Could not scan owned entities.",
                    "A temporary issue prevented S2AWH from gathering player-owned attachments.",
                    "This tick will fail open and retry automatically."
                );
                DebugLog(
                    "Owned entity scan error detail.",
                    $"Error: {ex.Message}",
                    "This message only shows once."
                );
                _hasLoggedOwnedEntityScanError = true;
            }

            return false;
        }
    }

    private void InvalidateOwnedEntityBucketsForFullResync()
    {
        _ownedEntityBucketsTick = -1;
        _ownedEntityLastFullResyncTick = -1;
        _ownedEntityBucketsInitialized = false;
        _ownedEntityBuckets.Clear();
        _ownedEntityRelationsByChild.Clear();
    }

    private void PrimePendingOwnedEntityRescans(int nowTick)
    {
        if (_pendingOwnedEntityRescanUntilTick.Count <= 0)
        {
            return;
        }

        _ownedEntityPendingRescanRemovalScratch.Clear();
        foreach (var pair in _pendingOwnedEntityRescanUntilTick)
        {
            if (pair.Value < nowTick)
            {
                _ownedEntityPendingRescanRemovalScratch.Add(pair.Key);
                continue;
            }

            _dirtyOwnedEntityHandles.Add(pair.Key);
        }

        int removalCount = _ownedEntityPendingRescanRemovalScratch.Count;
        for (int i = 0; i < removalCount; i++)
        {
            _pendingOwnedEntityRescanUntilTick.Remove(_ownedEntityPendingRescanRemovalScratch[i]);
        }
    }

    private void FullRebuildOwnedEntityBuckets(ref bool bucketOverflowed)
    {
        _ownedEntityBuckets.Clear();
        _ownedEntityRelationsByChild.Clear();
        _dirtyOwnedEntityHandles.Clear();

        foreach (CEntityInstance entityInstance in Utilities.GetAllEntities())
        {
            UpsertOwnedEntityRelations(entityInstance, ref bucketOverflowed);
            if (bucketOverflowed)
            {
                return;
            }
        }
    }

    private void ProcessDirtyOwnedEntities(ref bool bucketOverflowed)
    {
        _ownedEntityDirtyHandleScratch.Clear();
        _ownedEntityDirtyHandleScratch.AddRange(_dirtyOwnedEntityHandles);
        _dirtyOwnedEntityHandles.Clear();

        int dirtyCount = _ownedEntityDirtyHandleScratch.Count;
        for (int i = 0; i < dirtyCount; i++)
        {
            uint entityHandleRaw = _ownedEntityDirtyHandleScratch[i];
            RemoveOwnedEntityRelationsForHandle(entityHandleRaw);

            IntPtr? entityPointer = EntitySystem.GetEntityByHandle(entityHandleRaw);
            if (!entityPointer.HasValue || entityPointer.Value == IntPtr.Zero)
            {
                continue;
            }

            var entityInstance = new CEntityInstance(entityPointer.Value);
            if (!entityInstance.IsValid)
            {
                continue;
            }

            UpsertOwnedEntityRelations(entityInstance, ref bucketOverflowed);
            if (bucketOverflowed)
            {
                return;
            }
        }

        if (_collectDebugCounters)
        {
            _ownedEntityDirtyEntityUpdatesInWindow += dirtyCount;
        }
    }

    private void UpsertOwnedEntityRelations(CEntityInstance entityInstance, ref bool bucketOverflowed)
    {
        if (entityInstance == null || !entityInstance.IsValid)
        {
            return;
        }

        var entity = new CBaseEntity(entityInstance.Handle);
        if (!entity.IsValid)
        {
            return;
        }

        uint entityHandleRaw = entity.EntityHandle.Raw;
        int entityIndex = (int)(entityHandleRaw & (Utilities.MaxEdicts - 1));
        if (entityIndex <= 0 || entityIndex >= Utilities.MaxEdicts)
        {
            return;
        }

        CollectLinkedOwnerHandles(entityInstance, entity, entityHandleRaw, _ownedEntityScratchHandles, ref bucketOverflowed);
        if (bucketOverflowed || _ownedEntityScratchHandles.Count <= 0)
        {
            return;
        }

        if (!_ownedEntityRelationsByChild.TryGetValue(entityHandleRaw, out OwnedEntityBucket? childRelations))
        {
            childRelations = new OwnedEntityBucket();
            _ownedEntityRelationsByChild[entityHandleRaw] = childRelations;
        }

        CopyOwnedEntityBucket(_ownedEntityScratchHandles, childRelations);
        for (int i = 0; i < childRelations.Count; i++)
        {
            TryAddOwnedEntityRelation(childRelations.RawHandles[i], entityHandleRaw, ref bucketOverflowed);
            if (bucketOverflowed)
            {
                return;
            }
        }
    }

    private void CollectLinkedOwnerHandles(
        CEntityInstance entityInstance,
        CBaseEntity entity,
        uint entityHandleRaw,
        OwnedEntityBucket ownerHandles,
        ref bool bucketOverflowed)
    {
        ownerHandles.Count = 0;

        AddCollectedOwnerHandle(ownerHandles, entity.OwnerEntity.Raw, ref bucketOverflowed);
        if (bucketOverflowed)
        {
            return;
        }

        uint effectHandleRaw = entity.EffectEntity.Raw;
        if (effectHandleRaw != entity.OwnerEntity.Raw)
        {
            AddCollectedOwnerHandle(ownerHandles, effectHandleRaw, ref bucketOverflowed);
            if (bucketOverflowed)
            {
                return;
            }
        }

        if (TryResolveSceneParentEntityHandle(entityHandleRaw, out uint sceneParentHandleRaw))
        {
            AddCollectedOwnerHandle(ownerHandles, sceneParentHandleRaw, ref bucketOverflowed);
            if (bucketOverflowed)
            {
                return;
            }
        }

        AppendDesignerSpecificLinkedEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
    }

    private static void AddCollectedOwnerHandle(OwnedEntityBucket ownerHandles, uint ownerHandleRaw, ref bool bucketOverflowed)
    {
        if (ownerHandleRaw == 0 || ownerHandleRaw == uint.MaxValue)
        {
            return;
        }

        if (!TryResolveLiveEntityHandleRaw(ownerHandleRaw, out uint liveOwnerHandleRaw))
        {
            return;
        }

        AddUniqueBucketHandle(ownerHandles, liveOwnerHandleRaw, ref bucketOverflowed);
    }

    private static void AddUniqueBucketHandle(OwnedEntityBucket bucket, uint handleRaw, ref bool bucketOverflowed)
    {
        int count = bucket.Count;
        for (int i = 0; i < count; i++)
        {
            if (bucket.RawHandles[i] == handleRaw)
            {
                return;
            }
        }

        if (count >= bucket.RawHandles.Length)
        {
            if (bucket.RawHandles.Length >= MaxTrackedTransmitEntitiesPerTarget)
            {
                bucketOverflowed = true;
                return;
            }

            int newLength = Math.Min(MaxTrackedTransmitEntitiesPerTarget, bucket.RawHandles.Length * 2);
            Array.Resize(ref bucket.RawHandles, newLength);
        }

        bucket.RawHandles[count] = handleRaw;
        bucket.Count = count + 1;
    }

    private static void CopyOwnedEntityBucket(OwnedEntityBucket source, OwnedEntityBucket destination)
    {
        if (destination.RawHandles.Length < source.Count)
        {
            Array.Resize(ref destination.RawHandles, source.Count);
        }

        Array.Copy(source.RawHandles, destination.RawHandles, source.Count);
        destination.Count = source.Count;
    }

    private void RemoveOwnedEntityRelationsForHandle(uint entityHandleRaw)
    {
        if (!_ownedEntityRelationsByChild.TryGetValue(entityHandleRaw, out OwnedEntityBucket? childRelations))
        {
            return;
        }

        for (int i = 0; i < childRelations.Count; i++)
        {
            RemoveOwnedEntityRelation(childRelations.RawHandles[i], entityHandleRaw);
        }

        _ownedEntityRelationsByChild.Remove(entityHandleRaw);
    }

    private void RemoveOwnedEntityRelation(uint ownerHandleRaw, uint entityHandleRaw)
    {
        if (!_ownedEntityBuckets.TryGetValue(ownerHandleRaw, out OwnedEntityBucket? bucket))
        {
            return;
        }

        RemoveOwnedEntityBucketHandle(bucket, entityHandleRaw);
        if (bucket.Count <= 0)
        {
            _ownedEntityBuckets.Remove(ownerHandleRaw);
        }
    }

    private static void RemoveOwnedEntityBucketHandle(OwnedEntityBucket bucket, uint entityHandleRaw)
    {
        int count = bucket.Count;
        for (int i = 0; i < count; i++)
        {
            if (bucket.RawHandles[i] != entityHandleRaw)
            {
                continue;
            }

            int lastIndex = count - 1;
            bucket.RawHandles[i] = bucket.RawHandles[lastIndex];
            bucket.RawHandles[lastIndex] = 0;
            bucket.Count = lastIndex;
            return;
        }
    }

    private void MarkOwnedEntityDirty(CEntityInstance entity, bool scheduleRescan)
    {
        if (entity == null || !entity.IsValid)
        {
            return;
        }

        uint entityHandleRaw = entity.EntityHandle.Raw;
        int entityIndex = (int)(entityHandleRaw & (Utilities.MaxEdicts - 1));
        if (entityIndex <= 0 || entityIndex >= Utilities.MaxEdicts)
        {
            return;
        }

        _dirtyOwnedEntityHandles.Add(entityHandleRaw);
        if (scheduleRescan)
        {
            int rescanUntilTick = Server.TickCount + OwnedEntityPostSpawnRescanTicks;
            if (_pendingOwnedEntityRescanUntilTick.TryGetValue(entityHandleRaw, out int existingRescanUntilTick) &&
                existingRescanUntilTick > rescanUntilTick)
            {
                rescanUntilTick = existingRescanUntilTick;
            }

            if (_collectDebugCounters &&
                (!_pendingOwnedEntityRescanUntilTick.TryGetValue(entityHandleRaw, out int currentRescanUntilTick) ||
                 currentRescanUntilTick < rescanUntilTick))
            {
                _ownedEntityPostSpawnRescanMarksInWindow++;
            }

            _pendingOwnedEntityRescanUntilTick[entityHandleRaw] = rescanUntilTick;
        }

        _ownedEntityBucketsTick = -1;
    }

    [SuppressMessage(
        "Design",
        "CA1031:Do not catch general exception types",
        Justification = "Designer-specific attachment probing is best-effort hardening and must never break the main ownership scan.")]
    private void AppendDesignerSpecificLinkedEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        if (bucketOverflowed)
        {
            return;
        }

        string? designerName = entityInstance.DesignerName;
        if (string.IsNullOrWhiteSpace(designerName))
        {
            return;
        }

        try
        {
            if (designerName.Contains("beam", StringComparison.OrdinalIgnoreCase) ||
                designerName.Contains("laser", StringComparison.OrdinalIgnoreCase))
            {
                AppendBeamEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && LooksLikeGrenadeLinkedEntity(designerName))
            {
                AppendGrenadeEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && designerName.Contains("weapon_", StringComparison.OrdinalIgnoreCase))
            {
                AppendWeaponEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && designerName.Contains("particle", StringComparison.OrdinalIgnoreCase))
            {
                AppendParticleEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && designerName.Contains("sprite", StringComparison.OrdinalIgnoreCase))
            {
                AppendSpriteEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && designerName.Contains("flame", StringComparison.OrdinalIgnoreCase))
            {
                AppendFlameEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && designerName.Contains("rope", StringComparison.OrdinalIgnoreCase))
            {
                AppendRopeEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && designerName.Contains("trigger", StringComparison.OrdinalIgnoreCase))
            {
                AppendTriggerEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && designerName.Contains("ambient", StringComparison.OrdinalIgnoreCase))
            {
                AppendAmbientEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && designerName.Contains("chicken", StringComparison.OrdinalIgnoreCase))
            {
                AppendChickenEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && LooksLikePlayerPingEntity(designerName))
            {
                AppendPlayerPingEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && LooksLikePhysBoxEntity(designerName))
            {
                AppendPhysBoxEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && designerName.Contains("dogtags", StringComparison.OrdinalIgnoreCase))
            {
                AppendDogtagsEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && LooksLikePlantedC4Entity(designerName))
            {
                AppendPlantedC4EntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && designerName.Contains("hostage", StringComparison.OrdinalIgnoreCase))
            {
                AppendHostageEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && LooksLikeBreakableEntity(designerName))
            {
                AppendBreakableEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && LooksLikeInstructorEntity(designerName))
            {
                AppendInstructorEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && LooksLikeSceneEntity(designerName))
            {
                AppendSceneEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }

            if (!bucketOverflowed && LooksLikePointProximitySensorEntity(designerName))
            {
                AppendPointProximitySensorEntityRelations(entityInstance, ownerHandles, ref bucketOverflowed);
            }
        }
        catch
        {
            // Designer-specific attachment probing is optional hardening only.
        }
    }

    private static bool LooksLikeGrenadeLinkedEntity(string designerName)
    {
        return designerName.Contains("grenade", StringComparison.OrdinalIgnoreCase) ||
               designerName.Contains("projectile", StringComparison.OrdinalIgnoreCase) ||
               designerName.Contains("molotov", StringComparison.OrdinalIgnoreCase) ||
               designerName.Contains("incendiary", StringComparison.OrdinalIgnoreCase) ||
               designerName.Contains("flashbang", StringComparison.OrdinalIgnoreCase) ||
               designerName.Contains("decoy", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePlayerPingEntity(string designerName)
    {
        return designerName.Contains("player_ping", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePhysBoxEntity(string designerName)
    {
        return designerName.Contains("physbox", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePlantedC4Entity(string designerName)
    {
        return designerName.Contains("planted_c4", StringComparison.OrdinalIgnoreCase) ||
               designerName.Contains("plantedc4", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeBreakableEntity(string designerName)
    {
        return designerName.Contains("func_breakable", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeInstructorEntity(string designerName)
    {
        return designerName.Contains("instructor", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSceneEntity(string designerName)
    {
        return designerName.Contains("logic_choreographed_scene", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikePointProximitySensorEntity(string designerName)
    {
        return designerName.Contains("point_proximitysensor", StringComparison.OrdinalIgnoreCase);
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendBeamEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var beam = new CBeam(entityInstance.Handle);
        CollectRelatedEntityHandle(beam.EndEntity, ownerHandles, ref bucketOverflowed);
        if (bucketOverflowed)
        {
            return;
        }

        Span<CHandle<CBaseEntity>> attachEntities = beam.AttachEntity;
        for (int i = 0; i < attachEntities.Length; i++)
        {
            CollectRelatedEntityHandle(attachEntities[i], ownerHandles, ref bucketOverflowed);
            if (bucketOverflowed)
            {
                return;
            }
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendParticleEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var particleSystem = new CParticleSystem(entityInstance.Handle);
        Span<CHandle<CBaseEntity>> controlPointEntities = particleSystem.ControlPointEnts;
        for (int i = 0; i < controlPointEntities.Length; i++)
        {
            CollectRelatedEntityHandle(controlPointEntities[i], ownerHandles, ref bucketOverflowed);
            if (bucketOverflowed)
            {
                return;
            }
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendGrenadeEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var grenade = new CBaseGrenade(entityInstance.Handle);
        CollectRelatedEntityHandle(grenade.Thrower, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed)
        {
            CollectRelatedEntityHandle(grenade.OriginalThrower, ownerHandles, ref bucketOverflowed);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendWeaponEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var weapon = new CCSWeaponBase(entityInstance.Handle);
        CollectRelatedEntityHandle(weapon.PrevOwner, ownerHandles, ref bucketOverflowed);
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendRopeEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var rope = new CRopeKeyframe(entityInstance.Handle);
        if (rope.StartPointValid)
        {
            CollectRelatedEntityHandle(rope.StartPoint, ownerHandles, ref bucketOverflowed);
        }

        if (!bucketOverflowed && rope.EndPointValid)
        {
            CollectRelatedEntityHandle(rope.EndPoint, ownerHandles, ref bucketOverflowed);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendSpriteEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var sprite = new CSprite(entityInstance.Handle);
        CollectRelatedEntityHandle(sprite.AttachedToEntity, ownerHandles, ref bucketOverflowed);
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendFlameEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var entityFlame = new CEntityFlame(entityInstance.Handle);
        CollectRelatedEntityHandle(entityFlame.EntAttached, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed)
        {
            CollectRelatedEntityHandle(entityFlame.Attacker, ownerHandles, ref bucketOverflowed);
        }
    }

    private static void AppendTriggerEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var trigger = new CBaseTrigger(entityInstance.Handle);
        CollectRelatedEntityHandles(trigger.TouchingEntities, ownerHandles, ref bucketOverflowed);

        if (!bucketOverflowed && entityInstance.DesignerName.Contains("soundscape", StringComparison.OrdinalIgnoreCase))
        {
            var soundscapeTrigger = new CTriggerSoundscape(entityInstance.Handle);
            CollectRelatedEntityHandles(soundscapeTrigger.Spectators, ownerHandles, ref bucketOverflowed);
        }

        if (!bucketOverflowed && entityInstance.DesignerName.Contains("sos", StringComparison.OrdinalIgnoreCase))
        {
            var sndSosTrigger = new CTriggerSndSosOpvar(entityInstance.Handle);
            CollectRelatedEntityHandles(sndSosTrigger.TouchingPlayers, ownerHandles, ref bucketOverflowed);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendAmbientEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var ambientGeneric = new CAmbientGeneric(entityInstance.Handle);
        CollectRelatedEntityHandle(ambientGeneric.SoundSource, ownerHandles, ref bucketOverflowed);
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendChickenEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var chicken = new CChicken(entityInstance.Handle);
        CollectRelatedEntityHandle(chicken.Leader, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed)
        {
            CollectRelatedEntityHandle(chicken.FleeFrom, ownerHandles, ref bucketOverflowed);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendPlayerPingEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var playerPing = new CPlayerPing(entityInstance.Handle);
        CollectRelatedEntityHandle(playerPing.Player, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed)
        {
            CollectRelatedEntityHandle(playerPing.PingedEntity, ownerHandles, ref bucketOverflowed);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendPhysBoxEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var physBox = new CPhysBox(entityInstance.Handle);
        CollectRelatedEntityHandle(physBox.CarryingPlayer, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed)
        {
            CollectRelatedEntityHandle(physBox.PhysicsAttacker, ownerHandles, ref bucketOverflowed);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendDogtagsEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var dogtags = new CItemDogtags(entityInstance.Handle);
        CollectRelatedEntityHandle(dogtags.OwningPlayer, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed)
        {
            CollectRelatedEntityHandle(dogtags.KillingPlayer, ownerHandles, ref bucketOverflowed);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendPlantedC4EntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var plantedC4 = new CPlantedC4(entityInstance.Handle);
        CollectRelatedEntityHandle(plantedC4.BombDefuser, ownerHandles, ref bucketOverflowed);
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendHostageEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var hostage = new CHostage(entityInstance.Handle);
        CollectRelatedEntityHandle(hostage.Leader, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed)
        {
            CollectRelatedEntityHandle(hostage.LastLeader, ownerHandles, ref bucketOverflowed);
        }

        if (!bucketOverflowed)
        {
            CollectRelatedEntityHandle(hostage.HostageGrabber, ownerHandles, ref bucketOverflowed);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendBreakableEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var breakable = new CBreakable(entityInstance.Handle);
        CollectRelatedEntityHandle(breakable.Breaker, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed)
        {
            CollectRelatedEntityHandle(breakable.PhysicsAttacker, ownerHandles, ref bucketOverflowed);
        }
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendInstructorEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var instructorEvent = new CInstructorEventEntity(entityInstance.Handle);
        CollectRelatedEntityHandle(instructorEvent.TargetPlayer, ownerHandles, ref bucketOverflowed);
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendSceneEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var sceneEntity = new CSceneEntity(entityInstance.Handle);
        CollectRelatedEntityHandle(sceneEntity.HTarget1, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed) CollectRelatedEntityHandle(sceneEntity.HTarget2, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed) CollectRelatedEntityHandle(sceneEntity.HTarget3, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed) CollectRelatedEntityHandle(sceneEntity.HTarget4, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed) CollectRelatedEntityHandle(sceneEntity.HTarget5, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed) CollectRelatedEntityHandle(sceneEntity.HTarget6, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed) CollectRelatedEntityHandle(sceneEntity.HTarget7, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed) CollectRelatedEntityHandle(sceneEntity.HTarget8, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed) CollectRelatedEntityHandle(sceneEntity.Activator, ownerHandles, ref bucketOverflowed);
        if (!bucketOverflowed) CollectRelatedEntityHandles(sceneEntity.RemoveActorList, ownerHandles, ref bucketOverflowed);
    }

    [SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "Kept as instance method to keep designer-specific closure probes on a uniform helper surface.")]
    private void AppendPointProximitySensorEntityRelations(CEntityInstance entityInstance, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
    {
        var pointProximitySensor = new CPointProximitySensor(entityInstance.Handle);
        CollectRelatedEntityHandle(pointProximitySensor.TargetEntity, ownerHandles, ref bucketOverflowed);
    }

    private static void CollectRelatedEntityHandle<T>(CHandle<T> relatedHandle, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
        where T : NativeEntity
    {
        if (!relatedHandle.IsValid)
        {
            return;
        }

        if (!TryResolveLiveEntityHandleRaw(relatedHandle.Raw, out uint liveRelatedHandleRaw))
        {
            return;
        }

        AddUniqueBucketHandle(ownerHandles, liveRelatedHandleRaw, ref bucketOverflowed);
    }

    private static void CollectRelatedEntityHandles<T>(NetworkedVector<CHandle<T>> relatedHandles, OwnedEntityBucket ownerHandles, ref bool bucketOverflowed)
        where T : NativeEntity
    {
        int relatedCount = relatedHandles.Count;
        for (int i = 0; i < relatedCount; i++)
        {
            if (bucketOverflowed)
            {
                return;
            }

            if (!TryResolveLiveEntityHandleRaw(relatedHandles[i].Raw, out uint liveRelatedHandleRaw))
            {
                continue;
            }

            AddUniqueBucketHandle(ownerHandles, liveRelatedHandleRaw, ref bucketOverflowed);
        }
    }

    private void TryAddOwnedEntityRelation(uint ownerHandleRaw, uint entityHandleRaw, ref bool bucketOverflowed)
    {
        if (ownerHandleRaw == 0 || ownerHandleRaw == uint.MaxValue || ownerHandleRaw == entityHandleRaw)
        {
            return;
        }

        if (!_ownedEntityBuckets.TryGetValue(ownerHandleRaw, out OwnedEntityBucket? bucket))
        {
            bucket = new OwnedEntityBucket();
            _ownedEntityBuckets[ownerHandleRaw] = bucket;
        }

        AddOwnedEntityBucketHandle(bucket, ownerHandleRaw, entityHandleRaw, ref bucketOverflowed);
    }

    private void AddOwnedEntityBucketHandle(OwnedEntityBucket bucket, uint ownerHandleRaw, uint entityHandleRaw, ref bool bucketOverflowed)
    {
        int count = bucket.Count;
        for (int i = 0; i < count; i++)
        {
            if (bucket.RawHandles[i] == entityHandleRaw)
            {
                return;
            }
        }

        if (count >= bucket.RawHandles.Length)
        {
            if (bucket.RawHandles.Length >= MaxTrackedTransmitEntitiesPerTarget)
            {
                _lastOwnedEntityBucketOverflowOwnerHandleRaw = ownerHandleRaw;
                _lastOwnedEntityBucketOverflowEntityHandleRaw = entityHandleRaw;
                bucketOverflowed = true;
                return;
            }

            int newLength = Math.Min(MaxTrackedTransmitEntitiesPerTarget, bucket.RawHandles.Length * 2);
            Array.Resize(ref bucket.RawHandles, newLength);
        }

        bucket.RawHandles[count] = entityHandleRaw;
        bucket.Count = count + 1;
    }

    private static string DescribeTargetEntitySample(TargetTransmitEntities targetEntities, int sampleLimit)
    {
        int count = Math.Min(Math.Max(sampleLimit, 0), targetEntities.Count);
        if (count <= 0)
        {
            return "none";
        }

        string[] sample = new string[count];
        for (int i = 0; i < count; i++)
        {
            sample[i] = DescribeEntityHandle(targetEntities.RawHandles[i]);
        }

        return string.Join(", ", sample);
    }

    private static string DescribeOwnedRelationSample(uint ownerHandleRaw, uint entityHandleRaw)
    {
        if (ownerHandleRaw == 0 || entityHandleRaw == 0)
        {
            return "unavailable";
        }

        return $"{DescribeEntityHandle(ownerHandleRaw)} -> {DescribeEntityHandle(entityHandleRaw)}";
    }

    private static string DescribeEntityHandle(uint entityHandleRaw)
    {
        int entityIndex = (int)(entityHandleRaw & (Utilities.MaxEdicts - 1));
        if (entityIndex <= 0 || entityIndex >= Utilities.MaxEdicts)
        {
            return $"invalid[0x{entityHandleRaw:X8}]";
        }

        IntPtr? entityPointer = EntitySystem.GetEntityByHandle(entityHandleRaw);
        if (!entityPointer.HasValue || entityPointer.Value == IntPtr.Zero)
        {
            return $"missing[{entityIndex}|0x{entityHandleRaw:X8}]";
        }

        var entity = new CEntityInstance(entityPointer.Value);
        string designerName = entity.DesignerName;
        if (string.IsNullOrWhiteSpace(designerName))
        {
            designerName = "unknown";
        }

        return $"{designerName}[{entityIndex}|0x{entityHandleRaw:X8}]";
    }

    private void RecordTargetClosureOffenderSample(TargetTransmitEntities targetEntities, int sampleLimit)
    {
        int count = Math.Min(Math.Max(sampleLimit, 0), targetEntities.Count);
        for (int i = 0; i < count; i++)
        {
            RecordClosureOffenderHandle(targetEntities.RawHandles[i]);
        }
    }

    private void RecordClosureOffenderHandle(uint entityHandleRaw)
    {
        string offenderKey = GetClosureOffenderKey(entityHandleRaw);
        if (_closureOffenderCounts.TryGetValue(offenderKey, out int currentCount))
        {
            _closureOffenderCounts[offenderKey] = currentCount + 1;
            return;
        }

        _closureOffenderCounts[offenderKey] = 1;
    }

    private static string GetClosureOffenderKey(uint entityHandleRaw)
    {
        int entityIndex = (int)(entityHandleRaw & (Utilities.MaxEdicts - 1));
        if (entityIndex <= 0 || entityIndex >= Utilities.MaxEdicts)
        {
            return "invalid";
        }

        IntPtr? entityPointer = EntitySystem.GetEntityByHandle(entityHandleRaw);
        if (!entityPointer.HasValue || entityPointer.Value == IntPtr.Zero)
        {
            return "missing";
        }

        var entity = new CEntityInstance(entityPointer.Value);
        return string.IsNullOrWhiteSpace(entity.DesignerName) ? "unknown" : entity.DesignerName;
    }

    private string GetClosureOffenderSummary(int maxEntries)
    {
        if (_closureOffenderCounts.Count <= 0 || maxEntries <= 0)
        {
            return "none";
        }

        var orderedOffenders = _closureOffenderCounts
            .OrderByDescending(static pair => pair.Value)
            .ThenBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Take(maxEntries);

        return string.Join(", ", orderedOffenders.Select(static pair => $"{pair.Key} x{pair.Value}"));
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
