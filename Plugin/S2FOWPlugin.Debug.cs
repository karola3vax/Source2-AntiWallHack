using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using S2FOW.Core;
using S2FOW.Util;
using System.Text;

namespace S2FOW;

public partial class S2FOWPlugin
{
    private void PrintStartupBanner()
    {
        string[] banner =
        [
            "",
            "  ███████╗  ██████╗  ███████╗  ██████╗  ██╗    ██╗",
            "  ██╔════╝  ╚════██╗ ██╔════╝ ██╔═══██╗ ██║    ██║",
            "  ███████╗   █████╔╝ █████╗   ██║   ██║ ██║ █╗ ██║",
            "  ╚════██║  ██╔═══╝  ██╔══╝   ██║   ██║ ██║███╗██║",
            "  ███████║  ███████╗ ██║      ╚██████╔╝ ╚███╔███╔╝",
            "  ╚══════╝  ╚══════╝ ╚═╝       ╚═════╝   ╚══╝╚══╝ ",
            "           SERVER-SIDE ANTI-WALLHACK FOR CS2           ",
            $"           Version {ModuleVersion}  |  Config v{Config.Version}  |  API {MinimumApiVersionRequired}           ",
            $"           Author: {ModuleAuthor}           ",
            $"           Steam: {AuthorSteamProfile}           ",
            $"           Discord: {AuthorDiscord}           ",
            ""
        ];

        ConsoleColor previousColor = Console.ForegroundColor;
        try
        {
            WriteBannerLines(banner, ConsoleColor.Magenta, 0, 6);
            WriteBannerLines(banner, ConsoleColor.DarkMagenta, 6, 2);
            WriteBannerLines(banner, ConsoleColor.Magenta, 8, 1);
            WriteBannerLines(banner, ConsoleColor.DarkMagenta, 9, 3);
        }
        finally
        {
            Console.ForegroundColor = previousColor;
        }
    }

    private static void WriteBannerLines(string[] lines, ConsoleColor color, int startIndex, int count)
    {
        Console.ForegroundColor = color;
        for (int i = 0; i < count; i++)
            Console.WriteLine(lines[startIndex + i]);
    }

    private void UpdateDebugOutputs(ReadOnlySpan<Models.PlayerSnapshot> snapshots, List<CCSPlayerController> players, int currentTick)
    {
        if ((Config.Debug.ShowTargetPoints || Config.Debug.ShowRayLines) && _visibilityManager != null)
            _debugAabbRenderer?.Update(snapshots, _visibilityManager, currentTick);

        if (Config.Debug.ShowRayCount)
            UpdateTraceCountOverlay(players);
    }

    private void UpdateTraceCountOverlay(List<CCSPlayerController> players)
    {
        if (!Config.Debug.ShowRayCount)
            return;

        for (int i = 0; i < players.Count; i++)
            UpdateTraceCountOverlay(players[i]);
    }

    private void UpdateTraceCountOverlay(CCSPlayerController controller)
    {
        if (_visibilityManager == null)
            return;

        int slot = controller.Slot;
        if (!FowConstants.IsValidSlot(slot) || controller.IsBot)
            return;

        _visibilityManager.GetObserverTraceCounts(
            slot,
            out int skeleton,
            out int aabb,
            out int aim,
            out int total,
            out int targets,
            out int debugPoints,
            out int debugFallbackPoints);
        _visibilityManager.GetObserverDecisionCounts(
            slot,
            out int roundStart,
            out int deathForce,
            out int distanceCull,
            out int smokeBlocked,
            out int crosshairReveal,
            out int cacheHit,
            out int liveLos,
            out int peekGrace,
            out int budgetReuse,
            out int budgetFailClosed,
            out int budgetFailOpen,
            out int fovFull,
            out int fovPeripheral,
            out int fovRear);
        int serverTotalRays = _raycastEngine?.RaycastsThisFrame ?? 0;
        long serverMinRays = _perfMonitor?.MinRaycastsPerFrame ?? 0;
        long serverMaxRays = _perfMonitor?.PeakRaycastsPerFrame ?? 0;

        int currentTick = Server.TickCount;
        if (currentTick >= _nextServerAvgOverlayRefreshTick)
        {
            int avgRefreshTicks = Math.Max(1, (int)MathF.Round(1.0f / Server.TickInterval));
            _nextServerAvgOverlayRefreshTick = currentTick + avgRefreshTicks;
            _displayedServerAvgRaycasts = _perfMonitor?.AvgRaycastsPerFrame ?? 0.0;
        }

        if (currentTick < _nextTraceOverlayUpdateTick[slot])
            return;

        int updateIntervalTicks = Math.Max(1, (int)MathF.Round(0.05f / Server.TickInterval));
        _nextTraceOverlayUpdateTick[slot] = currentTick + updateIntervalTicks;

        controller.PrintToCenterHtml(
            BuildTraceOverlayHtml(
                RaycastEngine.VisibilityPrimitiveCount,
                skeleton,
                aabb,
                aim,
                total,
                targets,
                debugPoints,
                debugFallbackPoints,
                serverTotalRays,
                serverMinRays,
                _displayedServerAvgRaycasts,
                serverMaxRays,
                BuildDecisionSummaryHtml(
                    roundStart,
                    deathForce,
                    distanceCull,
                    smokeBlocked,
                    crosshairReveal,
                    cacheHit,
                    liveLos,
                    peekGrace,
                    budgetReuse,
                    budgetFailClosed,
                    budgetFailOpen,
                    fovFull,
                    fovPeripheral,
                    fovRear)),
            1);
    }

    private static string BuildTraceOverlayHtml(
        int primitives,
        int skeleton,
        int aabb,
        int aim,
        int total,
        int targets,
        int debugPoints,
        int debugFallbackPoints,
        int serverTotalRays,
        long serverMinRays,
        double serverAvgRays,
        long serverMaxRays,
        string decisionSummary)
    {
        return $"""
<font class='fontSize-m' color='#ffffff'>
<div style='background: rgba(6, 8, 10, 0.96); padding: 12px 16px; border: 2px solid rgba(255,255,255,0.82); border-radius: 6px; text-align: left; width: 410px; line-height: 1.24;'>
<b><font color='#ffffff'>S2FOW DEBUG</font></b><br/>
<font color='#ffe27a'>ENEMIES</font> <font color='#ffffff'>{targets}</font>
<font color='#d9d9d9'>|</font>
<font color='#9cffb8'>PARTS</font> <font color='#ffffff'>{primitives}</font><br/>
<font color='#ffe27a'>CHECKS</font> <font color='#ffffff'>{total}</font>
<font color='#d9d9d9'>|</font>
<font color='#8fefff'>DIRECT</font> <font color='#ffffff'>{skeleton}</font>
<font color='#d9d9d9'>|</font>
<font color='#a8c7ff'>BACKUP</font> <font color='#ffffff'>{aabb}</font>
<font color='#d9d9d9'>|</font>
<font color='#ffcc8a'>AIMING</font> <font color='#ffffff'>{aim}</font><br/>
<font color='#ffffff'>DOTS</font> <font color='#ffffff'>{debugPoints}</font>
<font color='#d9d9d9'>|</font>
<font color='#c4d4ff'>EXTRA</font> <font color='#ffffff'>{debugFallbackPoints}</font><br/>
<font color='#ffffff'>WHY</font> {decisionSummary}<br/>
<font color='#ffb8ec'>SERVER ALL</font> <font color='#ffffff'>{serverTotalRays}</font>
<font color='#d9d9d9'>|</font>
<font color='#8fefff'>MIN</font> <font color='#ffffff'>{serverMinRays}</font>
<font color='#d9d9d9'>|</font>
<font color='#ffe7a3'>AVG</font> <font color='#ffffff'>{serverAvgRays:F1}</font>
<font color='#d9d9d9'>|</font>
<font color='#ff9b9b'>MAX</font> <font color='#ffffff'>{serverMaxRays}</font>
</div>
</font>
""";
    }

    private static string BuildDecisionSummaryHtml(
        int roundStart,
        int deathForce,
        int distanceCull,
        int smokeBlocked,
        int crosshairReveal,
        int cacheHit,
        int liveLos,
        int peekGrace,
        int budgetReuse,
        int budgetFailClosed,
        int budgetFailOpen,
        int fovFull,
        int fovPeripheral,
        int fovRear)
    {
        StringBuilder builder = new();

        void AppendToken(string label, int count, string color)
        {
            if (count <= 0)
                return;

            if (builder.Length > 0)
                builder.Append(" <font color='#7d8796'>|</font> ");

            builder.Append("<font color='");
            builder.Append(color);
            builder.Append("'>");
            builder.Append(label);
            builder.Append(' ');
            builder.Append(count);
            builder.Append("</font>");
        }

        AppendToken("DIRECT", liveLos, "#7fe7ff");
        AppendToken("REUSED", cacheHit, "#93f7b0");
        AppendToken("AIMING", crosshairReveal, "#ffb86c");
        AppendToken("SMOKE", smokeBlocked, "#b48cff");
        AppendToken("SMOOTH", peekGrace, "#f6c177");
        AppendToken("START", roundStart, "#f0d58a");
        AppendToken("DEAD", deathForce, "#ff9d9d");
        AppendToken("TOO FAR", distanceCull, "#9ab7ff");
        AppendToken("FRONT", fovFull, "#7fe7ff");
        AppendToken("SIDE", fovPeripheral, "#93f7b0");
        AppendToken("BEHIND", fovRear, "#ff6b6b");
        AppendToken("LOAD REUSE", budgetReuse, "#8be9fd");
        AppendToken("LOAD HIDE", budgetFailClosed, "#ff6b6b");
        AppendToken("LOAD SHOW", budgetFailOpen, "#50fa7b");

        return builder.Length > 0
            ? builder.ToString()
            : "<font color='#7d8796'>NONE</font>";
    }
}
