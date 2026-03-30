namespace S2FOW.Util;

internal static class PluginOutput
{
    private const string PrefixLabel = "[S2FOW]";

    public static string Prefix(string message)
    {
        return $"{PrefixLabel} {message}";
    }
}
