using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DevDbStudio.Services.Dbms;

/// <summary>One HTTP request typed on the Query page: <c>METHOD /path [JSON body]</c>.</summary>
public sealed record CouchRequest(string Method, string Path, string? Body);

/// <summary>Parsing of CouchDB query scripts, the curl configuration they are run with, and formatting of the replies.</summary>
public static partial class CouchDbScripts
{
    public const string Insight = "GET /_dbs_info";
    public const string ListDatabases = "GET /_all_dbs";

    // Must not start with '@': curl would read a write-out value starting with '@' from a file.
    internal const string StatusMarker = "DDS-HTTP-STATUS";
    private const int MaxPathLength = 2048;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"^(GET|PUT|POST|DELETE|HEAD)\s+(\S+)\s*(.*)$", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex RequestLine();

    [GeneratedRegex(@"^(GET|PUT|POST|DELETE|HEAD) (\S+) -> (\d{3})$", RegexOptions.Multiline)]
    private static partial Regex ResultHeader();

    [GeneratedRegex(@"DDS-HTTP-STATUS (\d{3})\r?\n?")]
    private static partial Regex StatusLine();

    /// <summary>
    /// Splits a script into requests. A line starting with a method begins a request; any other line continues the previous
    /// request's JSON body. Blank lines and lines starting with <c>#</c> or <c>//</c> are ignored.
    /// </summary>
    public static (IReadOnlyList<CouchRequest> Requests, string? Error) Parse(string script)
    {
        var requests = new List<(string Method, string Path, StringBuilder Body)>();
        var lineNo = 0;
        foreach (var raw in script.Replace("\r", "").Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;

            var m = RequestLine().Match(line);
            if (m.Success && m.Groups[2].Value.StartsWith('/'))
            {
                var path = m.Groups[2].Value;
                if (path.Length > MaxPathLength || path.Any(char.IsControl))
                    return ([], $"Line {lineNo}: the path is not valid.");
                var body = new StringBuilder(m.Groups[3].Value);
                requests.Add((m.Groups[1].Value.ToUpperInvariant(), path, body));
            }
            else if (requests.Count > 0 && !LooksLikeRequest(line))
            {
                if (requests[^1].Body.Length > 0) requests[^1].Body.Append('\n');
                requests[^1].Body.Append(line);
            }
            else
            {
                return ([], $"Line {lineNo}: expected METHOD /path [JSON body], for example: GET /_all_dbs (methods: GET, PUT, POST, DELETE, HEAD).");
            }
        }

        if (requests.Count == 0) return ([], "Write at least one request, for example: GET /_all_dbs");
        return (requests.Select(r => new CouchRequest(r.Method, r.Path, r.Body.Length == 0 ? null : r.Body.ToString())).ToList(), null);
    }

    private static bool LooksLikeRequest(string line) =>
        RequestLine().IsMatch(line) || line.Split(' ', 2)[0].ToUpperInvariant() is "GET" or "PUT" or "POST" or "DELETE" or "HEAD" or "COPY" or "PATCH" or "OPTIONS";

    /// <summary>
    /// curl configuration for <c>curl -K -</c>. Everything (credentials, paths, bodies) is quoted into the configuration, which
    /// travels on stdin, so nothing reaches a shell or a command line.
    /// </summary>
    public static string BuildCurlConfig(IEnumerable<CouchRequest> requests, string baseUrl, string user, string password)
    {
        var sb = new StringBuilder();
        var first = true;
        foreach (var r in requests)
        {
            if (!first) sb.Append("next\n");
            first = false;
            sb.Append("silent\nshow-error\ngloboff\nmax-time = 60\n");
            sb.Append("user = ").Append(Quote(user + ":" + password)).Append('\n');
            sb.Append("url = ").Append(Quote(baseUrl + r.Path)).Append('\n');
            if (r.Method == "HEAD") sb.Append("head\n");
            else sb.Append("request = ").Append(Quote(r.Method)).Append('\n');
            sb.Append("header = \"Accept: application/json\"\n");
            if (r.Body is not null)
            {
                sb.Append("header = \"Content-Type: application/json\"\n");
                // data-raw never treats a leading '@' as a file name.
                sb.Append("data-raw = ").Append(Quote(r.Body)).Append('\n');
            }
            sb.Append("write-out = \"").Append(StatusMarker).Append(" %{http_code}\\n\"\n");
        }
        return sb.ToString();
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Pairs curl's output (body, then a status marker, per request) with the requests and indents JSON bodies.</summary>
    public static (string Text, bool AllSucceeded) Format(IReadOnlyList<CouchRequest> requests, string curlOutput)
    {
        var sb = new StringBuilder();
        var ok = true;
        var pos = 0;
        var i = 0;
        foreach (Match m in StatusLine().Matches(curlOutput))
        {
            var body = curlOutput[pos..m.Index].Trim();
            pos = m.Index + m.Length;
            var status = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (status is < 200 or >= 400) ok = false;

            var req = i < requests.Count ? requests[i] : null;
            sb.Append(req?.Method ?? "?").Append(' ').Append(req?.Path ?? "?").Append(" -> ").Append(status).Append('\n');
            if (body.Length > 0) sb.Append(Indent(body)).Append('\n');
            sb.Append('\n');
            i++;
        }
        // Requests without a reply mean curl failed (for example a timeout or a connection error).
        if (i < requests.Count) ok = false;
        return (sb.ToString().TrimEnd() + "\n", ok);
    }

    private static string Indent(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return JsonSerializer.Serialize(doc.RootElement, Pretty);
        }
        catch (JsonException) { return body; }
    }

    /// <summary>Splits text produced by <see cref="Format"/> back into per-request results.</summary>
    public static IReadOnlyList<(string Path, int Status, string Body)> SplitResults(string output)
    {
        var matches = ResultHeader().Matches(output);
        var list = new List<(string, int, string)>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index + matches[i].Length;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : output.Length;
            list.Add((matches[i].Groups[2].Value, int.Parse(matches[i].Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture), output[start..end].Trim()));
        }
        return list;
    }

    private static bool IsSystemDatabase(string name) => name.StartsWith('_');

    public static IReadOnlyList<string> ParseDatabases(string output)
    {
        try
        {
            var body = SplitResults(output).FirstOrDefault(r => r.Status == 200).Body;
            if (body is null) return [];
            return (JsonSerializer.Deserialize<List<string>>(body) ?? []).Where(n => !IsSystemDatabase(n)).Order(StringComparer.Ordinal).ToList();
        }
        catch (JsonException) { return []; }
    }

    public static DbInsight ParseInsight(string output)
    {
        try
        {
            var result = SplitResults(output).FirstOrDefault();
            if (result.Body is null) return DbInsight.Fail("Unexpected result from the database.");
            if (result.Status != 200) return DbInsight.Fail("The database did not return its database list (CouchDB 3.2 or later is needed for the size figures).");

            using var doc = JsonDocument.Parse(result.Body);
            var databases = 0;
            double bytes = 0;
            foreach (var d in doc.RootElement.EnumerateArray())
            {
                if (d.GetProperty("key").GetString() is { } key && !IsSystemDatabase(key)) databases++;
                if (d.TryGetProperty("info", out var info) && info.TryGetProperty("sizes", out var sizes) && sizes.TryGetProperty("file", out var file) && file.TryGetDouble(out var f))
                    bytes += f;
            }
            // CouchDB does not report a connection count or limit.
            return new DbInsight(true, 0, 0, databases, Math.Round(bytes / 1048576.0, 1), null);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return DbInsight.Fail("Unexpected result from the database.");
        }
    }
}
