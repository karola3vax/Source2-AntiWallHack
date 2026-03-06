using System.Diagnostics.CodeAnalysis;

[assembly: SuppressMessage(
    "Design",
    "CA1724:Type names should not match namespaces",
    Justification = "Plugin entry type intentionally matches the plugin identifier used by existing deployments.",
    Scope = "type",
    Target = "~T:S2AWH.S2AWH")]

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
    "Performance",
    "CA1814:Prefer jagged arrays over multidimensional",
    Justification = "The ray counter is a fixed slot x stage grid; rectangular arrays keep the hot-path indexing simpler and predictable.",
    Scope = "member",
    Target = "~F:S2AWH.S2AWH._viewerRayCountsWorking")]
[assembly: SuppressMessage(
    "Performance",
    "CA1814:Prefer jagged arrays over multidimensional",
    Justification = "The ray counter is a fixed slot x stage grid; rectangular arrays keep the hot-path indexing simpler and predictable.",
    Scope = "member",
    Target = "~F:S2AWH.S2AWH._viewerRayCountsDisplay")]

[assembly: SuppressMessage(
    "Performance",
    "CA1848:Use the LoggerMessage delegates",
    Justification = "Logging already flows through one small helper; delegate plumbing would add boilerplate without changing the runtime hot paths.",
    Scope = "member",
    Target = "~M:S2AWH.S2AWH.WriteLevelLine(System.String,System.String)")]
