using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Utils;
using System.Diagnostics.CodeAnalysis;

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
        PrepareTargetTransmitEntitiesForRemoval(targetEntities, Server.TickCount);

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
            targetEntities.PawnHandleRaw = pawnHandleRaw;
            targetEntities.Count = 0;
            targetEntities.OwnedClosureTick = -1;
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
            if (targetEntities.RawHandles.Length >= MaxTrackedTransmitEntitiesPerTarget)
            {
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
            if (!ShouldKeepTargetEntityHandle(targetEntities, entityHandleRaw))
            {
                continue;
            }

            targetEntities.RawHandles[writeIndex++] = entityHandleRaw;
        }

        targetEntities.Count = writeIndex;
    }

    private void PrepareTargetTransmitEntitiesForRemoval(TargetTransmitEntities targetEntities, int nowTick)
    {
        bool appendedOwnedClosure = false;
        if (targetEntities.OwnedClosureTick != nowTick)
        {
            AppendOwnedEntityHandleClosure(targetEntities, targetEntities.ControllerHandleRaw, nowTick);
            targetEntities.OwnedClosureTick = nowTick;
            appendedOwnedClosure = true;
        }

        if (appendedOwnedClosure || targetEntities.SanitizeTick != nowTick)
        {
            SanitizeTargetEntityList(targetEntities);
            targetEntities.SanitizeTick = nowTick;
        }
    }

    private bool ShouldKeepTargetEntityHandle(TargetTransmitEntities targetEntities, uint entityHandleRaw)
    {
        if (!TryResolveEntityHandleIndexForTransmit(entityHandleRaw, out _))
        {
            return false;
        }

        if (entityHandleRaw == targetEntities.PawnHandleRaw)
        {
            return true;
        }

        return TryResolveOwnerChainToTarget(targetEntities, entityHandleRaw) ||
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

    /// <summary>
    /// Resolves the scene-graph parent of an entity by walking
    /// CBaseEntity -> CBodyComponent -> SceneNode -> PParent -> Owner.
    /// Returns false if any step in the chain is null or invalid.
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

            parentEntityHandleRaw = parentOwner.EntityHandle.Raw;
            int parentIndex = (int)(parentEntityHandleRaw & (Utilities.MaxEdicts - 1));
            return parentIndex > 0 && parentIndex < Utilities.MaxEdicts;
        }
        catch
        {
            return false;
        }
    }

    private void AppendOwnedEntityHandleClosure(TargetTransmitEntities targetEntities, uint extraOwnerHandleRaw, int nowTick)
    {
        if (!TryEnsureOwnedEntityBuckets(nowTick))
        {
            return;
        }

        if (extraOwnerHandleRaw != 0 && extraOwnerHandleRaw != uint.MaxValue)
        {
            AppendOwnedChildren(targetEntities, extraOwnerHandleRaw);
        }

        int readIndex = 0;
        while (readIndex < targetEntities.Count)
        {
            AppendOwnedChildren(targetEntities, targetEntities.RawHandles[readIndex++]);
        }
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
            _ownedEntityBuckets.Clear();

            foreach (CEntityInstance entityInstance in Utilities.GetAllEntities())
            {
                if (entityInstance == null || !entityInstance.IsValid)
                {
                    continue;
                }

                var entity = new CBaseEntity(entityInstance.Handle);
                if (!entity.IsValid)
                {
                    continue;
                }

                uint entityHandleRaw = entity.EntityHandle.Raw;
                int entityIndex = (int)(entityHandleRaw & (Utilities.MaxEdicts - 1));
                if (entityIndex <= 0 || entityIndex >= Utilities.MaxEdicts)
                {
                    continue;
                }

                var ownerHandle = entity.OwnerEntity;
                bool hasOwner = ownerHandle.IsValid;
                bool hasParent = TryResolveSceneParentEntityHandle(entityHandleRaw, out uint sceneParentHandleRaw);

                if (!hasOwner && !hasParent)
                {
                    continue;
                }

                // Bucket under OwnerEntity (gameplay ownership: weapons, etc.)
                if (hasOwner)
                {
                    uint ownerHandleRaw = ownerHandle.Raw;
                    if (ownerHandleRaw != 0 && ownerHandleRaw != entityHandleRaw)
                    {
                        if (!_ownedEntityBuckets.TryGetValue(ownerHandleRaw, out OwnedEntityBucket? ownerBucket))
                        {
                            ownerBucket = new OwnedEntityBucket();
                            _ownedEntityBuckets[ownerHandleRaw] = ownerBucket;
                        }

                        AddOwnedEntityBucketHandle(ownerBucket, entityHandleRaw);
                    }
                }

                // Bucket under scene-graph parent (spatial parenting: wearables, bone-attached cosmetics, etc.)
                if (hasParent && sceneParentHandleRaw != 0 && sceneParentHandleRaw != entityHandleRaw &&
                    (!hasOwner || sceneParentHandleRaw != ownerHandle.Raw))
                {
                    if (!_ownedEntityBuckets.TryGetValue(sceneParentHandleRaw, out OwnedEntityBucket? parentBucket))
                    {
                        parentBucket = new OwnedEntityBucket();
                        _ownedEntityBuckets[sceneParentHandleRaw] = parentBucket;
                    }

                    AddOwnedEntityBucketHandle(parentBucket, entityHandleRaw);
                }
            }

            _hasLoggedOwnedEntityScanError = false;
            _ownedEntityBucketsTick = nowTick;
            return true;
        }
        catch (Exception ex)
        {
            _ownedEntityBucketsTick = -1;

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

    private static void AddOwnedEntityBucketHandle(OwnedEntityBucket bucket, uint entityHandleRaw)
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
                return;
            }

            int newLength = Math.Min(MaxTrackedTransmitEntitiesPerTarget, bucket.RawHandles.Length * 2);
            Array.Resize(ref bucket.RawHandles, newLength);
        }

        bucket.RawHandles[count] = entityHandleRaw;
        bucket.Count = count + 1;
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
