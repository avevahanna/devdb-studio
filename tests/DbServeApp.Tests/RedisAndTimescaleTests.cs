using DbServeApp.Services;
using DbServeApp.Services.Dbms;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbServeApp.Tests;

public class RedisAndTimescaleTests
{
    private sealed class FakeRunner(Func<string[], ProcessResult> reply) : IWslDockerRunner
    {
        public List<string[]> Calls { get; } = [];
        public string? LastStdin { get; private set; }

        public Task<ProcessResult> RunAsync(IEnumerable<string> dockerArgs, TimeSpan? timeout = null, string? stdin = null, CancellationToken ct = default)
        {
            var args = dockerArgs.ToArray();
            Calls.Add(args);
            LastStdin = stdin;
            return Task.FromResult(reply(args));
        }
    }

    private static ProcessResult Ok(string stdout = "") => new(0, stdout, "", false);

    private static DbInstance RedisInstance() => new()
    {
        Id = Guid.NewGuid(), Name = "r", DbmsKey = RedisProvider.ProviderKey, ContainerName = "dbapp-redis-1234",
        HostPort = 6379, DatabaseName = "0", Username = "default", Password = "Pa:ss@w/rd1!", VolumeName = "v", ImageTag = "8",
    };

    private static RedisProvider Redis(FakeRunner r) => new(r, NullLogger<RedisProvider>.Instance);
    private static TimescaleDbProvider Timescale(FakeRunner r) => new(r, NullLogger<TimescaleDbProvider>.Instance);

    [Fact]
    public void Redis_ConnectionString_EscapesPasswordAndMasks()
    {
        var p = Redis(new FakeRunner(_ => Ok()));

        Assert.Contains(Uri.EscapeDataString("Pa:ss@w/rd1!"), p.BuildConnectionString(RedisInstance(), false));
        var masked = p.BuildConnectionString(RedisInstance(), true);
        Assert.Contains("*****", masked);
        Assert.DoesNotContain(Uri.EscapeDataString("Pa:ss@w/rd1!"), masked);
        Assert.EndsWith("@127.0.0.1:6379/0", masked);
    }

    [Fact]
    public void Redis_DeclaresNonTableCapabilities()
    {
        IDbmsProvider p = Redis(new FakeRunner(_ => Ok()));

        Assert.False(p.SupportsTables);
        Assert.False(p.UsesDatabaseName);
        Assert.Equal(6379, p.DefaultPort);
    }

    [Fact]
    public async Task Redis_Execute_SendsCommandsOnStdin_AndPasswordViaEnvironment()
    {
        var runner = new FakeRunner(_ => Ok("PONG"));

        var r = await Redis(runner).ExecuteSqlAsync(RedisInstance(), "GET a", default);

        Assert.True(r.IsSuccess);
        Assert.Equal("GET a\n", runner.LastStdin);
        var args = runner.Calls.Single();
        Assert.Contains("REDISCLI_AUTH=Pa:ss@w/rd1!", args);
        Assert.Contains("redis-cli", args);
    }

    [Fact]
    public async Task Redis_Create_BindsToLoopback_AndPassesPasswordViaEnvironment()
    {
        var runner = new FakeRunner(a => a[0] == "run" ? Ok("id") : Ok("PONG"));
        var req = new InstanceCreateRequest("r1", RedisProvider.ProviderKey, 6380, "Passw0rd!x", "db0", "8");

        var r = await Redis(runner).CreateAsync(req, default);

        Assert.True(r.Success);
        var run = runner.Calls.First(c => c[0] == "run");
        Assert.Contains("127.0.0.1:6380:6379", run);
        Assert.Contains("REDIS_PASSWORD=Passw0rd!x", run);
        Assert.DoesNotContain(run, a => a.Contains("requirepass Passw0rd"));
        Assert.Equal("0", r.Instance!.DatabaseName);
    }

    [Fact]
    public async Task Redis_Create_CleansUp_WhenRunFails()
    {
        var runner = new FakeRunner(a => a[0] == "run" ? new ProcessResult(125, "", "boom", false) : Ok());
        var req = new InstanceCreateRequest("r1", RedisProvider.ProviderKey, 6380, "Passw0rd!x", "db0", "8");

        var r = await Redis(runner).CreateAsync(req, default);

        Assert.False(r.Success);
        Assert.Contains(runner.Calls, c => c[0] == "rm");
    }

    [Fact]
    public void Redis_ParseInsight_ReadsInfoOutput()
    {
        const string info = "# Clients\nconnected_clients:3\n# Memory\nused_memory:10485760\n# Keyspace\ndb0:keys=1,expires=0\ndb3:keys=2,expires=0\nmaxclients\n10000\n";

        var r = RedisScripts.ParseInsight(info);

        Assert.True(r.Ok);
        Assert.Equal(2, r.Connections);
        Assert.Equal(10000, r.MaxConnections);
        Assert.Equal(2, r.UserDatabases);
        Assert.Equal(10.0, r.SizeMb);
    }

    [Fact]
    public void Redis_ParseInsight_FailsOnGarbage() => Assert.False(RedisScripts.ParseInsight("nope").Ok);

    [Fact]
    public async Task Timescale_Create_UsesTimescaleImageAndOwnNames()
    {
        var runner = new FakeRunner(a => a[0] == "run" ? Ok("id") : Ok("accepting connections"));
        var req = new InstanceCreateRequest("t1", TimescaleDbProvider.ProviderKey, 5433, "Passw0rd!x", "metrics", "latest-pg17");

        var r = await Timescale(runner).CreateAsync(req, default);

        Assert.True(r.Success);
        var run = runner.Calls.First(c => c[0] == "run");
        Assert.Contains("docker.io/timescale/timescaledb:latest-pg17", run);
        Assert.Contains("127.0.0.1:5433:5432", run);
        Assert.StartsWith("dbapp-timescaledb-", r.Instance!.ContainerName);
        Assert.Equal(TimescaleDbProvider.ProviderKey, r.Instance.DbmsKey);
    }

    [Fact]
    public async Task Postgres_ContainerNames_AreUnchanged()
    {
        var runner = new FakeRunner(a => a[0] == "run" ? Ok("id") : Ok("accepting connections"));
        var req = new InstanceCreateRequest("p1", PostgresProvider.ProviderKey, 5434, "Passw0rd!x", "appdb", "17");

        var r = await new PostgresProvider(runner, NullLogger<PostgresProvider>.Instance).CreateAsync(req, default);

        Assert.StartsWith("dbapp-postgres-", r.Instance!.ContainerName);
    }

    [Theory]
    [InlineData("postgres", true)]
    [InlineData("timescaledb", true)]
    [InlineData("mysql", false)]
    [InlineData(null, false)]
    public void PostgresFamily_IsRecognised(string? key, bool expected)
    {
        Assert.Equal(expected, PostgresFamilyProvider.IsFamily(key));
        if (expected) Assert.Same(TableDialect.Postgres, TableDialect.For(key));
    }
}
