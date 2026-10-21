namespace DevDbStudio.Services;

/// <summary>
/// Where the container engine runs: inside a WSL distro on Windows, directly on the host on Linux.
/// Every engine or shell call goes through here so the rest of the app does not care about the OS.
/// </summary>
public static class EngineHost
{
    public static bool UsesWsl { get; } = OperatingSystem.IsWindows();

    /// <summary>Builds the process to start for <paramref name="exe"/> (docker, podman, sh, ...) with its arguments.</summary>
    public static (string File, string[] Args) Command(bool wsl, string distro, string exe, IEnumerable<string> args) =>
        wsl ? ("wsl.exe", ["-d", distro, "--exec", exe, .. args]) : (exe, [.. args]);

    public static Task<ProcessResult> RunAsync(string distro, string exe, IEnumerable<string> args, TimeSpan timeout,
        CancellationToken ct, string? stdin = null, IReadOnlyDictionary<string, string>? env = null)
    {
        var (file, argv) = Command(UsesWsl, distro, exe, args);
        return ProcessRunner.RunAsync(file, argv, timeout, ct, stdin, ForHost(env, UsesWsl));
    }

    /// <summary>On Linux the engine CLI is a direct child, so the environment is passed as is; for WSL it is forwarded with WSLENV.</summary>
    public static IReadOnlyDictionary<string, string>? ForHost(IReadOnlyDictionary<string, string>? env, bool wsl)
    {
        if (env is null || env.Count == 0) return null;
        return wsl ? ContainerEnvironment.ForWsl(env) : env;
    }
}
