using System.Text;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using S2FOW.Core;
using S2FOW.Util;

namespace S2FOW;

/// <summary>
/// Debug visualization and console output — startup banner, in-game HUD overlay,
/// and debug beam/point management.
///
/// When Debug.ShowRayCount is enabled, every human player sees a HUD overlay showing:
///   - How many enemies are being checked.
///   - How many raycasts (direct skeleton + backup AABB) were performed.
///   - Why each target was shown or hidden (line-of-sight, smoke, death grace, etc.).
///   - Total server-wide raycast count for this frame.
///
/// The overlay is rendered as HTML using PrintToCenterHtml, which CS2 renders as a
/// semi-transparent panel in the center of the screen.
/// </summary>
public partial class S2FOWPlugin
{
    // ────────────────────────────────────────────────────────────────────────
    //  Startup banner
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Prints the ASCII art "S2FOW" logo and version info to the server console
    /// when the plugin loads. Uses magenta coloring for visual prominence.
    /// </summary>
    private void PrintStartupBanner()
    {
        string[] banner = PluginText.BuildBanner(
            ModuleVersion,
            Config.Version,
            MinimumApiVersionRequired,
            ModuleAuthor,
            AuthorSteamProfile,
            AuthorDiscord);

        ConsoleColor previousColor = Console.ForegroundColor;
        try
        {
            // ASCII art lines in bright magenta.
            WriteBannerLines(banner, ConsoleColor.Magenta, 0, Math.Min(7, banner.Length));
            // Version/author lines in darker magenta.
            if (banner.Length > 7)
                WriteBannerLines(banner, ConsoleColor.DarkMagenta, 7, banner.Length - 7);
        }
        finally
        {
            Console.ForegroundColor = previousColor;
        }
    }

    /// <summary>Writes a block of banner lines to the console in a specific color.</summary>
    private static void WriteBannerLines(string[] lines, ConsoleColor color, int startIndex, int count)
    {
        Console.ForegroundColor = color;
        for (int i = 0; i < count; i++)
            Console.WriteLine(lines[startIndex + i]);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  Debug output per frame
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Called at the end of each CheckTransmit frame to update all debug visualizations.
    /// Updates the in-world beam visuals (target points, ray lines) and the HUD overlay.
    /// </summary>
    private void UpdateDebugOutputs(ReadOnlySpan<Models.PlayerSnapshot> snapshots, List<CCSPlayerController> players, int currentTick)
    {
        // Update 3D in-world beams (points on enemies and/or ray lines).
        if ((Config.Debug.ShowTargetPoints || Config.Debug.ShowRayLines) && _visibilityManager != null)
            _debugAabbRenderer?.Update(snapshots, _visibilityManager, currentTick);

        // Update the on-screen HUD overlay with ray counts and decision breakdowns.
        if (Config.Debug.ShowRayCount)
            UpdateTraceCountOverlay(players);
    }

    /// <summary>Updates the trace count HUD overlay for all connected human players.</summary>
    private void UpdateTraceCountOverlay(List<CCSPlayerController> players)
    {
        if (!Config.Debug.ShowRayCount)
            return;

        for (int i = 0; i < players.Count; i++)
            UpdateTraceCountOverlay(players[i]);
    }

    /// <summary>
    /// Updates the trace count HUD overlay for a single player.
    /// Gathers this player's per-frame trace counts and decision reasons
    /// from the VisibilityManager, then renders them as an HTML panel.
    /// </summary>
    private void UpdateTraceCountOverlay(CCSPlayerController controller)
    {
        if (_visibilityManager == null)
            return;

        int slot = controller.Slot;
        if (!FowConstants.IsValidSlot(slot) || controller.IsBot)
            return;

        // Get this observer's raycasting stats for the current frame.
        _visibilityManager.GetObserverTraceCounts(
            slot,
            out int skeleton,       // Rays cast to skeleton body points.
            out int aabb,           // Rays cast to AABB fallback corners.
            out int total,          // Total rays (skeleton + AABB).
            out int targets,        // How many enemies were checked.
            out int debugPoints,    // Number of check points rendered.
            out int debugFallbackPoints);  // Number of fallback points rendered.

        // Get this observer's decision breakdown (why each target was shown/hidden).
        _visibilityManager.GetObserverDecisionCounts(
            slot,
            out int roundStart,     // Targets shown because of round-start grace.
            out int deathForce,     // Targets shown because they just died.
            out int smokeBlocked,   // Targets hidden because smoke blocked all rays.
            out int liveLos,        // Targets checked via actual line-of-sight rays.
            out int budgetFailOpen); // Targets force-shown because ray budget ran out.

        int serverTotalRays = _raycastEngine?.RaycastsThisFrame ?? 0;

        // Render the overlay HTML and send it to the player's screen.
        controller.PrintToCenterHtml(
            BuildTraceOverlayHtml(
                RaycastEngine.VisibilityPrimitiveCount,
                skeleton,
                aabb,
                total,
                targets,
                debugPoints,
                debugFallbackPoints,
                serverTotalRays,
                BuildDecisionSummaryHtml(
                    roundStart,
                    deathForce,
                    smokeBlocked,
                    liveLos,
                    budgetFailOpen)),
            1);
    }

    // ────────────────────────────────────────────────────────────────────────
    //  HTML builders for the debug overlay
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the complete HTML for the debug overlay panel.
    /// Shows enemy count, ray breakdown (direct vs backup), point counts,
    /// decision reasons, and server-wide total rays.
    /// </summary>
    private static string BuildTraceOverlayHtml(
        int primitives,
        int skeleton,
        int aabb,
        int total,
        int targets,
        int debugPoints,
        int debugFallbackPoints,
        int serverTotalRays,
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
<font color='#a8c7ff'>BACKUP</font> <font color='#ffffff'>{aabb}</font><br/>
<font color='#ffffff'>DOTS</font> <font color='#ffffff'>{debugPoints}</font>
<font color='#d9d9d9'>|</font>
<font color='#c4d4ff'>EXTRA</font> <font color='#ffffff'>{debugFallbackPoints}</font><br/>
<font color='#ffffff'>WHY</font> {decisionSummary}<br/>
<font color='#ffb8ec'>SERVER THIS FRAME</font> <font color='#ffffff'>{serverTotalRays}</font>
</div>
</font>
""";
    }

    /// <summary>
    /// Builds the "WHY" line of the debug overlay — a colored summary of why each
    /// target was shown or hidden. Each reason gets a different color:
    ///   - DIRECT (cyan): checked via line-of-sight rays.
    ///   - SMOKE (purple): hidden because smoke blocked all rays.
    ///   - START (gold): shown because of round-start grace period.
    ///   - DEAD (red): shown because they just died (death grace).
    ///   - LOAD SHOW (green): force-shown because the ray budget ran out.
    /// </summary>
    private static string BuildDecisionSummaryHtml(
        int roundStart,
        int deathForce,
        int smokeBlocked,
        int liveLos,
        int budgetFailOpen)
    {
        StringBuilder builder = new();

        // Helper that appends a colored "LABEL count" token, separated by pipes.
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
        AppendToken("SMOKE", smokeBlocked, "#b48cff");
        AppendToken("START", roundStart, "#f0d58a");
        AppendToken("DEAD", deathForce, "#ff9d9d");
        AppendToken("LOAD SHOW", budgetFailOpen, "#50fa7b");

        return builder.Length > 0
            ? builder.ToString()
            : "<font color='#7d8796'>NONE</font>";
    }
}
