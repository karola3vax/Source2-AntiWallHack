namespace S2FOW.Core;

[Flags]
public enum ObserverFullUpdateReason
{
    None = 0,
    Hide = 1 << 0,
    Unhide = 1 << 1,
    OrphanCleanup = 1 << 2,
    SafetyClear = 1 << 3,
    PhaseBypass = 1 << 4,
    Toggle = 1 << 5
}
