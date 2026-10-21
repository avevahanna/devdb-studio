namespace DbServeApp.Services;

/// <summary>
/// Moves <c>-e NAME=value</c> options of <c>run</c>/<c>exec</c> out of the command line. The value is handed to the
/// engine CLI through its environment (forwarded into WSL with WSLENV), so secrets do not appear in any process list.
/// </summary>
public static class ContainerEnvironment
{
    // Options of run/exec that consume the next token; needed to find where the image/container name starts.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "-e", "--env", "--name", "-p", "--publish", "-v", "--volume", "--label", "-l", "-w", "--workdir",
        "-u", "--user", "--network", "--net", "--restart", "--env-file", "--entrypoint", "--hostname", "-h", "--memory", "-m", "--cpus",
    };

    public static (string[] Args, IReadOnlyDictionary<string, string> Env) Extract(IEnumerable<string> args)
    {
        var all = args.ToArray();
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (all.Length == 0 || all[0] is not ("run" or "exec")) return (all, env);

        var result = new List<string> { all[0] };
        var i = 1;
        for (; i < all.Length; i++)
        {
            var a = all[i];
            if (!a.StartsWith('-')) break; // first positional: image (run) or container (exec); the rest belongs to the command

            if (a is "-e" or "--env" && i + 1 < all.Length && TrySplit(all[i + 1], out var name, out var value))
            {
                env[name] = value;
                result.Add(a);
                result.Add(name);
                i++;
                continue;
            }
            result.Add(a);
            if (ValueOptions.Contains(a) && i + 1 < all.Length) result.Add(all[++i]);
        }
        result.AddRange(all.Skip(i));
        return (result.ToArray(), env);
    }

    private static bool TrySplit(string pair, out string name, out string value)
    {
        var eq = pair.IndexOf('=');
        name = eq > 0 ? pair[..eq] : "";
        value = eq > 0 ? pair[(eq + 1)..] : "";
        // Only plain names and single-line values survive an environment block and WSLENV intact.
        return eq > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(name[0])
            && !value.Contains('\n') && !value.Contains('\r') && !value.Contains('\0');
    }

    /// <summary>Environment for wsl.exe: the values plus a WSLENV entry that forwards them into the distro.</summary>
    public static IReadOnlyDictionary<string, string>? ForWsl(IReadOnlyDictionary<string, string> env)
    {
        if (env.Count == 0) return null;
        var result = new Dictionary<string, string>(env, StringComparer.Ordinal);
        var existing = Environment.GetEnvironmentVariable("WSLENV");
        result["WSLENV"] = string.Join(':', new[] { existing }.Where(s => !string.IsNullOrEmpty(s)).Concat(env.Keys));
        return result;
    }
}
