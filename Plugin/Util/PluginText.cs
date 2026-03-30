using S2FOW.Config;

namespace S2FOW.Util;

internal static class PluginText
{
    public static readonly string StatsCommandDescription = "Show the current S2FOW health summary";
    public static readonly string ToggleCommandDescription = "Turn S2FOW protection on or off";
    public static readonly string ProfileCommandDescription = "Show or override the active S2FOW auto-profile";

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

    public static string BuildStartupProfileLine(bool enabled, SecurityProfile profile, int configVersion)
    {
        string state = enabled ? "Protection on" : "Protection off";
        return $"Coverage mode: {state} | {profile} mode | Config v{configVersion}";
    }

    public static string BuildCoverageLine(
        AntiWallhackSettings antiWallhack,
        bool plantedC4RadarProtection,
        bool plantedC4EntityProtection)
    {
        List<string> protections = new(8) { "players" };

        if (antiWallhack.BlockRadarESP)
            protections.Add("radar");
        if (antiWallhack.BlockGrenadeESP)
            protections.Add("grenades");
        if (antiWallhack.BlockBulletImpactESP)
            protections.Add("impacts");
        if (antiWallhack.BlockDroppedWeaponESPDurationTicks > 0)
            protections.Add("dropped weapons");
        if (plantedC4RadarProtection)
            protections.Add("bomb radar");
        if (plantedC4EntityProtection)
            protections.Add("planted C4 entity");
        if (antiWallhack.SmokeBlocksWallhack)
            protections.Add("smoke blocking");

        return $"Coverage: {string.Join(", ", protections)}";
    }

    public static string[] BuildLegacyConfigWarning(string configPath)
    {
        return
        [
            "Old config format detected.",
            "S2FOW now expects the newer grouped config layout.",
            $"Update this file before enabling protection: {configPath}",
            "Protection has been left disabled until the config is updated."
        ];
    }
}
