namespace DbServeApp.Services.Dbms;

// TODO (next iteration): implement PostgresProvider.
//  - image postgres:<tag>, container port 5432, volume at /var/lib/postgresql/data, env POSTGRES_PASSWORD / POSTGRES_DB
//  - ExecuteSqlAsync via `docker exec -i -e PGPASSWORD=... <c> psql -U postgres -d <db> -v ON_ERROR_STOP=1` (SQL on stdin)
//  - readiness via `pg_isready`
// Not registered in DI yet, so it does not appear in the UI.
public sealed class PostgresProvider : IDbmsProvider
{
    public string Key => "postgres";
    public string DisplayName => "PostgreSQL";
    public int DefaultPort => 5432;
    public string DefaultImageTag => "16";
    public string DefaultDatabase => "postgres";

    public Task<InstanceCreateResult> CreateAsync(InstanceCreateRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<InstanceActionResult> StartAsync(DbInstance instance, CancellationToken ct) => throw new NotImplementedException();
    public Task<InstanceActionResult> StopAsync(DbInstance instance, CancellationToken ct) => throw new NotImplementedException();
    public Task<InstanceActionResult> DeleteAsync(DbInstance instance, bool deleteVolume, CancellationToken ct) => throw new NotImplementedException();
    public Task<QueryResult> ExecuteSqlAsync(DbInstance instance, string sql, CancellationToken ct) => throw new NotImplementedException();
    public string BuildConnectionString(DbInstance instance, bool maskPassword) => throw new NotImplementedException();
}
