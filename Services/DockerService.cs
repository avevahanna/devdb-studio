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
public sealed record ImageInfo(string Id, string Repository, string Tag, string Size, string Created);
public sealed record VolumeInfo(string Name, string Driver);
public sealed record NetworkInfo(string Id, string Name, string Driver, string Scope);

public sealed record HostInfo(
    string ServerVersion, string OperatingSystem, string Kernel, string Architecture, int Cpus,
    double MemTotalBytes, string StorageDriver, string RootDir, string CgroupVersion, string HostName);

public sealed record DockerSnapshot(
    DateTime Timestamp, HostInfo? Host, IReadOnlyList<ContainerInfo> Containers, IReadOnlyList<ContainerStat> Stats,
    IReadOnlyList<DiskUsage> Disk, IReadOnlyList<ImageInfo> Images, IReadOnlyList<VolumeInfo> Volumes,
    IReadOnlyList<NetworkInfo> Networks, string? Error, TimeSpan Elapsed,
    IReadOnlyList<string> Warnings, bool StatsOk);

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

public sealed partial class DockerService(IOptions<WslOptions> options, ILogger<DockerService> logger) : IDockerService
{
    private readonly WslOptions _opt = options.Value;
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
    private Task<ProcessResult> Docker(CancellationToken ct, TimeSpan? timeout, params string[] args) =>
        ProcessRunner.RunAsync("wsl.exe", ["-d", _opt.DistroName, "--exec", "docker", .. args],
            timeout ?? TimeSpan.FromSeconds(Math.Max(_opt.CommandTimeoutSeconds, 15)), ct);

    private static string Err(ProcessResult r) => r.StdErr.Length > 0 ? r.StdErr : r.StdOut;

    public async Task<DockerSnapshot> GetSnapshotAsync(bool force = false, CancellationToken ct = default)
    {
        var requestedAt = DateTime.UtcNow;
        // One fetch at a time, shared by all circuits; waiters get the fresh result from the cache.
        await _gate.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;
            // A fetch that finished while we waited is already fresh enough, even for a forced request.
            if (_last is not null && (force ? _fastAt >= requestedAt : now - _fastAt < FastTtl)) return _last;

            var sw = Stopwatch.StartNew();
            var refreshSlow = force || now - _slowAt >= SlowTtl;

            var info = await Docker(ct, null, "info", "--format", "json");
            // After `wsl --shutdown` the first call boots the VM and can exceed the normal timeout.
            if (info.TimedOut)
                info = await ProcessRunner.RunAsync("wsl.exe", ["-d", _opt.DistroName, "--exec", "docker", "info", "--format", "json"],
                    TimeSpan.FromSeconds(_opt.ColdStartTimeoutSeconds), ct);
            if (info.ExitCode != 0)
            {
                _fastAt = DateTime.UtcNow;
                return _last = new DockerSnapshot(DateTime.Now, null, [], [], [], [], [], [], Err(info), sw.Elapsed, [], false);
            }

            var ps = Docker(ct, null, "ps", "-a", "--format", "json");
            var stats = Docker(ct, SlowTimeout, "stats", "--no-stream", "--format", "json");
            Task<ProcessResult>? df = null, images = null, volumes = null, networks = null;
            if (refreshSlow)
            {
                df = Docker(ct, SlowTimeout, "system", "df", "--format", "json");
                images = Docker(ct, null, "images", "--format", "json");
                volumes = Docker(ct, null, "volume", "ls", "--format", "json");
                networks = Docker(ct, null, "network", "ls", "--format", "json");
            }
            await Task.WhenAll(new Task[] { ps, stats }.Concat(new[] { df, images, volumes, networks }.OfType<Task>()));

            // Failed sections keep their last good data; the warning stays until that section succeeds again.
            Update(ps.Result, "Containers", ParseContainer, ref _containers);

            var statsOk = stats.Result.ExitCode == 0;
            IReadOnlyList<ContainerStat> statList = statsOk ? Lines(stats.Result, ParseStat) : [];
            SetWarning("Statistics", statsOk ? null : $"Container statistics unavailable: {Err(stats.Result)}");

            if (refreshSlow)
            {
                var ok = Update(df!.Result, "Disk usage", ParseDisk, ref _disk);
                ok &= Update(images!.Result, "Images", ParseImage, ref _images);
                ok &= Update(volumes!.Result, "Volumes", ParseVolume, ref _volumes);
                ok &= Update(networks!.Result, "Networks", ParseNetwork, ref _networks);
                // Retry on the next call instead of serving stale data for the whole TTL.
                if (ok) _slowAt = DateTime.UtcNow;
            }

            _fastAt = DateTime.UtcNow;
            return _last = new DockerSnapshot(DateTime.Now, ParseHost(info.StdOut), _containers, statList,
                _disk, _images, _volumes, _networks, null, sw.Elapsed, _warnings.Values.ToList(), statsOk);
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
    private static IReadOnlyList<T> Lines<T>(ProcessResult r, Func<JsonElement, T> parse)
    {
        var list = new List<T>();
        if (r.ExitCode != 0) return list;
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            try { using var doc = JsonDocument.Parse(line); list.Add(parse(doc.RootElement)); }
            catch (JsonException) { }
        }
        return list;
    }

    private static string S(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double N(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static HostInfo? ParseHost(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var e = doc.RootElement;
            return new HostInfo(S(e, "ServerVersion"), S(e, "OperatingSystem"), S(e, "KernelVersion"), S(e, "Architecture"),
                (int)N(e, "NCPU"), N(e, "MemTotal"), S(e, "Driver"), S(e, "DockerRootDir"), S(e, "CgroupVersion"), S(e, "Name"));
        }
        catch (JsonException) { return null; }
    }

    private static ContainerInfo ParseContainer(JsonElement e)
    {
        var labels = S(e, "Labels");
        var project = labels.Split(',').FirstOrDefault(l => l.StartsWith("com.docker.compose.project=", StringComparison.Ordinal))?.Split('=', 2)[1] ?? "";
        return new(S(e, "ID"), S(e, "Names"), S(e, "Image"), S(e, "State"), S(e, "Status"), S(e, "HealthStatus"),
            S(e, "Ports"), S(e, "RunningFor"), S(e, "Command"), S(e, "Networks"), project);
    }

    private static ContainerStat ParseStat(JsonElement e)
    {
        var (memUsed, memLimit) = Pair(S(e, "MemUsage"));
        var (rx, tx) = Pair(S(e, "NetIO"));
        var (br, bw) = Pair(S(e, "BlockIO"));
        int.TryParse(S(e, "PIDs"), out var pids);
        var id = S(e, "ID");
        if (id.Length == 0) id = S(e, "Container");
        return new(id, S(e, "Name"), Pct(S(e, "CPUPerc")), Pct(S(e, "MemPerc")), memUsed, memLimit, rx, tx, br, bw, pids);
    }

    private static DiskUsage ParseDisk(JsonElement e) =>
        new(S(e, "Type"), int.TryParse(S(e, "TotalCount"), out var t) ? t : 0, int.TryParse(S(e, "Active"), out var a) ? a : 0,
            S(e, "Size"), S(e, "Reclaimable"));

    private static ImageInfo ParseImage(JsonElement e) =>
        new(S(e, "ID"), S(e, "Repository"), S(e, "Tag"), S(e, "Size"), S(e, "CreatedSince"));

    private static VolumeInfo ParseVolume(JsonElement e) => new(S(e, "Name"), S(e, "Driver"));
    private static NetworkInfo ParseNetwork(JsonElement e) => new(S(e, "ID"), S(e, "Name"), S(e, "Driver"), S(e, "Scope"));

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
