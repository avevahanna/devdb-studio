using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace DbServeApp.Services;

public sealed class WslKeepAliveService(IOptions<WslOptions> options, IContainerEngine engine, ILogger<WslKeepAliveService> log) : BackgroundService
{
    private readonly WslOptions _opt = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_opt.KeepAlive) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var distro = await engine.GetDistroAsync(stoppingToken);
                var psi = new ProcessStartInfo("wsl.exe")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                };
                psi.ArgumentList.Add("-d");
                psi.ArgumentList.Add(distro);
                psi.ArgumentList.Add("--exec");
                // cat blocks on stdin; when this app dies (even hard-killed) the pipe closes, cat exits and so does wsl.exe.
                psi.ArgumentList.Add("cat");

                using var proc = Process.Start(psi);
                if (proc is null) throw new InvalidOperationException("Could not start wsl.exe");
                log.LogInformation("WSL keep-alive session started (pid {Pid}).", proc.Id);

                using var reg = stoppingToken.Register(() =>
                {
                    try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
                });
                await proc.WaitForExitAsync(stoppingToken);
                log.LogWarning("WSL keep-alive session exited with code {Code}; restarting.", proc.ExitCode);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                log.LogWarning(ex, "WSL keep-alive failed.");
            }

            try { await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }
}
