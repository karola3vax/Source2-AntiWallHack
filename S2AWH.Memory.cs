using CounterStrikeSharp.API;

namespace S2AWH;

public partial class S2AWH
{
    private static int ConvertRevealHoldSecondsToTicks(float revealHoldSeconds)
    {
        float holdSeconds = Math.Clamp(revealHoldSeconds, 0.0f, 1.0f);
        if (holdSeconds <= 0.0f)
        {
            return 0;
        }

        float tickInterval = Server.TickInterval;
        if (!float.IsFinite(tickInterval) || tickInterval <= 0.0f)
        {
            // Startup-safe fallback when server globals are not initialized yet.
            tickInterval = 1.0f / 64.0f;
        }

        int convertedTicks = (int)Math.Ceiling(holdSeconds / tickInterval);
        return Math.Max(1, convertedTicks);
    }

    private static int ConvertUnknownStickySecondsToTicks()
    {
        float tickInterval = Server.TickInterval;
        if (!float.IsFinite(tickInterval) || tickInterval <= 0.0f)
        {
            // Startup-safe fallback when server globals are not initialized yet.
            tickInterval = 1.0f / 64.0f;
        }

        int convertedTicks = (int)Math.Ceiling(UnknownStickyWindowSeconds / tickInterval);
        return Math.Max(1, convertedTicks);
    }

    private void StoreStableDecision(int viewerSlot, int targetSlot, bool decision, int nowTick)
    {
        if ((uint)targetSlot >= VisibilitySlotCapacity)
        {
            return;
        }

        if (!_stableDecisionRows.TryGetValue(viewerSlot, out var stableRow))
        {
            stableRow = new StableDecisionRow();
            _stableDecisionRows[viewerSlot] = stableRow;
        }

        if (!stableRow.Known[targetSlot])
        {
            stableRow.Known[targetSlot] = true;
            stableRow.ActiveCount++;
        }

        stableRow.Decisions[targetSlot] = decision;
        stableRow.Ticks[targetSlot] = nowTick;
    }

    private bool TryGetStableDecision(int viewerSlot, int targetSlot, int nowTick, out bool decision)
    {
        decision = false;

        if ((uint)targetSlot >= VisibilitySlotCapacity ||
            !_stableDecisionRows.TryGetValue(viewerSlot, out var stableRow) ||
            !stableRow.Known[targetSlot])
        {
            return false;
        }

        int ageTicks = nowTick - stableRow.Ticks[targetSlot];
        if (ageTicks < 0 || ageTicks > _unknownStickyWindowTicks)
        {
            ClearStableDecisionEntry(viewerSlot, stableRow, targetSlot);
            return false;
        }

        decision = stableRow.Decisions[targetSlot];
        return true;
    }

    private bool TryResolveRevealHold(int viewerSlot, int targetSlot, int nowTick, bool countAsGenericHoldHit)
    {
        if (_revealHoldTicks <= 0)
        {
            return false;
        }

        if ((uint)targetSlot < VisibilitySlotCapacity &&
            _revealHoldRows.TryGetValue(viewerSlot, out var holdRow) &&
            holdRow.Known[targetSlot])
        {
            int holdUntilTick = holdRow.HoldUntilTick[targetSlot];
            if (holdUntilTick >= nowTick)
            {
                if (_collectDebugCounters && countAsGenericHoldHit)
                {
                    _holdHitKeepAliveInWindow++;
                }
                return true;
            }

            ClearRevealHoldEntry(viewerSlot, holdRow, targetSlot);
            if (_collectDebugCounters)
            {
                _holdExpiredInWindow++;
            }
        }

        return false;
    }

    private bool ResolveTransmitWithMemory(int viewerSlot, int targetSlot, VisibilityEval visibilityEval, int nowTick)
    {
        switch (visibilityEval)
        {
            case VisibilityEval.Visible:
                StoreStableDecision(viewerSlot, targetSlot, true, nowTick);
                if ((uint)targetSlot < VisibilitySlotCapacity)
                {
                    if (_revealHoldTicks > 0)
                    {
                        if (!_revealHoldRows.TryGetValue(viewerSlot, out var holdRow))
                        {
                            holdRow = new RevealHoldRow();
                            _revealHoldRows[viewerSlot] = holdRow;
                        }

                        if (!holdRow.Known[targetSlot])
                        {
                            holdRow.Known[targetSlot] = true;
                            holdRow.ActiveCount++;
                        }

                        holdRow.HoldUntilTick[targetSlot] = nowTick + _revealHoldTicks;

                        if (_collectDebugCounters)
                        {
                            _holdRefreshInWindow++;
                        }
                    }
                    else if (_revealHoldRows.TryGetValue(viewerSlot, out var holdRow) && holdRow.Known[targetSlot])
                    {
                        // Reveal hold disabled: remove stale memory immediately.
                        ClearRevealHoldEntry(viewerSlot, holdRow, targetSlot);
                    }
                }
                return true;

            case VisibilityEval.Hidden:
                StoreStableDecision(viewerSlot, targetSlot, false, nowTick);
                return TryResolveRevealHold(viewerSlot, targetSlot, nowTick, countAsGenericHoldHit: true);

            case VisibilityEval.UnknownTransient:
                if (_collectDebugCounters)
                {
                    _unknownEvalInWindow++;
                }

                if (TryGetStableDecision(viewerSlot, targetSlot, nowTick, out bool stickyDecision))
                {
                    if (_collectDebugCounters)
                    {
                        _unknownStickyHitInWindow++;
                    }
                    return stickyDecision;
                }

                if (TryResolveRevealHold(viewerSlot, targetSlot, nowTick, countAsGenericHoldHit: false))
                {
                    if (_collectDebugCounters)
                    {
                        _unknownHoldHitInWindow++;
                    }
                    return true;
                }

                if (_collectDebugCounters)
                {
                    _unknownFailOpenInWindow++;
                }
                return true;

            default:
                // Safety: unexpected enum values should never hide players.
                return true;
        }
    }

    private void ClearRevealHoldEntry(int viewerSlot, RevealHoldRow holdRow, int targetSlot)
    {
        if (!holdRow.Known[targetSlot])
        {
            return;
        }

        holdRow.Known[targetSlot] = false;
        holdRow.HoldUntilTick[targetSlot] = 0;
        holdRow.ActiveCount--;
        if (holdRow.ActiveCount <= 0)
        {
            _revealHoldRows.Remove(viewerSlot);
        }
    }

    private void ClearStableDecisionEntry(int viewerSlot, StableDecisionRow stableRow, int targetSlot)
    {
        if (!stableRow.Known[targetSlot])
        {
            return;
        }

        stableRow.Known[targetSlot] = false;
        stableRow.Decisions[targetSlot] = false;
        stableRow.Ticks[targetSlot] = 0;
        stableRow.ActiveCount--;
        if (stableRow.ActiveCount <= 0)
        {
            _stableDecisionRows.Remove(viewerSlot);
        }
    }

    private void RemoveTargetSlotFromRows<TRow>(Dictionary<int, TRow> rows, int targetSlot) where TRow : ISlotRow
    {
        if ((uint)targetSlot >= VisibilitySlotCapacity || rows.Count == 0)
        {
            return;
        }

        _viewerSlotsToRemove.Clear();
        foreach (var rowEntry in rows)
        {
            var row = rowEntry.Value;
            if (!row.IsTargetKnown(targetSlot))
            {
                continue;
            }

            row.ClearTargetSlot(targetSlot);
            if (row.IsEmpty)
            {
                _viewerSlotsToRemove.Add(rowEntry.Key);
            }
        }

        foreach (int viewerSlot in _viewerSlotsToRemove)
        {
            rows.Remove(viewerSlot);
        }
    }

    private void PurgeInactiveViewerRows()
    {
        // Build valid slot set from already-cached live players (no new native interop calls).
        _liveSlotSet.Clear();
        foreach (var player in _cachedLivePlayers)
        {
            _liveSlotSet.Add(player.Slot);
        }

        PurgeStaleViewerEntries(_visibilityCache, _liveSlotSet);
        PurgeStaleViewerEntries(_revealHoldRows, _liveSlotSet);
        PurgeStaleViewerEntries(_stableDecisionRows, _liveSlotSet);
    }

    private void PurgeStaleViewerEntries<TValue>(Dictionary<int, TValue> cache, HashSet<int> liveSlots)
    {
        if (cache.Count == 0)
        {
            return;
        }

        _viewerSlotsToRemove.Clear();
        foreach (var slot in cache.Keys)
        {
            if (!liveSlots.Contains(slot))
            {
                _viewerSlotsToRemove.Add(slot);
            }
        }

        foreach (int viewerSlot in _viewerSlotsToRemove)
        {
            cache.Remove(viewerSlot);
        }
    }
}
