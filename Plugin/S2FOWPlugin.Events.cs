using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using S2FOW.Core;

namespace S2FOW;

public partial class S2FOWPlugin
{
    // Event handlers.
    private HookResult OnPlayerDisconnect(EventPlayerDisconnect @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
        {
            ClearNoInterpState(player);
            _visibilityManager?.OnPlayerDisconnect(player.Slot);
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        if (victim != null)
        {
            int currentTick = Server.TickCount;
            _visibilityManager?.OnPlayerDeath(victim.Slot, currentTick);

            // Record temporal ownership for weapons that will drop on death
            if (Config.AntiWallhack.BlockDroppedWeaponESPDurationTicks > 0)
                _playerStateCache?.RecordTemporalOwnershipForDeath(victim.Slot, currentTick);
        }
        return HookResult.Continue;
    }

    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
            _visibilityManager?.OnPlayerSpawn(player.Slot, Server.TickCount);

        return HookResult.Continue;
    }

    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ApplyRuntimeProfile(resetRuntimeState: false, logProfileChange: true);
        ResetNoInterpState();
        _visibilityManager?.OnRoundStart(Server.TickCount);
        RefreshRoundPhaseFromGameRules(fallbackPhase: RoundPhase.FreezeTime);
        _projectileTracker?.Clear();
        _spottedStateScrubber?.Clear();
        _impactTracker?.Clear();
        return HookResult.Continue;
    }

    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        RefreshRoundPhaseFromGameRules(fallbackPhase: RoundPhase.Live);
        return HookResult.Continue;
    }

    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        SetRoundPhase(RoundPhase.RoundEnd);
        return HookResult.Continue;
    }

    private HookResult OnWarmupEnd(EventWarmupEnd @event, GameEventInfo info)
    {
        RefreshRoundPhaseFromGameRules(fallbackPhase: RoundPhase.Live);
        return HookResult.Continue;
    }

    private HookResult OnSmokeDetonate(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        _smokeTracker?.OnSmokeDetonate(
            @event.X, @event.Y, @event.Z,
            Server.TickCount,
            Config.AntiWallhack.SmokeBlockDelayTicks);
        return HookResult.Continue;
    }

    private HookResult OnSmokeExpired(EventSmokegrenadeExpired @event, GameEventInfo info)
    {
        _smokeTracker?.OnSmokeExpired(@event.X, @event.Y, @event.Z);
        return HookResult.Continue;
    }

    private void OnEntityCreated(CEntityInstance entity)
    {
        _playerStateCache?.MarkEntityDirty(entity);
        _projectileTracker?.OnEntityCreated(entity);
        if (Config.AntiWallhack.BlockBulletImpactESP)
            _impactTracker?.OnEntityCreated(entity);
    }

    private void OnEntitySpawned(CEntityInstance entity)
    {
        _playerStateCache?.MarkEntityDirty(entity);
        _projectileTracker?.OnEntitySpawned(entity);
        if (Config.AntiWallhack.BlockBulletImpactESP)
            _impactTracker?.OnEntitySpawned(entity);

        if (entity is CPlantedC4 plantedC4 && plantedC4.IsValid && plantedC4.Index > 0)
        {
            _trackedPlantedC4EntityIndex = (int)plantedC4.Index;
            _lastPlantedC4LookupTick = Server.TickCount;
            SyncTrackedPlantedC4RadarState();
        }
    }

    private void OnEntityDeleted(CEntityInstance entity)
    {
        _playerStateCache?.OnEntityDeleted(entity);
        _projectileTracker?.OnEntityDeleted(entity);
        _impactTracker?.OnEntityDeleted(entity);

        if (entity is CPlantedC4 || (entity != null && entity.Index == _trackedPlantedC4EntityIndex))
        {
            _spottedStateScrubber?.OnC4Removed();
            _trackedPlantedC4EntityIndex = 0;
            _lastPlantedC4LookupTick = int.MinValue;
        }
    }

    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        SetRoundPhase(RoundPhase.PostPlant);

        // Find the planted C4 entity
        try
        {
            var entities = Utilities.FindAllEntitiesByDesignerName<CPlantedC4>("planted_c4");
            foreach (var c4 in entities)
            {
                if (c4 != null && c4.IsValid)
                {
                    _trackedPlantedC4EntityIndex = (int)c4.Index;
                    _lastPlantedC4LookupTick = Server.TickCount;
                    SyncTrackedPlantedC4RadarState();
                    break;
                }
            }
        }
        catch
        {
            _suppressedEntityLookupErrors++;
        }

        return HookResult.Continue;
    }

    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        SetRoundPhase(RoundPhase.RoundEnd);
        _spottedStateScrubber?.OnC4Removed();
        _trackedPlantedC4EntityIndex = 0;
        _lastPlantedC4LookupTick = int.MinValue;
        return HookResult.Continue;
    }

    private HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
    {
        SetRoundPhase(RoundPhase.RoundEnd);
        _spottedStateScrubber?.OnC4Removed();
        _trackedPlantedC4EntityIndex = 0;
        _lastPlantedC4LookupTick = int.MinValue;
        return HookResult.Continue;
    }

    private HookResult OnBulletImpact(EventBulletImpact @event, GameEventInfo info)
    {
        if (!Config.AntiWallhack.BlockBulletImpactESP)
            return HookResult.Continue;

        var shooter = @event.Userid;
        if (shooter != null)
        {
            _impactTracker?.OnBulletImpact(shooter.Slot, @event.X, @event.Y, @event.Z, Server.TickCount);
        }
        return HookResult.Continue;
    }

    // Map lifecycle.
    private void OnMapStart(string mapName)
    {
        ApplyRuntimeProfile(resetRuntimeState: false, logProfileChange: true);
        ResetRuntimeState(mapName);
        RefreshRoundPhaseFromGameRules(fallbackPhase: RoundPhase.Live);
    }

    private void OnMapEnd()
    {
        ResetRuntimeState(logMapName: null);
    }
}
