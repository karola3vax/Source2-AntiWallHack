using S2FOW.Config;

namespace S2FOW.Util;

/// <summary>
/// Text strings and message builders used for console output.
/// </summary>
internal static class PluginText
{
    public static readonly string StatsCommandDescription =
        "Show S2FOW status, workload, warnings, and crash protection counters";

    public static readonly string ToggleCommandDescription =
        "Turn S2FOW player hiding on or off";

    public static string[] BuildBanner(
        string moduleVersion,
        int configVersion,
        int minimumApiVersion,
        string moduleAuthor,
        string authorSteamProfile,
        string authorDiscord)
    {
        return
        [
            "",
            "   SSSSS    22222   FFFFF   OOOOO   W     W",
            "  SS       22   22  FF     OO   OO  W     W",
            "   SSSSS        22  FFFF   OO   OO  W  W  W",
            "      SS      22    FF     OO   OO  W WWW W",
            "  SSSSS     222222  FF      OOOOO    WW WW ",
            "                                              ",
            "           SERVER-SIDE PLAYER VISIBILITY FOR CS2           ",
            $"           Version {moduleVersion} | Config schema v{configVersion} | API {minimumApiVersion}           ",
            "           Requires RayTrace | Uses full-update crash recovery           ",
            $"           Author: {moduleAuthor}           ",
            $"           Steam: {authorSteamProfile}           ",
            $"           Discord: {authorDiscord}           ",
            ""
        ];
    }

    public static string BuildStartupConfigLine(bool enabled, bool crashRecoveryReady, int configVersion)
    {
        string state = enabled
            ? crashRecoveryReady
                ? "protection on"
                : "protection paused because crash recovery is unavailable"
            : "protection off";
        string crashRecoveryState = crashRecoveryReady ? "crash recovery ready" : "crash recovery not ready";
        return $"Status: {state} | {crashRecoveryState} | config schema v{configVersion}";
    }

    public static string BuildCoverageLine(AntiWallhackSettings antiWallhack)
    {
        List<string> coveredItems = new(6) { "player bodies", "weapons", "wearables", "scene children", "carried hostages" };

        if (antiWallhack.SmokeBlocksWallhack)
            coveredItems.Add("players behind smoke");
        else
            coveredItems.Add("smoke hiding off");

        return $"What S2FOW can hide: {string.Join(", ", coveredItems)}";
    }
}
