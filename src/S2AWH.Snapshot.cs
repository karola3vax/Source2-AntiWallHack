using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;

namespace S2AWH;

/// <summary>
/// Snapshot building, bounds normalization, visibility cache rebuild, and player scanning.
/// </summary>
public partial class S2AWH
{
    private static bool IsLivePlayer(CCSPlayerController? player)
    {
        if (player == null || !player.IsValid || player.Connected != PlayerConnectedState.PlayerConnected)
        {
            return false;
        }

        return player.PawnIsAlive;
    }

    private void ClearVisibilityCache()
    {
        Array.Clear(_visibilityCache, 0, _visibilityCache.Length);
        Array.Clear(_revealHoldRows, 0, _revealHoldRows.Length);
        Array.Clear(_stableDecisionRows, 0, _stableDecisionRows.Length);
        Array.Clear(_visibleConfirmRows, 0, _visibleConfirmRows.Length);
        Array.Clear(_targetTransmitEntitiesCache, 0, _targetTransmitEntitiesCache.Length);
        Array.Clear(_snapshotStabilizeUntilTickBySlot, 0, _snapshotStabilizeUntilTickBySlot.Length);
        _roundStartGraceUntilTick = 0;
        _losEvaluator?.ClearCaches();
        _predictor?.ClearCaches();
        InvalidateLivePlayersCache();
        _cachedLivePlayers.Clear();
        _eligibleTargetsWithEntities.Clear();
        _ownedEntityBuckets.Clear();
        _ownedEntityRelationsByChild.Clear();
        _dirtyOwnedEntityHandles.Clear();
        _pendingOwnedEntityRescanUntilTick.Clear();
        _ownedEntityPeriodicResyncHandleSnapshot.Clear();
        _knownEntityHandles.Clear();
        _knownEntityHandleIndices.Clear();
        _staleKnownEntityHandleScratch.Clear();
        _knownEntityHandlesInitialized = false;
        _knownEntityBootstrapRetryUntilTick = -1;
        _ownedEntityPeriodicResyncCursor = 0;
        _ownedEntityPeriodicResyncInProgress = false;
        _sceneClosureVisitedNodes.Clear();
        _transmitMembershipByHandleScratch.Clear();
        ResetSceneParentSchemaCache();
        for (int slot = 0; slot < VisibilitySlotCapacity; slot++)
        {
            ClearViewerRayCountSlotState(slot);
        }
        Array.Clear(SnapshotTransforms, 0, SnapshotTransforms.Length);
        Array.Clear(SnapshotPawns, 0, SnapshotPawns.Length);
        ClearViewerRayCountOverlays();
        _viewerRayCounterTick = -1;
        _viewerRayCountsDisplayDirty = false;
        _staggeredViewerOffset = 0;
        _hasLoggedGlobalsNotReady = false;
        _hasLoggedPlayerScanError = false;
        _hasLoggedFilterEvaluationError = false;
        _hasLoggedWeaponSyncError = false;
        _hasLoggedOwnedEntityScanError = false;
        _hasLoggedEntityClosureCapError = false;
        _hasLoggedReverseReferenceAuditError = false;
        _hasLoggedCheckTransmitError = false;
        _hasLoggedOnTickError = false;
        _lastDebugCachePlayerCount = 0;
        ResetDebugWindowCounters();
    }

    private static bool IsNearWorldOrigin(float x, float y, float z)
    {
        return MathF.Abs(x) <= SnapshotZeroOriginEpsilon &&
               MathF.Abs(y) <= SnapshotZeroOriginEpsilon &&
               MathF.Abs(z) <= SnapshotZeroOriginEpsilon;
    }

    private static bool TryGetLocalBoundsCandidate(
        Vector minsWorldOrLocal,
        Vector maxsWorldOrLocal,
        float originX,
        float originY,
        float originZ,
        float referenceMinX,
        float referenceMinY,
        float referenceMinZ,
        float referenceMaxX,
        float referenceMaxY,
        float referenceMaxZ,
        out float outMinX,
        out float outMinY,
        out float outMinZ,
        out float outMaxX,
        out float outMaxY,
        out float outMaxZ)
    {
        outMinX = 0; outMinY = 0; outMinZ = 0; outMaxX = 0; outMaxY = 0; outMaxZ = 0;

        if (minsWorldOrLocal == null || maxsWorldOrLocal == null)
            return false;

        float rawMinX = minsWorldOrLocal.X;
        float rawMinY = minsWorldOrLocal.Y;
        float rawMinZ = minsWorldOrLocal.Z;
        float rawMaxX = maxsWorldOrLocal.X;
        float rawMaxY = maxsWorldOrLocal.Y;
        float rawMaxZ = maxsWorldOrLocal.Z;

        bool hasRawLocal = TryScoreLocalBoundsCandidate(
            rawMinX,
            rawMinY,
            rawMinZ,
            rawMaxX,
            rawMaxY,
            rawMaxZ,
            referenceMinX,
            referenceMinY,
            referenceMinZ,
            referenceMaxX,
            referenceMaxY,
            referenceMaxZ,
            out float rawLocalScore);

        bool hasWorldShifted = TryScoreLocalBoundsCandidate(
            rawMinX - originX,
            rawMinY - originY,
            rawMinZ - originZ,
            rawMaxX - originX,
            rawMaxY - originY,
            rawMaxZ - originZ,
            referenceMinX,
            referenceMinY,
            referenceMinZ,
            referenceMaxX,
            referenceMaxY,
            referenceMaxZ,
            out float worldShiftedScore);

        if (!hasRawLocal && !hasWorldShifted)
        {
            return false;
        }

        if (hasRawLocal && (!hasWorldShifted || rawLocalScore <= worldShiftedScore))
        {
            outMinX = rawMinX;
            outMinY = rawMinY;
            outMinZ = rawMinZ;
            outMaxX = rawMaxX;
            outMaxY = rawMaxY;
            outMaxZ = rawMaxZ;
            return true;
        }

        outMinX = rawMinX - originX;
        outMinY = rawMinY - originY;
        outMinZ = rawMinZ - originZ;
        outMaxX = rawMaxX - originX;
        outMaxY = rawMaxY - originY;
        outMaxZ = rawMaxZ - originZ;
        return true;
    }

    private static bool TryScoreLocalBoundsCandidate(
        float minX,
        float minY,
        float minZ,
        float maxX,
        float maxY,
        float maxZ,
        float referenceMinX,
        float referenceMinY,
        float referenceMinZ,
        float referenceMaxX,
        float referenceMaxY,
        float referenceMaxZ,
        out float score)
    {
        score = 0.0f;

        float extentX = maxX - minX;
        float extentY = maxY - minY;
        float extentZ = maxZ - minZ;
        if (extentX <= 0.0f || extentY <= 0.0f || extentZ <= 0.0f ||
            extentX > MaxBoundsExtentUnits || extentY > MaxBoundsExtentUnits || extentZ > MaxBoundsExtentUnits)
        {
            return false;
        }

        if (MathF.Abs(minX) > MaxLocalBoundsCoordinateUnits || MathF.Abs(maxX) > MaxLocalBoundsCoordinateUnits ||
            MathF.Abs(minY) > MaxLocalBoundsCoordinateUnits || MathF.Abs(maxY) > MaxLocalBoundsCoordinateUnits ||
            MathF.Abs(minZ) > MaxLocalBoundsCoordinateUnits || MathF.Abs(maxZ) > MaxLocalBoundsCoordinateUnits)
        {
            return false;
        }

        float centerX = (minX + maxX) * 0.5f;
        float centerY = (minY + maxY) * 0.5f;
        float centerZ = (minZ + maxZ) * 0.5f;
        if (MathF.Abs(centerX) > MaxLocalHorizontalCenterOffset ||
            MathF.Abs(centerY) > MaxLocalHorizontalCenterOffset ||
            centerZ < MinLocalVerticalCenter ||
            centerZ > MaxLocalVerticalCenter)
        {
            return false;
        }

        float referenceExtentX = referenceMaxX - referenceMinX;
        float referenceExtentY = referenceMaxY - referenceMinY;
        float referenceExtentZ = referenceMaxZ - referenceMinZ;
        float referenceCenterX = (referenceMinX + referenceMaxX) * 0.5f;
        float referenceCenterY = (referenceMinY + referenceMaxY) * 0.5f;
        float referenceCenterZ = (referenceMinZ + referenceMaxZ) * 0.5f;

        float centerDelta =
            MathF.Abs(centerX - referenceCenterX) +
            MathF.Abs(centerY - referenceCenterY) +
            MathF.Abs(centerZ - referenceCenterZ);
        float extentDelta =
            MathF.Abs(extentX - referenceExtentX) +
            MathF.Abs(extentY - referenceExtentY) +
            MathF.Abs(extentZ - referenceExtentZ);
        float containmentShrink =
            MathF.Max(0.0f, minX - referenceMinX) +
            MathF.Max(0.0f, minY - referenceMinY) +
            MathF.Max(0.0f, minZ - referenceMinZ) +
            MathF.Max(0.0f, referenceMaxX - maxX) +
            MathF.Max(0.0f, referenceMaxY - maxY) +
            MathF.Max(0.0f, referenceMaxZ - maxZ);
        if (containmentShrink > MaxBoundsContainmentShrinkUnits)
        {
            return false;
        }

        float absoluteCoordinatePenalty =
            MathF.Abs(minX) + MathF.Abs(minY) + MathF.Abs(minZ) +
            MathF.Abs(maxX) + MathF.Abs(maxY) + MathF.Abs(maxZ);

        score = (centerDelta * 4.0f) + extentDelta + (containmentShrink * 8.0f) + (absoluteCoordinatePenalty * 0.01f);
        return true;
    }

    private static int ConvertSecondsToTicks(float seconds)
    {
        if (seconds <= 0.0f)
        {
            return 0;
        }

        float tickInterval = Server.TickInterval;
        if (!float.IsFinite(tickInterval) || tickInterval <= 0.0f)
        {
            // Startup-safe fallback when server globals are not initialized yet.
            tickInterval = 1.0f / 64.0f;
        }

        return Math.Max(1, (int)Math.Ceiling(seconds / tickInterval));
    }

    private bool IsRoundStartGraceActive(int nowTick)
    {
        return nowTick < _roundStartGraceUntilTick;
    }

    private void InvalidateLivePlayersCache()
    {
        _cachedLivePlayersTick = -1;
        _cachedLivePlayersValid = false;
        Array.Clear(_liveSlotFlags, 0, _liveSlotFlags.Length);
        _entityHandleIndexCacheTick = -1;
        _entityHandleIndexCache.Clear();
        _ownedEntityBucketsTick = -1;
        _ownedEntityLastFullResyncTick = -1;
        _ownedEntityBucketsInitialized = false;
        _ownedEntityPeriodicResyncHandleSnapshot.Clear();
        _ownedEntityPeriodicResyncCursor = 0;
        _ownedEntityPeriodicResyncInProgress = false;
        _eligibleTargetsWithEntitiesTick = -1;
    }

    private void ResetDebugWindowCounters()
    {
        _ticksSinceLastTransmitReport = 0;
        _transmitCallbacksInWindow = 0;
        _transmitHiddenEntitiesInWindow = 0;
        _transmitFallbackChecksInWindow = 0;
        _transmitRemovalNoEffectInWindow = 0;
        _transmitFailOpenOwnedClosureInWindow = 0;
        _transmitFailOpenEntityClosureCapInWindow = 0;
        _transmitFailOpenQuarantineInWindow = 0;
        _transmitFailOpenReverseAuditInWindow = 0;
        _ownedEntityFullResyncsInWindow = 0;
        _ownedEntityDirtyEntityUpdatesInWindow = 0;
        _ownedEntityPostSpawnRescanMarksInWindow = 0;
        _ownedEntityPeriodicResyncBatchesInWindow = 0;
        _ownedEntityPeriodicResyncMarksInWindow = 0;
        _holdRefreshInWindow = 0;
        _holdHitKeepAliveInWindow = 0;
        _holdExpiredInWindow = 0;
        _unknownEvalInWindow = 0;
        _unknownStickyHitInWindow = 0;
        _unknownHoldHitInWindow = 0;
        _unknownFailOpenInWindow = 0;
        _unknownFromExceptionInWindow = 0;
        _closureOffenderCounts.Clear();
    }

    private bool RebuildVisibilityCacheSnapshot()
    {
        if (_transmitFilter == null)
        {
            return false;
        }

        int nowTick = Server.TickCount;
        if (!TryGetLivePlayers(nowTick, out var validPlayers))
        {
            return false;
        }

        var config = S2AWHState.Current;
        int playerCount = validPlayers.Count;
        if (playerCount > VisibilitySlotCapacity)
        {
            playerCount = VisibilitySlotCapacity;
        }

        // Live slots are rewritten below and dead slots are excluded by _liveSlotFlags/validPlayers.
        // Avoid full-array clears every rebuild tick to keep the snapshot pass cheaper.

        // Snapshot per-target metadata once per rebuild pass to avoid O(N^2) property reads.
        for (int i = 0; i < playerCount; i++)
        {
            var target = validPlayers[i];
            int slot = target.Slot;
            _snapshotTargetSlots[i] = slot;
            _snapshotTargetPawnHandles[i] = 0;
            _snapshotTargetStationary[i] = false;
            _snapshotTargetIsBot[i] = target.IsBot;
            _snapshotTargetTeams[i] = target.TeamNum;

            var targetCsPawn = target.PlayerPawn.Value;
            var targetPawnEntity = (CBasePlayerPawn?)targetCsPawn ?? target.Pawn.Value;
            if ((uint)slot < VisibilitySlotCapacity)
            {
                ref var t = ref SnapshotTransforms[slot];
                t = default;

                if (targetPawnEntity != null && targetPawnEntity.IsValid)
                {
                    var origin = targetPawnEntity.AbsOrigin;
                    if (origin == null)
                    {
                        SnapshotPawns[slot] = null;
                        continue;
                    }

                    t.OriginX = origin.X;
                    t.OriginY = origin.Y;
                    t.OriginZ = origin.Z;

                    if (nowTick < _snapshotStabilizeUntilTickBySlot[slot] &&
                        IsNearWorldOrigin(t.OriginX, t.OriginY, t.OriginZ))
                    {
                        SnapshotPawns[slot] = null;
                        continue;
                    }

                    SnapshotPawns[slot] = targetPawnEntity;
                    _snapshotTargetPawnHandles[i] = targetPawnEntity.EntityHandle.Raw;

                    var tVel = targetPawnEntity.AbsVelocity;
                    if (tVel != null)
                    {
                        t.VelocityX = tVel.X;
                        t.VelocityY = tVel.Y;
                        t.VelocityZ = tVel.Z;
                    }
                    else
                    {
                        t.VelocityX = 0.0f;
                        t.VelocityY = 0.0f;
                        t.VelocityZ = 0.0f;
                    }

                    _snapshotTargetStationary[i] =
                        (t.VelocityX * t.VelocityX + t.VelocityY * t.VelocityY + t.VelocityZ * t.VelocityZ) < StationarySpeedSqThreshold;

                    var collision = targetPawnEntity.Collision;
                    if (collision?.Mins == null || collision.Maxs == null)
                    {
                        SnapshotPawns[slot] = null;
                        _snapshotTargetPawnHandles[i] = 0;
                        continue;
                    }

                    var mins = collision.Mins;
                    var maxs = collision.Maxs;
                    float minsX = mins.X;
                    float minsY = mins.Y;
                    float minsZ = mins.Z;
                    float maxsX = maxs.X;
                    float maxsY = maxs.Y;
                    float maxsZ = maxs.Z;

                    float mergedMinX = minsX;
                    float mergedMinY = minsY;
                    float mergedMinZ = minsZ;
                    float mergedMaxX = maxsX;
                    float mergedMaxY = maxsY;
                    float mergedMaxZ = maxsZ;
                    float referenceMinX = minsX;
                    float referenceMinY = minsY;
                    float referenceMinZ = minsZ;
                    float referenceMaxX = maxsX;
                    float referenceMaxY = maxsY;
                    float referenceMaxZ = maxsZ;

                    var surroundingMins = collision.SurroundingMins;
                    var surroundingMaxs = collision.SurroundingMaxs;
                    if (TryGetLocalBoundsCandidate(
                            surroundingMins,
                            surroundingMaxs,
                            t.OriginX,
                            t.OriginY,
                            t.OriginZ,
                            referenceMinX,
                            referenceMinY,
                            referenceMinZ,
                            referenceMaxX,
                            referenceMaxY,
                            referenceMaxZ,
                            out float surroundingLocalMinX,
                            out float surroundingLocalMinY,
                            out float surroundingLocalMinZ,
                            out float surroundingLocalMaxX,
                            out float surroundingLocalMaxY,
                            out float surroundingLocalMaxZ))
                    {
                        mergedMinX = MathF.Min(mergedMinX, surroundingLocalMinX);
                        mergedMinY = MathF.Min(mergedMinY, surroundingLocalMinY);
                        mergedMinZ = MathF.Min(mergedMinZ, surroundingLocalMinZ);
                        mergedMaxX = MathF.Max(mergedMaxX, surroundingLocalMaxX);
                        mergedMaxY = MathF.Max(mergedMaxY, surroundingLocalMaxY);
                        mergedMaxZ = MathF.Max(mergedMaxZ, surroundingLocalMaxZ);
                    }

                    var specifiedSurroundingMins = collision.SpecifiedSurroundingMins;
                    var specifiedSurroundingMaxs = collision.SpecifiedSurroundingMaxs;
                    if (TryGetLocalBoundsCandidate(
                            specifiedSurroundingMins,
                            specifiedSurroundingMaxs,
                            t.OriginX,
                            t.OriginY,
                            t.OriginZ,
                            referenceMinX,
                            referenceMinY,
                            referenceMinZ,
                            referenceMaxX,
                            referenceMaxY,
                            referenceMaxZ,
                            out float specifiedLocalMinX,
                            out float specifiedLocalMinY,
                            out float specifiedLocalMinZ,
                            out float specifiedLocalMaxX,
                            out float specifiedLocalMaxY,
                            out float specifiedLocalMaxZ))
                    {
                        mergedMinX = MathF.Min(mergedMinX, specifiedLocalMinX);
                        mergedMinY = MathF.Min(mergedMinY, specifiedLocalMinY);
                        mergedMinZ = MathF.Min(mergedMinZ, specifiedLocalMinZ);
                        mergedMaxX = MathF.Max(mergedMaxX, specifiedLocalMaxX);
                        mergedMaxY = MathF.Max(mergedMaxY, specifiedLocalMaxY);
                        mergedMaxZ = MathF.Max(mergedMaxZ, specifiedLocalMaxZ);
                    }

                    {
                        var targetModelEntity = (CBaseModelEntity)targetPawnEntity;
                        float hitboxExpandRadius = targetModelEntity.CHitboxComponent.BoundsExpandRadius;
                        if (hitboxExpandRadius > 0.0f && hitboxExpandRadius <= 32.0f)
                        {
                            mergedMinX -= hitboxExpandRadius;
                            mergedMinY -= hitboxExpandRadius;
                            mergedMinZ -= hitboxExpandRadius;
                            mergedMaxX += hitboxExpandRadius;
                            mergedMaxY += hitboxExpandRadius;
                            mergedMaxZ += hitboxExpandRadius;
                        }
                    }

                    minsX = mergedMinX;
                    minsY = mergedMinY;
                    minsZ = mergedMinZ;
                    maxsX = mergedMaxX;
                    maxsY = mergedMaxY;
                    maxsZ = mergedMaxZ;

                    t.MinsX = minsX;
                    t.MinsY = minsY;
                    t.MinsZ = minsZ;
                    t.MaxsX = maxsX;
                    t.MaxsY = maxsY;
                    t.MaxsZ = maxsZ;
                    t.CenterX = (minsX + maxsX) * 0.5f;
                    t.CenterY = (minsY + maxsY) * 0.5f;
                    t.CenterZ = (minsZ + maxsZ) * 0.5f;

                    var viewOffset = targetPawnEntity.ViewOffset;
                    if (viewOffset != null)
                    {
                        t.ViewOffsetZ = viewOffset.Z;
                    }
                    else
                    {
                        t.ViewOffsetZ = 64.0f;
                    }

                    t.DuckAmount = 0.0f;
                    t.IsDucked = false;
                    t.IsDucking = false;
                    t.DuckReleasedThisTick = false;

                    t.JumpPressedThisTick = false;
                    t.JumpApexPending = false;
                    t.IsGrounded = false;
                    t.OnGroundLastTick = false;
                    t.JumpTimeMsecs = 0;
                    t.HeightAtJumpStart = 0.0f;
                    t.MaxJumpHeightThisJump = 0.0f;

                    if (targetPawnEntity.MovementServices is CCSPlayer_MovementServices movementServices)
                    {
                        t.DuckAmount = movementServices.DuckAmount;
                        t.IsDucked = movementServices.Ducked;
                        t.IsDucking = movementServices.Ducking;
                        ulong duckMask = (ulong)InputBitMask_t.IN_DUCK;
                        ulong queuedDownMask = movementServices.QueuedButtonDownMask;
                        ulong previousDownMask = movementServices.ButtonDownMaskPrev;
                        ulong changeMask = movementServices.QueuedButtonChangeMask;
                        bool duckButtonDown = (queuedDownMask & duckMask) != 0;
                        t.DuckReleasedThisTick =
                            (previousDownMask & duckMask) != 0 &&
                            (changeMask & duckMask) != 0 &&
                            !duckButtonDown;

                        ulong jumpMask = (ulong)InputBitMask_t.IN_JUMP;
                        bool jumpButtonDown = (queuedDownMask & jumpMask) != 0;
                        t.JumpPressedThisTick =
                            (changeMask & jumpMask) != 0 &&
                            jumpButtonDown;
                        t.JumpApexPending = movementServices.JumpApexPending;
                        t.IsGrounded = targetPawnEntity.GroundEntity.IsValid;
                        t.OnGroundLastTick = targetCsPawn?.OnGroundLastTick ?? false;
                        t.JumpTimeMsecs = movementServices.JumpTimeMsecs;
                        t.HeightAtJumpStart = movementServices.HeightAtJumpStart;
                        t.MaxJumpHeightThisJump = movementServices.MaxJumpHeightThisJump;
                    }

                    t.EyeAnglesPitch = 0.0f;
                    t.EyeAnglesYaw = 0.0f;
                    t.FovNormalX = 1.0f;
                    t.FovNormalY = 0.0f;
                    t.FovNormalZ = 0.0f;

                    var angles = targetCsPawn?.EyeAngles;
                    if (angles != null)
                    {
                        t.EyeAnglesPitch = angles.X;
                        t.EyeAnglesYaw = angles.Y;

                        float pitchRad = angles.X * MathF.PI / 180.0f;
                        float yawRad = angles.Y * MathF.PI / 180.0f;
                        (float sinPitch, float cosPitch) = MathF.SinCos(pitchRad);
                        (float sinYaw, float cosYaw) = MathF.SinCos(yawRad);

                        t.FovNormalX = cosPitch * cosYaw;
                        t.FovNormalY = cosPitch * sinYaw;
                        t.FovNormalZ = -sinPitch;
                    }

                    // Fallback eye position: Origin + ViewOffset
                    t.EyeX = t.OriginX;
                    t.EyeY = t.OriginY;
                    t.EyeZ = t.OriginZ + t.ViewOffsetZ;
                    t.IsValid = true;
                }
                else
                {
                    SnapshotPawns[slot] = null;
                }
            }
        }

        // Count eligible viewers (respects BotsDoLOS setting).
        int eligibleViewerCount = 0;
        for (int i = 0; i < playerCount; i++)
        {
            if (!validPlayers[i].IsBot || config.Visibility.BotsDoLOS)
            {
                eligibleViewerCount++;
            }
        }

        if (eligibleViewerCount == 0)
        {
            _staggeredViewerOffset = 0;
            return true;
        }

        // Staggered batching: spread viewers across UpdateFrequencyTicks.
        // For 20 eligible viewers with UpdateFrequencyTicks=2: process 10 per tick.
        int updateTicks = Math.Max(1, config.Core.UpdateFrequencyTicks);
        int viewersPerTick = (eligibleViewerCount + updateTicks - 1) / updateTicks;

        if (_staggeredViewerOffset >= eligibleViewerCount)
        {
            _staggeredViewerOffset = 0;
        }

        int processedEligible = 0;
        int currentEligibleIndex = 0;

        for (int viewerIndex = 0; viewerIndex < playerCount && processedEligible < viewersPerTick; viewerIndex++)
        {
            var viewer = validPlayers[viewerIndex];
            bool viewerIsBot = viewer.IsBot;
            if (viewerIsBot && !config.Visibility.BotsDoLOS)
            {
                continue;
            }

            if (currentEligibleIndex < _staggeredViewerOffset)
            {
                currentEligibleIndex++;
                continue;
            }

            currentEligibleIndex++;
            processedEligible++;

            int viewerSlot = viewer.Slot;
            if ((uint)viewerSlot >= VisibilitySlotCapacity)
            {
                continue; // invalid slot
            }

            ViewerVisibilityRow visibilityByTargetSlot = _visibilityCache[viewerSlot] ??= new ViewerVisibilityRow();

            // Stationary-Visible optimization: if the viewer isn't moving, we can reuse
            // cached Visible decisions for targets that also aren't moving. This skips
            // expensive LOS evaluation entirely for stationary pairs (buy time, holds).
            // Safe: keeping Visible is the optimistic direction (never hides visible players).
            ref var viewerSnapshot = ref SnapshotTransforms[viewerSlot];
            bool viewerStationary =
                viewerSnapshot.IsValid &&
                (viewerSnapshot.VelocityX * viewerSnapshot.VelocityX +
                 viewerSnapshot.VelocityY * viewerSnapshot.VelocityY +
                 viewerSnapshot.VelocityZ * viewerSnapshot.VelocityZ) < StationarySpeedSqThreshold;
            int viewerTeam = viewer.TeamNum;
            _viewerTargetCounts[viewerSlot] = 0;

            for (int targetIndex = 0; targetIndex < playerCount; targetIndex++)
            {
                if (targetIndex == viewerIndex)
                {
                    continue;
                }

                int targetSlot = _snapshotTargetSlots[targetIndex];
                if ((uint)targetSlot < (uint)visibilityByTargetSlot.Decisions.Length)
                {
                    uint currentPawnHandle = _snapshotTargetPawnHandles[targetIndex];

                    // Always-transmit fast paths that do not require LOS/predictor work.
                    if (!config.Visibility.IncludeBots && _snapshotTargetIsBot[targetIndex])
                    {
                        visibilityByTargetSlot.Decisions[targetSlot] = true;
                        visibilityByTargetSlot.Known[targetSlot] = true;
                        visibilityByTargetSlot.PawnHandles[targetSlot] = currentPawnHandle;
                        visibilityByTargetSlot.EvalTicks[targetSlot] = nowTick;
                        continue;
                    }

                    if (!config.Visibility.IncludeTeammates && _snapshotTargetTeams[targetIndex] == viewerTeam)
                    {
                        visibilityByTargetSlot.Decisions[targetSlot] = true;
                        visibilityByTargetSlot.Known[targetSlot] = true;
                        visibilityByTargetSlot.PawnHandles[targetSlot] = currentPawnHandle;
                        visibilityByTargetSlot.EvalTicks[targetSlot] = nowTick;
                        continue;
                    }

                    // Reuse Visible decision if both are stationary and same pawn.
                    // PawnHandle guard prevents stale reuse across player slot changes.
                    if (viewerStationary &&
                        visibilityByTargetSlot.Known[targetSlot] &&
                        visibilityByTargetSlot.Decisions[targetSlot] &&
                        currentPawnHandle != 0 &&
                        visibilityByTargetSlot.PawnHandles[targetSlot] == currentPawnHandle)
                    {
                        if (_snapshotTargetStationary[targetIndex])
                        {
                            continue; // Reuse cached Visible - both stationary, same pawn.
                        }
                    }

                    if (visibilityByTargetSlot.Known[targetSlot] &&
                        visibilityByTargetSlot.EvalTicks[targetSlot] == nowTick &&
                        currentPawnHandle != 0 &&
                        visibilityByTargetSlot.PawnHandles[targetSlot] == currentPawnHandle)
                    {
                        continue; // Reuse decision already computed earlier this tick (e.g. transmit fallback path).
                    }

                    VisibilityDecision visibilityDecision = EvaluateVisibilitySafe(
                        viewerSlot,
                        targetSlot,
                        viewerIsBot,
                        config,
                        nowTick,
                        "cache rebuild");
                    _viewerTargetCounts[viewerSlot]++;
                    visibilityByTargetSlot.Decisions[targetSlot] = ResolveTransmitWithMemory(viewerSlot, targetSlot, visibilityDecision, nowTick);
                    visibilityByTargetSlot.Known[targetSlot] = true;
                    visibilityByTargetSlot.PawnHandles[targetSlot] = currentPawnHandle;
                    visibilityByTargetSlot.EvalTicks[targetSlot] = nowTick;
                }
            }
        }

        // Check if this batch completed a full cycle through all viewers.
        bool isFullCycleComplete = (_staggeredViewerOffset + processedEligible) >= eligibleViewerCount;

        if (isFullCycleComplete)
        {
            // Full cycle complete: purge stale viewer rows from cached state.
            PurgeInactiveViewerRows();
            _staggeredViewerOffset = 0;
        }
        else
        {
            _staggeredViewerOffset += processedEligible;
        }

        if (isFullCycleComplete && _collectDebugCounters && _lastDebugCachePlayerCount != validPlayers.Count)
        {
            DebugLog(
                "Player count changed.",
                $"{validPlayers.Count} alive players, checking {viewersPerTick} per tick.",
                "Workload is spread evenly across ticks."
            );
            _lastDebugCachePlayerCount = validPlayers.Count;
        }

        return true;
    }

    private VisibilityDecision EvaluateVisibilitySafe(
        int viewerSlot,
        int targetSlot,
        bool viewerIsBot,
        S2AWHConfig config,
        int nowTick,
        string phase)
    {
        if (_transmitFilter == null)
        {
            return new VisibilityDecision(VisibilityEval.UnknownTransient);
        }

        try
        {
            return _transmitFilter.EvaluateVisibility(
                viewerSlot,
                targetSlot,
                viewerIsBot,
                nowTick,
                config,
                SnapshotTransforms,
                SnapshotPawns);
        }
        catch (Exception ex)
        {
            if (_collectDebugCounters)
            {
                _unknownFromExceptionInWindow++;
            }

            if (!_hasLoggedFilterEvaluationError)
            {
                WarnLog(
                    "A visibility check had an error.",
                    "A temporary issue occurred while checking if a player is visible.",
                    "S2AWH handled it safely - no crash, no impact."
                );
                DebugLog(
                    "Visibility error detail.",
                    $"Phase: {phase}. Pair: {viewerSlot}->{targetSlot}. Error: {ex.Message}",
                    "This message only shows once."
                );
                _hasLoggedFilterEvaluationError = true;
            }

            return new VisibilityDecision(VisibilityEval.UnknownTransient);
        }
    }

    private bool TryGetLivePlayers(int nowTick, out List<CCSPlayerController> livePlayers)
    {
        if (_cachedLivePlayersTick == nowTick)
        {
            livePlayers = _cachedLivePlayers;
            return _cachedLivePlayersValid;
        }

        _cachedLivePlayersTick = nowTick;
        _cachedLivePlayers.Clear();
        _cachedLivePlayersValid = false;

        try
        {
            Array.Clear(_liveSlotFlags, 0, _liveSlotFlags.Length);

            int maxPlayers = Math.Clamp(Server.MaxPlayers, 0, VisibilitySlotCapacity - 1);
            for (int slot = 0; slot < maxPlayers; slot++)
            {
                var player = Utilities.GetPlayerFromSlot(slot);
                if (IsLivePlayer(player))
                {
                    _cachedLivePlayers.Add(player!);
                    _liveSlotFlags[slot] = true;
                }
            }

            _hasLoggedGlobalsNotReady = false;
            _hasLoggedPlayerScanError = false;
            _cachedLivePlayersValid = true;
            livePlayers = _cachedLivePlayers;
            return _cachedLivePlayersValid;
        }
        catch (NativeException ex) when (ex.Message?.Contains("Global Variables not initialized yet.", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (!_hasLoggedGlobalsNotReady)
            {
                WarnLog(
                    "Server is still loading.",
                    "Player data isn't available yet.",
                    "S2AWH will start automatically once the server is ready."
                );
                _hasLoggedGlobalsNotReady = true;
            }

            _cachedLivePlayers.Clear();
            _cachedLivePlayersValid = false;
            Array.Clear(_liveSlotFlags, 0, _liveSlotFlags.Length);
            livePlayers = _cachedLivePlayers;
            return false;
        }
        catch (Exception ex)
        {
            if (!_hasLoggedPlayerScanError)
            {
                WarnLog(
                    "Could not read player list.",
                    "A temporary issue prevented reading who is alive.",
                    "S2AWH skipped this tick and will retry."
                );
                DebugLog(
                    "Player scan error detail.",
                    $"Error: {ex.Message}",
                    "This message only shows once."
                );
                _hasLoggedPlayerScanError = true;
            }

            _cachedLivePlayers.Clear();
            _cachedLivePlayersValid = false;
            Array.Clear(_liveSlotFlags, 0, _liveSlotFlags.Length);
            livePlayers = _cachedLivePlayers;
            return false;
        }
    }
}
