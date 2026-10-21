using DbServeApp.Services;
using DbServeApp.Services.Dbms;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbServeApp.Tests;

public class MongoDbTests
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

    private static DbInstance Instance() => new()
    {
        Id = Guid.NewGuid(), Name = "m", DbmsKey = MongoDbProvider.ProviderKey, ContainerName = "dbapp-mongodb-1234",
        HostPort = 27017, DatabaseName = "appdb", Username = "root", Password = "Pa:ss@w/rd1!", VolumeName = "v", ImageTag = "8",
    };

    private static MongoDbProvider Provider(FakeRunner r) => new(r, NullLogger<MongoDbProvider>.Instance);

    [Fact]
    public void ConnectionString_IncludesDatabaseAndEscapesCredentials()
    {
        var p = Provider(new FakeRunner(_ => Ok()));

        Assert.Equal("mongodb://root:Pa%3Ass%40w%2Frd1%21@127.0.0.1:27017/appdb?authSource=admin", p.BuildConnectionString(Instance(), false));
        Assert.Equal("mongodb://root:*****@127.0.0.1:27017/appdb?authSource=admin", p.BuildConnectionString(Instance(), true));
    }

    [Fact]
    public void Provider_DeclaresNonTableCapabilities()
    {
        IDbmsProvider p = Provider(new FakeRunner(_ => Ok()));

        Assert.False(p.SupportsTables);
        Assert.Equal(27017, p.DefaultPort);
        Assert.Equal("root", p.AdminUser);
    }

    [Fact]
    public async Task Execute_SendsScriptOnStdin_AndPasswordViaEnvironment()
    {
        var runner = new FakeRunner(_ => Ok("1"));

        var r = await Provider(runner).ExecuteSqlAsync(Instance(), "db.a.find()", default);

        Assert.True(r.IsSuccess);
        Assert.Equal("db.a.find()\n", runner.LastStdin);
        var args = runner.Calls.Single();
        Assert.Contains("MONGO_PWD=Pa:ss@w/rd1!", args);
        Assert.DoesNotContain(args.Where(a => a.StartsWith("exec mongosh")), a => a.Contains("Pa:ss"));
    }

    [Fact]
    public async Task Create_BindsToLoopback_AndSetsRootCredentials()
    {
        var runner = new FakeRunner(a => a[0] == "run" ? Ok("id") : a[0] == "logs" ? Ok("MongoDB init process complete; ready for start up.") : Ok("1"));
        var req = new InstanceCreateRequest("m1", MongoDbProvider.ProviderKey, 27018, "Passw0rd!x", "appdb", "8");

        var r = await Provider(runner).CreateAsync(req, default);

        Assert.True(r.Success);
        Assert.Contains("is ready", r.Message);
        var run = runner.Calls.First(c => c[0] == "run");
        Assert.Contains("127.0.0.1:27018:27017", run);
        Assert.Contains("MONGO_INITDB_ROOT_USERNAME=root", run);
        Assert.Contains("MONGO_INITDB_ROOT_PASSWORD=Passw0rd!x", run);
        Assert.Equal("appdb", r.Instance!.DatabaseName);
    }

    [Fact]
    public async Task Create_CleansUp_WhenRunFails()
    {
        var runner = new FakeRunner(a => a[0] == "run" ? new ProcessResult(125, "", "boom", false) : Ok());
        var req = new InstanceCreateRequest("m1", MongoDbProvider.ProviderKey, 27018, "Passw0rd!x", "appdb", "8");

        var r = await Provider(runner).CreateAsync(req, default);

        Assert.False(r.Success);
        Assert.Contains(runner.Calls, c => c[0] == "rm");
    }

    [Fact]
    public void ParseDatabases_ReadsLastJsonLine()
    {
        Assert.Equal(["a", "b"], MongoScripts.ParseDatabases("warning\n[\"a\",\"b\"]\n"));
        Assert.Empty(MongoScripts.ParseDatabases("nothing"));
        Assert.Empty(MongoScripts.ParseDatabases("[broken"));
    }

    [Fact]
    public void ParseInsight_ComputesFigures()
    {
        var r = MongoScripts.ParseInsight("{\"current\":4,\"available\":405,\"databases\":2,\"bytes\":10485760}");

        Assert.True(r.Ok);
        Assert.Equal(3, r.Connections);
        Assert.Equal(409, r.MaxConnections);
        Assert.Equal(2, r.UserDatabases);
        Assert.Equal(10.0, r.SizeMb);
    }

    [Fact]
    public void ParseInsight_FailsOnGarbage()
    {
        Assert.False(MongoScripts.ParseInsight("nope").Ok);
        Assert.False(MongoScripts.ParseInsight("{\"current\":1}").Ok);
    }
}
