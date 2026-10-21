namespace DbServeApp.Services.Dbms;

/// <summary>A DBMS flavour (SQL Server, PostgreSQL, MariaDB, MySQL) that knows how to run itself as a container in WSL.</summary>
public interface IDbmsProvider
{
    string Key { get; }
    string DisplayName { get; }
    int DefaultPort { get; }
    string DefaultImageTag { get; }
    string DefaultDatabase { get; }
    /// <summary>Fully qualified image name without tag.</summary>
    string ImageName { get; }
    string AdminUser { get; }
    string NamePrefix { get; }
    /// <summary>False for non-relational DBMSs: the table designer and table data tools do not apply.</summary>
    bool SupportsTables => true;
    /// <summary>False when the DBMS has no named databases to ask for (Redis uses numbered ones).</summary>
    bool UsesDatabaseName => true;
    /// <summary>Name of the language the Query page accepts.</summary>
    string QueryLanguage => "SQL";
    /// <summary>Harmless example shown in a fresh query editor; null falls back to the Query page default.</summary>
    string? SampleQuery => null;

    Task<InstanceCreateResult> CreateAsync(InstanceCreateRequest req, CancellationToken ct);
    Task<InstanceActionResult> StartAsync(DbInstance instance, CancellationToken ct);
    Task<InstanceActionResult> StopAsync(DbInstance instance, CancellationToken ct);
    Task<InstanceActionResult> DeleteAsync(DbInstance instance, bool deleteVolume, CancellationToken ct);
    Task<QueryResult> ExecuteSqlAsync(DbInstance instance, string sql, CancellationToken ct);
    string BuildConnectionString(DbInstance instance, bool maskPassword);
}
