using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Modules.Cvars;
using S2FOW.Util;

namespace S2FOW.Config;

/// <summary>
/// Detects the active game mode from server convars and map prefix,
/// then resolves the appropriate <see cref="GameModeProfile"/>.
/// </summary>
internal static class AutoConfigProfiler
{
    // CS2 game_type / game_mode combinations (from Valve documentation):
    //   game_type=0, game_mode=0 → Casual
    //   game_type=0, game_mode=1 → Competitive
    //   game_type=0, game_mode=2 → Wingman
    //   game_type=1, game_mode=0 → Arms Race
    //   game_type=1, game_mode=1 → Demolition
    //   game_type=1, game_mode=2 → Deathmatch
    //   game_type=3, game_mode=0 → Custom
    //   game_type=4, game_mode=0 → Guardian
    //   game_type=4, game_mode=1 → Co-op Strike

    /// <summary>
    /// Detects the game mode from server state and returns the matching profile.
    /// </summary>
    public static GameModeProfile Detect()
    {
        int gameType = GetConvarInt("game_type");
        int gameMode = GetConvarInt("game_mode");

        // Check convars first — most reliable.
        if (gameType == 0 && gameMode == 1)
            return GameModeProfile.Competitive5v5;

        if (gameType == 0 && gameMode == 2)
            return GameModeProfile.Wingman;

        if (gameType == 1 && gameMode == 2)
            return GameModeProfile.Deathmatch;

        if (gameType == 0 && gameMode == 0)
        {
            // Casual — but check if it's actually a retake server.
            if (IsRetakeServer())
                return GameModeProfile.Retake;
            return GameModeProfile.Casual;
        }

        // Fallback: check map prefix for common community modes.
        string mapName = GetCurrentMapName();
        if (IsRetakeMap(mapName))
            return GameModeProfile.Retake;
        if (IsDeathmatchMap(mapName))
            return GameModeProfile.Deathmatch;

        // Check max players as a final heuristic.
        int maxPlayers = GetConvarInt("sv_maxplayers");
        if (maxPlayers <= 0)
            maxPlayers = GetConvarInt("maxplayers");

        if (maxPlayers > 0 && maxPlayers <= 4)
            return GameModeProfile.Wingman;
        if (maxPlayers > 0 && maxPlayers <= 12)
            return GameModeProfile.Competitive5v5;
        if (maxPlayers > 24)
            return GameModeProfile.Deathmatch;

        return GameModeProfile.Casual;
    }

    /// <summary>
    /// Returns a human-readable name for the profile.
    /// </summary>
    public static string GetProfileName(GameModeProfile profile)
    {
        return profile switch
        {
            GameModeProfile.Auto => "Auto",
            GameModeProfile.Competitive5v5 => "Competitive 5v5",
            GameModeProfile.Wingman => "Wingman 2v2",
            GameModeProfile.Casual => "Casual",
            GameModeProfile.Deathmatch => "Deathmatch",
            GameModeProfile.Retake => "Retake",
            GameModeProfile.Custom => "Custom (no auto-tuning)",
            _ => "Unknown"
        };
    }

    private static bool IsRetakeServer()
    {
        // Common retake plugin convars.
        int retakeEnabled = GetConvarInt("sm_retakes_enabled");
        if (retakeEnabled > 0)
            return true;

        int retakesLive = GetConvarInt("css_retakes_enabled");
        if (retakesLive > 0)
            return true;

        return false;
    }

    private static bool IsRetakeMap(string mapName)
    {
        return mapName.StartsWith("retake_", StringComparison.OrdinalIgnoreCase) ||
               mapName.StartsWith("retakes_", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeathmatchMap(string mapName)
    {
        return mapName.StartsWith("dm_", StringComparison.OrdinalIgnoreCase) ||
               mapName.StartsWith("aim_", StringComparison.OrdinalIgnoreCase) ||
               mapName.StartsWith("ffa_", StringComparison.OrdinalIgnoreCase) ||
               mapName.StartsWith("hs_", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCurrentMapName()
    {
        try
        {
            return Server.MapName ?? string.Empty;
        }
        catch
        {
            PluginDiagnostics.RecordAutoProfileProbeError();
            return string.Empty;
        }
    }

    private static int GetConvarInt(string name)
    {
        try
        {
            var cvar = ConVar.Find(name);
            return cvar?.GetPrimitiveValue<int>() ?? 0;
        }
        catch
        {
            PluginDiagnostics.RecordAutoProfileProbeError();
            return 0;
        }
    }
}
