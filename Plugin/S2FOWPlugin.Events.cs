using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using S2FOW.Core;

namespace S2FOW;

/// <summary>
/// Game event handlers — reacts to things that happen during a match.
///
/// Each handler listens for a specific game event (player death, smoke detonation,
/// round start, etc.) and updates the plugin's internal state accordingly.
/// All handlers return HookResult.Continue so the event keeps flowing to other plugins.
/// </summary>
public partial class S2FOWPlugin
{
    // ────────────────────────────────────────────────────────────────────────
    //  Player events
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A player disconnected from the server.
    /// We clear their NOINTERP state and tell the visibility manager to forget them.
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
    /// We record the tick so the plugin can keep them visible for a brief "death grace"
    /// period (default 128 ticks ≈ 2 seconds). This prevents the corpse from vanishing
    /// mid-death-animation, which would look jarring.
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
    /// A player just spawned (either at round start or after a respawn).
    /// We record the tick so the plugin can apply a brief "spawn grace" window
    /// to avoid glitches while the player model initializes.
    /// </summary>
    private HookResult OnPlayerSpawn(EventPlayerSpawn @event, GameEventInfo info)
    {
        var player = @event.Userid;
        if (player != null)
            _visibilityManager?.OnPlayerSpawn(player.Slot, Server.TickCount);

        return HookResult.Continue;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Round phase events
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A new round has started. Reset NOINTERP state and update the round phase.
    /// During freeze time, all players are visible (they cannot move yet).
    /// </summary>
    private HookResult OnRoundStart(EventRoundStart @event, GameEventInfo info)
    {
        ResetNoInterpState();
        _visibilityManager?.OnRoundStart(Server.TickCount);
        RefreshRoundPhaseFromGameRules(fallbackPhase: RoundPhase.FreezeTime);
        return HookResult.Continue;
    }

    /// <summary>
    /// The freeze period just ended — the round is now live.
    /// Players can move, so we switch to active visibility checking.
    /// </summary>
    private HookResult OnRoundFreezeEnd(EventRoundFreezeEnd @event, GameEventInfo info)
    {
        RefreshRoundPhaseFromGameRules(fallbackPhase: RoundPhase.Live);
        return HookResult.Continue;
    }

    /// <summary>
    /// The round ended. Switch to RoundEnd phase — everyone becomes visible
    /// since the round outcome is already decided.
    /// </summary>
    private HookResult OnRoundEnd(EventRoundEnd @event, GameEventInfo info)
    {
        SetRoundPhase(RoundPhase.RoundEnd);
        return HookResult.Continue;
    }

    /// <summary>
    /// Warmup just ended. Refresh the round phase from the engine's game rules.
    /// </summary>
    private HookResult OnWarmupEnd(EventWarmupEnd @event, GameEventInfo info)
    {
        RefreshRoundPhaseFromGameRules(fallbackPhase: RoundPhase.Live);
        return HookResult.Continue;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Smoke grenade events
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A smoke grenade just detonated (bloomed) at the given world coordinates.
    /// We track it so we can block visibility through smoke clouds.
    /// The smoke starts small and grows to full size over a short "bloom" period.
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
    /// A smoke grenade has expired (dissipated). We remove it from our tracker
    /// so it no longer blocks visibility.
    /// </summary>
    private HookResult OnSmokeExpired(EventSmokegrenadeExpired @event, GameEventInfo info)
    {
        _smokeTracker?.OnSmokeExpired(@event.X, @event.Y, @event.Z);
        return HookResult.Continue;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Bomb events
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The bomb was planted. Switch to PostPlant phase — this affects how
    /// aggressively we check visibility (post-plant rounds tend to be more static).
    /// </summary>
    private HookResult OnBombPlanted(EventBombPlanted @event, GameEventInfo info)
    {
        SetRoundPhase(RoundPhase.PostPlant);
        return HookResult.Continue;
    }

    /// <summary>The bomb was defused. Round is effectively over — make everyone visible.</summary>
    private HookResult OnBombDefused(EventBombDefused @event, GameEventInfo info)
    {
        SetRoundPhase(RoundPhase.RoundEnd);
        return HookResult.Continue;
    }

    /// <summary>The bomb exploded. Round is effectively over — make everyone visible.</summary>
    private HookResult OnBombExploded(EventBombExploded @event, GameEventInfo info)
    {
        SetRoundPhase(RoundPhase.RoundEnd);
        return HookResult.Continue;
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Map lifecycle
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A new map just loaded. Reset all internal state (smoke positions, cached
    /// snapshots, debug visuals, performance counters) for the new map.
    /// </summary>
    private void OnMapStart(string mapName)
    {
        ResetRuntimeState(mapName);
        RefreshRoundPhaseFromGameRules(fallbackPhase: RoundPhase.Live);
    }

    /// <summary>
    /// The current map is ending. Reset state in preparation for the next map.
    /// </summary>
    private void OnMapEnd()
    {
        ResetRuntimeState(logMapName: null);
    }
}
