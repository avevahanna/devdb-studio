using System.Text.Json;
using DbServeApp.Services.Dbms;
using Microsoft.Extensions.Options;

namespace DbServeApp.Services;

public enum EngineChoice { Auto, Docker, Podman }

/// <summary>Which container engine CLI (docker or podman) is used inside the WSL distro.</summary>
public interface IContainerEngine
{
    EngineChoice Choice { get; }
    /// <summary>Raised after the choice changed or detection was forced to run again.</summary>
    event Action? Changed;
    /// <summary>The effective executable, "docker" or "podman". In Auto mode the result is detected and cached.</summary>
    Task<string> GetExecutableAsync(CancellationToken ct = default);
    /// <summary>Forgets the cached auto-detection result so the next call probes again.</summary>
    void InvalidateDetection();
    Task SetChoiceAsync(EngineChoice choice, CancellationToken ct = default);
}

public static class ContainerEngines
{
    public static string DisplayName(string executable) => executable == "podman" ? "Podman" : "Docker";
}

public sealed class ContainerEngine : IContainerEngine
{
    private static readonly TimeSpan FailureCacheTtl = TimeSpan.FromSeconds(15);

    private readonly WslOptions _opt;
    private readonly string _settingsPath;
    private readonly ILogger<ContainerEngine> _log;
    private readonly SemaphoreSlim _detectLock = new(1, 1);
    private volatile EngineChoice _choice;
    private string? _detected;
    private bool _detectedHealthy;
    private DateTime _detectedAt;

    public ContainerEngine(IOptions<WslOptions> wsl, IOptions<StorageOptions> storage, ILogger<ContainerEngine> log)
    {
        _opt = wsl.Value;
        _log = log;
        var configured = storage.Value.InstancesFile;
        var dir = string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DbServeApp")
            : Path.GetDirectoryName(Path.GetFullPath(configured)) ?? ".";
        _settingsPath = Path.Combine(dir, "settings.json");

        _choice = Enum.TryParse<EngineChoice>(_opt.ContainerEngine, true, out var fromConfig) ? fromConfig : EngineChoice.Auto;
        try
        {
            if (File.Exists(_settingsPath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(_settingsPath));
                if (s is not null && Enum.TryParse<EngineChoice>(s.ContainerEngine, true, out var saved)) _choice = saved;
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not read {Path}; using configured container engine", _settingsPath);
        }
    }

    private sealed record Settings(string ContainerEngine);

    public EngineChoice Choice => _choice;
    public event Action? Changed;

    public async Task<string> GetExecutableAsync(CancellationToken ct = default)
    {
        switch (_choice)
        {
            case EngineChoice.Docker: return "docker";
            case EngineChoice.Podman: return "podman";
        }
        if (TryCached(out var cached)) return cached;

        await _detectLock.WaitAsync(ct);
        try
        {
            if (TryCached(out cached)) return cached;
            var (exe, healthy) = await DetectAsync(ct);
            _detected = exe;
            _detectedHealthy = healthy;
            _detectedAt = DateTime.UtcNow;
            _log.LogInformation("Detected container engine: {Engine} (healthy: {Healthy})", exe, healthy);
            return exe;
        }
        finally { _detectLock.Release(); }
    }

    // A working engine stays cached until invalidated; a failed detection is retried after a short while.
    private bool TryCached(out string exe)
    {
        exe = _detected ?? "";
        return _detected is not null && (_detectedHealthy || DateTime.UtcNow - _detectedAt < FailureCacheTtl);
    }

    public void InvalidateDetection()
    {
        _detected = null;
        Changed?.Invoke();
    }

    public async Task SetChoiceAsync(EngineChoice choice, CancellationToken ct = default)
    {
        _choice = choice;
        _detected = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            await File.WriteAllTextAsync(_settingsPath, JsonSerializer.Serialize(new Settings(choice.ToString())), ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "Could not save container engine choice to {Path}", _settingsPath);
        }
        Changed?.Invoke();
    }

    // Prefer an engine that actually answers, then fall back to one that is merely installed.
    private async Task<(string Exe, bool Healthy)> DetectAsync(CancellationToken ct)
    {
        (string Exe, string Arg, bool Healthy)[] probes =
            [("docker", "info", true), ("podman", "info", true), ("docker", "--version", false), ("podman", "--version", false)];
        var timeout = TimeSpan.FromSeconds(Math.Max(_opt.ColdStartTimeoutSeconds, 15));
        foreach (var (exe, arg, healthy) in probes)
        {
            var r = await ProcessRunner.RunAsync("wsl.exe", ["-d", _opt.DistroName, "--exec", exe, arg], timeout, ct);
            if (r.ExitCode == 0) return (exe, healthy);
        }
        return ("docker", false);
    }
}
