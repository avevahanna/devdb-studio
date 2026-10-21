namespace DbServeApp.Services.Dbms;

public sealed record TableRef(string Schema, string Name)
{
    public override string ToString() => $"{Schema}.{Name}";
}

public sealed record TableListResult(IReadOnlyList<TableRef> Tables, string? Error);

/// <summary>Table data operations (random population, clearing) for SQL Server, PostgreSQL, MariaDB and MySQL.</summary>
public interface ITableDataService
{
    Task<TableListResult> ListTablesAsync(Guid instanceId, string database, CancellationToken ct = default);
    /// <summary>User databases on the instance (system databases excluded); empty when the instance cannot be queried.</summary>
    Task<IReadOnlyList<string>> ListDatabasesAsync(Guid instanceId, CancellationToken ct = default);
    Task<QueryResult> PopulateAsync(Guid instanceId, string database, string schema, string table, int rows, CancellationToken ct = default);
    Task<QueryResult> ClearAsync(Guid instanceId, string database, string schema, string table, CancellationToken ct = default);
}

public sealed class TableDataService(IInstanceManager manager) : ITableDataService
{
    private static QueryResult Fail(string msg) => new(false, "", msg, -1, TimeSpan.Zero);

    private async Task<string?> DbmsKeyAsync(Guid id, CancellationToken ct) =>
        (await manager.GetAllAsync(ct)).FirstOrDefault(i => i.Id == id)?.DbmsKey;

    private async Task<bool> IsPostgresAsync(Guid id, CancellationToken ct) =>
        PostgresFamilyProvider.IsFamily(await DbmsKeyAsync(id, ct));

    // MariaDB and MySQL share the same SQL scripts.
    private async Task<bool> IsMariaDbAsync(Guid id, CancellationToken ct) =>
        await DbmsKeyAsync(id, ct) is MariaDbProvider.ProviderKey or MySqlProvider.ProviderKey;

    private Task<QueryResult> Run(Guid id, string database, string sql, CancellationToken ct) =>
        manager.ExecuteSqlAsync(id, MssqlDataScript.Use(database) + sql, ct);

    private Task<QueryResult> RunPg(Guid id, string database, string sql, CancellationToken ct) =>
        manager.ExecuteSqlAsync(id, TableDialect.Postgres.Use(database) + sql, ct);

    private Task<QueryResult> RunMy(Guid id, string database, string sql, CancellationToken ct) =>
        manager.ExecuteSqlAsync(id, TableDialect.MariaDb.Use(database) + sql, ct);

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(Guid instanceId, CancellationToken ct = default)
    {
        if (await DbmsKeyAsync(instanceId, ct) == RedisProvider.ProviderKey) return RedisScripts.Databases;
        if (await DbmsKeyAsync(instanceId, ct) == MongoDbProvider.ProviderKey)
        {
            var mr = await manager.ExecuteSqlAsync(instanceId, MongoScripts.ListDatabases, ct);
            return mr.IsSuccess ? MongoScripts.ParseDatabases(mr.StdOut) : [];
        }
        var key = await DbmsKeyAsync(instanceId, ct);
        var sql = PostgresFamilyProvider.IsFamily(key) ? "SELECT datname FROM pg_database WHERE NOT datistemplate ORDER BY 1;" : key switch
        {
            MariaDbProvider.ProviderKey or MySqlProvider.ProviderKey =>
                "SELECT schema_name FROM information_schema.schemata WHERE schema_name NOT IN ('information_schema','performance_schema','mysql','sys') ORDER BY 1;",
            _ => "SELECT name FROM sys.databases WHERE database_id > 4 ORDER BY name;",
        };
        var r = await manager.ExecuteSqlAsync(instanceId, sql, ct);
        if (!r.IsSuccess) return [];
        var table = SqlcmdOutputParser.Parse(r.StdOut).Select(b => b.Table).FirstOrDefault(t => t is not null);
        return table?.Rows.Where(x => x.Count >= 1 && x[0].Length > 0).Select(x => x[0]).ToList() ?? [];
    }

    public async Task<TableListResult> ListTablesAsync(Guid instanceId, string database, CancellationToken ct = default)
    {
        var key = await DbmsKeyAsync(instanceId, ct);
        var r = PostgresFamilyProvider.IsFamily(key) ? await RunPg(instanceId, database, PostgresDataScript.ListTablesSql, ct) : key switch
        {
            MariaDbProvider.ProviderKey or MySqlProvider.ProviderKey => await RunMy(instanceId, database, MariaDbDataScript.ListTablesSql, ct),
            _ => await Run(instanceId, database, MssqlDataScript.ListTablesSql, ct),
        };
        if (!r.IsSuccess) return new TableListResult([], (r.StdErr + "\n" + r.StdOut).Trim());
        var table = SqlcmdOutputParser.Parse(r.StdOut).Select(b => b.Table).FirstOrDefault(t => t is not null);
        var list = table?.Rows.Where(x => x.Count >= 2).Select(x => new TableRef(x[0], x[1])).ToList() ?? [];
        return new TableListResult(list, null);
    }

    public async Task<QueryResult> ClearAsync(Guid instanceId, string database, string schema, string table, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(table)) return Fail("Choose a table.");
        if (await IsMariaDbAsync(instanceId, ct))
        {
            var mr = await RunMy(instanceId, database, MariaDbDataScript.ClearSql(schema, table), ct);
            return mr.IsSuccess ? mr with { StdOut = $"Cleared all data from {schema}.{table}." } : mr;
        }
        if (await IsPostgresAsync(instanceId, ct))
        {
            var pr = await RunPg(instanceId, database, PostgresDataScript.ClearSql(schema, table), ct);
            // psql only echoes the "DO" command tag here.
            return pr.IsSuccess ? pr with { StdOut = $"Cleared all data from {schema}.{table}." } : pr;
        }
        var r = await Run(instanceId, database, MssqlDataScript.ClearSql(schema, table), ct);
        return r.IsSuccess ? r with { StdOut = $"Cleared all data from {schema}.{table}." + (r.StdOut.Length > 0 ? "\n" + r.StdOut : "") } : r;
    }

    private static ResultTable? FirstTable(QueryResult r) =>
        SqlcmdOutputParser.Parse(r.StdOut).Select(b => b.Table).FirstOrDefault(t => t is not null);

    public async Task<QueryResult> PopulateAsync(Guid instanceId, string database, string schema, string table, int rows, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(table)) return Fail("Choose a table.");
        if (rows is < 1 or > MssqlDataScript.MaxRows) return Fail($"Number of rows must be between 1 and {MssqlDataScript.MaxRows}.");
        if (await IsPostgresAsync(instanceId, ct)) return await PopulatePostgresAsync(instanceId, database, schema, table, rows, ct);
        if (await IsMariaDbAsync(instanceId, ct)) return await PopulateMariaDbAsync(instanceId, database, schema, table, rows, ct);

        var colsRes = await Run(instanceId, database, MssqlDataScript.ColumnsSql(schema, table), ct);
        if (!colsRes.IsSuccess) return colsRes;
        var cols = FirstTable(colsRes) is { } ct1 ? MssqlDataScript.ParseColumns(ct1) : [];
        if (cols.Count == 0) return Fail($"Table {schema}.{table} was not found or has no columns.");

        var uniqRes = await Run(instanceId, database, MssqlDataScript.UniqueSetsSql(schema, table), ct);
        if (!uniqRes.IsSuccess) return uniqRes;
        var fkRes = await Run(instanceId, database, MssqlDataScript.ForeignKeysSql(schema, table), ct);
        if (!fkRes.IsSuccess) return fkRes;

        var meta = new TableMeta(
            cols,
            FirstTable(uniqRes) is { } ut ? MssqlDataScript.ParseUniqueSets(ut) : [],
            FirstTable(fkRes) is { } ft ? MssqlDataScript.ParseForeignKeys(ft) : []);

        var (plan, planError) = MssqlDataScript.Plan(schema, table, meta);
        if (plan is null) return Fail(planError ?? "Could not plan the insert.");

        var fkTuples = new List<IReadOnlyList<string[]>>();
        foreach (var group in plan.FkGroups)
        {
            var r = await Run(instanceId, database, MssqlDataScript.FkValuesSql(group), ct);
            if (!r.IsSuccess) return r;
            fkTuples.Add(FirstTable(r)?.Rows.Where(x => x.Count >= group.Columns.Count).Select(x => x.ToArray()).ToList() ?? []);
        }

        var maxes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (column, kind) in plan.Counters.Where(c => MssqlDataScript.NeedsMax(c.Value)))
        {
            var r = await Run(instanceId, database, MssqlDataScript.MaxSql(schema, table, column, kind), ct);
            if (!r.IsSuccess) return r;
            maxes[column] = FirstTable(r) is { Rows.Count: > 0 } t ? t.Rows[0][0] : null;
        }

        var existing = new List<HashSet<string>>();
        foreach (var set in plan.TupleSets)
        {
            var r = await Run(instanceId, database, MssqlDataScript.ExistingTuplesSql(schema, table, set), ct);
            if (!r.IsSuccess) return r;
            existing.Add((FirstTable(r)?.Rows ?? []).Select(x => string.Join('\u0001', x)).ToHashSet());
        }

        var (script, error) = MssqlDataScript.BuildInsert(schema, table, plan, rows, fkTuples, existing, maxes, Random.Shared);
        if (script is null) return Fail(error ?? "Could not build insert script.");

        var result = await manager.ExecuteSqlAsync(instanceId, MssqlDataScript.Use(database) + script, ct);
        return result.IsSuccess ? result with { StdOut = $"Inserted {rows} random row{(rows == 1 ? "" : "s")} into {schema}.{table}." } : result;
    }

    private async Task<QueryResult> PopulateMariaDbAsync(Guid id, string database, string schema, string table, int rows, CancellationToken ct)
    {
        var colsRes = await RunMy(id, database, MariaDbDataScript.ColumnsSql(schema, table, await DbmsKeyAsync(id, ct) == MariaDbProvider.ProviderKey), ct);
        if (!colsRes.IsSuccess) return colsRes;
        var cols = FirstTable(colsRes) is { } ct1 ? MariaDbDataScript.ParseColumns(ct1) : [];
        if (cols.Count == 0) return Fail($"Table {schema}.{table} was not found or has no columns.");

        var uniqRes = await RunMy(id, database, MariaDbDataScript.UniqueSetsSql(schema, table), ct);
        if (!uniqRes.IsSuccess) return uniqRes;
        var fkRes = await RunMy(id, database, MariaDbDataScript.ForeignKeysSql(schema, table), ct);
        if (!fkRes.IsSuccess) return fkRes;

        var meta = new MyMeta(
            cols,
            FirstTable(uniqRes) is { } ut ? MariaDbDataScript.ParseUniqueSets(ut) : [],
            FirstTable(fkRes) is { } ft ? MariaDbDataScript.ParseForeignKeys(ft) : []);

        var (plan, planError) = MariaDbDataScript.Plan(meta);
        if (plan is null) return Fail(planError ?? "Could not plan the insert.");

        // A required foreign key cannot be satisfied from an empty parent table.
        for (var g = 0; g < plan.FkGroups.Count; g++)
        {
            if (!MariaDbDataScript.FkRequired(plan, g)) continue;
            var fk = plan.FkGroups[g];
            var r = await RunMy(id, database, MariaDbDataScript.HasRowsSql(fk.RefSchema, fk.RefTable), ct);
            if (!r.IsSuccess) return r;
            if (FirstTable(r) is { Rows.Count: > 0 } t && t.Rows[0][0] == "0")
                return Fail($"The referenced table {fk.RefSchema}.{fk.RefTable} has no rows. Populate it first.");
        }

        var (script, error) = MariaDbDataScript.BuildInsert(schema, table, plan, rows, Random.Shared);
        if (script is null) return Fail(error ?? "Could not build insert script.");

        var result = await RunMy(id, database, script, ct);
        return result.IsSuccess ? result with { StdOut = $"Inserted {rows} random row{(rows == 1 ? "" : "s")} into {schema}.{table}." } : result;
    }

    private async Task<QueryResult> PopulatePostgresAsync(Guid id, string database, string schema, string table, int rows, CancellationToken ct)
    {
        var colsRes = await RunPg(id, database, PostgresDataScript.ColumnsSql(schema, table), ct);
        if (!colsRes.IsSuccess) return colsRes;
        var cols = FirstTable(colsRes) is { } ct1 ? PostgresDataScript.ParseColumns(ct1) : [];
        if (cols.Count == 0) return Fail($"Table {schema}.{table} was not found or has no columns.");

        var uniqRes = await RunPg(id, database, PostgresDataScript.UniqueSetsSql(schema, table), ct);
        if (!uniqRes.IsSuccess) return uniqRes;
        var fkRes = await RunPg(id, database, PostgresDataScript.ForeignKeysSql(schema, table), ct);
        if (!fkRes.IsSuccess) return fkRes;

        var meta = new PgMeta(
            cols,
            FirstTable(uniqRes) is { } ut ? PostgresDataScript.ParseUniqueSets(ut) : [],
            FirstTable(fkRes) is { } ft ? PostgresDataScript.ParseForeignKeys(ft) : []);

        var (plan, planError) = PostgresDataScript.Plan(meta);
        if (plan is null) return Fail(planError ?? "Could not plan the insert.");

        // A required foreign key cannot be satisfied from an empty parent table.
        for (var g = 0; g < plan.FkGroups.Count; g++)
        {
            if (!PostgresDataScript.FkRequired(plan, g)) continue;
            var fk = plan.FkGroups[g];
            var r = await RunPg(id, database, PostgresDataScript.HasRowsSql(fk.RefSchema, fk.RefTable), ct);
            if (!r.IsSuccess) return r;
            if (FirstTable(r) is { Rows.Count: > 0 } t && t.Rows[0][0] == "0")
                return Fail($"The referenced table {fk.RefSchema}.{fk.RefTable} has no rows. Populate it first.");
        }

        var (script, error) = PostgresDataScript.BuildInsert(schema, table, plan, rows, Random.Shared);
        if (script is null) return Fail(error ?? "Could not build insert script.");

        var result = await RunPg(id, database, script, ct);
        return result.IsSuccess ? result with { StdOut = $"Inserted {rows} random row{(rows == 1 ? "" : "s")} into {schema}.{table}." } : result;
    }
}

