using Microsoft.Extensions.Options;

namespace DbServeApp.Services;

/// <summary>Runs docker inside the configured WSL distro. All Docker access for DBMS management goes through this.</summary>
public interface IWslDockerRunner
{
    /// <param name="dockerArgs">Arguments after "docker"; passed as discrete tokens (no shell).</param>
    Task<ProcessResult> RunAsync(IEnumerable<string> dockerArgs, TimeSpan? timeout = null, string? stdin = null, CancellationToken ct = default);
}

public sealed class WslDockerRunner(IOptions<WslOptions> options) : IWslDockerRunner
{
    private readonly WslOptions _opt = options.Value;

    public Task<ProcessResult> RunAsync(IEnumerable<string> dockerArgs, TimeSpan? timeout = null, string? stdin = null, CancellationToken ct = default) =>
        ProcessRunner.RunAsync("wsl.exe", ["-d", _opt.DistroName, "--exec", "docker", .. dockerArgs],
            timeout ?? TimeSpan.FromSeconds(Math.Max(_opt.CommandTimeoutSeconds, 15)), ct, stdin);
}
