using S2FOW.Config;

namespace S2FOW.Util;

/// <summary>
/// Text strings and message builders used for console output.
/// Centralizes all user-facing text so it can be easily found and updated.
/// </summary>
internal static class PluginText
{
    /// <summary>Description shown for the css_fow_stats command.</summary>
    public static readonly string StatsCommandDescription = "Show the current S2FOW health summary";

    /// <summary>Description shown for the css_fow_toggle command.</summary>
    public static readonly string ToggleCommandDescription = "Turn S2FOW protection on or off";

    /// <summary>
    /// Builds the ASCII art banner that is printed to the server console when the plugin loads.
    /// Shows the plugin name, version, config version, API version, and author info.
    /// </summary>
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
            "           SERVER-SIDE ANTI-WALLHACK FOR CS2           ",
            $"           Version {moduleVersion}  |  Config v{configVersion}  |  API {minimumApiVersion}           ",
            $"           Author: {moduleAuthor}           ",
            $"           Steam: {authorSteamProfile}           ",
            $"           Discord: {authorDiscord}           ",
            ""
        ];
    }

    /// <summary>Builds a one-line summary of the protection status and config version.</summary>
    public static string BuildStartupConfigLine(bool enabled, int configVersion)
    {
        string state = enabled ? "Protection on" : "Protection off";
        return $"Coverage mode: {state} | Config v{configVersion}";
    }

    /// <summary>
    /// Builds a one-line summary of what entity types are currently protected.
    /// Always includes player entities and their associated closure. Adds "smoke blocking" if enabled.
    /// </summary>
    public static string BuildCoverageLine(AntiWallhackSettings antiWallhack)
    {
        List<string> protections = new(6) { "players", "weapons", "wearables", "scene children", "hostage carry" };

        if (antiWallhack.SmokeBlocksWallhack)
            protections.Add("smoke blocking");

        return $"Coverage: {string.Join(", ", protections)}";
    }
}
