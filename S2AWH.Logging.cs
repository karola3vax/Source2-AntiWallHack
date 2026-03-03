using CounterStrikeSharp.API;
using Microsoft.Extensions.Logging;

namespace S2AWH;

public partial class S2AWH
{
    private const string LevelInformation = "INFO";
    private const string LevelWarning = "WARN";
    private const string LevelDebug = "DEBUG";
    private const int MaxLogSentenceLength = 320;

    private void InfoLog(string whatHappened, string whyHappened, string result)
    {
        WriteLog(LevelInformation, whatHappened, whyHappened, result);
    }

    private void WarnLog(string whatHappened, string whyHappened, string result)
    {
        WriteLog(LevelWarning, whatHappened, whyHappened, result);
    }

    private void DebugLog(string whatHappened, string whyHappened, string result)
    {
        if (!_collectDebugCounters)
        {
            return;
        }

        WriteLog(LevelDebug, whatHappened, whyHappened, result);
    }

    private void DebugSummaryLog()
    {
        if (!_collectDebugCounters)
        {
            return;
        }

        var config = S2AWHState.Current;
        int serverTick = Server.TickCount;
        bool hasLivePlayers = TryGetLivePlayers(serverTick, out var livePlayers);
        int livePlayerCount = hasLivePlayers ? livePlayers.Count : 0;
        int humanPlayerCount = 0;
        int botPlayerCount = 0;

        if (hasLivePlayers)
        {
            foreach (var player in livePlayers)
            {
                if (player.IsBot)
                {
                    botPlayerCount++;
                }
                else
                {
                    humanPlayerCount++;
                }
            }
        }

        int visibilityPairCount = CountVisibilityPairEntries(livePlayerCount);
        int revealHoldPairCount = CountPairEntries(_revealHoldRows);
        int stableDecisionPairCount = CountPairEntries(_stableDecisionRows);

        int activeViewers = 0;
        for (int i = 0; i < VisibilitySlotCapacity; i++)
        {
            if (_visibilityCache[i] != null) activeViewers++;
        }

        int losSurfaceRows = Math.Clamp(config.Aabb.LosSurfaceProbeRows, 1, 3);
        int baseLosSurfaceRaysPerPair = losSurfaceRows * 4;
        int estimatedBaseLosRaysPerSnapshot = livePlayerCount > 1
            ? livePlayerCount * (livePlayerCount - 1) * baseLosSurfaceRaysPerPair
            : 0;

        float snapshotsPerSecond = 0.0f;
        if (config.Core.UpdateFrequencyTicks > 0 && Server.TickInterval > 0.0f)
        {
            snapshotsPerSecond = 1.0f / (config.Core.UpdateFrequencyTicks * Server.TickInterval);
        }

        string healthStatus = _transmitFilter == null
            ? "waiting for RayTrace"
            : hasLivePlayers
                ? "active"
                : "active (waiting for live player data)";

        var lines = new List<string>(16)
        {
            $"S2AWH status report (last {DebugSummaryIntervalTicks} ticks).",
            $"Status: {healthStatus}.",
            $"Tick: {serverTick}. Checking every {config.Core.UpdateFrequencyTicks} tick(s) ({snapshotsPerSecond:F2}/sec).",
            hasLivePlayers
                ? $"Players alive: {livePlayerCount} ({humanPlayerCount} humans, {botPlayerCount} bots)."
                : "Waiting for players to join.",
            $"Tracking: {activeViewers} viewers, {visibilityPairCount} visibility decisions.",
            $"Anti pop-in: {revealHoldPairCount} reveal holds, {stableDecisionPairCount} saved decisions.",
            $"Rays: base LOS surface probes {baseLosSurfaceRaysPerPair} per pair, ~{estimatedBaseLosRaysPerSnapshot} per scan (plus aim/micro/preload).",
            $"Hidden {_transmitHiddenEntitiesInWindow} entities from wallhacks this window.",
            $"Safety checks: {_transmitFallbackChecksInWindow} extra, {_transmitRemovalNoEffectInWindow} redundant.",
            $"Reveal hold: {_holdRefreshInWindow} refreshed, {_holdHitKeepAliveInWindow} kept alive, {_holdExpiredInWindow} expired.",
            $"Uncertain checks: {_unknownEvalInWindow} total, {_unknownStickyHitInWindow} reused, {_unknownHoldHitInWindow} held, {_unknownFailOpenInWindow} fail-open, {_unknownFromExceptionInWindow} errors.",
            "If you see counts going up, S2AWH is working."
        };

        WriteLevelBox(LevelDebug, lines);
    }

    private int CountVisibilityPairEntries(int livePlayerCount)
    {
        if (livePlayerCount <= 1)
        {
            return 0;
        }

        int total = 0;
        for (int i = 0; i < VisibilitySlotCapacity; i++)
        {
            var row = _visibilityCache[i];
            if (row == null) continue;

            var known = row.Known;
            for (int k = 0; k < known.Length; k++)
            {
                if (known[k]) total++;
            }
        }

        return total;
    }

    private static int CountPairEntries<TRow>(TRow?[] rowsArray) where TRow : ISlotRow
    {
        int total = 0;
        for (int i = 0; i < VisibilitySlotCapacity; i++)
        {
            var row = rowsArray[i];
            if (row != null)
            {
                total += row.ActiveCount;
            }
        }
        return total;
    }

    private void WriteLog(string level, string whatHappened, string whyHappened, string result)
    {
        string sentence = BuildCompactSentence(whatHappened, whyHappened, result);
        WriteLevelLine(level, sentence);
    }

    private void WriteLevelBox(string level, List<string> lines)
    {
        if (lines.Count == 0)
        {
            return;
        }

        const int maxInnerWidth = 92;
        var wrappedLines = new List<string>(lines.Count * 2);
        int widest = 0;

        foreach (var line in lines)
        {
            foreach (var wrapped in WrapLine(line, maxInnerWidth))
            {
                wrappedLines.Add(wrapped);
                if (wrapped.Length > widest)
                {
                    widest = wrapped.Length;
                }
            }
        }

        if (widest < 24)
        {
            widest = 24;
        }

        string border = "+" + new string('-', widest + 2) + "+";
        WriteLevelLine(level, border);
        foreach (var line in wrappedLines)
        {
            WriteLevelLine(level, $"| {line.PadRight(widest)} |");
        }
        WriteLevelLine(level, border);
    }

    private static IEnumerable<string> WrapLine(string text, int maxWidth)
    {
        string normalized = NormalizeClause(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            yield return string.Empty;
            yield break;
        }

        int index = 0;
        while (index < normalized.Length)
        {
            int remaining = normalized.Length - index;
            if (remaining <= maxWidth)
            {
                yield return normalized[index..];
                yield break;
            }

            int end = index + maxWidth;
            int split = normalized.LastIndexOf(' ', end - 1, maxWidth);
            if (split <= index)
            {
                split = end;
            }

            yield return normalized[index..split].TrimEnd();
            index = split;
            while (index < normalized.Length && normalized[index] == ' ')
            {
                index++;
            }
        }
    }

    private void WriteLevelLine(string level, string text)
    {
        if (Logger != null)
        {
            switch (level)
            {
                case LevelWarning:
                    Logger.LogWarning("[S2AWH] {Message}", text);
                    return;
                case LevelDebug:
                    Logger.LogDebug("[S2AWH] {Message}", text);
                    return;
                default:
                    Logger.LogInformation("[S2AWH] {Message}", text);
                    return;
            }
        }

        Console.WriteLine($"[S2AWH][{level}] {text}");
    }

    private static string BuildCompactSentence(string whatHappened, string whyHappened, string result)
    {
        string what = NormalizeClause(whatHappened);
        string why = NormalizeClause(whyHappened);
        string res = NormalizeClause(result);

        string sentence = what;
        if (!string.IsNullOrWhiteSpace(why))
        {
            sentence = $"{sentence} because {LowercaseFirst(why)}";
        }

        if (!string.IsNullOrWhiteSpace(res))
        {
            sentence = $"{sentence}, {LowercaseFirst(res)}";
        }

        sentence = EnsureSentenceEnding(sentence);
        if (sentence.Length > MaxLogSentenceLength)
        {
            sentence = sentence[..(MaxLogSentenceLength - 3)] + "...";
        }

        return sentence;
    }

    private static string NormalizeClause(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalized = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        while (normalized.Contains("  ", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);
        }

        normalized = TrimPrefix(normalized, "So ");
        normalized = TrimPrefix(normalized, "So, ");
        normalized = TrimPrefix(normalized, "Therefore ");
        normalized = TrimPrefix(normalized, "Therefore, ");
        normalized = TrimPrefix(normalized, "As a result ");
        normalized = TrimPrefix(normalized, "As a result, ");
        normalized = normalized.TrimEnd('.', ';', ':', ' ');
        return normalized;
    }

    private static string TrimPrefix(string value, string prefix)
    {
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..].TrimStart()
            : value;
    }

    private static string LowercaseFirst(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.Length == 1)
        {
            return char.IsUpper(value[0])
                ? char.ToLowerInvariant(value[0]).ToString()
                : value;
        }

        // Only lowercase sentence-like words (e.g. "The ...").
        // Keep product names/acronyms like "S2AWH" unchanged.
        if (char.IsUpper(value[0]) && char.IsLower(value[1]))
        {
            return char.ToLowerInvariant(value[0]) + value[1..];
        }

        return value;
    }

    private static string EnsureSentenceEnding(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return ".";
        }

        char last = value[^1];
        return last is '.' or '!' or '?' ? value : value + ".";
    }
}
