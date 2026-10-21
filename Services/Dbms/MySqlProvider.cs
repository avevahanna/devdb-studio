namespace DbServeApp.Services.Dbms;

public sealed class MySqlProvider(IWslDockerRunner docker, ILogger<MySqlProvider> log) : MySqlFamilyProvider(docker, log)
{
    public const string ProviderKey = "mysql";

    public override string Key => ProviderKey;
    public override string DisplayName => "MySQL";
    // 8.4 is the current LTS release.
    public override string DefaultImageTag => "8.4";
    // Fully qualified so Podman does not depend on unqualified-search-registries.
    public override string ImageName => "docker.io/library/mysql";
    public override string NamePrefix => "mysql";

    protected override string RootPasswordEnv => "MYSQL_ROOT_PASSWORD";
    protected override string DatabaseEnv => "MYSQL_DATABASE";
    protected override string[] ClientCandidates => ["mysql"];
    // Left at the client default (TLS preferred): caching_sha2_password needs a secure channel for the first login.
    protected override string[] ClientExtraArgs => [];
    // MySQL only applies this limit to SELECT statements; the process timeout still covers the rest.
    protected override string StatementTimeoutSql(int seconds) => $"SET SESSION max_execution_time={seconds * 1000}";
}
