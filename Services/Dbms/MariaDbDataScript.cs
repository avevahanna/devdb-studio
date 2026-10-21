using System.Globalization;
using System.Text;

namespace DbServeApp.Services.Dbms;

public enum MyKind { Int, Decimal, Float, Bool, Text, Date, DateTime, Time, Year, Uuid, Json, Blob, Enum, Set, Unknown }

public sealed record MyColumn(
    string Name, string TypeSql, string DataType, MyKind Kind, int Length, int Precision, int Scale,
    bool Unsigned, bool NotNull, bool Skipped, bool HasDefault, IReadOnlyList<string> Labels);

public sealed record MyForeignKey(string RefSchema, string RefTable, IReadOnlyList<string> Columns, IReadOnlyList<string> RefColumns);

public sealed record MyMeta(
    IReadOnlyList<MyColumn> Columns,
    IReadOnlyList<IReadOnlyList<string>> UniqueSets,
    IReadOnlyList<MyForeignKey> ForeignKeys);

public sealed record MyPlan(
    IReadOnlyList<MyColumn> ValueColumns,
    IReadOnlyList<(MyColumn Column, int Index)> SqlCounters,
    IReadOnlyList<MyForeignKey> FkGroups,
    IReadOnlyList<(MyColumn Column, int Group, int Index)> FkColumns,
    IReadOnlySet<string> TextCounters);

/// <summary>Builds SQL for reading table metadata, generating random rows and clearing a table on MariaDB/MySQL.</summary>
public static class MariaDbDataScript
{
    public const int MaxRows = MssqlDataScript.MaxRows;
    // Upper bound for the parent rows a foreign key can pick from.
    private const int FkSampleSize = 100_000;
    private const int BatchSize = 500;
    private const int SuffixLength = 7;
    private const string Sep = "~|~";

    private static string Q(string id) => TableDialect.MariaDb.Quote(id);
    private static string Lit(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'";
    private static string Inv(IFormattable v) => v.ToString(null, CultureInfo.InvariantCulture);

    // MariaDB stores JSON as LONGTEXT guarded by a CHECK(json_valid(...)) constraint; MySQL has a native JSON type
    // and a CHECK_CONSTRAINTS view without a table column.
    private static string JsonCheckSql(bool mariaDb) => mariaDb
        ? """
          CASE WHEN EXISTS (SELECT 1 FROM information_schema.CHECK_CONSTRAINTS k
                            WHERE k.CONSTRAINT_SCHEMA = c.TABLE_SCHEMA AND k.TABLE_NAME = c.TABLE_NAME
                              AND LOWER(k.CHECK_CLAUSE) LIKE CONCAT('%json_valid(`', LOWER(c.COLUMN_NAME), '`)%')) THEN 'YES' ELSE 'NO' END
          """
        : "'NO'";

    public static string FullName(string schema, string table) => $"{Q(schema.Trim())}.{Q(table.Trim())}";

    public static string ListTablesSql =>
        "SELECT TABLE_SCHEMA, TABLE_NAME FROM information_schema.TABLES WHERE TABLE_TYPE = 'BASE TABLE' AND TABLE_SCHEMA = DATABASE() ORDER BY 1, 2;";

    public static string ColumnsSql(string schema, string table, bool mariaDb) => $"""
        SELECT c.COLUMN_NAME, c.DATA_TYPE, c.COLUMN_TYPE, c.IS_NULLABLE, c.EXTRA,
               CASE WHEN c.COLUMN_DEFAULT IS NOT NULL AND c.COLUMN_DEFAULT <> 'NULL' THEN 'YES' ELSE 'NO' END,
               COALESCE(c.CHARACTER_MAXIMUM_LENGTH, 0), COALESCE(c.NUMERIC_PRECISION, 0), COALESCE(c.NUMERIC_SCALE, 0),
               {JsonCheckSql(mariaDb)}
        FROM information_schema.COLUMNS c
        WHERE c.TABLE_SCHEMA = {Lit(schema.Trim())} AND c.TABLE_NAME = {Lit(table.Trim())}
        ORDER BY c.ORDINAL_POSITION;
        """;

    // Prefix indexes (SUB_PART) are skipped: uniqueness is then decided by a prefix we cannot control.
    public static string UniqueSetsSql(string schema, string table) => $"""
        SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY SEQ_IN_INDEX SEPARATOR '{Sep}')
        FROM information_schema.STATISTICS
        WHERE TABLE_SCHEMA = {Lit(schema.Trim())} AND TABLE_NAME = {Lit(table.Trim())} AND NON_UNIQUE = 0
        GROUP BY INDEX_NAME
        HAVING COUNT(SUB_PART) = 0;
        """;

    public static string ForeignKeysSql(string schema, string table) => $"""
        SELECT REFERENCED_TABLE_SCHEMA, REFERENCED_TABLE_NAME,
               GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION SEPARATOR '{Sep}'),
               GROUP_CONCAT(REFERENCED_COLUMN_NAME ORDER BY ORDINAL_POSITION SEPARATOR '{Sep}')
        FROM information_schema.KEY_COLUMN_USAGE
        WHERE TABLE_SCHEMA = {Lit(schema.Trim())} AND TABLE_NAME = {Lit(table.Trim())} AND REFERENCED_TABLE_NAME IS NOT NULL
        GROUP BY CONSTRAINT_NAME, REFERENCED_TABLE_SCHEMA, REFERENCED_TABLE_NAME;
        """;

    public static string HasRowsSql(string schema, string table) =>
        $"SELECT COUNT(*) FROM (SELECT 1 FROM {FullName(schema, table)} LIMIT 1) s;";

    // TRUNCATE is refused for tables that are referenced by a foreign key, so DELETE (which also respects the keys) is used.
    public static string ClearSql(string schema, string table)
    {
        var n = FullName(schema, table);
        return $"DELETE FROM {n};\nALTER TABLE {n} AUTO_INCREMENT = 1;";
    }

    // ---- metadata parsing -------------------------------------------------

    private static bool Yes(string s) => s == "YES";
    private static string[] Split(string s) => s.Length == 0 || s == "NULL" ? [] : s.Split(Sep);
    private static int ToInt(string s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    public static IReadOnlyList<MyColumn> ParseColumns(ResultTable t) =>
        t.Rows.Where(r => r.Count >= 10).Select(r =>
        {
            var dataType = r[1].ToLowerInvariant();
            var columnType = r[2];
            var extra = r[4];
            var skipped = extra.Contains("auto_increment", StringComparison.OrdinalIgnoreCase)
                || extra.Contains("VIRTUAL", StringComparison.OrdinalIgnoreCase)
                || extra.Contains("STORED", StringComparison.OrdinalIgnoreCase)
                || extra.Contains("PERSISTENT", StringComparison.OrdinalIgnoreCase)
                || (extra.Contains("GENERATED", StringComparison.OrdinalIgnoreCase) && !extra.Contains("DEFAULT_GENERATED", StringComparison.OrdinalIgnoreCase));
            var labels = dataType is "enum" or "set" ? ParseLabels(columnType) : [];
            var isJson = Yes(r[9]) || dataType == "json";
            var kind = Classify(dataType, columnType, isJson);
            return new MyColumn(
                r[0], columnType, dataType, kind, ToInt(r[6]), ToInt(r[7]), ToInt(r[8]),
                columnType.Contains("unsigned", StringComparison.OrdinalIgnoreCase),
                r[3] == "NO", skipped, Yes(r[5]), labels);
        }).ToList();

    public static IReadOnlyList<IReadOnlyList<string>> ParseUniqueSets(ResultTable t) =>
        t.Rows.Where(r => r.Count >= 1 && r[0].Length > 0 && r[0] != "NULL").Select(r => (IReadOnlyList<string>)Split(r[0])).ToList();

    public static IReadOnlyList<MyForeignKey> ParseForeignKeys(ResultTable t) =>
        t.Rows.Where(r => r.Count >= 4 && r[2].Length > 0).Select(r => new MyForeignKey(r[0], r[1], Split(r[2]), Split(r[3]))).ToList();

    /// <summary>Reads 'a','b','it''s' out of enum('a','b','it''s').</summary>
    private static List<string> ParseLabels(string columnType)
    {
        var labels = new List<string>();
        var open = columnType.IndexOf('(');
        if (open < 0) return labels;
        var sb = new StringBuilder();
        var inQuote = false;
        for (var i = open + 1; i < columnType.Length; i++)
        {
            var ch = columnType[i];
            if (!inQuote) { if (ch == '\'') inQuote = true; continue; }
            if (ch == '\'')
            {
                if (i + 1 < columnType.Length && columnType[i + 1] == '\'') { sb.Append('\''); i++; }
                else { labels.Add(sb.ToString()); sb.Clear(); inQuote = false; }
            }
            else sb.Append(ch);
        }
        return labels;
    }

    private static MyKind Classify(string dataType, string columnType, bool isJson)
    {
        if (dataType == "tinyint" && columnType.StartsWith("tinyint(1)", StringComparison.OrdinalIgnoreCase)) return MyKind.Bool;
        return dataType switch
        {
            "tinyint" or "smallint" or "mediumint" or "int" or "integer" or "bigint" => MyKind.Int,
            "decimal" or "numeric" => MyKind.Decimal,
            "float" or "double" => MyKind.Float,
            "char" or "varchar" or "tinytext" or "text" or "mediumtext" or "longtext" => isJson ? MyKind.Json : MyKind.Text,
            "json" => MyKind.Json,
            "date" => MyKind.Date,
            "datetime" or "timestamp" => MyKind.DateTime,
            "time" => MyKind.Time,
            "year" => MyKind.Year,
            "uuid" => MyKind.Uuid,
            "binary" or "varbinary" or "tinyblob" or "blob" or "mediumblob" or "longblob" => MyKind.Blob,
            "enum" => MyKind.Enum,
            "set" => MyKind.Set,
            _ => MyKind.Unknown,
        };
    }

    // ---- planning ---------------------------------------------------------

    private static readonly MyKind[] CounterOrder = [MyKind.Int, MyKind.Uuid, MyKind.Date, MyKind.DateTime, MyKind.Decimal, MyKind.Text];
    private static readonly MyKind[] SqlCounterKinds = [MyKind.Int, MyKind.Decimal, MyKind.Date, MyKind.DateTime];

    public static (MyPlan? Plan, string? Error) Plan(MyMeta meta)
    {
        var cols = meta.Columns.Where(c => !c.Skipped).ToList();
        var byName = cols.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

        var fkGroups = new List<MyForeignKey>();
        var fkCols = new Dictionary<string, (int Group, int Index)>(StringComparer.OrdinalIgnoreCase);
        foreach (var fk in meta.ForeignKeys)
        {
            if (fk.Columns.Count != fk.RefColumns.Count || fk.Columns.Any(c => !byName.ContainsKey(c) || fkCols.ContainsKey(c))) continue;
            fkGroups.Add(fk);
            for (var i = 0; i < fk.Columns.Count; i++) fkCols[fk.Columns[i]] = (fkGroups.Count - 1, i);
        }

        var counters = new Dictionary<string, MyKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in meta.UniqueSets)
        {
            // A set that holds an auto increment column (not in byName) is unique on its own, and so is one that already has a counter.
            if (set.Any(c => !byName.ContainsKey(c)) || set.Any(counters.ContainsKey)) continue;
            var candidates = set.Where(c => !fkCols.ContainsKey(c)).Select(c => byName[c]).ToList();
            MyColumn? pick = null;
            foreach (var kind in CounterOrder)
            {
                pick = candidates.FirstOrDefault(c => c.Kind == kind);
                if (pick is not null) break;
            }
            if (pick is null)
                return (null, $"Cannot generate unique values for ({string.Join(", ", set)}): it consists only of foreign keys or unsupported types.");
            if (pick.Kind == MyKind.Text && pick.Length < SuffixLength)
                return (null, $"Column {pick.Name} must be unique but is shorter than {SuffixLength} characters.");
            counters[pick.Name] = pick.Kind;
        }

        var bad = cols.Where(c => c.Kind == MyKind.Unknown && c.NotNull && !c.HasDefault && !fkCols.ContainsKey(c.Name))
            .Select(c => $"{c.Name} ({c.TypeSql})").ToList();
        if (bad.Count > 0) return (null, $"Unsupported column type(s) that cannot be NULL: {string.Join(", ", bad)}.");

        var sqlCounters = cols.Where(c => counters.TryGetValue(c.Name, out var k) && SqlCounterKinds.Contains(k))
            .Select((c, i) => (Column: c, Index: i)).ToList();
        var sqlCounterNames = sqlCounters.Select(x => x.Column.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var textCounters = counters.Where(kv => kv.Value == MyKind.Text).Select(kv => kv.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Unknown types that have a default are left to the database.
        var values = cols.Where(c => !fkCols.ContainsKey(c.Name) && !sqlCounterNames.Contains(c.Name) && !(c.Kind == MyKind.Unknown && c.HasDefault)).ToList();
        var fkColumns = fkCols.Select(kv => (Column: byName[kv.Key], Group: kv.Value.Group, Index: kv.Value.Index)).ToList();
        return (new MyPlan(values, sqlCounters, fkGroups, fkColumns, textCounters), null);
    }

    // ---- script building --------------------------------------------------

    public static (string? Script, string? Error) BuildInsert(string schema, string table, MyPlan plan, int rows, Random rnd)
    {
        var target = FullName(schema, table);
        var insertCols = plan.ValueColumns.Select(c => Q(c.Name))
            .Concat(plan.SqlCounters.Select(c => Q(c.Column.Name)))
            .Concat(plan.FkColumns.Select(c => Q(c.Column.Name))).ToList();
        if (insertCols.Count == 0) return (null, "The table has no columns that can be filled with data.");

        var selectExprs = plan.ValueColumns.Select((_, i) => $"v.c{i}")
            .Concat(plan.SqlCounters.Select(c => CounterExpr(c.Column, c.Index)))
            .Concat(plan.FkColumns.Select(c => $"f{c.Group}.{Q(plan.FkGroups[c.Group].RefColumns[c.Index])}")).ToList();

        var textBase = rnd.Next(0, 0x0FFFFFFF - rows);
        var sb = new StringBuilder();
        sb.AppendLine("START TRANSACTION;");
        for (var start = 0; start < rows; start += BatchSize)
        {
            var count = Math.Min(BatchSize, rows - start);

            // Counters continue above the current maximum; each batch recomputes it, so batches never overlap.
            foreach (var (col, idx) in plan.SqlCounters)
                sb.AppendLine($"SET @m{idx} = (SELECT COALESCE(MAX({Q(col.Name)}), {CounterDefault(col)}) FROM {target});");

            var rowsSql = new List<string>(count);
            for (var n = 0; n < count; n++)
            {
                var suffix = Convert.ToString(textBase + start + n, 16).PadLeft(SuffixLength, '0');
                var cells = new List<string> { Inv(n + 1) };
                cells.AddRange(plan.ValueColumns.Select(c => Value(c, plan.TextCounters.Contains(c.Name) ? suffix : null, rnd)));
                cells.AddRange(plan.FkGroups.Select(_ => Inv(rnd.Next())));
                if (n == 0)
                {
                    var names = new List<string> { "i" };
                    names.AddRange(plan.ValueColumns.Select((_, i) => $"c{i}"));
                    names.AddRange(plan.FkGroups.Select((_, g) => $"rk{g}"));
                    cells = cells.Select((cell, k) => $"{cell} AS {names[k]}").ToList();
                }
                rowsSql.Add("SELECT " + string.Join(", ", cells));
            }

            sb.AppendLine($"INSERT INTO {target} ({string.Join(", ", insertCols)})");
            sb.AppendLine($"SELECT {string.Join(", ", selectExprs)}");
            sb.AppendLine($"FROM ({string.Join("\nUNION ALL ", rowsSql)}) v");
            for (var gi = 0; gi < plan.FkGroups.Count; gi++)
            {
                var fk = plan.FkGroups[gi];
                var parent = FullName(fk.RefSchema, fk.RefTable);
                var refCols = string.Join(", ", fk.RefColumns.Select(Q));
                // A random row number picks the parent; the numbering lets composite keys take all columns from one row.
                sb.AppendLine($"CROSS JOIN (SELECT COUNT(*) AS c FROM (SELECT 1 FROM {parent} LIMIT {FkSampleSize}) s) k{gi}");
                sb.AppendLine($"{(FkRequired(plan, gi) ? "JOIN" : "LEFT JOIN")} (SELECT ROW_NUMBER() OVER () AS rn, {refCols} FROM (SELECT {refCols} FROM {parent} LIMIT {FkSampleSize}) s) f{gi} ON f{gi}.rn = 1 + (v.rk{gi} MOD k{gi}.c)");
            }
            sb.AppendLine(";");
        }
        sb.AppendLine("COMMIT;");
        return (sb.ToString(), null);
    }

    /// <summary>True when any column of the foreign key is NOT NULL, so the parent table must have rows.</summary>
    public static bool FkRequired(MyPlan plan, int group) =>
        plan.FkColumns.Any(c => c.Group == group && c.Column.NotNull);

    private static string CounterDefault(MyColumn c) => c.Kind switch
    {
        MyKind.Date => "CURDATE()",
        MyKind.DateTime => "NOW()",
        _ => "0",
    };

    private static string CounterExpr(MyColumn c, int index) => c.Kind switch
    {
        MyKind.Date => $"DATE_ADD(@m{index}, INTERVAL v.i DAY)",
        MyKind.DateTime => $"DATE_ADD(@m{index}, INTERVAL v.i SECOND)",
        _ => $"@m{index} + v.i",
    };

    private static string Value(MyColumn c, string? suffix, Random r)
    {
        var key = c.Name.ToLowerInvariant();
        switch (c.Kind)
        {
            case MyKind.Int:
            {
                var v = RandomData.Integer(key, c.DataType is "tinyint" or "smallint" ? c.DataType : "int", r);
                if (c.DataType == "tinyint" && !c.Unsigned) v = v % 127 + 1;
                return Inv(v);
            }
            case MyKind.Decimal:
            case MyKind.Float:
                return Inv(RandomData.Decimal(key, c.Precision, c.Scale, r));
            case MyKind.Bool: return r.Next(2).ToString(CultureInfo.InvariantCulture);
            case MyKind.Text: return Lit(RandomData.Text(key, c.Length > 0 ? c.Length : 255, suffix, r));
            case MyKind.Date: return Lit(RandomData.Date(key, r).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            case MyKind.DateTime:
                return Lit(RandomData.Date(key, r).AddSeconds(r.Next(86400)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            case MyKind.Time: return Lit(TimeSpan.FromSeconds(r.Next(86400)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture));
            case MyKind.Year: return Inv(RandomData.Date(key, r).Year);
            case MyKind.Uuid: return Lit(Guid.NewGuid().ToString());
            case MyKind.Json: return Lit($"{{\"id\": {r.Next(1, 10000)}, \"label\": \"{RandomData.Words(r, 2)}\"}}");
            case MyKind.Blob:
                var bytes = new byte[c.Length > 0 ? Math.Min(8, c.Length) : 8];
                r.NextBytes(bytes);
                return "X'" + Convert.ToHexString(bytes) + "'";
            case MyKind.Enum:
            case MyKind.Set:
                return c.Labels.Count == 0 ? "NULL" : Lit(c.Labels[r.Next(c.Labels.Count)]);
            default: return "NULL";
        }
    }
}
