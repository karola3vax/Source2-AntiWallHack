using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using S2FOW.Core;
using S2FOW.Models;
using S2FOW.Util;

namespace S2FOW;

/// <summary>
/// The CheckTransmit hot path — this is the heart of the anti-wallhack system.
///
/// Every network frame (64 times per second), the Source 2 engine prepares a list of
/// entities to send to each connected client. This is called the "transmit list".
///
/// Our hook intercepts that list and removes enemy players that the client should not
/// be able to see. Because the server never sends the enemy data, no wallhack on the
/// client can reveal them — the data simply does not exist on their machine.
///
/// The overall flow each frame:
///   1. Take a snapshot of every player's position, speed, team, etc.
///   2. For each human player (the "observer"), look at every enemy (the "target").
///   3. Ask the VisibilityManager: "Can this observer see this target?"
///   4. If not → remove the target's pawn and weapon entities from the transmit list.
///   5. If transitioning from hidden to visible → set NOINTERP to prevent rubber-banding.
/// </summary>
public partial class S2FOWPlugin
{
    /// <summary>
    /// Called by the engine every network frame with the list of transmit info
    /// for all connected clients. This is where we actually hide enemy players.
    /// </summary>
    private void OnCheckTransmit(CCheckTransmitInfoList infoList)
    {
        // Do nothing if the plugin is not ready or is disabled in config.
        if (!_initialized || _visibilityManager == null || _raycastEngine == null)
            return;

        if (!Config.General.Enabled)
            return;

        // Get the list of all connected players and clear any pending NOINTERP flags.
        var players = Utilities.GetPlayers();
        ClearPendingNoInterp(players);

        // Start the performance timer for this frame.
        _perfMonitor?.BeginFrame();
        _raycastEngine.ResetFrameCounter();
        int currentTick = Server.TickCount;
        _raycastEngine.SetFrameBudget(Config.Performance.MaxRaycastsPerFrame);

        // Tell the visibility manager what tick we are on so it can track time-based decisions.
        _visibilityManager.SetFrameTick(currentTick);
        _visibilityManager.BeginFrame();

        // During warmup, freeze time, and round end, skip all visibility work —
        // everyone should see everyone.
        if (_visibilityManager.ShouldBypassVisibilityWorkForCurrentPhase())
        {
            _debugAabbRenderer?.Clear();
            _perfMonitor?.EndFrame(0);
            return;
        }

        // Take a snapshot of every player's current state (position, speed, team, weapons, etc.).
        // This is done once per frame so all visibility checks use consistent data.
        _playerStateCache!.BuildSnapshots(players);

        var snapshots = _playerStateCache.Snapshots;
        var activeSlots = _playerStateCache.ActiveSlots;

        // Process each observer (a human player who is alive and on a real team).
        foreach ((CCheckTransmitInfo info, CCSPlayerController? controller) in infoList)
        {
            if (controller == null)
                continue;

            int observerSlot = controller.Slot;
            if (!FowConstants.IsValidSlot(observerSlot))
                continue;

            // If another hook or the engine already removed a pawn, make sure no
            // associated child entities remain orphaned in this transmit set.
            EnforcePawnChildInvariant(info, activeSlots, snapshots);

            ref readonly var observer = ref snapshots[observerSlot];
            if (!observer.IsValid || !observer.IsAlive)
                continue;

            // Spectators should see everything — only filter for T and CT players.
            if (observer.Team != CsTeam.Terrorist && observer.Team != CsTeam.CounterTerrorist)
                continue;

            // Bots don't run wallhack clients, so don't waste CPU filtering for them.
            if (observer.IsBot)
                continue;

            // In debug mode, hide other observers' debug point beams so they do not clutter the view.
            if (Config.Debug.ShowTargetPoints && _debugAabbRenderer != null)
                _debugAabbRenderer.RemoveOtherObserverPointEntities(info, observerSlot);

            // Check each enemy target for this observer.
            for (int i = 0; i < activeSlots.Length; i++)
            {
                int targetSlot = activeSlots[i];
                ref readonly var target = ref snapshots[targetSlot];

                // Skip invalid targets, self, and teammates.
                if (!target.IsValid || target.Slot == observerSlot || target.Team == observer.Team)
                    continue;

                // Dead/dying players are always transmitted. Hiding a pawn while
                // the engine is creating death/ragdoll deltas is the crash case
                // CounterStrikeSharp explicitly warns plugins about.
                if (!target.IsAlive)
                {
                    _visibilityManager.MarkForceVisible(observerSlot, target.Slot);
                    _perfMonitor?.RecordDeadForceTransmit();
                    continue;
                }

                // A live pawn without a valid controller is not a normal LOS target.
                // Clear it only together with its known associated closure.
                if (!target.HasValidPawnController)
                {
                    if (RemoveHiddenTargetEntities(info, targetSlot))
                        _perfMonitor?.RecordInvalidControllerPawnClear();
                    continue;
                }

                // If dependency collection was incomplete, fail open. A wallhack
                // leak is preferable to orphaning a networked child entity.
                if (!target.CanHideControlledLivePawn)
                {
                    _visibilityManager.MarkForceVisible(observerSlot, target.Slot);
                    _perfMonitor?.RecordUnsafeHideSkipped();
                    continue;
                }

                // For alive enemies, run the full visibility check (rays, smoke, etc.).
                bool shouldTransmit = _visibilityManager.ShouldTransmit(in observer, in target, currentTick);

                // If the target should be hidden, remove their entities from the transmit list.
                if (!shouldTransmit)
                {
                    RemoveHiddenTargetEntities(info, targetSlot);
                }
            }
        }

        // Update debug overlays and handle NOINTERP for players transitioning to visible.
        UpdateDebugOutputs(snapshots, players, currentTick);
        ForceFlexStateResync(players, currentTick);
        _perfMonitor?.EndFrame(_raycastEngine.RaycastsThisFrame);
    }

    /// <summary>
    /// Removes a hidden target's pawn entity, all weapon entities, all wearable
    /// entities (gloves, agent accessories), and any hostage carry prop from the
    /// transmit list so the client never receives them.
    ///
    /// Important: We intentionally leave the player's "controller" entity transmitted.
    /// The controller only carries scoreboard metadata (name, score, ping) and no
    /// world-space position. Removing it would crash the client with a
    /// "CopyExistingEntity" error during delta updates.
    ///
    /// All child entities that are parented or bone-merged to the pawn MUST be
    /// hidden together. If any remain while the pawn is removed, the client
    /// receives orphaned entity data and crashes with:
    ///   FATAL ERROR: CL_CopyExistingEntity: missing client entity
    ///
    /// Entity types collected (verified against cs2-dumper schema):
    ///   - Pawn body (C_CSPlayerPawn)
    ///   - Weapons (CPlayer_WeaponServices → m_hMyWeapons, m_hActiveWeapon, m_hLastWeapon)
    ///   - Wearables (C_BaseCombatCharacter → m_hMyWearables at offset 4440)
    ///   - Hostage carry prop (CCSPlayer_HostageServices → m_hCarriedHostageProp at offset 76)
    /// </summary>
    private void EnforcePawnChildInvariant(
        CCheckTransmitInfo info,
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
                _perfMonitor?.RecordOrphanClosureCleanup();
        }
    }

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
    /// When a player transitions from hidden to visible, their model would appear to
    /// "teleport" from their last known position to their current one. To prevent this,
    /// we set the NOINTERP effect flag which tells the client: "snap to the new position
    /// instead of interpolating from the old one."
    ///
    /// We hold NOINTERP for 2 ticks (≈31ms) so the client receives at least one clean
    /// snapshot even under single-packet loss, then clear it so normal smooth movement resumes.
    /// </summary>
    private void ForceFlexStateResync(List<CCSPlayerController> players, int currentTick)
    {
        if (_visibilityManager == null)
            return;

        for (int i = 0; i < players.Count; i++)
        {
            var controller = players[i];
            int slot = controller.Slot;

            // Only process players that the visibility manager flagged for resync.
            if (!FowConstants.IsValidSlot(slot) || !_visibilityManager.NeedsFlexResync(slot))
                continue;

            var pawn = controller.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            // Set the NOINTERP flag and schedule its removal in 2 ticks.
            pawn.Effects |= EffectNoInterp;
            _clearNoInterpAfterTick[slot] = currentTick + 2;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_fEffects");

            // Mark eye angles as changed so the client gets the correct view direction.
            Utilities.SetStateChanged(pawn, "CCSPlayerPawn", "m_angEyeAngles");

            // Force the interpolation history to repopulate so the client does not
            // blend from a stale position.
            Utilities.SetStateChanged(pawn, "CBaseAnimGraph", "m_bInitiallyPopulateInterpHistory");
        }
    }

    /// <summary>
    /// Clears the NOINTERP flag on players whose scheduled removal tick has arrived.
    /// This restores normal smooth movement interpolation.
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

            // Not time yet — keep waiting.
            if (currentTick < _clearNoInterpAfterTick[slot])
                continue;

            // Time to remove NOINTERP.
            _clearNoInterpAfterTick[slot] = 0;

            var pawn = controller.PlayerPawn.Value;
            if (pawn == null || !pawn.IsValid)
                continue;

            // Only clear if the flag is actually set (avoid redundant network updates).
            if ((pawn.Effects & EffectNoInterp) == 0)
                continue;

            pawn.Effects &= ~EffectNoInterp;
            Utilities.SetStateChanged(pawn, "CBaseEntity", "m_fEffects");
        }
    }
}
