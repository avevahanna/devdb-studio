using Microsoft.Extensions.Options;

namespace DbServeApp.Services;

/// <summary>Runs the container engine (docker or podman) inside the configured WSL distro. All engine access for DBMS management goes through this.</summary>
public interface IWslDockerRunner
{
    /// <param name="dockerArgs">Arguments after the engine executable; passed as discrete tokens (no shell).</param>
    Task<ProcessResult> RunAsync(IEnumerable<string> dockerArgs, TimeSpan? timeout = null, string? stdin = null, CancellationToken ct = default);
}

public sealed class WslDockerRunner(IOptions<WslOptions> options, IContainerEngine engine) : IWslDockerRunner
{
    private readonly WslOptions _opt = options.Value;

    public async Task<ProcessResult> RunAsync(IEnumerable<string> dockerArgs, TimeSpan? timeout = null, string? stdin = null, CancellationToken ct = default)
    {
        var exe = await engine.GetExecutableAsync(ct);
        var distro = await engine.GetDistroAsync(ct);
        return await ProcessRunner.RunAsync("wsl.exe", ["-d", distro, "--exec", exe, .. dockerArgs],
            timeout ?? TimeSpan.FromSeconds(Math.Max(_opt.CommandTimeoutSeconds, 15)), ct, stdin);
    }
}
