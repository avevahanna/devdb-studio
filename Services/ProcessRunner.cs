using System.Diagnostics;
using System.Text;

namespace DbServeApp.Services;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, bool TimedOut);

public static class ProcessRunner
{
    private const int MaxOutputBytes = 2 * 1024 * 1024;

    public static async Task<ProcessResult> RunAsync(
        string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct = default, string? stdin = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in arguments) psi.ArgumentList.Add(a);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            using var process = new Process { StartInfo = psi };
            process.Start();
            if (stdin is not null)
            {
                // The child may exit before reading everything; a broken pipe is not an error here.
                try { await process.StandardInput.WriteAsync(stdin.AsMemory(), cts.Token); }
                catch (IOException) { }
            }
            try { process.StandardInput.Close(); } catch (IOException) { }

            // Raw bytes: wsl.exe's own messages are UTF-16LE while commands inside the distro are UTF-8.
            var outTask = ReadCapped(process.StandardOutput.BaseStream);
            var errTask = ReadCapped(process.StandardError.BaseStream);

            var timedOut = false;
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                if (ct.IsCancellationRequested)
                {
                    await DrainAsync(outTask, errTask);
                    throw;
                }
                timedOut = true;
            }

            var (o, e) = await DrainAsync(outTask, errTask);
            var stdout = Decode(o);
            var stderr = Decode(e);
            if (timedOut)
            {
                var msg = $"Command timed out after {timeout.TotalSeconds:0}s.";
                return new ProcessResult(-1, stdout, stderr.Length > 0 ? $"{msg}\n{stderr}" : msg, true);
            }
            return new ProcessResult(process.ExitCode, stdout, stderr, false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ProcessResult(-1, "", $"Failed to run '{fileName}': {ex.Message}", false);
        }
    }

    // Output accumulates in a caller-owned buffer so partial output survives a drain timeout.
    private sealed class Capture
    {
        public readonly MemoryStream Buffer = new();
        public Task Reader = Task.CompletedTask;
        public byte[] Snapshot() { lock (Buffer) return Buffer.ToArray(); }
    }

    private static Capture ReadCapped(Stream stream)
    {
        var cap = new Capture();
        cap.Reader = Task.Run(async () =>
        {
            var buffer = new byte[8192];
            try
            {
                int n;
                while ((n = await stream.ReadAsync(buffer)) > 0)
                {
                    // Keep draining past the cap so the child never blocks on a full pipe.
                    lock (cap.Buffer)
                        if (cap.Buffer.Length < MaxOutputBytes) cap.Buffer.Write(buffer, 0, (int)Math.Min(n, MaxOutputBytes - cap.Buffer.Length));
                }
            }
            catch (Exception) { }
        });
        return cap;
    }

    private static async Task<(byte[], byte[])> DrainAsync(Capture o, Capture e)
    {
        try { await Task.WhenAll(o.Reader, e.Reader).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { }
        return (o.Snapshot(), e.Snapshot());
    }
    private static string Decode(byte[] b)
    {
        if (b.Length == 0) return "";
        var text = LooksUtf16(b) ? Encoding.Unicode.GetString(b) : Encoding.UTF8.GetString(b);
        return text.Replace("\0", "").Replace("\uFEFF", "").Trim();
    }

    private static bool LooksUtf16(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return true;
        var pairs = Math.Min(b.Length, 256) / 2;
        if (pairs == 0) return false;
        var zeros = 0;
        for (var i = 0; i < pairs; i++) if (b[i * 2 + 1] == 0) zeros++;
        return zeros * 10 >= pairs * 4;
    }
}
