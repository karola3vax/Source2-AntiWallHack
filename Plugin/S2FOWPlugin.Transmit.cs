using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using S2FOW.Config;
using S2FOW.Core;
using S2FOW.Util;

namespace S2FOW;

public partial class S2FOWPlugin
{
    // CheckTransmit hot path.
    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        if (!_initialized || _visibilityManager == null || _raycastEngine == null)
            return;

        if (!Config.General.Enabled)
            return;

        var players = Utilities.GetPlayers();
        ClearPendingNoInterp(players);
        _perfMonitor?.BeginFrame();
        _raycastEngine.ResetFrameCounter();
        int currentTick = Server.TickCount;

        // Adaptive budget: scale with alive player count.
        int frameBudget = Config.Performance.MaxRaycastsPerFrame;
        if (Config.Performance.AdaptiveBudgetEnabled)
        {
            int aliveCount = 0;
            for (int i = 0; i < players.Count; i++)
            {
                var p = players[i];
                if (p.IsValid && p.PawnIsAlive)
                    aliveCount++;
            }
            int scaledBudget = Config.Performance.BaseBudgetPerPlayer * aliveCount;
            frameBudget = Math.Min(scaledBudget, Config.Performance.MaxAdaptiveBudget);
            _raycastEngine.SetFrameBudget(frameBudget);
        }

        _visibilityManager.SetFrameTick(currentTick);
        _visibilityManager.BeginFrame();
        Array.Clear(_hiddenPairs);

        if (_visibilityManager.ShouldBypassVisibilityWorkForCurrentPhase())
        {
            _debugAabbRenderer?.Clear();
            _perfMonitor?.EndFrame(0);
            return;
        }

        // Build player snapshots once per frame
        _playerStateCache!.BuildSnapshots(players, currentTick);

        // Update projectile positions once per frame
        if (Config.AntiWallhack.BlockGrenadeESP)
            _projectileTracker?.UpdatePositions();

        var snapshots = _playerStateCache.Snapshots;
        var activeSlots = _playerStateCache.ActiveSlots;
        var terroristSlots = _playerStateCache.TerroristSlots;
        var counterTerroristSlots = _playerStateCache.CounterTerroristSlots;
        Span<int> syntheticBotObserverSlots = stackalloc int[FowConstants.MaxSlots];
        int syntheticBotObserverCount = 0;
        for (int i = 0; i < activeSlots.Length && syntheticBotObserverCount < syntheticBotObserverSlots.Length; i++)
        {
            int slot = activeSlots[i];
            ref readonly var botObserver = ref snapshots[slot];
            if (!botObserver.IsValid || !botObserver.IsAlive || !botObserver.IsBot)
                continue;

            if (botObserver.Team != CsTeam.Terrorist && botObserver.Team != CsTeam.CounterTerrorist)
                continue;

            syntheticBotObserverSlots[syntheticBotObserverCount++] = slot;
        }
        _playerStateCache.CollectUnresolvedEntitiesToHide(
            _unresolvedEntitiesToHide,
            currentTick,
            Config.General.SecurityProfile == SecurityProfile.Strict);

        bool shouldBlockBombRadarEsp = ShouldBlockBombRadarESP();
        bool shouldHidePlantedBombEntity = ShouldHidePlantedBombEntity();
        bool hasTrackedPlantedC4 = false;
        CPlantedC4? trackedPlantedC4 = null;
        if ((shouldBlockBombRadarEsp || shouldHidePlantedBombEntity) &&
            TryGetTrackedPlantedC4(out var resolvedTrackedPlantedC4))
        {
            hasTrackedPlantedC4 = true;
            trackedPlantedC4 = resolvedTrackedPlantedC4;
        }

        bool anyHiddenPlayerPairs = false;

        // Per-observer budget fairness: count eligible observers first so we
        // can distribute the frame ray budget evenly. Without this, the first
        // few observers in slot order can consume the entire budget and starve
        // later observers, leaving them with only cache fallback.
        bool perObserverFairness = Config.Performance.PerObserverBudgetFairnessEnabled && frameBudget > 0;
        int eligibleObserverCount = 0;
        if (perObserverFairness)
        {
            foreach ((CCheckTransmitInfo _, CCSPlayerController? ctrl) in infoList)
            {
                if (ctrl == null || !FowConstants.IsValidSlot(ctrl.Slot))
                    continue;
                ref readonly var snap = ref snapshots[ctrl.Slot];
                if (snap.IsValid && snap.IsAlive && !snap.IsBot &&
                    (snap.Team == CsTeam.Terrorist || snap.Team == CsTeam.CounterTerrorist))
                    eligibleObserverCount++;
            }

            eligibleObserverCount += syntheticBotObserverCount;
        }
        int processedObservers = 0;

        // Process each observer
        foreach ((CCheckTransmitInfo info, CCSPlayerController? controller) in infoList)
        {
            if (controller == null)
                continue;

            int observerSlot = controller.Slot;
            if (!FowConstants.IsValidSlot(observerSlot))
                continue;

            ref readonly var observer = ref snapshots[observerSlot];
            if (!observer.IsValid || !observer.IsAlive)
                continue;

            // Skip spectators (team None/Spectator). They should see everything.
            if (observer.Team != CsTeam.Terrorist && observer.Team != CsTeam.CounterTerrorist)
                continue;

            // Skip bot observers. They do not have wallhack clients.
            if (observer.IsBot)
                continue;

            // Per-observer budget fairness: set the engine budget ceiling so this
            // observer can use at most its fair share plus any leftover from previous.
            if (perObserverFairness && eligibleObserverCount > 0)
            {
                int raysUsedSoFar = _raycastEngine.RaycastsThisFrame;
                int remainingBudget = frameBudget - raysUsedSoFar;
                int remainingObservers = eligibleObserverCount - processedObservers;
                int fairShare = remainingBudget / Math.Max(1, remainingObservers);
                int minShare = (int)(frameBudget * Config.Performance.MinObserverBudgetShare);
                int observerBudget = Math.Max(fairShare, minShare);
                int budgetCeiling = Math.Min(raysUsedSoFar + observerBudget, frameBudget);
                _raycastEngine.SetFrameBudget(budgetCeiling);
                processedObservers++;
            }

            int observerPairBase = observerSlot * FowConstants.MaxSlots;
            bool observerHasHiddenEnemy = false;
            bool observerHasDeadEnemy = false;

            for (int i = 0; i < _unresolvedEntitiesToHide.Count; i++)
            {
                int unresolvedIdx = _unresolvedEntitiesToHide[i];
                if (FowConstants.IsValidEntityIndex(unresolvedIdx))
                    info.TransmitEntities.Remove(unresolvedIdx);
            }

            if (Config.Debug.ShowTargetPoints && _debugAabbRenderer != null)
                _debugAabbRenderer.RemoveOtherObserverPointEntities(info, observerSlot);

            // Check each enemy target, including recently dead enemies that may
            // still be force-visible for a short post-death grace window.
            for (int i = 0; i < activeSlots.Length; i++)
            {
                int targetSlot = activeSlots[i];
                ref readonly var target = ref snapshots[targetSlot];

                if (!target.IsValid || target.Slot == observerSlot || target.Team == observer.Team)
                    continue;

                if (!target.IsAlive)
                    observerHasDeadEnemy = true;

                bool shouldTransmit = target.IsAlive
                    ? _visibilityManager.ShouldTransmit(in observer, in target, currentTick)
                    : _visibilityManager.ShouldTransmitRecentlyDead(target.Slot, currentTick);

                if (!shouldTransmit)
                {
                    _hiddenPairs[observerPairBase + targetSlot] = true;
                    observerHasHiddenEnemy = true;
                    anyHiddenPlayerPairs = true;
                    RemoveHiddenTargetEntities(info, targetSlot, in observer, currentTick);
                }
            }

            if (shouldHidePlantedBombEntity &&
                hasTrackedPlantedC4 &&
                trackedPlantedC4 != null &&
                ShouldHidePlantedC4FromObserver(observerSlot, snapshots, trackedPlantedC4, currentTick))
            {
                int c4Index = (int)trackedPlantedC4.Index;
                if (FowConstants.IsValidEntityIndex(c4Index))
                    info.TransmitEntities.Remove(c4Index);

                var c4EffectEntity = trackedPlantedC4.EffectEntity.Value;
                int c4EffectIndex = c4EffectEntity != null && c4EffectEntity.IsValid ? (int)c4EffectEntity.Index : 0;
                if (FowConstants.IsValidEntityIndex(c4EffectIndex))
                    info.TransmitEntities.Remove(c4EffectIndex);
            }

            // Remove projectiles owned by hidden enemies
            if (Config.AntiWallhack.BlockGrenadeESP &&
                _projectileTracker != null &&
                (observerHasHiddenEnemy || observerHasDeadEnemy))
            {
                float revealDistSqr = Config.AntiWallhack.GrenadeRevealDistance *
                                      Config.AntiWallhack.GrenadeRevealDistance;

                using var projEnum = _projectileTracker.GetActiveProjectiles();
                while (projEnum.MoveNext())
                {
                    var proj = projEnum.Current;
                    int projEntityIndex = proj.Key;
                    int ownerSlot = proj.Value;

                    // Only hide projectiles from enemy team
                    if (!FowConstants.IsValidSlot(ownerSlot))
                        continue;

                    ref readonly var ownerSnap = ref snapshots[ownerSlot];
                    if (!ownerSnap.IsValid || ownerSnap.Team == observer.Team)
                        continue;

                    // Check if the owner is hidden from this observer
                    if (!ownerSnap.IsAlive ||
                        _hiddenPairs[observerPairBase + ownerSlot])
                    {
                        // Proximity override: don't hide if projectile is close to observer
                        if (revealDistSqr > 0.0f &&
                            _projectileTracker.TryGetProjectilePosition(projEntityIndex, out float px, out float py, out float pz))
                        {
                            float distSqr = Util.VectorMath.DistanceSquared(
                                observer.EyePosX, observer.EyePosY, observer.EyePosZ,
                                px, py, pz);

                            if (distSqr <= revealDistSqr)
                                continue; // Close enough to observer - reveal it
                        }

                        if (FowConstants.IsValidEntityIndex(projEntityIndex))
                            info.TransmitEntities.Remove(projEntityIndex);
                    }
                }
            }

            // Remove bullet impact decals/effects from hidden enemies
            if (Config.AntiWallhack.BlockBulletImpactESP &&
                _impactTracker != null &&
                observerHasHiddenEnemy)
            {
                using var impactEnum = _impactTracker.GetActiveImpactEntities();
                while (impactEnum.MoveNext())
                {
                    var impact = impactEnum.Current;
                    int impactEntityIndex = impact.Key;
                    int shooterSlot = impact.Value;

                    if (!FowConstants.IsValidSlot(shooterSlot))
                        continue;

                    ref readonly var shooterSnap = ref snapshots[shooterSlot];
                    if (!shooterSnap.IsValid || shooterSnap.Team == observer.Team)
                        continue;

                    // If the shooter is hidden from this observer, hide their impact decals too
                    if (_hiddenPairs[observerPairBase + shooterSlot])
                    {
                        if (FowConstants.IsValidEntityIndex(impactEntityIndex))
                            info.TransmitEntities.Remove(impactEntityIndex);
                    }
                }
            }
        }

        // Stress-test pass for bot observers: run the same visibility solver
        // without trying to hide or transmit entities to a client.
        for (int i = 0; i < syntheticBotObserverCount; i++)
        {
            int observerSlot = syntheticBotObserverSlots[i];
            ref readonly var observer = ref snapshots[observerSlot];

            if (perObserverFairness && eligibleObserverCount > 0)
            {
                int raysUsedSoFar = _raycastEngine.RaycastsThisFrame;
                int remainingBudget = frameBudget - raysUsedSoFar;
                int remainingObservers = eligibleObserverCount - processedObservers;
                int fairShare = remainingBudget / Math.Max(1, remainingObservers);
                int minShare = (int)(frameBudget * Config.Performance.MinObserverBudgetShare);
                int observerBudget = Math.Max(fairShare, minShare);
                int budgetCeiling = Math.Min(raysUsedSoFar + observerBudget, frameBudget);
                _raycastEngine.SetFrameBudget(budgetCeiling);
                processedObservers++;
            }

            for (int t = 0; t < activeSlots.Length; t++)
            {
                int targetSlot = activeSlots[t];
                ref readonly var target = ref snapshots[targetSlot];

                if (!target.IsValid || target.Slot == observerSlot || target.Team == observer.Team)
                    continue;

                if (target.IsAlive)
                {
                    _visibilityManager.ShouldTransmit(in observer, in target, currentTick);
                }
                else
                {
                    _visibilityManager.ShouldTransmitRecentlyDead(target.Slot, currentTick);
                }
            }
        }

        // Restore full frame budget for post-observer work (bomb radar, C4 traces).
        if (perObserverFairness)
            _raycastEngine.SetFrameBudget(frameBudget);

        // Scrub radar spotted state for hidden enemies
        if (Config.AntiWallhack.BlockRadarESP && anyHiddenPlayerPairs)
        {
            _spottedStateScrubber?.ScrubPlayerSpottedState(
                players, snapshots, terroristSlots, counterTerroristSlots,
                (obsSlot, tgtSlot) => _hiddenPairs[obsSlot * FowConstants.MaxSlots + tgtSlot]);
        }

        if (shouldBlockBombRadarEsp && hasTrackedPlantedC4)
        {
            _spottedStateScrubber?.BlockBombRadarESP(
                players,
                snapshots,
                (slot, c4Entity) => CanObserverSeeTrackedC4(slot, c4Entity, currentTick));
        }

        UpdateDebugOutputs(snapshots, players, currentTick);
        ForceFlexStateResync(players, currentTick);
        _perfMonitor?.EndFrame(_raycastEngine.RaycastsThisFrame);
    }

    /// <summary>
    /// Removes the hidden target's pawn-linked entities while intentionally leaving
    /// the persistent <see cref="CCSPlayerController"/> transmitted.
    ///
    /// Counter-Strike 2 expects controller entities to remain present in the client
    /// entity list; removing them can trigger CopyExistingEntity crashes during
    /// delta updates. The controller only carries scoreboard-style metadata and no
    /// reliable world-space position, so keeping it transmitted is the safer
    /// compatibility trade-off without reintroducing wallhack-useful coordinates.
    /// </summary>
    private void RemoveHiddenTargetEntities(
        CCheckTransmitInfo info,
        int targetSlot,
        in Models.PlayerSnapshot observer,
        int currentTick)
    {
        int assocCount = _playerStateCache?.Snapshots[targetSlot].AssociatedEntityCount ?? 0;
        for (int entityOffset = 0; entityOffset < assocCount; entityOffset++)
        {
            int entityIndex = _playerStateCache!.GetAssociatedEntity(targetSlot, entityOffset);
            if (!FowConstants.IsValidEntityIndex(entityIndex))
                continue;

            if (_playerStateCache.ShouldRevealDroppedWeaponToObserver(entityIndex, in observer, currentTick))
                continue;

            info.TransmitEntities.Remove(entityIndex);
        }
    }

    private void ForceFlexStateResync(List<CCSPlayerController> players, int currentTick)
    {
        if (_visibilityManager == null)
            return;

        for (int i = 0; i < players.Count; i++)
        {
            var controller = players[i];
            int slot = controller.Slot;

            if (!FowConstants.IsValidSlot(slot) || !_visibilityManager.NeedsFlexResync(slot))
                continue;

            var pawn = controller.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            pawn.Effects |= EffectNoInterp;
            // Hold NOINTERP for 2 ticks so the client receives at least one
            // NOINTERP snapshot even under single-packet loss.
            _clearNoInterpAfterTick[slot] = currentTick + 2;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_fEffects");

            // Player pawns exclude m_flexWeight from their network table, so resync the
            // eye/head state that still replicates and feeds the client eye pipeline.
            Utilities.SetStateChanged(pawn, "CBaseFlex", "m_vLookTargetPosition");
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_angEyeAngles");
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_vHeadConstraintOffset");

            // Keep the resync surface narrow. Broad struct/controller dirtying produced
            // unresolved offset spam on live servers and is not reliable through CSS.
            Utilities.SetStateChanged(pawn, "CBaseAnimGraph", "m_bInitiallyPopulateInterpHistory");
        }
    }

    private void ClearPendingNoInterp(List<CCSPlayerController> players)
    {
        int currentTick = Server.TickCount;
        for (int i = 0; i < players.Count; i++)
        {
            var controller = players[i];
            int slot = controller.Slot;

            if (!FowConstants.IsValidSlot(slot) || _clearNoInterpAfterTick[slot] == 0)
                continue;

            if (currentTick < _clearNoInterpAfterTick[slot])
                continue;

            _clearNoInterpAfterTick[slot] = 0;

            var pawn = controller.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            if ((pawn.Effects & EffectNoInterp) == 0)
                continue;

            pawn.Effects &= ~EffectNoInterp;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_fEffects");
        }
    }
}
