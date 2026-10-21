using System.Globalization;
using System.Text.RegularExpressions;

namespace DbServeApp.Services.Dbms;

/// <summary>redis-cli commands and parsing of INFO output.</summary>
public static partial class RedisScripts
{
    public const string Insight = "INFO\nCONFIG GET maxclients";

    /// <summary>Redis has a fixed number of numbered databases (16 by default).</summary>
    public static IReadOnlyList<string> Databases { get; } = Enumerable.Range(0, 16).Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList();

    [GeneratedRegex(@"^db\d+:keys=", RegexOptions.Multiline)]
    private static partial Regex KeyspaceLine();

    public static DbInsight ParseInsight(string output)
    {
        var lines = output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        string? Value(string name) => lines.FirstOrDefault(l => l.StartsWith(name + ":", StringComparison.Ordinal))?[(name.Length + 1)..];

        if (!int.TryParse(Value("connected_clients"), NumberStyles.None, CultureInfo.InvariantCulture, out var clients))
            return DbInsight.Fail("Unexpected result from the database.");

        var maxIndex = Array.IndexOf(lines, "maxclients");
        var max = maxIndex >= 0 && maxIndex + 1 < lines.Length
            && int.TryParse(lines[maxIndex + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : 0;
        double.TryParse(Value("used_memory"), NumberStyles.Float, CultureInfo.InvariantCulture, out var bytes);
        var databases = lines.Count(l => KeyspaceLine().IsMatch(l));

        // The probe connection itself is counted, so it is left out.
        return new DbInsight(true, Math.Max(0, clients - 1), max, databases, Math.Round(bytes / 1048576.0, 1), null);
    }
}
