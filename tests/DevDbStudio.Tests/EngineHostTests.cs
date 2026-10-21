using DevDbStudio.Services;

namespace DevDbStudio.Tests;

public class EngineHostTests
{
    [Fact]
    public void Command_Wsl_WrapsInWslExec()
    {
        var (file, args) = EngineHost.Command(true, "Ubuntu", "docker", ["ps", "-a"]);
        Assert.Equal("wsl.exe", file);
        Assert.Equal(["-d", "Ubuntu", "--exec", "docker", "ps", "-a"], args);
    }

    [Fact]
    public void Command_Native_RunsEngineDirectly()
    {
        var (file, args) = EngineHost.Command(false, "", "podman", ["info"]);
        Assert.Equal("podman", file);
        Assert.Equal(["info"], args);
    }

    [Fact]
    public void Command_Native_ShellScriptKeepsDiscreteArguments()
    {
        var (file, args) = EngineHost.Command(false, "", "sh", ["-c", "cat /proc/loadavg; df -Pk /"]);
        Assert.Equal("sh", file);
        Assert.Equal(["-c", "cat /proc/loadavg; df -Pk /"], args);
    }

    [Fact]
    public void ForHost_Native_PassesEnvironmentWithoutWslenv()
    {
        var env = new Dictionary<string, string> { ["MSSQL_SA_PASSWORD"] = "x" };
        var result = EngineHost.ForHost(env, false);
        Assert.NotNull(result);
        Assert.Equal("x", result["MSSQL_SA_PASSWORD"]);
        Assert.DoesNotContain("WSLENV", result.Keys);
    }

    [Fact]
    public void ForHost_Wsl_ForwardsViaWslenv()
    {
        var env = new Dictionary<string, string> { ["MSSQL_SA_PASSWORD"] = "x" };
        var result = EngineHost.ForHost(env, true);
        Assert.NotNull(result);
        Assert.Contains("MSSQL_SA_PASSWORD", result["WSLENV"]);
    }

    [Fact]
    public void ForHost_EmptyOrNull_ReturnsNull()
    {
        Assert.Null(EngineHost.ForHost(null, false));
        Assert.Null(EngineHost.ForHost(new Dictionary<string, string>(), true));
    }
}
