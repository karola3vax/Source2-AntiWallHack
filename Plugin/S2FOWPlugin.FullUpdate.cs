using CounterStrikeSharp.API.Core;
using S2FOW.Core;
using S2FOW.Util;

namespace S2FOW;

/// <summary>
/// Handles forced client refreshes after S2FOW hides or shows enemies.
///
/// A full update is a crash-recovery refresh for one viewer. S2FOW queues at most
/// one refresh per viewer per frame, combines multiple reasons into that one queued
/// refresh. Real hide/show transitions are sent immediately; repeated non-critical
/// refreshes, such as round-state refreshes, are throttled.
/// If full-update support is unavailable, S2FOW leaves all players visible instead
/// of hiding without the recovery path. If one send fails because a viewer is not
/// ready during a connection or round transition, S2FOW counts it and retries later.
/// </summary>
public partial class S2FOWPlugin
{
    /// <summary>Clears the per-frame queue. Called at the start and end of a frame.</summary>
    private void ResetObserverFullUpdateFrameQueue()
    {
        Array.Clear(_observerFullUpdateQueued);
        Array.Clear(_observerFullUpdateReasons);
    }

    /// <summary>Clears both the current queue and the per-viewer throttle timers.</summary>
    private void ResetObserverFullUpdateState()
    {
        ResetObserverFullUpdateFrameQueue();
        Array.Clear(_nextObserverFullUpdateTick);
    }

    /// <summary>
    /// Requests a forced refresh for one viewer. Multiple requests in the same frame
    /// are combined into one refresh with combined reason counters.
    /// </summary>
    private void QueueObserverFullUpdate(int observerSlot, ObserverFullUpdateReason reason)
    {
        if (!FowConstants.IsValidSlot(observerSlot) || reason == ObserverFullUpdateReason.None)
            return;

        _perfMonitor?.RecordFullUpdateRequested(reason);

        if (_observerFullUpdateQueued[observerSlot])
        {
            _observerFullUpdateReasons[observerSlot] |= reason;
            _perfMonitor?.RecordFullUpdateCoalesced(reason);
            return;
        }

        _observerFullUpdateQueued[observerSlot] = true;
        _observerFullUpdateReasons[observerSlot] = reason;
    }

    /// <summary>Queues a forced refresh for every valid human viewer.</summary>
    private void QueueAllObserversFullUpdate(List<CCSPlayerController> players, ObserverFullUpdateReason reason)
    {
        for (int i = 0; i < players.Count; i++)
        {
            var controller = players[i];
            if (!IsFullUpdateObserver(controller))
                continue;

            QueueObserverFullUpdate(controller.Slot, reason);
        }
    }

    /// <summary>
    /// Sends queued forced refreshes. Each viewer can receive one only when their
    /// 32-tick throttle has expired. Failed sends are counted and logged once.
    /// </summary>
    private void ProcessObserverFullUpdates(List<CCSPlayerController> players, int currentTick)
    {
        if (players.Count == 0)
        {
            ResetObserverFullUpdateFrameQueue();
            return;
        }

        for (int i = 0; i < players.Count; i++)
        {
            var controller = players[i];
            if (!IsFullUpdateObserver(controller))
                continue;

            int observerSlot = controller.Slot;
            if (!_observerFullUpdateQueued[observerSlot])
                continue;

            ObserverFullUpdateReason reason = _observerFullUpdateReasons[observerSlot];
            _observerFullUpdateQueued[observerSlot] = false;
            _observerFullUpdateReasons[observerSlot] = ObserverFullUpdateReason.None;

            bool shouldThrottle = ShouldThrottleFullUpdate(reason);
            if (shouldThrottle && currentTick < _nextObserverFullUpdateTick[observerSlot])
            {
                _perfMonitor?.RecordFullUpdateThrottled(reason);
                continue;
            }

            if (_networkFullUpdateService == null)
            {
                _perfMonitor?.RecordFullUpdateFailed(reason);
                LogFullUpdateFailureOnce("full-update service is unavailable; protection is paused and all players are sent normally");
                continue;
            }

            if (!_networkFullUpdateService.TryForceFullUpdate(controller, out string error))
            {
                _perfMonitor?.RecordFullUpdateFailed(reason);
                LogFullUpdateFailureOnce($"full update send failed for slot {observerSlot}: {error}. S2FOW will retry later; protection remains active because crash recovery support is installed");
                continue;
            }

            if (shouldThrottle)
                _nextObserverFullUpdateTick[observerSlot] = currentTick + FullUpdateThrottleTicks;

            _perfMonitor?.RecordFullUpdateSent(reason);
        }

        ResetObserverFullUpdateFrameQueue();
    }

    /// <summary>
    /// Immediately queues and processes forced refreshes for all viewers. Used when
    /// protection is turned off so clients receive normally visible players again.
    /// </summary>
    private void ForceFullUpdateAllObserversNow(ObserverFullUpdateReason reason)
    {
        var players = CounterStrikeSharp.API.Utilities.GetPlayers();
        ResetObserverFullUpdateFrameQueue();
        QueueAllObserversFullUpdate(players, reason);
        ProcessObserverFullUpdates(players, CounterStrikeSharp.API.Server.TickCount);
    }

    /// <summary>Returns true for connected human viewers that can receive a forced refresh.</summary>
    private static bool IsFullUpdateObserver(CCSPlayerController? controller)
    {
        return controller != null &&
               controller.IsValid &&
               !controller.IsBot &&
               FowConstants.IsValidSlot(controller.Slot);
    }

    /// <summary>
    /// Hide/show changes need an immediate refresh to avoid late pop-in. Repeated
    /// phase/toggle refreshes can be throttled because they are not tied to a new
    /// child-before-body risk.
    /// </summary>
    private static bool ShouldThrottleFullUpdate(ObserverFullUpdateReason reason)
    {
        const ObserverFullUpdateReason immediateReasons =
            ObserverFullUpdateReason.Hide |
            ObserverFullUpdateReason.Unhide |
            ObserverFullUpdateReason.OrphanCleanup |
            ObserverFullUpdateReason.SafetyClear;

        return (reason & immediateReasons) == 0;
    }

    /// <summary>Logs forced-refresh failure only once to avoid console spam.</summary>
    private void LogFullUpdateFailureOnce(string detail)
    {
        if (_loggedFullUpdateFailure)
            return;

        _loggedFullUpdateFailure = true;
        Log($"ForceFullUpdate unavailable or failed: {detail}");
    }
}
