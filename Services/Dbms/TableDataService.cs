namespace DbServeApp.Services.Dbms;

public sealed record TableRef(string Schema, string Name)
{
    public override string ToString() => $"{Schema}.{Name}";
}

public sealed record TableListResult(IReadOnlyList<TableRef> Tables, string? Error);

/// <summary>Table data operations (random population, clearing). Currently SQL Server only.</summary>
public interface ITableDataService
{
    Task<TableListResult> ListTablesAsync(Guid instanceId, string database, CancellationToken ct = default);
    Task<QueryResult> PopulateAsync(Guid instanceId, string database, string schema, string table, int rows, CancellationToken ct = default);
    Task<QueryResult> ClearAsync(Guid instanceId, string database, string schema, string table, CancellationToken ct = default);
}

public sealed class TableDataService(IInstanceManager manager) : ITableDataService
{
    private static QueryResult Fail(string msg) => new(false, "", msg, -1, TimeSpan.Zero);

    private Task<QueryResult> Run(Guid id, string database, string sql, CancellationToken ct) =>
        manager.ExecuteSqlAsync(id, MssqlDataScript.Use(database) + sql, ct);

    public async Task<TableListResult> ListTablesAsync(Guid instanceId, string database, CancellationToken ct = default)
    {
        var r = await Run(instanceId, database, MssqlDataScript.ListTablesSql, ct);
        if (!r.IsSuccess) return new TableListResult([], (r.StdErr + "\n" + r.StdOut).Trim());
        var table = SqlcmdOutputParser.Parse(r.StdOut).Select(b => b.Table).FirstOrDefault(t => t is not null);
        var list = table?.Rows.Where(x => x.Count >= 2).Select(x => new TableRef(x[0], x[1])).ToList() ?? [];
        return new TableListResult(list, null);
    }

    public async Task<QueryResult> ClearAsync(Guid instanceId, string database, string schema, string table, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(table)) return Fail("Choose a table.");
        var r = await Run(instanceId, database, MssqlDataScript.ClearSql(schema, table), ct);
        return r.IsSuccess ? r with { StdOut = $"Cleared all data from {schema}.{table}." + (r.StdOut.Length > 0 ? "\n" + r.StdOut : "") } : r;
    }

    private static ResultTable? FirstTable(QueryResult r) =>
        SqlcmdOutputParser.Parse(r.StdOut).Select(b => b.Table).FirstOrDefault(t => t is not null);

    public async Task<QueryResult> PopulateAsync(Guid instanceId, string database, string schema, string table, int rows, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(table)) return Fail("Choose a table.");
        if (rows is < 1 or > MssqlDataScript.MaxRows) return Fail($"Number of rows must be between 1 and {MssqlDataScript.MaxRows}.");

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
}

