namespace S2FOW.Util;

/// <summary>
/// Adds the "[S2FOW]" prefix to messages so they are easily identifiable
/// in the server console or chat output.
/// </summary>
internal static class PluginOutput
{
    /// <summary>The tag shown before every plugin message.</summary>
    private const string PrefixLabel = "[S2FOW]";

    /// <summary>Prepends "[S2FOW] " to any message string.</summary>
    public static string Prefix(string message)
    {
        return $"{PrefixLabel} {message}";
    }
}
