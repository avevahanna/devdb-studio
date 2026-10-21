using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace DbServeApp.Services;

public sealed record ContainerInfo(
    string Id, string Name, string Image, string State, string Status, string Health,
    string Ports, string Created, string Command, string Networks, string Project);

public sealed record ContainerStat(
    string Id, string Name, double CpuPercent, double MemPercent, double MemUsedBytes, double MemLimitBytes,
    double NetRxBytes, double NetTxBytes, double BlockReadBytes, double BlockWriteBytes, int Pids);

public sealed record DiskUsage(string Type, int Total, int Active, string Size, string Reclaimable);
public sealed record ImageInfo(string Id, string Repository, string Tag, string Size, string Created)
{
    public IReadOnlyList<string> UsedBy { get; init; } = [];
}
public sealed record VolumeInfo(string Name, string Driver)
{
    public string Mountpoint { get; init; } = "";
    public string Size { get; init; } = "";
    public IReadOnlyList<string> UsedBy { get; init; } = [];
}
public sealed record NetworkInfo(string Id, string Name, string Driver, string Scope)
{
    public string Subnet { get; init; } = "";
    public string Gateway { get; init; } = "";
    public bool Internal { get; init; }
    public IReadOnlyList<string> Attached { get; init; } = [];
}

public sealed record HostInfo(
    string ServerVersion, string OperatingSystem, string Kernel, string Architecture, int Cpus,
    double MemTotalBytes, string StorageDriver, string RootDir, string CgroupVersion, string HostName)
{
    public string LoggingDriver { get; init; } = "";
    public string DefaultRuntime { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record DockerSnapshot(
    DateTime Timestamp, HostInfo? Host, IReadOnlyList<ContainerInfo> Containers, IReadOnlyList<ContainerStat> Stats,
    IReadOnlyList<DiskUsage> Disk, IReadOnlyList<ImageInfo> Images, IReadOnlyList<VolumeInfo> Volumes,
    IReadOnlyList<NetworkInfo> Networks, string? Error, TimeSpan Elapsed,
    IReadOnlyList<string> Warnings, bool StatsOk)
{
    /// <summary>Inspect data per container, keyed by the 12-character short id.</summary>
    public IReadOnlyDictionary<string, ContainerDetail> Details { get; init; } = new Dictionary<string, ContainerDetail>();
    public HostMetrics? Metrics { get; init; }
    public IReadOnlyList<DockerEvent> Events { get; init; } = [];

    public ContainerDetail? DetailFor(string containerId) =>
        Details.TryGetValue(DockerService.ShortId(containerId), out var d) ? d : null;
}

public enum ContainerAction { Start, Stop, Restart, Pause, Unpause, Kill, Remove }

public sealed record ActionResult(bool Success, string Message);

public interface IDockerService
{
    /// <summary>Shared, cached snapshot. <paramref name="force"/> bypasses the cache and also reloads slow-changing data.</summary>
    Task<DockerSnapshot> GetSnapshotAsync(bool force = false, CancellationToken ct = default);
    Task<ActionResult> ExecuteAsync(string containerId, ContainerAction action, CancellationToken ct = default);
    Task<string> GetLogsAsync(string containerId, int tail = 200, CancellationToken ct = default);
    /// <summary>Returns docker inspect JSON with environment variable values masked.</summary>
    Task<string> GetInspectAsync(string containerId, CancellationToken ct = default);
}

public sealed partial class DockerService : IDockerService
{
    private readonly WslOptions _opt;
    private readonly ILogger<DockerService> logger;
    private readonly IContainerEngine _engine;
    private volatile bool _reset;

    public DockerService(IOptions<WslOptions> options, IContainerEngine engine, ILogger<DockerService> logger)
    {
        _opt = options.Value;
        _engine = engine;
        this.logger = logger;
        // Cached data belongs to the previous engine, so drop it on the next snapshot.
        engine.Changed += () => _reset = true;
    }

    private static readonly TimeSpan SlowTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FastTtl = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SlowTtl = TimeSpan.FromSeconds(30);
    private const int MaxTextChars = 200_000;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private DockerSnapshot? _last;
    private DateTime _fastAt = DateTime.MinValue, _slowAt = DateTime.MinValue;
    private IReadOnlyList<ContainerInfo> _containers = [];
    private IReadOnlyList<DiskUsage> _disk = [];
    private IReadOnlyList<ImageInfo> _images = [];
    private IReadOnlyList<VolumeInfo> _volumes = [];
    private IReadOnlyList<NetworkInfo> _networks = [];

    [GeneratedRegex(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]*\z")]
    private static partial Regex SafeId();

    // --exec avoids shell quoting; arguments are passed as discrete tokens.
    private async Task<ProcessResult> Docker(CancellationToken ct, TimeSpan? timeout, params string[] args)
    {
        var exe = await _engine.GetExecutableAsync(ct);
        return await ProcessRunner.RunAsync("wsl.exe", ["-d", _opt.DistroName, "--exec", exe, .. args],
            timeout ?? TimeSpan.FromSeconds(Math.Max(_opt.CommandTimeoutSeconds, 15)), ct);
    }

    private static string Err(ProcessResult r) => r.StdErr.Length > 0 ? r.StdErr : r.StdOut;

    private void ResetCache()
    {
        _last = null;
        _fastAt = _slowAt = DateTime.MinValue;
        _containers = []; _disk = []; _images = []; _volumes = []; _networks = [];
        ResetExtras();
        _warnings.Clear();
    }

    public async Task<DockerSnapshot> GetSnapshotAsync(bool force = false, CancellationToken ct = default)
    {
        var requestedAt = DateTime.UtcNow;
        // One fetch at a time, shared by all circuits; waiters get the fresh result from the cache.
        await _gate.WaitAsync(ct);
        try
        {
            if (_reset) { _reset = false; ResetCache(); }
            var now = DateTime.UtcNow;
            // A fetch that finished while we waited is already fresh enough, even for a forced request.
            if (_last is not null && (force ? _fastAt >= requestedAt : now - _fastAt < FastTtl)) return _last;

            var sw = Stopwatch.StartNew();
            var refreshSlow = force || now - _slowAt >= SlowTtl;

            var info = await Docker(ct, null, "info", "--format", "json");
            // After `wsl --shutdown` the first call boots the VM and can exceed the normal timeout.
            if (info.TimedOut)
                info = await ProcessRunner.RunAsync("wsl.exe", ["-d", _opt.DistroName, "--exec", await _engine.GetExecutableAsync(ct), "info", "--format", "json"],
                    TimeSpan.FromSeconds(_opt.ColdStartTimeoutSeconds), ct);
            if (info.ExitCode != 0)
            {
                _fastAt = DateTime.UtcNow;
                return _last = new DockerSnapshot(DateTime.Now, null, [], [], [], [], [], [], Err(info), sw.Elapsed, [], false);
            }

            var ps = Docker(ct, null, "ps", "-a", "--format", "json");
            var stats = Docker(ct, SlowTimeout, "stats", "--no-stream", "--format", "json");
            var metrics = FetchHostMetricsAsync(ct);
            Task<ProcessResult>? df = null, images = null, volumes = null, networks = null;
            Task<Dictionary<string, string>?>? volumeSizes = null;
            Task<IReadOnlyList<DockerEvent>?>? events = null;
            if (refreshSlow)
            {
                df = Docker(ct, SlowTimeout, "system", "df", "--format", "json");
                images = Docker(ct, null, "images", "--format", "json");
                volumes = Docker(ct, null, "volume", "ls", "--format", "json");
                networks = Docker(ct, null, "network", "ls", "--format", "json");
                volumeSizes = FetchVolumeSizesAsync(ct);
                events = FetchEventsAsync(ct);
            }
            await Task.WhenAll(new Task[] { ps, stats, metrics }.Concat(new Task?[] { df, images, volumes, networks, volumeSizes, events }.OfType<Task>()));

            // Failed sections keep their last good data; the warning stays until that section succeeds again.
            Update(ps.Result, "Containers", ParseContainer, ref _containers);

            // Inspect is heavier than ps, so it only reruns when the container set or a state changed (or on a slow refresh).
            var sig = string.Join('|', _containers.Select(c => c.Id + ":" + c.State));
            if (refreshSlow || sig != _detailSig)
                if (await RefreshDetailsAsync(_containers, ct)) _detailSig = sig;

            var statsOk = stats.Result.ExitCode == 0;
            IReadOnlyList<ContainerStat> statList = statsOk ? Lines(stats.Result, ParseStat) : [];
            SetWarning("Statistics", statsOk ? null : $"Container statistics unavailable: {Err(stats.Result)}");

            if (refreshSlow)
            {
                var ok = Update(df!.Result, "Disk usage", ParseDisk, ref _disk);
                ok &= Update(images!.Result, "Images", ParseImage, ref _images);
                ok &= Update(volumes!.Result, "Volumes", ParseVolume, ref _volumes);
                ok &= Update(networks!.Result, "Networks", ParseNetwork, ref _networks);
                if (volumeSizes!.Result is { } vs) _volumeSizes = vs;
                if (events!.Result is { } ev) _events = ev;
                if (networks.Result.ExitCode == 0) _netExtra = await FetchNetworkExtraAsync(_networks, ct);
                // Retry on the next call instead of serving stale data for the whole TTL.
                if (ok) _slowAt = DateTime.UtcNow;
            }

            _fastAt = DateTime.UtcNow;
            return _last = new DockerSnapshot(DateTime.Now, ParseHost(info.StdOut), _containers, statList,
                _disk, EnrichImages(), EnrichVolumes(), EnrichNetworks(), null, sw.Elapsed, _warnings.Values.ToList(), statsOk)
            {
                Details = _details,
                Metrics = metrics.Result,
                Events = _events,
            };
        }
        finally { _gate.Release(); }
    }

    private readonly Dictionary<string, string> _warnings = [];

    private void SetWarning(string section, string? message)
    {
        if (message is null) _warnings.Remove(section);
        else _warnings[section] = message;
    }

    private bool Update<T>(ProcessResult r, string what, Func<JsonElement, T> parse, ref IReadOnlyList<T> target)
    {
        if (r.ExitCode == 0)
        {
            target = Lines(r, parse);
            SetWarning(what, null);
            return true;
        }
        SetWarning(what, $"{what} could not be refreshed (showing last known data): {Err(r)}");
        return false;
    }
    public async Task<ActionResult> ExecuteAsync(string containerId, ContainerAction action, CancellationToken ct = default)
    {
        if (!SafeId().IsMatch(containerId)) return new(false, "Invalid container id.");
        string[] args = action switch
        {
            ContainerAction.Start => ["start", containerId],
            ContainerAction.Stop => ["stop", "-t", "10", containerId],
            ContainerAction.Restart => ["restart", "-t", "10", containerId],
            ContainerAction.Pause => ["pause", containerId],
            ContainerAction.Unpause => ["unpause", containerId],
            ContainerAction.Kill => ["kill", containerId],
            ContainerAction.Remove => ["rm", containerId],
            _ => throw new ArgumentOutOfRangeException(nameof(action))
        };
        var r = await Docker(ct, SlowTimeout, args);
        logger.LogInformation("Container action {Action} on {Container}: exit {ExitCode}, timedOut {TimedOut}", action, containerId, r.ExitCode, r.TimedOut);
        if (r.TimedOut)
            return new(false, $"{action} timed out; the container may still change state. Check the refreshed status.");
        return r.ExitCode == 0
            ? new(true, $"{action} succeeded for {containerId}.")
            : new(false, $"{action} failed: {Err(r)}");
    }

    public async Task<string> GetLogsAsync(string containerId, int tail = 200, CancellationToken ct = default)
    {
        if (!SafeId().IsMatch(containerId)) return "Invalid container id.";
        var r = await Docker(ct, null, "logs", "--tail", tail.ToString(CultureInfo.InvariantCulture), "--timestamps", containerId);
        // docker logs replays the container's stderr on stderr, so merge both streams.
        var text = string.Join('\n', new[] { r.StdOut, r.StdErr }.Where(s => s.Length > 0));
        if (text.Length == 0) return "(no log output)";
        return text.Length > MaxTextChars ? "…(truncated)\n" + text[^MaxTextChars..] : text;
    }

    public async Task<string> GetInspectAsync(string containerId, CancellationToken ct = default)
    {
        if (!SafeId().IsMatch(containerId)) return "Invalid container id.";
        var r = await Docker(ct, null, "inspect", containerId);
        if (r.ExitCode != 0) return Err(r);
        var text = MaskEnvironment(r.StdOut);
        return text.Length > MaxTextChars ? text[..MaxTextChars] + "\n…(truncated)" : text;
    }

    // Database images pass credentials via env vars, command lines and labels; never send those values to the browser.
    internal static string MaskEnvironment(string inspectJson)
    {
        try
        {
            var root = JsonNode.Parse(inspectJson);
            if (root is null) return "(empty)";
            if (root is JsonArray items)
            {
                foreach (var item in items)
                {
                    if (item?["Config"] is not JsonObject config) continue;
                    MaskKeyValues(config["Env"] as JsonArray);
                    MaskArgs(config["Cmd"] as JsonArray);
                    MaskArgs(config["Entrypoint"] as JsonArray);
                    // Podman records the full `podman run` command line, including -e SECRET=value.
                    MaskArgs(config["CreateCommand"] as JsonArray);
                    MaskArgs(item["Args"] as JsonArray);
                    if (config["Labels"] is JsonObject labels)
                        foreach (var k in labels.Select(kv => kv.Key).ToList()) labels[k] = "********";
                }
            }
            return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or NullReferenceException)
        {
            return "Inspect output could not be parsed, so it is hidden to avoid leaking secrets.";
        }
    }

    private static void MaskKeyValues(JsonArray? env)
    {
        if (env is null) return;
        for (var i = 0; i < env.Count; i++)
        {
            var s = env[i]?.GetValue<string>();
            var eq = s?.IndexOf('=') ?? -1;
            if (s is not null && eq > 0) env[i] = s[..(eq + 1)] + "********";
        }
    }

    // Arguments may carry secrets (--requirepass x, -p=x); keep the program and flag names, mask everything after the first argument.
    private static void MaskArgs(JsonArray? args)
    {
        if (args is null) return;
        for (var i = 1; i < args.Count; i++)
        {
            var s = args[i]?.GetValue<string>();
            if (s is null) continue;
            if (s.StartsWith('-') && s.Contains('=')) args[i] = s[..(s.IndexOf('=') + 1)] + "********";
            else if (!s.StartsWith('-')) args[i] = "********";
        }
    }
    // Docker prints one JSON object per line; Podman prints a single JSON array. Accept both.
    private static IReadOnlyList<T> Lines<T>(ProcessResult r, Func<JsonElement, T> parse)
    {
        var list = new List<T>();
        if (r.ExitCode != 0) return list;
        var text = r.StdOut.Trim();
        if (text.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                foreach (var item in doc.RootElement.EnumerateArray()) list.Add(parse(item));
            }
            catch (JsonException) { }
            return list;
        }
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            try { using var doc = JsonDocument.Parse(line); list.Add(parse(doc.RootElement)); }
            catch (JsonException) { }
        }
        return list;
    }

    // Field names differ in case and underscores between Docker and Podman ("ID"/"Id"/"id", "cpu_percent"/"CPUPerc"),
    // so lookups compare lowercase names without "_" and "-". Callers pass names in that normalized form.
    private static string Norm(string s) => s.Replace("_", "").Replace("-", "").ToLowerInvariant();

    private static JsonElement? Find(JsonElement e, string[] names)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var want in names)
            foreach (var p in e.EnumerateObject())
                if (p.Value.ValueKind != JsonValueKind.Null && Norm(p.Name) == want) return p.Value;
        return null;
    }

    private static string Text(JsonElement v, string separator = ", ") => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Number => v.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Array => string.Join(separator, v.EnumerateArray().Select(x => Text(x, separator)).Where(s => s.Length > 0)),
        _ => "",
    };

    private static string S(JsonElement e, params string[] names) => Find(e, names) is { } v ? Text(v) : "";

    private static double N(JsonElement e, params string[] names)
    {
        if (Find(e, names) is not { } v) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.GetDouble();
        return double.TryParse(Text(v), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
    }

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "kB", "MB", "GB", "TB"];
        var i = 0;
        while (bytes >= 1000 && i < units.Length - 1) { bytes /= 1000; i++; }
        return bytes.ToString(i == 0 ? "0" : "0.##", CultureInfo.InvariantCulture) + units[i];
    }

    private static string SizeText(JsonElement e, params string[] names) =>
        Find(e, names) is { ValueKind: JsonValueKind.Number } v ? FormatBytes(v.GetDouble()) : S(e, names);

    private static string EpochOrText(JsonElement e, params string[] names)
    {
        if (Find(e, names) is { ValueKind: JsonValueKind.Number } v && v.TryGetInt64(out var secs) && secs > 0)
            return DateTimeOffset.FromUnixTimeSeconds(secs).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        return S(e, names);
    }

    internal static HostInfo? ParseHost(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            // `podman info` nests everything under host/store/version.
            if (Find(e, ["host"]) is { ValueKind: JsonValueKind.Object } host)
            {
                var store = Find(e, ["store"]) ?? default;
                var version = Find(e, ["version"]) ?? default;
                var distro = Find(host, ["distribution"]) ?? default;
                var os = $"{S(distro, "distribution")} {S(distro, "version")}".Trim();
                return new HostInfo(S(version, "version"), os.Length > 0 ? os : S(host, "os"), S(host, "kernel"), S(host, "arch"),
                    (int)N(host, "cpus"), N(host, "memtotal"), S(store, "graphdrivername"), S(store, "graphroot"),
                    S(host, "cgroupversion"), S(host, "hostname"))
                {
                    LoggingDriver = S(host, "logdriver"),
                    DefaultRuntime = S(Find(host, ["ociruntime"]) ?? default, "name"),
                };
            }
            return new HostInfo(S(e, "serverversion"), S(e, "operatingsystem"), S(e, "kernelversion"), S(e, "architecture"),
                (int)N(e, "ncpu"), N(e, "memtotal"), S(e, "driver"), S(e, "dockerrootdir"), S(e, "cgroupversion"), S(e, "name"))
            {
                LoggingDriver = S(e, "loggingdriver"),
                DefaultRuntime = S(e, "defaultruntime"),
                Warnings = Find(e, ["warnings"]) is { ValueKind: JsonValueKind.Array } w
                    ? w.EnumerateArray().Select(x => Text(x)).Where(x => x.Length > 0).ToList() : [],
            };
        }
        catch (JsonException) { return null; }
    }

    private static string FormatPorts(JsonElement e)
    {
        if (Find(e, ["ports"]) is not { } v) return "";
        if (v.ValueKind != JsonValueKind.Array) return Text(v);
        return string.Join(", ", v.EnumerateArray().Select(p => p.ValueKind != JsonValueKind.Object ? Text(p) :
            N(p, "hostport") > 0
                ? $"{S(p, "hostip")}:{S(p, "hostport")}->{S(p, "containerport")}/{S(p, "protocol")}".TrimStart(':')
                : $"{S(p, "containerport")}/{S(p, "protocol")}"));
    }

    private static string ComposeProject(JsonElement e)
    {
        if (Find(e, ["labels"]) is not { } labels) return "";
        if (labels.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "com.docker.compose.project", "io.podman.compose.project" })
                if (labels.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String) return p.GetString() ?? "";
            return "";
        }
        return Text(labels).Split(',').FirstOrDefault(l => l.StartsWith("com.docker.compose.project=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? "";
    }

    internal static ContainerInfo ParseContainer(JsonElement e)
    {
        var state = S(e, "state");
        var status = S(e, "status");
        return new(S(e, "id"), S(e, "names", "name"), S(e, "image"), state, status.Length > 0 ? status : state,
            S(e, "healthstatus", "health"), FormatPorts(e), EpochOrText(e, "runningfor", "createdat", "created"),
            Find(e, ["command"]) is { } c ? Text(c, " ") : "", S(e, "networks"), ComposeProject(e));
    }

    internal static ContainerStat ParseStat(JsonElement e)
    {
        var (memUsed, memLimit) = Pair(S(e, "memusage"));
        var (rx, tx) = Pair(S(e, "netio"));
        var (br, bw) = Pair(S(e, "blockio"));
        int.TryParse(S(e, "pids", "pid"), out var pids);
        var id = S(e, "id", "containerid");
        if (id.Length == 0) id = S(e, "container");
        return new(id, S(e, "name"), Pct(S(e, "cpuperc", "cpupercent")), Pct(S(e, "memperc", "mempercent")), memUsed, memLimit, rx, tx, br, bw, pids);
    }

    private static DiskUsage ParseDisk(JsonElement e) =>
        new(S(e, "type"), (int)N(e, "totalcount", "total"), (int)N(e, "active"), SizeText(e, "size"), S(e, "reclaimable"));

    internal static ImageInfo ParseImage(JsonElement e)
    {
        var repo = S(e, "repository");
        var tag = S(e, "tag");
        if (repo.Length == 0)
        {
            // Podman lists "repo:tag" names; split at the last colon that is not part of a registry port.
            var name = Find(e, ["names", "repotags"]) is { ValueKind: JsonValueKind.Array } a && a.GetArrayLength() > 0 ? Text(a[0]) : "";
            var colon = name.LastIndexOf(':');
            if (colon > name.LastIndexOf('/')) { repo = name[..colon]; tag = name[(colon + 1)..]; }
            else repo = name;
        }
        return new(S(e, "id"), repo, tag, SizeText(e, "size"), EpochOrText(e, "createdsince", "createdat", "created"));
    }

    internal static VolumeInfo ParseVolume(JsonElement e) => new(S(e, "name"), S(e, "driver")) { Mountpoint = S(e, "mountpoint") };

    internal static NetworkInfo ParseNetwork(JsonElement e)
    {
        var scope = S(e, "scope");
        return new(S(e, "id"), S(e, "name"), S(e, "driver"), scope.Length > 0 ? scope : "local");
    }

    private static double Pct(string s) =>
        double.TryParse(s.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static (double, double) Pair(string s)
    {
        var parts = s.Split('/', StringSplitOptions.TrimEntries);
        return (ParseSize(parts.ElementAtOrDefault(0)), ParseSize(parts.ElementAtOrDefault(1)));
    }

    public static double ParseSize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return 0;
        var m = Regex.Match(s.Trim(), @"^([\d.]+)\s*([A-Za-z]*)$");
        if (!m.Success || !double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) return 0;
        double mult = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "kb" => 1e3, "mb" => 1e6, "gb" => 1e9, "tb" => 1e12,
            "kib" => 1024, "mib" => 1024d * 1024, "gib" => 1024d * 1024 * 1024, "tib" => 1024d * 1024 * 1024 * 1024,
            _ => 1
        };
        return n * mult;
    }
}
