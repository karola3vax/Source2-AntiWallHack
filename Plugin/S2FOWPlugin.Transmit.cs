using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using S2FOW.Core;
using S2FOW.Models;
using S2FOW.Util;

namespace S2FOW;

/// <summary>
/// Per-frame player hiding.
///
/// CheckTransmit is the engine callback where CS2 gives S2FOW the list of
/// networked objects it is about to send to each viewer. S2FOW edits that list:
/// if a viewer should not see an enemy, S2FOW removes that enemy's player body and
/// connected objects from only that viewer's list.
///
/// In the code, "observer" means the viewer receiving updates. "target" means the
/// enemy being checked for that viewer.
///
/// Safety rule: S2FOW only hides a living enemy when it knows all connected objects
/// for that enemy were collected. If anything is missing or uncertain, the enemy is
/// shown instead of hidden.
/// </summary>
public partial class S2FOWPlugin
{
    /// <summary>
    /// Runs once per network frame. This is the only place where S2FOW actually
    /// removes hidden enemies from each viewer's update list.
    /// </summary>
    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        // If S2FOW is not ready or protection is off, leave the engine's update
        // lists untouched.
        if (!_initialized || _visibilityManager == null || _raycastEngine == null)
            return;

        if (!Config.General.Enabled)
            return;

        // Hiding players without the full-update recovery path can trigger the
        // "CopyExistingEntity: missing client entity" client crash. When the
        // required gamedata/engine bridge is missing or has failed, S2FOW pauses
        // protection and leaves every player visible.
        if (!IsCrashRecoveryReady)
            return;

        // Read current players and clear any short-lived visual refresh flags that
        // have expired from earlier hide/show transitions.
        var players = Utilities.GetPlayers();
        ResetObserverFullUpdateFrameQueue();
        ClearPendingNoInterp(players);

        // Start this frame's counters and apply the configured raycast limit.
        _perfMonitor?.BeginFrame();
        _raycastEngine.ResetFrameCounter();
        int currentTick = Server.TickCount;
        _raycastEngine.SetFrameBudget(Config.Performance.MaxRaycastsPerFrame);

        // Give the visibility manager the current time so round-start, death, spawn,
        // smoke lifetime, and throttling rules all use the same tick.
        _visibilityManager.SetFrameTick(currentTick);
        _visibilityManager.BeginFrame();

        // During non-live round states, everyone should be visible. S2FOW clears
        // debug drawings, queues viewer refreshes, and skips all hiding work.
        if (_visibilityManager.ShouldBypassVisibilityWorkForCurrentPhase())
        {
            _debugAabbRenderer?.Clear();
            ResetDeferredRevealState();
            QueueAllObserversFullUpdate(players, ObserverFullUpdateReason.PhaseBypass);
            ProcessObserverFullUpdates(players, currentTick);
            _perfMonitor?.EndFrame(0);
            return;
        }

        // Read every player's state once so all visibility decisions in this frame
        // are based on the same positions, teams, weapons, and connected objects.
        _playerStateCache!.BuildSnapshots(players);

        var snapshots = _playerStateCache.Snapshots;
        var activeSlots = _playerStateCache.ActiveSlots;

        // Process each human viewer that is alive and on a playing team.
        foreach ((CCheckTransmitInfo info, CCSPlayerController? controller) in infoList)
        {
            if (controller == null)
                continue;

            int observerSlot = controller.Slot;
            if (!FowConstants.IsValidSlot(observerSlot))
                continue;

            // If the engine or another plugin has already removed a player's body,
            // also remove that player's connected objects from this viewer's update
            // list. A viewer must never receive child objects without the body they
            // belong to.
            EnforcePawnChildInvariant(info, observerSlot, activeSlots, snapshots);

            ref readonly var observer = ref snapshots[observerSlot];
            if (!observer.IsValid || !observer.IsAlive)
                continue;

            // Spectators and unassigned players should see normally.
            if (observer.Team != CsTeam.Terrorist && observer.Team != CsTeam.CounterTerrorist)
                continue;

            // Bots do not need player hiding work.
            if (observer.IsBot)
                continue;

            // In debug mode, each viewer should see only their own debug points.
            if (Config.Debug.ShowTargetPoints && _debugAabbRenderer != null)
                _debugAabbRenderer.RemoveOtherObserverPointEntities(info, observerSlot);

            // Check each enemy against this viewer.
            for (int i = 0; i < activeSlots.Length; i++)
            {
                int targetSlot = activeSlots[i];
                ref readonly var target = ref snapshots[targetSlot];

                // Skip disconnected players, the viewer themself, and teammates.
                if (!target.IsValid || target.Slot == observerSlot || target.Team == observer.Team)
                    continue;

                // Dead or dying players stay visible. Hiding during death cleanup is
                // unsafe because the engine may still be building death/ragdoll updates.
                if (!target.IsAlive)
                {
                    MarkTargetVisibleSafely(info, observerSlot, target.Slot, currentTick);
                    _perfMonitor?.RecordDeadForceTransmit();
                    continue;
                }

                // A live body without a valid controller is unusual. Remove it only
                // together with the connected objects S2FOW already knows about.
                if (!target.HasValidPawnController)
                {
                    if (RemoveHiddenTargetEntities(info, targetSlot))
                    {
                        _perfMonitor?.RecordInvalidControllerPawnClear();
                        QueueObserverFullUpdate(observerSlot, ObserverFullUpdateReason.Hide | ObserverFullUpdateReason.SafetyClear);
                    }
                    continue;
                }

                // If S2FOW could not collect the complete connected-object list, show
                // the enemy. Showing too much is safer than hiding the body while a
                // connected object is still sent to the viewer.
                if (!target.CanHideControlledLivePawn)
                {
                    MarkTargetVisibleSafely(info, observerSlot, target.Slot, currentTick);
                    _perfMonitor?.RecordUnsafeHideSkipped();
                    continue;
                }

                // Ask the visibility manager whether this viewer should receive this enemy.
                bool wasHiddenBeforeDecision = _visibilityManager.IsPairHidden(observerSlot, target.Slot);
                bool shouldTransmit = _visibilityManager.ShouldTransmit(in observer, in target, currentTick);

                // If not visible, remove the enemy body and connected objects from
                // this viewer's update list. Queue a viewer refresh only when the
                // pair changed from visible to hidden; already-hidden pairs do not
                // need another full update every frame.
                if (!shouldTransmit)
                {
                    ClearDeferredReveal(observerSlot, targetSlot);
                    if (!wasHiddenBeforeDecision)
                        QueueObserverFullUpdate(observerSlot, ObserverFullUpdateReason.Hide);

                    RemoveHiddenTargetEntities(info, targetSlot);
                    continue;
                }

                // If the enemy just changed from hidden to visible, do not reveal
                // them in this same packet. First queue the forced refresh, then
                // keep body and child objects hidden briefly so weapons/wearables
                // cannot appear before the player body exists on the viewer.
                if (wasHiddenBeforeDecision)
                    BeginDeferredReveal(observerSlot, targetSlot, currentTick, ObserverFullUpdateReason.Unhide);

                if (ShouldKeepDeferredRevealHidden(observerSlot, targetSlot, currentTick))
                    RemoveHiddenTargetEntities(info, targetSlot);
            }
        }

        // Send queued viewer refreshes, update optional debug visuals, and refresh
        // players that just changed from hidden to visible.
        ProcessObserverFullUpdates(players, currentTick);
        UpdateDebugOutputs(snapshots, players, currentTick);
        ForceFlexStateResync(players, currentTick);
        _perfMonitor?.EndFrame(_raycastEngine.RaycastsThisFrame);
    }

    /// <summary>
    /// If a player body is absent from a viewer's update list, S2FOW also removes
    /// that player's connected objects from the same viewer's list.
    ///
    /// This protects against a client crash where the viewer receives a weapon,
    /// wearable, hostage prop, or attached scene object whose player body is missing.
    /// </summary>
    private void EnforcePawnChildInvariant(
        CCheckTransmitInfo info,
        int observerSlot,
        ReadOnlySpan<int> activeSlots,
        ReadOnlySpan<PlayerSnapshot> snapshots)
    {
        for (int i = 0; i < activeSlots.Length; i++)
        {
            int targetSlot = activeSlots[i];
            ref readonly var target = ref snapshots[targetSlot];
            if (!target.IsValid || !FowConstants.IsValidEntityIndex((int)target.PawnEntityIndex))
                continue;

            if (info.TransmitEntities.Contains((int)target.PawnEntityIndex))
                continue;

            if (RemoveHiddenTargetEntities(info, targetSlot))
            {
                _perfMonitor?.RecordOrphanClosureCleanup();
                QueueObserverFullUpdate(observerSlot, ObserverFullUpdateReason.Hide | ObserverFullUpdateReason.OrphanCleanup);
            }
        }
    }

    /// <summary>
    /// Removes all known networked objects for one hidden enemy from one viewer's
    /// update list. This includes the body, weapons, wearables, hostage objects,
    /// and attached scene objects collected in the player snapshot.
    /// </summary>
    private bool RemoveHiddenTargetEntities(
        CCheckTransmitInfo info,
        int targetSlot)
    {
        int assocCount = _playerStateCache?.Snapshots[targetSlot].AssociatedEntityCount ?? 0;
        bool removedAny = false;
        for (int entityOffset = 0; entityOffset < assocCount; entityOffset++)
        {
            int entityIndex = _playerStateCache!.GetAssociatedEntity(targetSlot, entityOffset);
            if (!FowConstants.IsValidEntityIndex(entityIndex))
                continue;

            if (info.TransmitEntities.Contains(entityIndex))
                removedAny = true;

            info.TransmitEntities.Remove(entityIndex);
        }

        return removedAny;
    }

    /// <summary>
    /// Shows a target for safety, but if they were hidden last frame, holds their
    /// body and connected objects back briefly until a forced refresh can rebuild
    /// the player body on the viewer's client.
    /// </summary>
    private void MarkTargetVisibleSafely(
        CCheckTransmitInfo info,
        int observerSlot,
        int targetSlot,
        int currentTick)
    {
        bool wasHiddenBeforeDecision = _visibilityManager?.IsPairHidden(observerSlot, targetSlot) ?? false;
        _visibilityManager?.MarkForceVisible(observerSlot, targetSlot);

        if (wasHiddenBeforeDecision)
            BeginDeferredReveal(observerSlot, targetSlot, currentTick, ObserverFullUpdateReason.Unhide);

        if (ShouldKeepDeferredRevealHidden(observerSlot, targetSlot, currentTick))
            RemoveHiddenTargetEntities(info, targetSlot);
    }

    private void BeginDeferredReveal(
        int observerSlot,
        int targetSlot,
        int currentTick,
        ObserverFullUpdateReason reason)
    {
        if (!FowConstants.IsValidSlot(observerSlot) || !FowConstants.IsValidSlot(targetSlot))
            return;

        int pairIndex = GetObserverTargetPairIndex(observerSlot, targetSlot);
        _deferredRevealUntilTick[pairIndex] = Math.Max(
            _deferredRevealUntilTick[pairIndex],
            currentTick + RevealSettleTicks);
        QueueObserverFullUpdate(observerSlot, reason);
    }

    private bool ShouldKeepDeferredRevealHidden(int observerSlot, int targetSlot, int currentTick)
    {
        if (!FowConstants.IsValidSlot(observerSlot) || !FowConstants.IsValidSlot(targetSlot))
            return false;

        return currentTick < _deferredRevealUntilTick[GetObserverTargetPairIndex(observerSlot, targetSlot)];
    }

    private void ClearDeferredReveal(int observerSlot, int targetSlot)
    {
        if (!FowConstants.IsValidSlot(observerSlot) || !FowConstants.IsValidSlot(targetSlot))
            return;

        _deferredRevealUntilTick[GetObserverTargetPairIndex(observerSlot, targetSlot)] = 0;
    }

    private void ResetDeferredRevealState()
    {
        Array.Clear(_deferredRevealUntilTick);
    }

    private void ClearDeferredRevealStateForSlot(int slot)
    {
        if (!FowConstants.IsValidSlot(slot))
            return;

        for (int i = 0; i < FowConstants.MaxSlots; i++)
        {
            _deferredRevealUntilTick[GetObserverTargetPairIndex(slot, i)] = 0;
            _deferredRevealUntilTick[GetObserverTargetPairIndex(i, slot)] = 0;
        }
    }

    private static int GetObserverTargetPairIndex(int observerSlot, int targetSlot)
    {
        return observerSlot * FowConstants.MaxSlots + targetSlot;
    }

    /// <summary>
    /// When a player changes from hidden to visible, briefly set the NOINTERP
    /// visual-refresh flag so the client uses the player's current position
    /// immediately. After two ticks, S2FOW clears the flag and normal smooth
    /// movement resumes.
    /// </summary>
    private void ForceFlexStateResync(List<CCSPlayerController> players, int currentTick)
    {
        if (_visibilityManager == null)
            return;

        for (int i = 0; i < players.Count; i++)
        {
            var controller = players[i];
            int slot = controller.Slot;

            // Only process players that the visibility manager flagged for refresh.
            if (!FowConstants.IsValidSlot(slot) || !_visibilityManager.NeedsFlexResync(slot))
                continue;

            var pawn = controller.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            // Set the short visual-refresh flag and schedule removal in two ticks.
            pawn.Effects |= EffectNoInterp;
            _clearNoInterpAfterTick[slot] = currentTick + 2;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_fEffects");

            // Mark eye angles as changed so the viewer receives the current facing direction.
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_angEyeAngles");

            // Ask the client to rebuild movement history from the current state.
            Utilities.SetStateChanged(pawn, "CBaseAnimGraph", "m_bInitiallyPopulateInterpHistory");
        }
    }

    /// <summary>
    /// Clears the short visual-refresh flag once its scheduled removal tick arrives.
    /// </summary>
    private void ClearPendingNoInterp(List<CCSPlayerController> players)
    {
        int currentTick = Server.TickCount;
        for (int i = 0; i < players.Count; i++)
        {
            var controller = players[i];
            int slot = controller.Slot;

            if (!FowConstants.IsValidSlot(slot) || _clearNoInterpAfterTick[slot] == 0)
                continue;

            // Not time yet; keep the flag for now.
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
