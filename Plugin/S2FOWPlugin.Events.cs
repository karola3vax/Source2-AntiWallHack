using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using S2FOW.Core;

namespace S2FOW;

/// <summary>
/// Game event handlers: reacts to important match events.
///
/// These handlers update S2FOW's memory of the round, player spawns and deaths,
/// smoke grenades, bomb state, and map changes. They do not hide players directly.
/// They only update information used later during the per-viewer visibility check.
/// </summary>
public partial class S2FOWPlugin
{
    // Player events

    /// <summary>
    /// A player disconnected from the server.
    /// S2FOW clears that player's visual-refresh state and forgets their old visibility data.
    /// </summary>
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

    /// <summary>
    /// A player just died.
    /// S2FOW records the tick so the player can stay visible briefly after death.
    /// This avoids a body disappearing during the death animation.
    /// </summary>
    private HookResult OnPlayerDeath(EventPlayerDeath @event, GameEventInfo info)
    {
        var victim = @event.Userid;
        if (victim != null)
        {
            int currentTick = Server.TickCount;
            _visibilityManager?.OnPlayerDeath(victim.Slot, currentTick);
        }
        return HookResult.Continue;
    }

    /// <summary>
    /// A player just spawned.
    /// S2FOW records the tick so the player stays visible briefly while their model
    /// and connected objects are still settling in.
    /// </summary>
    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
            _visibilityManager?.OnPlayerSpawn(player.Slot, Server.TickCount);

        return HookResult.Continue;
    }

    // Round phase events

    /// <summary>
    /// A new round has started.
    /// S2FOW clears visual-refresh state and marks the round phase. During freeze
    /// time, all players are shown because they cannot move yet.
    /// </summary>
    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ResetNoInterpState();
        _visibilityManager?.OnRoundStart(Server.TickCount);
        RefreshRoundPhaseFromGameRules(fallbackPhase: RoundPhase.FreezeTime);
        return HookResult.Continue;
    }

    /// <summary>
    /// Freeze time just ended.
    /// Players can move now, so S2FOW switches to active visibility checking.
    /// </summary>
    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        RefreshRoundPhaseFromGameRules(fallbackPhase: RoundPhase.Live);
        return HookResult.Continue;
    }

    /// <summary>
    /// The round ended.
    /// Everyone is shown because the round outcome is already decided.
    /// </summary>
    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        SetRoundPhase(RoundPhase.RoundEnd);
        return HookResult.Continue;
    }

    /// <summary>
    /// Warmup ended.
    /// S2FOW refreshes the round phase from the game rules object.
    /// </summary>
    private HookResult OnWarmupEnd(EventWarmupEnd @event, GameEventInfo info)
    {
        RefreshRoundPhaseFromGameRules(fallbackPhase: RoundPhase.Live);
        return HookResult.Continue;
    }

    // Smoke grenade events

    /// <summary>
    /// A smoke grenade just detonated at the given world coordinates.
    /// S2FOW tracks the smoke so visibility through that smoke can be blocked.
    /// The smoke starts small and grows to full size over a short bloom period.
    /// </summary>
    private HookResult OnSmokeDetonate(EventSmokegrenadeDetonate @event, GameEventInfo info)
    {
        _smokeTracker?.OnSmokeDetonate(
            @event.X, @event.Y, @event.Z,
            Server.TickCount,
            Config.AntiWallhack.SmokeBloomDurationTicks);
        return HookResult.Continue;
    }

    /// <summary>
    /// A smoke grenade expired.
    /// S2FOW removes it from the smoke tracker so it no longer blocks visibility.
    /// </summary>
    private HookResult OnSmokeExpired(EventSmokegrenadeExpired @event, GameEventInfo info)
    {
        _smokeTracker?.OnSmokeExpired(@event.X, @event.Y, @event.Z);
        return HookResult.Continue;
    }

    // Bomb events

    /// <summary>
    /// The bomb was planted.
    /// S2FOW marks the round as post-plant so visibility decisions use that phase.
    /// </summary>
    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        SetRoundPhase(RoundPhase.PostPlant);
        return HookResult.Continue;
    }

    /// <summary>The bomb was defused. The round is effectively over, so everyone is shown.</summary>
    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        SetRoundPhase(RoundPhase.RoundEnd);
        return HookResult.Continue;
    }

    /// <summary>The bomb exploded. The round is effectively over, so everyone is shown.</summary>
    private HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
    {
        SetRoundPhase(RoundPhase.RoundEnd);
        return HookResult.Continue;
    }

    // Map lifecycle

    /// <summary>
    /// A new map just loaded.
    /// S2FOW resets smoke positions, player snapshots, debug visuals, and counters.
    /// </summary>
    private void OnMapStart(string mapName)
    {
        ResetRuntimeState(mapName);
        RefreshRoundPhaseFromGameRules(fallbackPhase: RoundPhase.Live);
    }

    /// <summary>
    /// The current map is ending.
    /// S2FOW resets state so the next map starts cleanly.
    /// </summary>
    private void OnMapEnd()
    {
        ResetRuntimeState(logMapName: null);
    }
}
