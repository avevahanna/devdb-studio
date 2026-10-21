using System.Text.Json;

namespace DevDbStudio.Services.Dbms;

/// <summary>mongosh snippets and parsing of their JSON output.</summary>
public static class MongoScripts
{
    public const string ListDatabases =
        "JSON.stringify(db.adminCommand({ listDatabases: 1, nameOnly: true }).databases.map(d => d.name).filter(n => !['admin','config','local'].includes(n)).sort())";

    public const string Insight =
        "(() => { const a = db.getSiblingDB('admin'); const c = a.serverStatus().connections; " +
        "const l = a.adminCommand({ listDatabases: 1 }); " +
        "return JSON.stringify({ current: c.current, available: c.available, " +
        "databases: l.databases.filter(d => !['admin','config','local'].includes(d.name)).length, bytes: Number(l.totalSize) }); })()";

    // mongosh may print warnings around the value, so the last line that looks like JSON wins.
    private static string? LastJsonLine(string output) =>
        output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(l => l.StartsWith('{') || l.StartsWith('['));

    public static IReadOnlyList<string> ParseDatabases(string output)
    {
        try
        {
            var line = LastJsonLine(output);
            return line is null ? [] : JsonSerializer.Deserialize<List<string>>(line) ?? [];
        }
        catch (JsonException) { return []; }
    }

    public static DbInsight ParseInsight(string output)
    {
        try
        {
            var line = LastJsonLine(output);
            if (line is null) return DbInsight.Fail("Unexpected result from the database.");
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            var current = r.GetProperty("current").GetInt32();
            var available = r.GetProperty("available").GetInt32();
            // The probe connection itself is counted, so it is left out.
            return new DbInsight(true, Math.Max(0, current - 1), current + available,
                r.GetProperty("databases").GetInt32(), Math.Round(r.GetProperty("bytes").GetDouble() / 1048576.0, 1), null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return DbInsight.Fail("Unexpected result from the database.");
        }
    }
}
