using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using S2FOW.Core;

namespace S2FOW;

/// <summary>
/// Helper methods used by multiple parts of the plugin:
///   - Round phase tracking (warmup → freeze → live → post-plant → round end).
///   - State resets between maps and rounds.
///   - NOINTERP flag management for individual players.
///   - Debug renderer creation.
/// </summary>
public partial class S2FOWPlugin
{
    /// <summary>
    /// Creates (or destroys) the debug beam renderer based on current debug settings.
    /// The renderer is only needed when ShowTargetPoints or ShowRayLines are enabled.
    /// </summary>
    private void RebuildDebugRenderer()
    {
        _debugAabbRenderer?.Clear();
        _debugAabbRenderer = Config.Debug.ShowTargetPoints || Config.Debug.ShowRayLines
            ? new DebugAabbRenderer(_raycastEngine!, Config)
            : null;
    }

    /// <summary>
    /// Resets all runtime state. Called on map changes and config reloads.
    /// This ensures no stale data (old smoke positions, cached player states,
    /// debug visuals, performance counters) carries over between maps.
    /// </summary>
    private void ResetRuntimeState(string? logMapName)
    {
        ResetNoInterpState();
        _visibilityManager?.OnMapChange();
        _visibilityManager?.SetRoundPhase(_currentRoundPhase);
        _playerStateCache?.ResetTracking();
        _debugAabbRenderer?.Clear();
        _perfMonitor?.Reset();

        if (!string.IsNullOrEmpty(logMapName))
            Log($"Map changed to {logMapName}. Internal state refreshed.");
    }

    /// <summary>
    /// Clears all pending NOINTERP flags across all players.
    /// Used during round resets and plugin unload to ensure no player
    /// gets stuck with the "no interpolation" effect.
    /// </summary>
    private void ResetNoInterpState()
    {
        ClearPendingNoInterp(Utilities.GetPlayers());
        Array.Clear(_clearNoInterpAfterTick);
    }

    /// <summary>
    /// Immediately clears the NOINTERP flag for a single player.
    /// Used when a player disconnects to clean up their state.
    /// </summary>
    private void ClearNoInterpState(CCSPlayerController controller)
    {
        int slot = controller.Slot;
        if (!FowConstants.IsValidSlot(slot))
            return;

        _clearNoInterpAfterTick[slot] = 0;

        var pawn = controller.PlayerPawn.Value;
        if (pawn == null || !pawn.IsValid || (pawn.Effects & EffectNoInterp) == 0)
            return;

        pawn.Effects &= ~EffectNoInterp;
        Utilities.SetStateChanged(pawn, "CBaseEntity", "m_fEffects");
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Round phase management
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Determines the current round phase by querying the engine's game rules entity.
    /// The game rules entity knows whether it is warmup, freeze time, or if the bomb
    /// is planted. If the entity cannot be found (e.g., during a map transition),
    /// we fall back to the provided default phase.
    /// </summary>
    private void RefreshRoundPhaseFromGameRules(RoundPhase fallbackPhase)
    {
        if (!TryGetGameRules(out CCSGameRules? gameRules) || gameRules == null)
        {
            SetRoundPhase(fallbackPhase);
            return;
        }

        CCSGameRules resolvedGameRules = gameRules;

        // Check conditions in priority order:
        // Warmup overrides everything, then freeze time, then bomb planted.
        if (resolvedGameRules.WarmupPeriod)
        {
            SetRoundPhase(RoundPhase.Warmup);
            return;
        }

        if (resolvedGameRules.FreezePeriod)
        {
            SetRoundPhase(RoundPhase.FreezeTime);
            return;
        }

        if (resolvedGameRules.BombPlanted)
        {
            SetRoundPhase(RoundPhase.PostPlant);
            return;
        }

        // No special condition — use the fallback (Live or RoundEnd).
        SetRoundPhase(fallbackPhase == RoundPhase.RoundEnd ? RoundPhase.RoundEnd : RoundPhase.Live);
    }

    /// <summary>
    /// Updates the round phase and tells the visibility manager about the change.
    /// Logs the transition to the console so operators can track phase changes.
    /// </summary>
    private void SetRoundPhase(RoundPhase phase)
    {
        if (_currentRoundPhase == phase)
        {
            // Even if the phase has not changed, re-send it to the visibility manager
            // so it can re-check any internal state that depends on the phase.
            _visibilityManager?.SetRoundPhase(phase);
            return;
        }

        _currentRoundPhase = phase;
        _visibilityManager?.SetRoundPhase(phase);
        Log($"Round phase -> {_currentRoundPhase}");
    }
}
