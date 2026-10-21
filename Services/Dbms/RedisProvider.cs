using System.Diagnostics;

namespace DbServeApp.Services.Dbms;

/// <summary>Redis (key-value store). Commands are sent to redis-cli inside the container, one per line.</summary>
public sealed class RedisProvider(IWslDockerRunner docker, ILogger<RedisProvider> log) : IDbmsProvider
{
    public const string ProviderKey = "redis";
    private static readonly TimeSpan PullAndRunTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(60);

    public string Key => ProviderKey;
    public string DisplayName => "Redis";
    public int DefaultPort => 6379;
    public string DefaultImageTag => "8";
    // Redis has numbered databases instead of named ones; the create form does not ask for one.
    public string DefaultDatabase => "db0";
    public string ImageName => "docker.io/library/redis";
    public string AdminUser => "default";
    public string NamePrefix => "redis";

    public bool SupportsTables => false;
    public bool UsesDatabaseName => false;
    public string QueryLanguage => "Redis commands (one per line)";
    public string SampleQuery => "SET greeting \"Hello\"\nGET greeting\nKEYS *";

    public async Task<InstanceCreateResult> CreateAsync(InstanceCreateRequest req, CancellationToken ct)
    {
        var errors = InstanceValidation.Validate(req);
        if (errors.Count > 0) return InstanceCreateResult.Fail(string.Join(" ", errors));

        var id = Guid.NewGuid();
        var shortId = id.ToString("N")[..8];
        var container = $"dbapp-{Key}-{shortId}";
        var volume = $"{container}-data";
        var tag = req.ImageTag.Trim();

        try
        {
            var run = await docker.RunAsync(
            [
                "run", "-d", "--name", container,
                "--label", "dbapp.managed=true", "--label", $"dbapp.id={id}", "--label", $"dbapp.dbms={Key}",
                // Loopback only: the password is the sole protection of this server.
                "-p", $"127.0.0.1:{req.HostPort}:6379",
                // The password stays out of the command line; the shell expands it from the environment.
                "-e", $"REDIS_PASSWORD={req.Password}",
                "-v", $"{volume}:/data",
                $"{ImageName}:{tag}",
                "sh", "-c", "exec redis-server --requirepass \"$REDIS_PASSWORD\" --appendonly yes"
            ], PullAndRunTimeout, ct: ct);

            if (run.ExitCode != 0)
            {
                await CleanupAsync(container, volume);
                return InstanceCreateResult.Fail("Container run failed.", Err(run));
            }

            var instance = new DbInstance
            {
                Id = id,
                Name = req.Name.Trim(),
                DbmsKey = Key,
                ContainerName = container,
                HostPort = req.HostPort,
                CreatedUtc = DateTime.UtcNow,
                Status = InstanceStatus.Running,
                DatabaseName = "0",
                Username = AdminUser,
                Password = req.Password,
                VolumeName = volume,
                ImageTag = tag,
            };

            var ready = await WaitUntilReadyAsync(instance, ct);
            if (ready.Error is not null)
                return new InstanceCreateResult(true, instance, $"Container started, but {DisplayName} is not confirmed ready: {ready.Error}", ready.Detail);
            return new InstanceCreateResult(true, instance, $"Instance created and {DisplayName} is ready.");
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync(container, volume);
            throw;
        }
    }

    public async Task<InstanceActionResult> StartAsync(DbInstance instance, CancellationToken ct)
    {
        var r = await docker.RunAsync(["start", instance.ContainerName], ct: ct);
        return Result(r, $"Started {instance.Name}.", "Container start failed.");
    }

    public async Task<InstanceActionResult> StopAsync(DbInstance instance, CancellationToken ct)
    {
        var r = await docker.RunAsync(["stop", "-t", "30", instance.ContainerName], StopTimeout, ct: ct);
        return Result(r, $"Stopped {instance.Name}.", "Container stop failed.");
    }

    public async Task<InstanceActionResult> DeleteAsync(DbInstance instance, bool deleteVolume, CancellationToken ct)
    {
        var rm = await docker.RunAsync(["rm", "-f", instance.ContainerName], StopTimeout, ct: ct);
        if (rm.ExitCode != 0 && !Err(rm).Contains("No such container", StringComparison.OrdinalIgnoreCase))
            return new(false, "Container removal failed.", Err(rm));

        if (!deleteVolume) return new(true, $"Deleted {instance.Name}. Data volume '{instance.VolumeName}' was kept.");

        var vol = await docker.RunAsync(["volume", "rm", instance.VolumeName], ct: ct);
        if (vol.ExitCode != 0 && !Err(vol).Contains("No such volume", StringComparison.OrdinalIgnoreCase))
            return new(false, "Container removed, but the data volume could not be deleted.", Err(vol));
        return new(true, $"Deleted {instance.Name} and its data volume.");
    }

    public Task<QueryResult> ExecuteSqlAsync(DbInstance instance, string script, CancellationToken ct) =>
        RunCliAsync(instance, script, QueryTimeout, ct);

    public string BuildConnectionString(DbInstance i, bool maskPassword) =>
        $"redis://{Uri.EscapeDataString(i.Username)}:{(maskPassword ? "*****" : Uri.EscapeDataString(i.Password))}@127.0.0.1:{i.HostPort}/{i.DatabaseName}";

    private async Task<QueryResult> RunCliAsync(DbInstance instance, string script, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // The password travels as an environment variable and the commands on stdin.
        var r = await docker.RunAsync(
        [
            "exec", "-i",
            "-e", $"REDISCLI_AUTH={instance.Password}",
            instance.ContainerName,
            "redis-cli", "-h", "127.0.0.1", "-p", "6379", "-n", DatabaseIndex(instance.DatabaseName), "--no-auth-warning"
        ], timeout, script.TrimEnd() + "\n", ct);

        return new QueryResult(r.ExitCode == 0 && !r.TimedOut, r.StdOut, r.StdErr, r.ExitCode, sw.Elapsed);
    }

    private static string DatabaseIndex(string name) => int.TryParse(name, out var n) && n is >= 0 and <= 255 ? n.ToString() : "0";

    private async Task<(string? Error, string Detail)> WaitUntilReadyAsync(DbInstance instance, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyWait;
        QueryResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await RunCliAsync(instance, "PING", TimeSpan.FromSeconds(15), ct);
            if (last.IsSuccess && last.StdOut.Contains("PONG", StringComparison.Ordinal)) return (null, "");
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        return ("timed out waiting for the server to accept connections.", last is null ? "" : last.StdOut + last.StdErr);
    }

    private async Task CleanupAsync(string container, string volume)
    {
        try
        {
            await docker.RunAsync(["rm", "-f", container], StopTimeout);
            await docker.RunAsync(["volume", "rm", volume]);
        }
        catch (Exception ex) { log.LogWarning(ex, "Cleanup of {Container} failed", container); }
    }

    private static string Err(ProcessResult r) => r.StdErr.Length > 0 ? r.StdErr : r.StdOut;

    private static InstanceActionResult Result(ProcessResult r, string ok, string fail) =>
        r.ExitCode == 0 ? new(true, ok) : new(false, r.TimedOut ? "The command timed out." : fail, Err(r));
}
