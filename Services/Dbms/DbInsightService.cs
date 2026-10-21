using System.Globalization;

namespace DbServeApp.Services.Dbms;

/// <summary>Live figures read from inside a database server (not from the container engine).</summary>
public sealed record DbInsight(bool Ok, int Connections, int MaxConnections, int UserDatabases, double SizeMb, string? Error)
{
    public static DbInsight Fail(string error) => new(false, 0, 0, 0, 0, error);
}

public interface IDbInsightService
{
    Task<DbInsight> GetAsync(DbInstance instance, CancellationToken ct = default);
}

public sealed class DbInsightService(IInstanceManager manager) : IDbInsightService
{
    private const string Mssql =
        "SELECT (SELECT COUNT(*) FROM sys.dm_exec_sessions WHERE is_user_process = 1) AS connections, " +
        "CAST(@@MAX_CONNECTIONS AS INT) AS max_connections, " +
        "(SELECT COUNT(*) FROM sys.databases WHERE database_id > 4) AS user_databases, " +
        "(SELECT CAST(SUM(size) * 8 / 1024.0 AS DECIMAL(18,1)) FROM sys.master_files) AS size_mb;";

    private const string Postgres =
        "SELECT (SELECT count(*) FROM pg_stat_activity WHERE backend_type = 'client backend') AS connections, " +
        "current_setting('max_connections')::int AS max_connections, " +
        "(SELECT count(*) FROM pg_database WHERE NOT datistemplate) AS user_databases, " +
        "(SELECT ROUND(SUM(pg_database_size(datname)) / 1048576.0, 1) FROM pg_database) AS size_mb;";

    private const string MySqlFamily =
        "SELECT (SELECT COUNT(*) FROM information_schema.processlist) AS connections, " +
        "@@max_connections AS max_connections, " +
        "(SELECT COUNT(*) FROM information_schema.schemata WHERE schema_name NOT IN ('information_schema','performance_schema','mysql','sys')) AS user_databases, " +
        "(SELECT COALESCE(ROUND(SUM(data_length + index_length) / 1048576, 1), 0) FROM information_schema.tables) AS size_mb;";

    public async Task<DbInsight> GetAsync(DbInstance instance, CancellationToken ct = default)
    {
        if (instance.DbmsKey == MongoDbProvider.ProviderKey)
        {
            var mr = await manager.ExecuteSqlAsync(instance.Id, MongoScripts.Insight, ct);
            return mr.IsSuccess ? MongoScripts.ParseInsight(mr.StdOut) : DbInsight.Fail((mr.StdErr + "\n" + mr.StdOut).Trim());
        }
        if (instance.DbmsKey == RedisProvider.ProviderKey)
        {
            var rr = await manager.ExecuteSqlAsync(instance.Id, RedisScripts.Insight, ct);
            return rr.IsSuccess ? RedisScripts.ParseInsight(rr.StdOut) : DbInsight.Fail((rr.StdErr + "\n" + rr.StdOut).Trim());
        }
        var sql = instance.DbmsKey switch
        {
            PostgresProvider.ProviderKey or TimescaleDbProvider.ProviderKey => Postgres,
            MariaDbProvider.ProviderKey or MySqlProvider.ProviderKey => MySqlFamily,
            _ => Mssql,
        };
        var r = await manager.ExecuteSqlAsync(instance.Id, sql, ct);
        if (!r.IsSuccess) return DbInsight.Fail((r.StdErr + "\n" + r.StdOut).Trim());

        var row = SqlcmdOutputParser.Parse(r.StdOut).Select(b => b.Table).FirstOrDefault(t => t is not null)?.Rows.FirstOrDefault();
        if (row is null || row.Count < 4) return DbInsight.Fail("Unexpected result from the database.");

        static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
        // The probe connection itself is counted by every DBMS, so it is left out.
        return new DbInsight(true, Math.Max(0, (int)D(row[0]) - 1), (int)D(row[1]), (int)D(row[2]), D(row[3]), null);
    }
}
