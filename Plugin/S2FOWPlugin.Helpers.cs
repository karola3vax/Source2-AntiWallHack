using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Utils;
using S2FOW.Config;
using S2FOW.Core;

namespace S2FOW;

public partial class S2FOWPlugin
{
    private void RebuildDebugRenderer()
    {
        _debugAabbRenderer?.Clear();
        _debugAabbRenderer = Config.Debug.ShowTargetPoints || Config.Debug.ShowRayLines
            ? new DebugAabbRenderer(_raycastEngine!, Config)
            : null;
    }

    private void ResetRuntimeState(string? logMapName)
    {
        ResetNoInterpState();
        _visibilityManager?.OnMapChange();
        _visibilityManager?.SetRoundPhase(_currentRoundPhase);
        _playerStateCache?.ResetTracking();
        _debugAabbRenderer?.Clear();
        _perfMonitor?.Reset();
        _projectileTracker?.Clear();
        _spottedStateScrubber?.Clear();
        _impactTracker?.Clear();
        _trackedPlantedC4EntityIndex = 0;
        _lastPlantedC4LookupTick = int.MinValue;

        if (!string.IsNullOrEmpty(logMapName))
            Log($"Map changed to {logMapName}. Internal state refreshed.");
    }

    private void ResetNoInterpState()
    {
        ClearPendingNoInterp(Utilities.GetPlayers());
        Array.Clear(_clearNoInterpAfterTick);
        Array.Clear(_nextTraceOverlayUpdateTick);
        _nextServerAvgOverlayRefreshTick = 0;
        _displayedServerAvgRaycasts = 0.0;
    }

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

    private bool ShouldBlockBombRadarESP()
    {
        return Config.General.SecurityProfile == SecurityProfile.Strict ||
               Config.AntiWallhack.BlockBombRadarESP;
    }

    private bool ShouldHidePlantedBombEntity()
    {
        return Config.AntiWallhack.HidePlantedBombEntityWhenNotVisible;
    }

    private void SyncTrackedPlantedC4RadarState()
    {
        if (_spottedStateScrubber == null)
            return;

        if (_trackedPlantedC4EntityIndex > 0 && ShouldBlockBombRadarESP())
        {
            _spottedStateScrubber.OnC4Planted(_trackedPlantedC4EntityIndex);
            return;
        }

        _spottedStateScrubber.OnC4Removed();
    }

    private bool CanObserverSeeTrackedC4(int observerSlot, CPlantedC4 c4Entity, int currentTick)
    {
        if (_playerStateCache == null || _visibilityManager == null || !FowConstants.IsValidSlot(observerSlot))
            return false;

        var observerSnapshot = _playerStateCache.Snapshots[observerSlot];
        return _visibilityManager.CanSeePlantedC4(in observerSnapshot, c4Entity, currentTick);
    }

    private bool TryGetTrackedPlantedC4(out CPlantedC4 plantedC4)
    {
        plantedC4 = null!;
        if (_trackedPlantedC4EntityIndex <= 0)
        {
            int currentTick = Server.TickCount;
            if (_lastPlantedC4LookupTick == currentTick)
                return false;

            _lastPlantedC4LookupTick = currentTick;

            try
            {
                var plantedC4Entities = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4");
                foreach (var c4 in plantedC4Entities)
                {
                    if (c4 != null && c4.IsValid && c4.Index > 0)
                    {
                        _trackedPlantedC4EntityIndex = (int)c4.Index;
                        _lastPlantedC4LookupTick = currentTick;
                        SyncTrackedPlantedC4RadarState();
                        plantedC4 = c4;
                        return true;
                    }
                }
            }
            catch
            {
                _suppressedEntityLookupErrors++;
            }

            return false;
        }

        var entity = Utilities.GetEntityFromIndex<CPlantedC4>(_trackedPlantedC4EntityIndex);
        if (entity == null || !entity.IsValid)
        {
            _spottedStateScrubber?.OnC4Removed();
            _trackedPlantedC4EntityIndex = 0;
            return false;
        }

        plantedC4 = entity;
        return true;
    }

    private bool ShouldHidePlantedC4FromObserver(
        int observerSlot,
        ReadOnlySpan<Models.PlayerSnapshot> snapshots,
        CPlantedC4 plantedC4,
        int currentTick)
    {
        if (!CanObserverSeeTrackedC4(observerSlot, plantedC4, currentTick))
            return true;

        if (!plantedC4.BeingDefused)
            return false;

        var defuserPawn = plantedC4.BombDefuser.Value;
        if (defuserPawn == null || !defuserPawn.IsValid)
            return false;

        var defuserController = defuserPawn.Controller.Value as CCSPlayerController;
        if (defuserController == null || !defuserController.IsValid || !FowConstants.IsValidSlot(defuserController.Slot))
            return false;

        ref readonly var observer = ref snapshots[observerSlot];
        ref readonly var defuser = ref snapshots[defuserController.Slot];

        if (!observer.IsValid || !defuser.IsValid || defuser.Team == observer.Team)
            return false;

        return _hiddenPairs[observerSlot * FowConstants.MaxSlots + defuserController.Slot];
    }

    private void RefreshRoundPhaseFromGameRules(RoundPhase fallbackPhase)
    {
        if (!TryGetGameRules(out CCSGameRules? gameRules) || gameRules == null)
        {
            SetRoundPhase(fallbackPhase);
            return;
        }

        CCSGameRules resolvedGameRules = gameRules;

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

        SetRoundPhase(fallbackPhase == RoundPhase.RoundEnd ? RoundPhase.RoundEnd : RoundPhase.Live);
    }

    private void SetRoundPhase(RoundPhase phase)
    {
        if (_currentRoundPhase == phase)
        {
            _visibilityManager?.SetRoundPhase(phase);
            return;
        }

        _currentRoundPhase = phase;
        _visibilityManager?.SetRoundPhase(phase);
        Log($"Round phase -> {_currentRoundPhase}");
    }
}
