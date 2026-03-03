using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Design",
    "CA1724:Type names should not match namespaces",
    Justification = "Plugin entry type intentionally matches the plugin identifier used by existing deployments.",
    Scope = "type",
    Target = "~T:S2AWH.S2AWH")]

[assembly: SuppressMessage(
    "Design",
    "CA1034:Do not nest type",
    Justification = "Nested settings types intentionally mirror JSON config sections.",
    Scope = "type",
    Target = "~T:S2AWH.S2AWHConfig+CoreSettings")]
[assembly: SuppressMessage(
    "Design",
    "CA1034:Do not nest type",
    Justification = "Nested settings types intentionally mirror JSON config sections.",
    Scope = "type",
    Target = "~T:S2AWH.S2AWHConfig+TraceSettings")]
[assembly: SuppressMessage(
    "Design",
    "CA1034:Do not nest type",
    Justification = "Nested settings types intentionally mirror JSON config sections.",
    Scope = "type",
    Target = "~T:S2AWH.S2AWHConfig+PreloadSettings")]
[assembly: SuppressMessage(
    "Design",
    "CA1034:Do not nest type",
    Justification = "Nested settings types intentionally mirror JSON config sections.",
    Scope = "type",
    Target = "~T:S2AWH.S2AWHConfig+AabbSettings")]
[assembly: SuppressMessage(
    "Design",
    "CA1034:Do not nest type",
    Justification = "Nested settings types intentionally mirror JSON config sections.",
    Scope = "type",
    Target = "~T:S2AWH.S2AWHConfig+VisibilitySettings")]
[assembly: SuppressMessage(
    "Design",
    "CA1034:Do not nest type",
    Justification = "Nested settings types intentionally mirror JSON config sections.",
    Scope = "type",
    Target = "~T:S2AWH.S2AWHConfig+DiagnosticsSettings")]

[assembly: SuppressMessage(
    "Design",
    "CA1031:Do not catch general exception types",
    Justification = "External native/plugin boundaries can throw heterogeneous exceptions; this path must fail-open.",
    Scope = "member",
    Target = "~M:S2AWH.S2AWH.TryInitializeModules(System.String)")]
[assembly: SuppressMessage(
    "Design",
    "CA1031:Do not catch general exception types",
    Justification = "Visibility evaluation must remain fail-open and non-crashing.",
    Scope = "member",
    Target = "~M:S2AWH.S2AWH.EvaluateVisibilitySafe(System.Int32,System.Int32,System.Boolean,S2AWH.S2AWHConfig,System.Int32,System.String)")]
[assembly: SuppressMessage(
    "Design",
    "CA1031:Do not catch general exception types",
    Justification = "Player enumeration can fail transiently during engine state transitions; plugin should continue safely.",
    Scope = "member",
    Target = "~M:S2AWH.S2AWH.TryGetLivePlayers(System.Int32,System.Collections.Generic.List{CounterStrikeSharp.API.Core.CCSPlayerController}@)")]
[assembly: SuppressMessage(
    "Design",
    "CA1031:Do not catch general exception types",
    Justification = "Weapon service resolution is external/native-facing and must remain resilient.",
    Scope = "member",
    Target = "~M:S2AWH.S2AWH.TryGetTargetTransmitEntities(CounterStrikeSharp.API.Core.CCSPlayerController,System.Int32,S2AWH.S2AWH+TargetTransmitEntities@)")]
