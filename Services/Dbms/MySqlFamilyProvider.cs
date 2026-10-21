using System.Collections.Concurrent;
using System.Diagnostics;

namespace DbServeApp.Services.Dbms;

/// <summary>
/// Shared container handling for the MySQL protocol family (MariaDB, and MySQL later). Subclasses only describe
/// the image, environment variable names and client binaries that differ between the two.
/// </summary>
public abstract class MySqlFamilyProvider(IWslDockerRunner docker, ILogger log) : IDbmsProvider
{
    private static readonly TimeSpan PullAndRunTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(90);

    // The client binary differs between images (mariadb vs mysql); detect once per container.
    private readonly ConcurrentDictionary<string, string> _clients = new();

    public abstract string Key { get; }
    public abstract string DisplayName { get; }
    public abstract string DefaultImageTag { get; }
    public abstract string ImageName { get; }
    public abstract string NamePrefix { get; }
    public virtual int DefaultPort => 3306;
    public virtual string DefaultDatabase => "appdb";
    public string AdminUser => "root";

    protected abstract string RootPasswordEnv { get; }
    protected abstract string DatabaseEnv { get; }
    /// <summary>Client binaries to try, in order of preference.</summary>
    protected abstract string[] ClientCandidates { get; }
    /// <summary>Extra client options, used to switch off TLS: traffic only crosses the container's own loopback.</summary>
    protected abstract string[] ClientExtraArgs { get; }
    /// <summary>Statement that makes the server abort any statement running longer than <paramref name="seconds"/>.</summary>
    protected abstract string StatementTimeoutSql(int seconds);

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
                "-p", $"127.0.0.1:{req.HostPort}:3306",
                "-e", $"{RootPasswordEnv}={req.Password}",
                "-e", $"{DatabaseEnv}={req.DatabaseName}",
                "-v", $"{volume}:/var/lib/mysql",
                $"{ImageName}:{tag}"
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
                DatabaseName = req.DatabaseName,
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
        _clients.TryRemove(instance.ContainerName, out _);
        var rm = await docker.RunAsync(["rm", "-f", instance.ContainerName], StopTimeout, ct: ct);
        if (rm.ExitCode != 0 && !Err(rm).Contains("No such container", StringComparison.OrdinalIgnoreCase))
            return new(false, "Container removal failed.", Err(rm));

        if (!deleteVolume) return new(true, $"Deleted {instance.Name}. Data volume '{instance.VolumeName}' was kept.");

        var vol = await docker.RunAsync(["volume", "rm", instance.VolumeName], ct: ct);
        if (vol.ExitCode != 0 && !Err(vol).Contains("No such volume", StringComparison.OrdinalIgnoreCase))
            return new(false, "Container removed, but the data volume could not be deleted.", Err(vol));
        return new(true, $"Deleted {instance.Name} and its data volume.");
    }

    public Task<QueryResult> ExecuteSqlAsync(DbInstance instance, string sql, CancellationToken ct) =>
        RunClientAsync(instance, sql, QueryTimeout, ct);

    public string BuildConnectionString(DbInstance i, bool maskPassword) =>
        $"Server=127.0.0.1;Port={i.HostPort};Database={i.DatabaseName};User ID={i.Username};Password={(maskPassword ? "*****" : i.Password)};";

    private async Task<string> GetClientAsync(DbInstance instance, CancellationToken ct)
    {
        if (_clients.TryGetValue(instance.ContainerName, out var known)) return known;
        var probe = string.Join(" || ", ClientCandidates.Select(c => $"command -v {c}"));
        var r = await docker.RunAsync(["exec", instance.ContainerName, "sh", "-c", probe], TimeSpan.FromSeconds(20), ct: ct);
        var path = r.ExitCode == 0 ? r.StdOut.Trim().Split('\n')[0].Trim() : "";
        if (path.Length == 0) return ClientCandidates[0];
        return _clients[instance.ContainerName] = path;
    }

    private async Task<QueryResult> RunClientAsync(DbInstance instance, string sql, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var client = await GetClientAsync(instance, ct);
        // Password travels as an environment variable and the SQL on stdin, so nothing needs shell quoting.
        // The server cancels the statement just before our own process timeout.
        var statementSeconds = Math.Max(5, (int)timeout.TotalSeconds - 5);
        var r = await docker.RunAsync(
        [
            "exec", "-i",
            "-e", $"MYSQL_PWD={instance.Password}",
            instance.ContainerName,
            client, "-h", "127.0.0.1", "-P", "3306", "-u", instance.Username, "--protocol=tcp", .. ClientExtraArgs,
            "--default-character-set=utf8mb4", "--table", $"--init-command={StatementTimeoutSql(statementSeconds)}",
            "-D", instance.DatabaseName
        ], timeout, sql.TrimEnd() + "\n", ct);

        var stdout = MySqlOutputConverter.ToSqlcmdStyle(r.StdOut);
        return new QueryResult(r.ExitCode == 0 && !r.TimedOut, stdout, r.StdErr, r.ExitCode, sw.Elapsed);
    }

    private async Task<(string? Error, string Detail)> WaitUntilReadyAsync(DbInstance instance, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyWait;
        QueryResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            // The init-time server has networking disabled, so a TCP connection means the real server is up.
            last = await RunClientAsync(instance, "SELECT 1;", TimeSpan.FromSeconds(20), ct);
            if (last.IsSuccess) return (null, "");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
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
