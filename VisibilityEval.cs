namespace S2AWH;

internal enum VisibilityEval : byte
{
    Hidden = 0,
    Visible = 1,
    UnknownTransient = 2
}

internal readonly struct VisibilityDecision
{
    public VisibilityEval Eval { get; }
    public bool IsPredictiveVisible { get; }

    public VisibilityDecision(VisibilityEval eval, bool isPredictiveVisible = false)
    {
        Eval = eval;
        IsPredictiveVisible = isPredictiveVisible;
    }
}
