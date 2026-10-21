using DbServeApp.Services;
using DbServeApp.Services.Dbms;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbServeApp.Tests;

public class ComposePreviewTests
{
    private sealed class NoRunner : IWslDockerRunner
    {
        public Task<ProcessResult> RunAsync(IEnumerable<string> dockerArgs, TimeSpan? timeout = null, string? stdin = null, CancellationToken ct = default) =>
            Task.FromResult(new ProcessResult(0, "", "", false));
    }

    private static readonly IWslDockerRunner R = new NoRunner();
    private static PostgresProvider Pg() => new(R, NullLogger<PostgresProvider>.Instance);
    private static RedisProvider Redis() => new(R, NullLogger<RedisProvider>.Instance);

    private static InstanceCreateRequest Req(string key, int port = 5440, string pw = "Passw0rd!x", string db = "appdb", string tag = "17") =>
        new("n1", key, port, pw, db, tag);

    [Fact]
    public void Render_ShowsRealRunSettings()
    {
        var yaml = ComposePreview.Render(Pg(), Req("postgres", 5441, tag: "16"), maskPassword: false);

        Assert.Contains("image: \"docker.io/library/postgres:16\"", yaml);
        Assert.Contains("- \"127.0.0.1:5441:5432\"", yaml);
        Assert.Contains("POSTGRES_PASSWORD: \"Passw0rd!x\"", yaml);
        Assert.Contains("POSTGRES_DB: \"appdb\"", yaml);
        Assert.Contains(":/var/lib/postgresql/data\"", yaml); // pre-18 layout
    }

    [Fact]
    public void Render_MasksPassword_WhenAsked()
    {
        var yaml = ComposePreview.Render(Pg(), Req("postgres"), maskPassword: true);

        Assert.DoesNotContain("Passw0rd!x", yaml);
        Assert.Contains(ComposePreview.PasswordMask, yaml);
    }

    [Fact]
    public void Render_EscapesDollarForCompose_AndKeepsRedisCommand()
    {
        var yaml = ComposePreview.Render(Redis(), Req("redis", 6390, db: "db0", tag: "8"), maskPassword: false);

        Assert.Contains("command: [\"sh\", \"-c\", \"exec redis-server --requirepass \\\"$$REDIS_PASSWORD\\\" --appendonly yes\"]", yaml);
    }

    [Fact]
    public void Parse_ReadsPortTagPasswordAndDatabase()
    {
        var p = Pg();
        var req = Req("postgres");
        var yaml = ComposePreview.Render(p, req, maskPassword: false)
            .Replace("127.0.0.1:5440:5432", "127.0.0.1:5555:5432")
            .Replace("postgres:17", "postgres:16")
            .Replace("Passw0rd!x", "N3w$ecret!")
            .Replace("appdb", "otherdb");

        var edit = ComposePreview.Parse(p, req, yaml, maskPassword: false);

        Assert.Equal(5555, edit.HostPort);
        Assert.Equal("16", edit.Tag);
        Assert.Equal("N3w$ecret!", edit.Password);
        Assert.Equal("otherdb", edit.Database);
    }

    [Fact]
    public void Parse_MaskedPassword_IsNotTreatedAsAnEdit()
    {
        var p = Pg();
        var req = Req("postgres");
        var edit = ComposePreview.Parse(p, req, ComposePreview.Render(p, req, maskPassword: true), maskPassword: true);

        Assert.Null(edit.Password);
        Assert.Empty(edit.Ignored);
    }

    [Fact]
    public void Parse_FlagsEditsTheFormCannotExpress()
    {
        var p = Pg();
        var req = Req("postgres");
        var yaml = ComposePreview.Render(p, req, maskPassword: false)
            .Replace("    environment:", "    environment:\n      EXTRA_SETTING: \"1\"");

        var edit = ComposePreview.Parse(p, req, yaml, maskPassword: false);

        Assert.Contains(edit.Ignored, l => l.Contains("EXTRA_SETTING"));
    }

    [Fact]
    public void Check_WarnsAboutNonLoopbackBinding()
    {
        var p = Pg();
        var req = Req("postgres");
        var yaml = ComposePreview.Render(p, req, maskPassword: false).Replace("127.0.0.1:5440", "0.0.0.0:5440");

        var issues = ComposePreview.Check(p, req, [], yaml, null);

        Assert.Contains(issues, i => i.Severity == PreviewSeverity.Warning && i.Message.Contains("0.0.0.0:5440:5432"));
    }

    [Fact]
    public void Check_DoesNotMistakeVolumesForPorts()
    {
        var p = Pg();
        var req = Req("postgres");

        var issues = ComposePreview.Check(p, req, [], ComposePreview.Render(p, req, maskPassword: false), null);

        Assert.DoesNotContain(issues, i => i.Message.Contains("Port binding"));
    }

    [Fact]
    public void Check_ReportsErrorsForDuplicatesAndWeakInput()
    {
        var p = Pg();
        var other = new DbInstance { Id = Guid.NewGuid(), Name = "N1", HostPort = 5440, DbmsKey = "postgres" };
        var req = Req("postgres", pw: "weak", tag: "latest");

        var issues = ComposePreview.Check(p, req, [other], null, null);

        Assert.Contains(issues, i => i.Severity == PreviewSeverity.Error && i.Message.Contains("5440"));
        Assert.Contains(issues, i => i.Severity == PreviewSeverity.Error && i.Message.Contains("already exists"));
        Assert.Contains(issues, i => i.Severity == PreviewSeverity.Error && i.Message.StartsWith("Password"));
        Assert.Contains(issues, i => i.Severity == PreviewSeverity.Warning && i.Message.Contains("floats"));
    }
}
