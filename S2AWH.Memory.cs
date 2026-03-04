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
        if ((uint)targetSlot >= VisibilitySlotCapacity || (uint)viewerSlot >= VisibilitySlotCapacity)
        {
            return;
        }

        StableDecisionRow? stableRow = _stableDecisionRows[viewerSlot];
        if (stableRow == null)
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

        if ((uint)targetSlot >= VisibilitySlotCapacity || (uint)viewerSlot >= VisibilitySlotCapacity)
        {
            return false;
        }

        StableDecisionRow? stableRow = _stableDecisionRows[viewerSlot];
        if (stableRow == null || !stableRow.Known[targetSlot])
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

        if ((uint)targetSlot < VisibilitySlotCapacity && (uint)viewerSlot < VisibilitySlotCapacity)
        {
            RevealHoldRow? holdRow = _revealHoldRows[viewerSlot];
            if (holdRow != null && holdRow.Known[targetSlot])
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
        }

        return false;
    }

    private bool ResolveTransmitWithMemory(int viewerSlot, int targetSlot, VisibilityDecision visibilityDecision, int nowTick)
    {
        VisibilityEval visibilityEval = visibilityDecision.Eval;

        switch (visibilityEval)
        {
            case VisibilityEval.Visible:
                if (!visibilityDecision.IsPredictiveVisible &&
                    !TryResolveVisibleReacquire(viewerSlot, targetSlot, nowTick))
                {
                    StoreStableDecision(viewerSlot, targetSlot, false, nowTick);
                    return false;
                }

                if (visibilityDecision.IsPredictiveVisible)
                {
                    ClearVisibleConfirmEntry(viewerSlot, targetSlot);
                }

                StoreStableDecision(viewerSlot, targetSlot, true, nowTick);
                if ((uint)targetSlot < VisibilitySlotCapacity)
                {
                    if (_revealHoldTicks > 0)
                    {
                        RevealHoldRow? holdRow = null;
                        if ((uint)viewerSlot < VisibilitySlotCapacity)
                        {
                            holdRow = _revealHoldRows[viewerSlot];
                            if (holdRow == null)
                            {
                                holdRow = new RevealHoldRow();
                                _revealHoldRows[viewerSlot] = holdRow;
                            }
                        }

                        if (holdRow != null)
                        {
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
                    }
                    else if ((uint)viewerSlot < VisibilitySlotCapacity)
                    {
                        RevealHoldRow? holdRow = _revealHoldRows[viewerSlot];
                        if (holdRow != null && holdRow.Known[targetSlot])
                        {
                            ClearRevealHoldEntry(viewerSlot, holdRow, targetSlot);
                        }
                    }
                }
                return true;

            case VisibilityEval.Hidden:
                ClearVisibleConfirmEntry(viewerSlot, targetSlot);
                StoreStableDecision(viewerSlot, targetSlot, false, nowTick);
                return TryResolveRevealHold(viewerSlot, targetSlot, nowTick, countAsGenericHoldHit: true);

            case VisibilityEval.UnknownTransient:
                if (_collectDebugCounters)
                {
                    _unknownEvalInWindow++;
                }

                if (HasPendingVisibleConfirm(viewerSlot, targetSlot))
                {
                    return false;
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
        if (holdRow.ActiveCount <= 0 && (uint)viewerSlot < VisibilitySlotCapacity)
        {
            _revealHoldRows[viewerSlot] = null;
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
        if (stableRow.ActiveCount <= 0 && (uint)viewerSlot < VisibilitySlotCapacity)
        {
            _stableDecisionRows[viewerSlot] = null;
        }
    }

    private bool TryResolveVisibleReacquire(int viewerSlot, int targetSlot, int nowTick)
    {
        if (VisibleReacquireConfirmTicks <= 0 ||
            (uint)viewerSlot >= VisibilitySlotCapacity ||
            (uint)targetSlot >= VisibilitySlotCapacity)
        {
            return true;
        }

        StableDecisionRow? stableRow = _stableDecisionRows[viewerSlot];
        bool wasHidden = stableRow != null &&
                         stableRow.Known[targetSlot] &&
                         !stableRow.Decisions[targetSlot];
        if (!wasHidden)
        {
            ClearVisibleConfirmEntry(viewerSlot, targetSlot);
            return true;
        }

        VisibleConfirmRow? confirmRow = _visibleConfirmRows[viewerSlot];
        if (confirmRow == null)
        {
            confirmRow = new VisibleConfirmRow();
            _visibleConfirmRows[viewerSlot] = confirmRow;
        }

        if (!confirmRow.Known[targetSlot])
        {
            confirmRow.Known[targetSlot] = true;
            confirmRow.FirstVisibleTick[targetSlot] = nowTick;
            confirmRow.ActiveCount++;
            return false;
        }

        if ((nowTick - confirmRow.FirstVisibleTick[targetSlot]) < VisibleReacquireConfirmTicks)
        {
            return false;
        }

        ClearVisibleConfirmEntry(viewerSlot, targetSlot);
        return true;
    }

    private bool HasPendingVisibleConfirm(int viewerSlot, int targetSlot)
    {
        if ((uint)viewerSlot >= VisibilitySlotCapacity || (uint)targetSlot >= VisibilitySlotCapacity)
        {
            return false;
        }

        VisibleConfirmRow? confirmRow = _visibleConfirmRows[viewerSlot];
        return confirmRow != null && confirmRow.Known[targetSlot];
    }

    private void ClearVisibleConfirmEntry(int viewerSlot, int targetSlot)
    {
        if ((uint)viewerSlot >= VisibilitySlotCapacity || (uint)targetSlot >= VisibilitySlotCapacity)
        {
            return;
        }

        VisibleConfirmRow? confirmRow = _visibleConfirmRows[viewerSlot];
        if (confirmRow == null || !confirmRow.Known[targetSlot])
        {
            return;
        }

        confirmRow.Known[targetSlot] = false;
        confirmRow.FirstVisibleTick[targetSlot] = 0;
        confirmRow.ActiveCount--;
        if (confirmRow.ActiveCount <= 0)
        {
            _visibleConfirmRows[viewerSlot] = null;
        }
    }

    private void PurgeInactiveViewerRows()
    {
        for (int i = 0; i < VisibilitySlotCapacity; i++)
        {
            if (!_liveSlotFlags[i])
            {
                _visibilityCache[i] = null;
                _revealHoldRows[i] = null;
                _stableDecisionRows[i] = null;
                _visibleConfirmRows[i] = null;
            }
        }
    }
}
