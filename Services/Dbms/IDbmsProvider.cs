namespace DbServeApp.Services.Dbms;

/// <summary>A DBMS flavour (SQL Server, PostgreSQL, ...) that knows how to run itself as a container in WSL.</summary>
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

    Task<InstanceCreateResult> CreateAsync(InstanceCreateRequest req, CancellationToken ct);
    Task<InstanceActionResult> StartAsync(DbInstance instance, CancellationToken ct);
    Task<InstanceActionResult> StopAsync(DbInstance instance, CancellationToken ct);
    Task<InstanceActionResult> DeleteAsync(DbInstance instance, bool deleteVolume, CancellationToken ct);
    Task<QueryResult> ExecuteSqlAsync(DbInstance instance, string sql, CancellationToken ct);
    string BuildConnectionString(DbInstance instance, bool maskPassword);
}
