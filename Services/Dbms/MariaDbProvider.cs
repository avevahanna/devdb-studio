namespace DbServeApp.Services.Dbms;

// TODO (next iteration): implement MariaDbProvider.
//  - image mariadb:<tag>, container port 3306, volume at /var/lib/mysql, env MARIADB_ROOT_PASSWORD / MARIADB_DATABASE
//  - ExecuteSqlAsync via `docker exec -i -e MYSQL_PWD=... <c> mariadb -uroot <db>` (SQL on stdin)
//  - readiness via `healthcheck.sh --connect` or `mariadb-admin ping`
// Not registered in DI yet, so it does not appear in the UI.
public sealed class MariaDbProvider : IDbmsProvider
{
    public string Key => "mariadb";
    public string DisplayName => "MariaDB";
    public int DefaultPort => 3306;
    public string DefaultImageTag => "11";
    public string DefaultDatabase => "mysql";
    public string ImageName => "docker.io/library/mariadb";
    public string AdminUser => "root";
    public string NamePrefix => "maria";

    public Task<InstanceCreateResult> CreateAsync(InstanceCreateRequest req, CancellationToken ct) => throw new NotImplementedException();
    public Task<InstanceActionResult> StartAsync(DbInstance instance, CancellationToken ct) => throw new NotImplementedException();
    public Task<InstanceActionResult> StopAsync(DbInstance instance, CancellationToken ct) => throw new NotImplementedException();
    public Task<InstanceActionResult> DeleteAsync(DbInstance instance, bool deleteVolume, CancellationToken ct) => throw new NotImplementedException();
    public Task<QueryResult> ExecuteSqlAsync(DbInstance instance, string sql, CancellationToken ct) => throw new NotImplementedException();
    public string BuildConnectionString(DbInstance instance, bool maskPassword) => throw new NotImplementedException();
}
