using DevDbStudio.Services;
using DevDbStudio.Services.Dbms;
using Microsoft.Extensions.Logging.Abstractions;

namespace DevDbStudio.Tests;

public class CouchDbTests
{
    private sealed class FakeRunner(Func<string[], string?, ProcessResult> reply) : IWslDockerRunner
    {
        public List<(string[] Args, string? Stdin)> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(IEnumerable<string> dockerArgs, TimeSpan? timeout = null, string? stdin = null, CancellationToken ct = default)
        {
            var args = dockerArgs.ToArray();
            Calls.Add((args, stdin));
            return Task.FromResult(reply(args, stdin));
        }
    }

    private static string Reply(int status, string body) => body + "\n" + CouchDbScripts.StatusMarker + " " + status + "\n";

    private static DbInstance Instance() => new()
    {
        Id = Guid.NewGuid(), Name = "c", DbmsKey = CouchDbProvider.ProviderKey, ContainerName = "dbapp-couchdb-abcd1234",
        HostPort = 5990, Status = InstanceStatus.Running, DatabaseName = "-", Username = "admin", Password = "Passw0rd!x",
        VolumeName = "dbapp-couchdb-abcd1234-data", ImageTag = "3",
    };

    [Fact]
    public void Parse_ReadsRequestsAndContinuesBodies()
    {
        var (reqs, error) = CouchDbScripts.Parse("# comment\nGET /_all_dbs\n\nput /db\nPOST /db {\n  \"a\": 1\n}\n");

        Assert.Null(error);
        Assert.Equal(["GET", "PUT", "POST"], reqs.Select(r => r.Method));
        Assert.Null(reqs[0].Body);
        Assert.Equal("{\n\"a\": 1\n}", reqs[2].Body);
    }

    [Theory]
    [InlineData("")]
    [InlineData("SELECT 1")]
    [InlineData("GET _all_dbs")]
    [InlineData("{\"a\":1}")]
    public void Parse_RejectsInvalidScripts(string script) => Assert.NotNull(CouchDbScripts.Parse(script).Error);

    [Fact]
    public void CurlConfig_QuotesCredentialsAndBodies_AndSeparatesRequests()
    {
        var (reqs, _) = CouchDbScripts.Parse("POST /db {\"q\": \"a\\b\"}\nGET /x");
        var config = CouchDbScripts.BuildCurlConfig(reqs, "http://127.0.0.1:5984", "admin", "p\"w\\d");

        Assert.Contains("user = \"admin:p\\\"w\\\\d\"", config);
        Assert.Contains("data-raw = \"{\\\"q\\\": \\\"a\\\\b\\\"}\"", config);
        Assert.Contains("url = \"http://127.0.0.1:5984/x\"", config);
        Assert.Single(config.Split('\n'), l => l == "next");
    }

    [Fact]
    public void Format_AddsStatusAndIndentsJson_AndFlagsErrors()
    {
        var (reqs, _) = CouchDbScripts.Parse("GET /\nGET /missing");
        var raw = Reply(200, "{\"couchdb\":\"Welcome\"}") + Reply(404, "{\"error\":\"not_found\"}");

        var (text, ok) = CouchDbScripts.Format(reqs, raw);

        Assert.False(ok);
        Assert.Contains("GET / -> 200", text);
        Assert.Contains("GET /missing -> 404", text);
        Assert.Contains("\"couchdb\": \"Welcome\"", text);
    }

    [Fact]
    public void Format_MissingReply_IsAFailure()
    {
        var (reqs, _) = CouchDbScripts.Parse("GET /a\nGET /b");
        Assert.False(CouchDbScripts.Format(reqs, Reply(200, "{}")).AllSucceeded);
    }

    [Fact]
    public void ParseInsight_CountsUserDatabasesAndSize()
    {
        var (reqs, _) = CouchDbScripts.Parse(CouchDbScripts.Insight);
        var body = "[{\"key\":\"_users\",\"info\":{\"sizes\":{\"file\":1048576}}},{\"key\":\"shop\",\"info\":{\"sizes\":{\"file\":1048576}}}]";
        var insight = CouchDbScripts.ParseInsight(CouchDbScripts.Format(reqs, Reply(200, body)).Text);

        Assert.True(insight.Ok);
        Assert.Equal(1, insight.UserDatabases);
        Assert.Equal(2.0, insight.SizeMb);
    }

    [Fact]
    public void ParseDatabases_SkipsSystemDatabases()
    {
        var (reqs, _) = CouchDbScripts.Parse(CouchDbScripts.ListDatabases);
        var text = CouchDbScripts.Format(reqs, Reply(200, "[\"_users\",\"b\",\"a\"]")).Text;

        Assert.Equal(["a", "b"], CouchDbScripts.ParseDatabases(text));
    }

    [Fact]
    public void BuildRunArgs_BindsLoopbackAndPassesCredentialsAsEnv()
    {
        var p = new CouchDbProvider(new FakeRunner((_, _) => new ProcessResult(0, "", "", false)), NullLogger<CouchDbProvider>.Instance);
        var args = p.BuildRunArgs(new InstanceCreateRequest("c", "couchdb", 5990, "Passw0rd!x", "couchdb", "3"), Guid.NewGuid(), "dbapp-couchdb-x", "dbapp-couchdb-x-data");

        Assert.Contains("127.0.0.1:5990:5984", args);
        Assert.Contains("COUCHDB_USER=admin", args);
        Assert.Contains("COUCHDB_PASSWORD=Passw0rd!x", args);
        Assert.Equal("docker.io/library/couchdb:3", args[^1]);
    }

    [Fact]
    public async Task Execute_SendsConfigOnStdin_NotOnTheCommandLine()
    {
        var runner = new FakeRunner((_, _) => new ProcessResult(0, Reply(200, "{\"ok\":true}"), "", false));
        var p = new CouchDbProvider(runner, NullLogger<CouchDbProvider>.Instance);

        var r = await p.ExecuteSqlAsync(Instance(), "GET /", default);

        Assert.True(r.IsSuccess);
        var call = Assert.Single(runner.Calls);
        Assert.DoesNotContain(call.Args, a => a.Contains("Passw0rd!x"));
        Assert.Contains("admin:Passw0rd!x", call.Stdin);
    }

    [Fact]
    public async Task Execute_InvalidScript_FailsWithoutRunningAnything()
    {
        var runner = new FakeRunner((_, _) => new ProcessResult(0, "", "", false));
        var p = new CouchDbProvider(runner, NullLogger<CouchDbProvider>.Instance);

        var r = await p.ExecuteSqlAsync(Instance(), "SELECT 1", default);

        Assert.False(r.IsSuccess);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Create_WaitsForUp_ThenCreatesSystemDatabases()
    {
        var up = 0;
        var runner = new FakeRunner((a, stdin) =>
        {
            if (a[0] == "run") return new ProcessResult(0, "id", "", false);
            if (stdin is not null && stdin.Contains("/_up")) return new ProcessResult(0, Reply(++up < 2 ? 503 : 200, "{\"status\":\"ok\"}"), "", false);
            return new ProcessResult(0, Reply(201, "{\"ok\":true}") + Reply(201, "{\"ok\":true}"), "", false);
        });
        var p = new CouchDbProvider(runner, NullLogger<CouchDbProvider>.Instance);

        var r = await p.CreateAsync(new InstanceCreateRequest("c", CouchDbProvider.ProviderKey, 5990, "Passw0rd!x", "couchdb", "3"), default);

        Assert.True(r.Success);
        Assert.Contains("is ready", r.Message);
        Assert.Contains(runner.Calls, c => c.Stdin is not null && c.Stdin.Contains("/_users") && c.Stdin.Contains("/_replicator"));
    }

    [Fact]
    public void ConnectionString_MasksAndEscapesPassword()
    {
        var p = new CouchDbProvider(new FakeRunner((_, _) => new ProcessResult(0, "", "", false)), NullLogger<CouchDbProvider>.Instance);
        var i = Instance() with { Password = "a/b@c" };

        Assert.Equal("http://admin:a%2Fb%40c@127.0.0.1:5990/", p.BuildConnectionString(i, false));
        Assert.DoesNotContain("a%2F", p.BuildConnectionString(i, true));
    }
}
