using System.Diagnostics;
using System.Globalization;

namespace DbServeApp.Services.Dbms;

public sealed class PostgresProvider(IWslDockerRunner docker, ILogger<PostgresProvider> log) : IDbmsProvider
{
    public const string ProviderKey = "postgres";
    private static readonly TimeSpan PullAndRunTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(90);

    public string Key => ProviderKey;
    public string DisplayName => "PostgreSQL";
    public int DefaultPort => 5432;
    public string DefaultImageTag => "17";
    public string DefaultDatabase => "postgres";
    // Fully qualified so Podman does not depend on unqualified-search-registries.
    public string ImageName => "docker.io/library/postgres";
    public string AdminUser => "postgres";
    public string NamePrefix => "pg";

    // PostgreSQL 18+ images keep the data in a version-specific directory below /var/lib/postgresql.
    private static string DataMount(string tag)
    {
        if (tag.Equals("latest", StringComparison.OrdinalIgnoreCase)) return "/var/lib/postgresql";
        var digits = new string(tag.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major >= 18
            ? "/var/lib/postgresql"
            : "/var/lib/postgresql/data";
    }

    public async Task<InstanceCreateResult> CreateAsync(InstanceCreateRequest req, CancellationToken ct)
    {
        var errors = InstanceValidation.Validate(req);
        if (errors.Count > 0) return InstanceCreateResult.Fail(string.Join(" ", errors));

        var id = Guid.NewGuid();
        var shortId = id.ToString("N")[..8];
        var container = $"dbapp-postgres-{shortId}";
        var volume = $"{container}-data";
        var tag = req.ImageTag.Trim();

        try
        {
            var run = await docker.RunAsync(
            [
                "run", "-d", "--name", container,
                "--label", "dbapp.managed=true", "--label", $"dbapp.id={id}", "--label", $"dbapp.dbms={Key}",
                // Loopback only: the password is the sole protection of this server.
                "-p", $"127.0.0.1:{req.HostPort}:5432",
                "-e", $"POSTGRES_PASSWORD={req.Password}",
                "-e", $"POSTGRES_DB={req.DatabaseName}",
                "-v", $"{volume}:{DataMount(tag)}",
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
                return new InstanceCreateResult(true, instance, $"Container started, but PostgreSQL is not confirmed ready: {ready.Error}", ready.Detail);
            return new InstanceCreateResult(true, instance, "Instance created and PostgreSQL is ready.");
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

    public Task<QueryResult> ExecuteSqlAsync(DbInstance instance, string sql, CancellationToken ct) =>
        RunPsqlAsync(instance, instance.DatabaseName, sql, QueryTimeout, ct);

    public string BuildConnectionString(DbInstance i, bool maskPassword) =>
        $"Host=127.0.0.1;Port={i.HostPort};Database={i.DatabaseName};Username={i.Username};Password={(maskPassword ? "*****" : i.Password)};";

    private async Task<QueryResult> RunPsqlAsync(DbInstance instance, string database, string sql, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // Password and options travel as environment variables and the SQL on stdin, so nothing needs shell quoting.
        // The server cancels the statement just before our own process timeout.
        var statementMs = Math.Max(5, (int)timeout.TotalSeconds - 5) * 1000;
        var r = await docker.RunAsync(
        [
            "exec", "-i",
            "-e", $"PGPASSWORD={instance.Password}",
            "-e", "PGCONNECT_TIMEOUT=15",
            "-e", $"PGOPTIONS=-c statement_timeout={statementMs} -c client_min_messages=warning",
            instance.ContainerName,
            "psql", "-h", "127.0.0.1", "-U", instance.Username, "-d", database,
            "-X", "-v", "ON_ERROR_STOP=1", "-P", "pager=off", "-P", "null=NULL"
        ], timeout, sql.TrimEnd() + "\n", ct);

        var stdout = PsqlOutputConverter.ToSqlcmdStyle(r.StdOut);
        return new QueryResult(r.ExitCode == 0 && !r.TimedOut, stdout, r.StdErr, r.ExitCode, sw.Elapsed);
    }

    private async Task<(string? Error, string Detail)> WaitUntilReadyAsync(DbInstance instance, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyWait;
        QueryResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            // The init-time server only listens on a Unix socket, so a TCP connection means the real server is up.
            last = await RunPsqlAsync(instance, instance.DatabaseName, "SELECT 1;", TimeSpan.FromSeconds(20), ct);
            if (last.IsSuccess) return (null, "");
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
        }
        return ("timed out waiting for PostgreSQL to accept connections.", last is null ? "" : last.StdOut + last.StdErr);
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
