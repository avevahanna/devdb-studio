namespace DbServeApp.Services.Dbms;

public sealed class MariaDbProvider(IWslDockerRunner docker, ILogger<MariaDbProvider> log) : MySqlFamilyProvider(docker, log)
{
    public const string ProviderKey = "mariadb";

    public override string Key => ProviderKey;
    public override string DisplayName => "MariaDB";
    public override string DefaultImageTag => "11";
    // Fully qualified so Podman does not depend on unqualified-search-registries.
    public override string ImageName => "docker.io/library/mariadb";
    public override string NamePrefix => "maria";

    protected override string RootPasswordEnv => "MARIADB_ROOT_PASSWORD";
    protected override string DatabaseEnv => "MARIADB_DATABASE";
    protected override string[] ClientCandidates => ["mariadb", "mysql"];
    protected override string[] ClientExtraArgs => ["--skip-ssl"];
    protected override string StatementTimeoutSql(int seconds) => $"SET SESSION max_statement_time={seconds}";
}
