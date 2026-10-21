using System.Diagnostics;

namespace DbServeApp.Services.Dbms;

/// <summary>MongoDB (document database). Scripts are JavaScript run with mongosh inside the container.</summary>
public sealed class MongoDbProvider(IWslDockerRunner docker, ILogger<MongoDbProvider> log) : IDbmsProvider
{
    public const string ProviderKey = "mongodb";
    private static readonly TimeSpan PullAndRunTimeout = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ReadyWait = TimeSpan.FromSeconds(90);

    public string Key => ProviderKey;
    public string DisplayName => "MongoDB";
    public int DefaultPort => 27017;
    public string DefaultImageTag => "8";
    public string DefaultDatabase => "appdb";
    public string ImageName => "docker.io/library/mongo";
    public string AdminUser => "root";
    public string NamePrefix => "mongo";

    public bool SupportsTables => false;
    public string QueryLanguage => "mongosh (JavaScript)";
    public string SampleQuery => "db.runCommand({ buildInfo: 1 }).version";

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
                "-p", $"127.0.0.1:{req.HostPort}:27017",
                "-e", $"MONGO_INITDB_ROOT_USERNAME={AdminUser}",
                "-e", $"MONGO_INITDB_ROOT_PASSWORD={req.Password}",
                "-v", $"{volume}:/data/db",
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
            return new InstanceCreateResult(true, instance, $"Instance created and {DisplayName} is ready. The database '{req.DatabaseName}' appears once it contains data.");
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
        RunShellAsync(instance, script, QueryTimeout, ct);

    public string BuildConnectionString(DbInstance i, bool maskPassword) =>
        $"mongodb://{Uri.EscapeDataString(i.Username)}:{(maskPassword ? "*****" : Uri.EscapeDataString(i.Password))}@127.0.0.1:{i.HostPort}/{i.DatabaseName}?authSource=admin";

    private async Task<QueryResult> RunShellAsync(DbInstance instance, string script, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        // Password and user travel as environment variables and the script on stdin, so nothing needs quoting.
        // The database name is validated to letters, digits and underscore.
        var r = await docker.RunAsync(
        [
            "exec", "-i",
            "-e", $"MONGO_USER={instance.Username}",
            "-e", $"MONGO_PWD={instance.Password}",
            instance.ContainerName,
            "sh", "-c",
            // The script arrives on stdin and is handed to --eval, which gives clean output and real exit codes.
            $"exec mongosh --quiet --norc --host 127.0.0.1 --port 27017 -u \"$MONGO_USER\" -p \"$MONGO_PWD\" --authenticationDatabase admin {instance.DatabaseName} --eval \"$(cat)\""
        ], timeout, script.TrimEnd() + "\n", ct);

        return new QueryResult(r.ExitCode == 0 && !r.TimedOut, r.StdOut, r.StdErr, r.ExitCode, sw.Elapsed);
    }

    private async Task<(string? Error, string Detail)> WaitUntilReadyAsync(DbInstance instance, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + ReadyWait;
        QueryResult? last = null;
        while (DateTime.UtcNow < deadline)
        {
            last = await RunShellAsync(instance, "db.runCommand({ ping: 1 }).ok", TimeSpan.FromSeconds(20), ct);
            if (last.IsSuccess && last.StdOut.Contains('1')) return (null, "");
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
