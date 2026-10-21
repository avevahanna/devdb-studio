using System.Globalization;
using System.Text;

namespace DevDbStudio.Services.Dbms;

public enum PgKind { Int, Numeric, Float, Money, Bool, Text, Date, Timestamp, Time, Uuid, Json, Bytea, Inet, Enum, Unknown }

public sealed record PgColumn(
    string Name, string TypeSql, PgKind Kind, int Length, int Precision, int Scale,
    bool NotNull, bool Skipped, bool HasDefault, IReadOnlyList<string> EnumLabels);

public sealed record PgForeignKey(string RefSchema, string RefTable, IReadOnlyList<string> Columns, IReadOnlyList<string> RefColumns);

public sealed record PgMeta(
    IReadOnlyList<PgColumn> Columns,
    IReadOnlyList<IReadOnlyList<string>> UniqueSets,
    IReadOnlyList<PgForeignKey> ForeignKeys);

public sealed record PgPlan(
    IReadOnlyList<PgColumn> ValueColumns,
    IReadOnlyList<(PgColumn Column, int Index)> SqlCounters,
    IReadOnlyList<PgForeignKey> FkGroups,
    IReadOnlyList<(PgColumn Column, int Group, int Index)> FkColumns,
    IReadOnlySet<string> TextCounters);

/// <summary>Builds PostgreSQL for reading table metadata, generating random rows and clearing a table.</summary>
public static class PostgresDataScript
{
    public const int MaxRows = MssqlDataScript.MaxRows;
    private const int BatchSize = 500;
    private const int SuffixLength = 7;
    private const string Sep = "~|~";

    private static string Q(string id) => TableDialect.Postgres.Quote(id);
    private static string Lit(string s) => "'" + s.Replace("'", "''") + "'";
    private static string Inv(IFormattable v) => v.ToString(null, CultureInfo.InvariantCulture);

    public static string FullName(string schema, string table) => $"{Q(schema.Trim())}.{Q(table.Trim())}";
    private static string RegClass(string schema, string table) => $"to_regclass({Lit(FullName(schema, table))})";

    public const string ListTablesSql =
        "SELECT table_schema, table_name FROM information_schema.tables WHERE table_type = 'BASE TABLE' AND table_schema NOT IN ('pg_catalog', 'information_schema') AND table_schema NOT LIKE '\\_timescaledb%' AND table_schema <> 'timescaledb_information' ORDER BY 1, 2;";

    public static string ColumnsSql(string schema, string table) => $"""
        SELECT a.attname,
               format_type(a.atttypid, a.atttypmod),
               CASE WHEN a.attnotnull THEN 't' ELSE 'f' END,
               CASE WHEN a.attidentity <> '' OR a.attgenerated <> '' OR COALESCE(pg_get_expr(d.adbin, d.adrelid), '') LIKE 'nextval(%' THEN 't' ELSE 'f' END,
               CASE WHEN d.adbin IS NOT NULL THEN 't' ELSE 'f' END,
               COALESCE((SELECT string_agg(e.enumlabel, '{Sep}' ORDER BY e.enumsortorder) FROM pg_enum e WHERE e.enumtypid = a.atttypid), '')
        FROM pg_attribute a
        LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
        WHERE a.attrelid = {RegClass(schema, table)} AND a.attnum > 0 AND NOT a.attisdropped
        ORDER BY a.attnum;
        """;

    public static string UniqueSetsSql(string schema, string table) => $"""
        SELECT array_to_string(ARRAY(
                 SELECT a.attname FROM unnest(i.indkey::int2[]) WITH ORDINALITY k(attnum, ord)
                 JOIN pg_attribute a ON a.attrelid = i.indrelid AND a.attnum = k.attnum
                 ORDER BY k.ord), '{Sep}')
        FROM pg_index i
        WHERE i.indrelid = {RegClass(schema, table)} AND i.indisunique AND i.indisvalid AND i.indpred IS NULL AND i.indexprs IS NULL;
        """;

    public static string ForeignKeysSql(string schema, string table) => $"""
        SELECT n.nspname, c.relname,
               (SELECT string_agg(a.attname, '{Sep}' ORDER BY k.ord) FROM unnest(con.conkey) WITH ORDINALITY k(attnum, ord)
                JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum),
               (SELECT string_agg(a.attname, '{Sep}' ORDER BY k.ord) FROM unnest(con.confkey) WITH ORDINALITY k(attnum, ord)
                JOIN pg_attribute a ON a.attrelid = con.confrelid AND a.attnum = k.attnum)
        FROM pg_constraint con
        JOIN pg_class c ON c.oid = con.confrelid
        JOIN pg_namespace n ON n.oid = c.relnamespace
        WHERE con.contype = 'f' AND con.conrelid = {RegClass(schema, table)};
        """;

    public static string HasRowsSql(string schema, string table) =>
        $"SELECT count(*) FROM (SELECT 1 FROM {FullName(schema, table)} LIMIT 1) s;";

    public static string ClearSql(string schema, string table)
    {
        var n = FullName(schema, table);
        var o = Lit(n);
        // TRUNCATE is refused when other tables reference this one, so fall back to DELETE and restart the sequences by hand.
        return $"""
            DO $do$
            DECLARE r record;
            BEGIN
                IF to_regclass({o}) IS NULL THEN RAISE EXCEPTION 'Table not found.'; END IF;
                IF EXISTS (SELECT 1 FROM pg_constraint WHERE contype = 'f' AND confrelid = to_regclass({o}) AND conrelid <> confrelid) THEN
                    DELETE FROM {n};
                    FOR r IN SELECT pg_get_serial_sequence({o}, a.attname) AS seq FROM pg_attribute a
                             WHERE a.attrelid = to_regclass({o}) AND a.attnum > 0 AND NOT a.attisdropped LOOP
                        IF r.seq IS NOT NULL THEN EXECUTE 'ALTER SEQUENCE ' || r.seq || ' RESTART'; END IF;
                    END LOOP;
                ELSE
                    TRUNCATE TABLE {n} RESTART IDENTITY;
                END IF;
            END
            $do$;
            """;
    }

    // ---- metadata parsing -------------------------------------------------

    private static bool T(string s) => s == "t";
    private static string[] Split(string s) => s.Length == 0 ? [] : s.Split(Sep);

    public static IReadOnlyList<PgColumn> ParseColumns(ResultTable t) =>
        t.Rows.Where(r => r.Count >= 6).Select(r =>
        {
            var labels = Split(r[5]);
            var (kind, len, prec, scale) = Classify(r[1], labels.Length > 0);
            return new PgColumn(r[0], r[1], kind, len, prec, scale, T(r[2]), T(r[3]), T(r[4]), labels);
        }).ToList();

    public static IReadOnlyList<IReadOnlyList<string>> ParseUniqueSets(ResultTable t) =>
        t.Rows.Where(r => r.Count >= 1 && r[0].Length > 0).Select(r => (IReadOnlyList<string>)Split(r[0])).ToList();

    public static IReadOnlyList<PgForeignKey> ParseForeignKeys(ResultTable t) =>
        t.Rows.Where(r => r.Count >= 4 && r[2].Length > 0).Select(r => new PgForeignKey(r[0], r[1], Split(r[2]), Split(r[3]))).ToList();

    private static (PgKind Kind, int Length, int Precision, int Scale) Classify(string type, bool isEnum)
    {
        var t = type.Trim().ToLowerInvariant();
        if (isEnum) return (PgKind.Enum, 0, 0, 0);
        if (t is "smallint" or "integer" or "bigint") return (PgKind.Int, 0, t == "smallint" ? 5 : 10, 0);
        if (t.StartsWith("numeric") || t.StartsWith("decimal"))
        {
            var (p, s) = (12, 2);
            var open = t.IndexOf('(');
            if (open >= 0)
            {
                var parts = t[(open + 1)..t.IndexOf(')')].Split(',');
                p = int.Parse(parts[0].Trim(), CultureInfo.InvariantCulture);
                s = parts.Length > 1 ? int.Parse(parts[1].Trim(), CultureInfo.InvariantCulture) : 0;
            }
            return (PgKind.Numeric, 0, p, s);
        }
        if (t is "real" or "double precision") return (PgKind.Float, 0, 10, 2);
        if (t == "money") return (PgKind.Money, 0, 12, 2);
        if (t == "boolean") return (PgKind.Bool, 0, 0, 0);
        if (t.StartsWith("character varying") || t.StartsWith("character(") || t == "character" || t is "text" or "citext" or "bpchar")
        {
            var open = t.IndexOf('(');
            var len = open >= 0 ? int.Parse(t[(open + 1)..t.IndexOf(')')], CultureInfo.InvariantCulture) : t == "character" ? 1 : 255;
            return (PgKind.Text, len, 0, 0);
        }
        if (t == "date") return (PgKind.Date, 0, 0, 0);
        if (t.StartsWith("timestamp")) return (PgKind.Timestamp, 0, 0, 0);
        if (t.StartsWith("time")) return (PgKind.Time, 0, 0, 0);
        if (t == "uuid") return (PgKind.Uuid, 0, 0, 0);
        if (t is "json" or "jsonb") return (PgKind.Json, 0, 0, 0);
        if (t == "bytea") return (PgKind.Bytea, 0, 0, 0);
        if (t == "inet") return (PgKind.Inet, 0, 0, 0);
        return (PgKind.Unknown, 0, 0, 0);
    }

    // ---- planning ---------------------------------------------------------

    private static readonly PgKind[] CounterOrder = [PgKind.Int, PgKind.Uuid, PgKind.Date, PgKind.Timestamp, PgKind.Numeric, PgKind.Text];
    private static readonly PgKind[] SqlCounterKinds = [PgKind.Int, PgKind.Numeric, PgKind.Date, PgKind.Timestamp];

    public static (PgPlan? Plan, string? Error) Plan(PgMeta meta)
    {
        var cols = meta.Columns.Where(c => !c.Skipped).ToList();
        var byName = cols.ToDictionary(c => c.Name, StringComparer.Ordinal);

        var fkGroups = new List<PgForeignKey>();
        var fkCols = new Dictionary<string, (int Group, int Index)>(StringComparer.Ordinal);
        foreach (var fk in meta.ForeignKeys)
        {
            if (fk.Columns.Count != fk.RefColumns.Count || fk.Columns.Any(c => !byName.ContainsKey(c) || fkCols.ContainsKey(c))) continue;
            fkGroups.Add(fk);
            for (var i = 0; i < fk.Columns.Count; i++) fkCols[fk.Columns[i]] = (fkGroups.Count - 1, i);
        }

        var counters = new Dictionary<string, PgKind>(StringComparer.Ordinal);
        foreach (var set in meta.UniqueSets)
        {
            // A set that holds an identity/serial column (not in byName) is unique on its own, and so is one that already has a counter.
            if (set.Any(c => !byName.ContainsKey(c)) || set.Any(counters.ContainsKey)) continue;
            var candidates = set.Where(c => !fkCols.ContainsKey(c)).Select(c => byName[c]).ToList();
            PgColumn? pick = null;
            foreach (var kind in CounterOrder)
            {
                pick = candidates.FirstOrDefault(c => c.Kind == kind);
                if (pick is not null) break;
            }
            if (pick is null)
                return (null, $"Cannot generate unique values for ({string.Join(", ", set)}): it consists only of foreign keys or unsupported types.");
            if (pick.Kind == PgKind.Text && pick.Length < SuffixLength)
                return (null, $"Column {pick.Name} must be unique but is shorter than {SuffixLength} characters.");
            counters[pick.Name] = pick.Kind;
        }

        var bad = cols.Where(c => c.Kind == PgKind.Unknown && c.NotNull && !c.HasDefault && !fkCols.ContainsKey(c.Name))
            .Select(c => $"{c.Name} ({c.TypeSql})").ToList();
        if (bad.Count > 0) return (null, $"Unsupported column type(s) that cannot be NULL: {string.Join(", ", bad)}.");

        var sqlCounters = cols.Where(c => counters.TryGetValue(c.Name, out var k) && SqlCounterKinds.Contains(k))
            .Select((c, i) => (Column: c, Index: i)).ToList();
        var sqlCounterNames = sqlCounters.Select(x => x.Column.Name).ToHashSet(StringComparer.Ordinal);
        var textCounters = counters.Where(kv => kv.Value == PgKind.Text).Select(kv => kv.Key).ToHashSet(StringComparer.Ordinal);

        // Unknown types that have a default are left to the database.
        var values = cols.Where(c => !fkCols.ContainsKey(c.Name) && !sqlCounterNames.Contains(c.Name) && !(c.Kind == PgKind.Unknown && c.HasDefault)).ToList();
        var fkColumns = fkCols.Select(kv => (Column: byName[kv.Key], Group: kv.Value.Group, Index: kv.Value.Index)).ToList();
        return (new PgPlan(values, sqlCounters, fkGroups, fkColumns, textCounters), null);
    }

    // ---- script building --------------------------------------------------

    public static (string? Script, string? Error) BuildInsert(string schema, string table, PgPlan plan, int rows, Random rnd)
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
        sb.AppendLine("BEGIN;");
        for (var start = 0; start < rows; start += BatchSize)
        {
            var count = Math.Min(BatchSize, rows - start);
            var vals = new List<string>(count);
            for (var n = 0; n < count; n++)
            {
                var suffix = Convert.ToString(textBase + start + n, 16).PadLeft(SuffixLength, '0');
                var cells = new List<string> { Inv(n + 1) };
                cells.AddRange(plan.ValueColumns.Select(c => Value(c, plan.TextCounters.Contains(c.Name) ? suffix : null, rnd)));
                vals.Add("(" + string.Join(", ", cells) + ")");
            }

            // Counters continue above the current maximum; each batch recomputes it, so batches never overlap.
            if (plan.SqlCounters.Count > 0)
                sb.AppendLine($"WITH b AS (SELECT {string.Join(", ", plan.SqlCounters.Select(c => $"COALESCE(MAX({Q(c.Column.Name)}), {CounterDefault(c.Column)}) AS m{c.Index}"))} FROM {target})");
            sb.AppendLine($"INSERT INTO {target} ({string.Join(", ", insertCols)})");
            sb.AppendLine($"SELECT {string.Join(", ", selectExprs)}");
            sb.AppendLine($"FROM (VALUES {string.Join(",\n  ", vals)}) AS v(i{string.Concat(plan.ValueColumns.Select((_, i) => $", c{i}"))})");
            if (plan.SqlCounters.Count > 0) sb.AppendLine("CROSS JOIN b");
            for (var gi = 0; gi < plan.FkGroups.Count; gi++)
            {
                var fk = plan.FkGroups[gi];
                var nullable = !FkRequired(plan, gi);
                // Referencing v keeps the subquery correlated, so a new parent row is drawn for every inserted row.
                sb.AppendLine($"{(nullable ? "LEFT" : "CROSS")} JOIN LATERAL (SELECT {string.Join(", ", fk.RefColumns.Select(Q))} FROM {FullName(fk.RefSchema, fk.RefTable)} WHERE v.i IS NOT NULL ORDER BY random() LIMIT 1) f{gi}{(nullable ? " ON true" : "")}");
            }
            sb.AppendLine(";");
        }
        sb.AppendLine("COMMIT;");
        return (sb.ToString(), null);
    }

    /// <summary>True when any column of the foreign key is NOT NULL, so the parent table must have rows.</summary>
    public static bool FkRequired(PgPlan plan, int group) =>
        plan.FkColumns.Any(c => c.Group == group && c.Column.NotNull);

    private static string CounterDefault(PgColumn c) => c.Kind switch
    {
        PgKind.Date => "CURRENT_DATE",
        PgKind.Timestamp => c.TypeSql.Contains("with time zone", StringComparison.OrdinalIgnoreCase) ? "now()" : "LOCALTIMESTAMP",
        _ => "0",
    };

    private static string CounterExpr(PgColumn c, int index) => c.Kind == PgKind.Timestamp
        ? $"b.m{index} + v.i * interval '1 second'"
        : $"b.m{index} + v.i";

    private static string Value(PgColumn c, string? suffix, Random r)
    {
        var key = c.Name.ToLowerInvariant();
        string Cast(string literal) => $"CAST({literal} AS {c.TypeSql})";
        switch (c.Kind)
        {
            case PgKind.Int:
                return Cast(Lit(Inv(RandomData.Integer(key, c.Precision == 5 ? "smallint" : "integer", r))));
            case PgKind.Numeric:
            case PgKind.Float:
            case PgKind.Money:
                return Cast(Lit(Inv(RandomData.Decimal(key, c.Precision, c.Scale, r))));
            case PgKind.Bool: return Cast(r.Next(2) == 0 ? "'true'" : "'false'");
            case PgKind.Text: return Cast(Lit(RandomData.Text(key, c.Length, suffix, r)));
            case PgKind.Date: return Cast(Lit(RandomData.Date(key, r).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
            case PgKind.Timestamp:
                return Cast(Lit(RandomData.Date(key, r).AddSeconds(r.Next(86400)).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));
            case PgKind.Time: return Cast(Lit(TimeSpan.FromSeconds(r.Next(86400)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture)));
            case PgKind.Uuid: return Cast(Lit(Guid.NewGuid().ToString()));
            case PgKind.Json: return Cast(Lit($"{{\"id\": {r.Next(1, 10000)}, \"label\": \"{RandomData.Words(r, 2)}\"}}"));
            case PgKind.Bytea:
                var bytes = new byte[8];
                r.NextBytes(bytes);
                return Cast(Lit("\\x" + Convert.ToHexString(bytes).ToLowerInvariant()));
            case PgKind.Inet: return Cast(Lit($"10.{r.Next(256)}.{r.Next(256)}.{r.Next(1, 255)}"));
            case PgKind.Enum: return Cast(Lit(c.EnumLabels[r.Next(c.EnumLabels.Count)]));
            default: return $"CAST(NULL AS {c.TypeSql})";
        }
    }
}
