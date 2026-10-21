using System.Globalization;
using System.Text.RegularExpressions;

namespace DevDbStudio.Services.Dbms;

/// <summary>TimescaleDB: PostgreSQL with the time-series extension. Everything but the image is shared with PostgreSQL.</summary>
public sealed partial class TimescaleDbProvider(IWslDockerRunner docker, ILogger<TimescaleDbProvider> log) : PostgresFamilyProvider(docker, log)
{
    public const string ProviderKey = "timescaledb";

    public override string Key => ProviderKey;
    public override string DisplayName => "TimescaleDB";
    // Tags look like latest-pg17 or 2.17.2-pg16.
    public override string DefaultImageTag => "latest-pg17";
    public override string ImageName => "docker.io/timescale/timescaledb";
    public override string NamePrefix => "ts";

    [GeneratedRegex(@"pg(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex PgMajor();

    protected override string DataMount(string tag) =>
        PgMajor().Match(tag) is { Success: true } m
        && int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) && major < 18
            ? "/var/lib/postgresql/data"
            : "/var/lib/postgresql";
}
