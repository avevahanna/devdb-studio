using System.Globalization;
using System.Text.Json;

namespace DbServeApp.Services;

public sealed record MountInfo(string Type, string Name, string Source, string Destination, bool ReadWrite);
public sealed record NetAttach(string Network, string Ip, string Gateway);

/// <summary>Per-container data from <c>inspect</c>. Environment variables and command lines are deliberately not included.</summary>
public sealed record ContainerDetail(
    string Id, DateTimeOffset? Created, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt, int RestartCount, int ExitCode,
    bool OomKilled, string Error, string RestartPolicy, double MemLimitBytes, double CpuLimit, int Pid, string ImageId,
    string HealthStatus, string HealthOutput, IReadOnlyList<MountInfo> Mounts, IReadOnlyList<NetAttach> Networks);

/// <summary>Resources of the WSL distro that hosts the container engine, read from /proc and df.</summary>
public sealed record HostMetrics(
    double Load1, double Load5, double Load15, double UptimeSeconds,
    double MemTotalBytes, double MemAvailableBytes, double SwapTotalBytes, double SwapFreeBytes,
    double DiskTotalBytes, double DiskUsedBytes);

public sealed record DockerEvent(DateTimeOffset Time, string Type, string Action, string Name, string Detail);

internal sealed record NetworkExtra(string Subnet, string Gateway, bool Internal, IReadOnlyList<string> Attached);

public sealed partial class DockerService
{
    private IReadOnlyDictionary<string, ContainerDetail> _details = new Dictionary<string, ContainerDetail>();
    private string _detailSig = "";
    private IReadOnlyList<DockerEvent> _events = [];
    private Dictionary<string, string> _volumeSizes = [];
    private IReadOnlyDictionary<string, NetworkExtra> _netExtra = new Dictionary<string, NetworkExtra>();

    private void ResetExtras()
    {
        _details = new Dictionary<string, ContainerDetail>();
        _detailSig = "";
        _events = [];
        _volumeSizes = [];
        _netExtra = new Dictionary<string, NetworkExtra>();
    }

    /// <summary>First 12 characters of an id, without a "sha256:" prefix; the form <c>ps</c> and <c>stats</c> use.</summary>
    public static string ShortId(string id)
    {
        var s = id.StartsWith("sha256:", StringComparison.Ordinal) ? id[7..] : id;
        return s.Length > 12 ? s[..12] : s;
    }

    // ---- host metrics -------------------------------------------------------------------------------------------

    private async Task<HostMetrics?> FetchHostMetricsAsync(CancellationToken ct)
    {
        const string script = "cat /proc/loadavg; cat /proc/uptime; grep -E '^(MemTotal|MemAvailable|SwapTotal|SwapFree):' /proc/meminfo; df -Pk /";
        try
        {
            var r = await ProcessRunner.RunAsync("wsl.exe", ["-d", _opt.DistroName, "--exec", "sh", "-c", script],
                TimeSpan.FromSeconds(Math.Max(_opt.CommandTimeoutSeconds, 15)), ct);
            return r.ExitCode == 0 ? ParseHostMetrics(r.StdOut) : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Host metrics unavailable");
            return null;
        }
    }

    internal static HostMetrics? ParseHostMetrics(string text)
    {
        var lines = text.Replace("\r", "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length < 2) return null;
        var load = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var up = lines[1].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (load.Length < 3 || up.Length < 1) return null;

        static double D(string? s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        double Kb(string key)
        {
            var line = lines.FirstOrDefault(l => l.StartsWith(key + ":", StringComparison.Ordinal));
            var p = line?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return p is { Length: > 1 } ? D(p[1]) * 1024 : 0;
        }

        double diskTotal = 0, diskUsed = 0;
        var df = lines.LastOrDefault(l => l.EndsWith(" /", StringComparison.Ordinal));
        if (df is not null)
        {
            var p = df.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length >= 6) { diskTotal = D(p[1]) * 1024; diskUsed = D(p[2]) * 1024; }
        }
        return new HostMetrics(D(load[0]), D(load[1]), D(load[2]), D(up[0]),
            Kb("MemTotal"), Kb("MemAvailable"), Kb("SwapTotal"), Kb("SwapFree"), diskTotal, diskUsed);
    }

    // ---- inspect ------------------------------------------------------------------------------------------------

    private async Task<bool> RefreshDetailsAsync(IReadOnlyList<ContainerInfo> containers, CancellationToken ct)
    {
        var ids = containers.Select(c => c.Id).Where(id => SafeId().IsMatch(id)).ToArray();
        if (ids.Length == 0)
        {
            _details = new Dictionary<string, ContainerDetail>();
            SetWarning("Container details", null);
            return true;
        }
        var r = await Docker(ct, SlowTimeout, new[] { "inspect" }.Concat(ids).ToArray());
        if (r.ExitCode != 0)
        {
            SetWarning("Container details", $"Container details could not be refreshed (showing last known data): {Err(r)}");
            return false;
        }
        _details = Lines(r, ParseDetail).GroupBy(d => ShortId(d.Id)).ToDictionary(g => g.Key, g => g.First());
        SetWarning("Container details", null);
        return true;
    }

    private static DateTimeOffset? Date(string s) =>
        DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d.Year > 1 ? d : null;

    internal static ContainerDetail ParseDetail(JsonElement e)
    {
        var state = Find(e, ["state"]) ?? default;
        var host = Find(e, ["hostconfig"]) ?? default;
        var health = Find(state, ["health"]) ?? default;

        var healthOut = "";
        if (Find(health, ["log"]) is { ValueKind: JsonValueKind.Array } log && log.GetArrayLength() > 0)
            healthOut = S(log[log.GetArrayLength() - 1], "output").Trim();

        var restarts = N(e, "restartcount");
        if (restarts == 0) restarts = N(state, "restartcount");

        var cpu = N(host, "nanocpus") / 1e9;
        if (cpu == 0 && N(host, "cpuquota") > 0)
        {
            var period = N(host, "cpuperiod");
            cpu = N(host, "cpuquota") / (period > 0 ? period : 100_000);
        }

        var mounts = new List<MountInfo>();
        if (Find(e, ["mounts"]) is { ValueKind: JsonValueKind.Array } ma)
            foreach (var m in ma.EnumerateArray())
                mounts.Add(new MountInfo(S(m, "type"), S(m, "name"), S(m, "source"), S(m, "destination", "target"),
                    Find(m, ["rw"]) is not { ValueKind: JsonValueKind.False }));

        var nets = new List<NetAttach>();
        var settings = Find(e, ["networksettings"]) ?? default;
        if (Find(settings, ["networks"]) is { ValueKind: JsonValueKind.Object } na)
            foreach (var n in na.EnumerateObject())
                nets.Add(new NetAttach(n.Name, S(n.Value, "ipaddress"), S(n.Value, "gateway")));

        return new ContainerDetail(
            S(e, "id"), Date(S(e, "created")), Date(S(state, "startedat")), Date(S(state, "finishedat")),
            (int)restarts, (int)N(state, "exitcode"),
            Find(state, ["oomkilled"]) is { ValueKind: JsonValueKind.True }, S(state, "error"),
            S(Find(host, ["restartpolicy"]) ?? default, "name"), N(host, "memory"), cpu, (int)N(state, "pid"),
            S(e, "image", "imageid"), S(health, "status"), healthOut, mounts, nets);
    }

    // ---- volumes, images, networks ------------------------------------------------------------------------------

    // `system df -v` is the only source of volume sizes. Engines that format it differently simply yield no sizes.
    private async Task<Dictionary<string, string>?> FetchVolumeSizesAsync(CancellationToken ct)
    {
        try
        {
            var r = await Docker(ct, SlowTimeout, "system", "df", "-v", "--format", "json");
            if (r.ExitCode != 0) return null;
            using var doc = JsonDocument.Parse(r.StdOut.Trim());
            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            if (Find(doc.RootElement, ["volumes"]) is { ValueKind: JsonValueKind.Array } arr)
                foreach (var v in arr.EnumerateArray())
                {
                    var size = SizeText(v, "size");
                    if (S(v, "name") is { Length: > 0 } name && size.Length > 0 && size != "N/A") result[name] = size;
                }
            return result;
        }
        catch (JsonException) { return null; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Volume sizes unavailable");
            return null;
        }
    }

    private async Task<IReadOnlyDictionary<string, NetworkExtra>> FetchNetworkExtraAsync(IReadOnlyList<NetworkInfo> networks, CancellationToken ct)
    {
        var result = new Dictionary<string, NetworkExtra>();
        var ids = networks.Select(n => n.Id).Where(id => SafeId().IsMatch(id)).ToArray();
        if (ids.Length == 0) return result;
        var r = await Docker(ct, SlowTimeout, new[] { "network", "inspect" }.Concat(ids).ToArray());
        if (r.ExitCode != 0) return _netExtra;
        foreach (var (id, extra) in Lines(r, ParseNetworkExtra)) result[ShortId(id)] = extra;
        return result;
    }

    internal static (string Id, NetworkExtra Extra) ParseNetworkExtra(JsonElement e)
    {
        string subnet = "", gateway = "";
        var ipam = Find(e, ["ipam"]) ?? default;
        // Docker: IPAM.Config[]; Podman: subnets[] (top level or under IPAM).
        var first = Find(ipam, ["config"]) is { ValueKind: JsonValueKind.Array } cfg && cfg.GetArrayLength() > 0 ? cfg[0]
            : Find(e, ["subnets"]) is { ValueKind: JsonValueKind.Array } sn && sn.GetArrayLength() > 0 ? sn[0]
            : default;
        subnet = S(first, "subnet");
        gateway = S(first, "gateway");

        var attached = new List<string>();
        if (Find(e, ["containers"]) is { ValueKind: JsonValueKind.Object } cs)
            foreach (var c in cs.EnumerateObject())
            {
                var ip = S(c.Value, "ipv4address");
                attached.Add(ip.Length > 0 ? $"{S(c.Value, "name")} ({ip.Split('/')[0]})" : S(c.Value, "name"));
            }
        return (S(e, "id"), new NetworkExtra(subnet, gateway, Find(e, ["internal"]) is { ValueKind: JsonValueKind.True }, attached));
    }

    private IReadOnlyList<ImageInfo> EnrichImages() =>
        _images.Select(i => i with
        {
            UsedBy = _containers
                .Where(c => _details.TryGetValue(ShortId(c.Id), out var d) && d.ImageId.Length > 0 && ShortId(d.ImageId) == ShortId(i.Id))
                .Select(c => c.Name).ToList()
        }).ToList();

    private IReadOnlyList<VolumeInfo> EnrichVolumes() =>
        _volumes.Select(v => v with
        {
            Size = _volumeSizes.GetValueOrDefault(v.Name, ""),
            UsedBy = _containers
                .Where(c => _details.TryGetValue(ShortId(c.Id), out var d) && d.Mounts.Any(m => m.Name == v.Name))
                .Select(c => c.Name).ToList()
        }).ToList();

    private IReadOnlyList<NetworkInfo> EnrichNetworks() =>
        _networks.Select(n => _netExtra.TryGetValue(ShortId(n.Id), out var x)
            ? n with { Subnet = x.Subnet, Gateway = x.Gateway, Internal = x.Internal, Attached = x.Attached }
            : n).ToList();

    // ---- events -------------------------------------------------------------------------------------------------

    // The filters skip the exec_* noise that every query run through this app produces.
    private static readonly string[] EventNames =
        ["create", "start", "die", "stop", "kill", "oom", "restart", "pause", "unpause", "destroy", "health_status"];

    private async Task<IReadOnlyList<DockerEvent>?> FetchEventsAsync(CancellationToken ct)
    {
        try
        {
            var args = new List<string> { "events", "--since", "1h", "--until", "0s", "--format", "json", "--filter", "type=container" };
            foreach (var n in EventNames) { args.Add("--filter"); args.Add($"event={n}"); }
            var r = await Docker(ct, SlowTimeout, args.ToArray());
            if (r.ExitCode != 0) return null;
            return Lines(r, ParseEvent).OrderByDescending(x => x.Time).Take(60).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Events unavailable");
            return null;
        }
    }

    internal static DockerEvent ParseEvent(JsonElement e)
    {
        var attrs = Find(Find(e, ["actor"]) ?? default, ["attributes"]) ?? default;
        var name = S(attrs, "name");
        if (name.Length == 0) name = S(e, "name");
        var action = S(e, "action", "status");
        var detail = S(attrs, "exitcode") is { Length: > 0 } code ? $"exit code {code}" : "";
        if (detail.Length == 0 && S(attrs, "image") is { Length: > 0 } image) detail = image;
        return new DockerEvent(EventTime(e), S(e, "type"), action, name, detail);
    }

    private static DateTimeOffset EventTime(JsonElement e)
    {
        foreach (var key in new[] { "timenano", "time" })
        {
            if (Find(e, [key]) is not { } v) continue;
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
                return n switch
                {
                    > 100_000_000_000_000_000 => DateTimeOffset.FromUnixTimeMilliseconds(n / 1_000_000),
                    > 100_000_000_000 => DateTimeOffset.FromUnixTimeMilliseconds(n),
                    _ => DateTimeOffset.FromUnixTimeSeconds(n),
                };
            if (Date(Text(v)) is { } d) return d;
        }
        return DateTimeOffset.MinValue;
    }
}
