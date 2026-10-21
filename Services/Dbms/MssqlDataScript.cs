using System.Globalization;
using System.Text;

namespace DevDbStudio.Services.Dbms;

public sealed record ColumnInfo(
    string Name, string Type, int MaxLength, int Precision, int Scale,
    bool Nullable, bool Identity, bool Computed);

/// <summary>A foreign key constraint. <see cref="RefTable"/> and <see cref="RefColumns"/> are already quoted.</summary>
public sealed record ForeignKeyInfo(string RefTable, IReadOnlyList<string> Columns, IReadOnlyList<string> RefColumns);

public sealed record TableMeta(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<IReadOnlyList<string>> UniqueSets,
    IReadOnlyList<ForeignKeyInfo> ForeignKeys);

/// <summary>How a column that must be unique gets its values.</summary>
public enum CounterKind { Int, Date, DateTime, Decimal, Text, Guid }

public sealed record InsertPlan(
    IReadOnlyList<ColumnInfo> Columns,
    IReadOnlyList<ForeignKeyInfo> FkGroups,
    IReadOnlyDictionary<string, (int Group, int Index)> FkColumns,
    IReadOnlyDictionary<string, CounterKind> Counters,
    IReadOnlyList<IReadOnlyList<string>> TupleSets);

/// <summary>Builds T-SQL for reading table metadata, generating random rows and clearing a table.</summary>
public static class MssqlDataScript
{
    public const int MaxRows = 10_000;
    public const int FkSampleSize = 5000;
    public const int ExistingTupleLimit = 100_000;
    private const int BatchSize = 500;
    private const int SuffixLength = 7;
    private const int TupleAttempts = 1000;

    public static string Use(string? database) =>
        string.IsNullOrWhiteSpace(database) ? "" : $"USE {MssqlTableScript.Quote(database.Trim())};\nGO\n";

    public static string FullName(string schema, string table) =>
        $"{MssqlTableScript.Quote(schema.Trim())}.{MssqlTableScript.Quote(table.Trim())}";

    private static string Lit(string s) => "N'" + s.Replace("'", "''") + "'";
    private static string Q(string id) => MssqlTableScript.Quote(id);

    public const string ListTablesSql =
        "SELECT TABLE_SCHEMA, TABLE_NAME FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE' ORDER BY 1, 2;";

    public static string ColumnsSql(string schema, string table) => $"""
        SELECT c.name, ty.name AS type_name, c.max_length, c.precision, c.scale, c.is_nullable, c.is_identity, c.is_computed
        FROM sys.columns c
        JOIN sys.types ty ON ty.user_type_id = c.system_type_id
        WHERE c.object_id = OBJECT_ID({Lit(FullName(schema, table))})
        ORDER BY c.column_id;
        """;

    public static string UniqueSetsSql(string schema, string table) => $"""
        SELECT i.index_id, c.name
        FROM sys.indexes i
        JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id AND ic.is_included_column = 0
        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
        WHERE i.object_id = OBJECT_ID({Lit(FullName(schema, table))}) AND (i.is_primary_key = 1 OR i.is_unique = 1)
        ORDER BY i.index_id, ic.key_ordinal;
        """;

    public static string ForeignKeysSql(string schema, string table) => $"""
        SELECT fk.object_id AS fk_id, QUOTENAME(SCHEMA_NAME(rt.schema_id)) + '.' + QUOTENAME(rt.name) AS ref_table, pc.name AS col, QUOTENAME(rc.name) AS ref_col
        FROM sys.foreign_keys fk
        JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
        JOIN sys.tables rt ON rt.object_id = fk.referenced_object_id
        JOIN sys.columns pc ON pc.object_id = fkc.parent_object_id AND pc.column_id = fkc.parent_column_id
        JOIN sys.columns rc ON rc.object_id = fkc.referenced_object_id AND rc.column_id = fkc.referenced_column_id
        WHERE fk.parent_object_id = OBJECT_ID({Lit(FullName(schema, table))})
        ORDER BY fk.object_id, fkc.constraint_column_id;
        """;

    public static string ClearSql(string schema, string table)
    {
        var n = FullName(schema, table);
        var o = Lit(n);
        // After DELETE the identity must restart at its seed. DBCC CHECKIDENT uses the reseed value as the next
        // identity itself when the table has never held a row, and as "value + increment" otherwise.
        return $"""
            IF OBJECT_ID({o}) IS NULL RAISERROR('Table not found.', 16, 1);
            ELSE IF EXISTS (SELECT 1 FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID({o}))
            BEGIN
                DELETE FROM {n};
                IF OBJECTPROPERTY(OBJECT_ID({o}), 'TableHasIdentity') = 1
                BEGIN
                    DECLARE @used bit = CASE WHEN EXISTS (SELECT 1 FROM sys.identity_columns WHERE object_id = OBJECT_ID({o}) AND last_value IS NOT NULL) THEN 1 ELSE 0 END;
                    DECLARE @seed bigint = CONVERT(bigint, IDENT_SEED({o})) - CASE WHEN @used = 1 THEN CONVERT(bigint, IDENT_INCR({o})) ELSE 0 END;
                    DBCC CHECKIDENT ({o}, RESEED, @seed) WITH NO_INFOMSGS;
                END
            END
            ELSE TRUNCATE TABLE {n};
            """;
    }

    public static IReadOnlyList<ColumnInfo> ParseColumns(ResultTable t) =>
        t.Rows.Where(r => r.Count >= 8).Select(r => new ColumnInfo(
            r[0], r[1].ToLowerInvariant(),
            int.TryParse(r[2], out var ml) ? ml : 0,
            int.TryParse(r[3], out var p) ? p : 0,
            int.TryParse(r[4], out var s) ? s : 0,
            r[5] == "1", r[6] == "1", r[7] == "1")).ToList();

    public static IReadOnlyList<IReadOnlyList<string>> ParseUniqueSets(ResultTable t) =>
        t.Rows.Where(r => r.Count >= 2).GroupBy(r => r[0]).Select(g => (IReadOnlyList<string>)g.Select(r => r[1]).ToList()).ToList();

    public static IReadOnlyList<ForeignKeyInfo> ParseForeignKeys(ResultTable t) =>
        t.Rows.Where(r => r.Count >= 4).GroupBy(r => r[0])
            .Select(g => new ForeignKeyInfo(g.First()[1], g.Select(r => r[2]).ToList(), g.Select(r => r[3]).ToList())).ToList();

    public static string FkValuesSql(ForeignKeyInfo fk) =>
        $"SELECT TOP {FkSampleSize} {string.Join(", ", fk.RefColumns)} FROM {fk.RefTable} WHERE {string.Join(" AND ", fk.RefColumns.Select(c => c + " IS NOT NULL"))};";

    public static string ExistingTuplesSql(string schema, string table, IReadOnlyList<string> columns) =>
        $"SELECT TOP {ExistingTupleLimit} {string.Join(", ", columns.Select(Q))} FROM {FullName(schema, table)};";

    public static string MaxSql(string schema, string table, string column, CounterKind kind)
    {
        var c = Q(column);
        var from = FullName(schema, table);
        return kind switch
        {
            CounterKind.Int => $"SELECT ISNULL(MAX(CONVERT(bigint, {c})), 0) AS v FROM {from};",
            CounterKind.Decimal => $"SELECT CONVERT(varchar(60), MAX({c})) AS v FROM {from};",
            _ => $"SELECT CONVERT(varchar(40), MAX({c}), 121) AS v FROM {from};",
        };
    }

    public static bool NeedsMax(CounterKind k) => k is CounterKind.Int or CounterKind.Date or CounterKind.DateTime or CounterKind.Decimal;

    public static bool IsSkipped(ColumnInfo c) => c.Identity || c.Computed || c.Type is "timestamp" or "rowversion";

    private static bool IsInt(string t) => t is "int" or "bigint" or "smallint" or "tinyint";
    private static bool IsChar(string t) => t is "nvarchar" or "varchar" or "nchar" or "char";
    private static bool IsNumeric(string t) => IsInt(t) || t is "bit" or "decimal" or "numeric" or "float" or "real" or "money" or "smallmoney";

    private static bool IsSupported(ColumnInfo c) => c.Type is
        "int" or "bigint" or "smallint" or "tinyint" or "bit" or "decimal" or "numeric" or "float" or "real" or "money" or "smallmoney"
        or "date" or "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" or "time" or "uniqueidentifier"
        or "nvarchar" or "varchar" or "nchar" or "char" or "ntext" or "text" or "varbinary" or "binary" or "xml";

    private static int CharLength(ColumnInfo c) =>
        c.MaxLength < 0 ? 200 : c.Type.StartsWith('n') ? c.MaxLength / 2 : c.MaxLength;

    private static CounterKind? CounterFor(ColumnInfo c) => c.Type switch
    {
        _ when IsInt(c.Type) => CounterKind.Int,
        "date" => CounterKind.Date,
        "datetime" or "datetime2" or "smalldatetime" or "datetimeoffset" => CounterKind.DateTime,
        "decimal" or "numeric" or "money" or "smallmoney" => CounterKind.Decimal,
        _ when IsChar(c.Type) && CharLength(c) >= SuffixLength => CounterKind.Text,
        "uniqueidentifier" => CounterKind.Guid,
        _ => null,
    };

    private static int CounterRank(CounterKind k) => k switch
    {
        CounterKind.Int => 0, CounterKind.Guid => 1, CounterKind.Date => 2, CounterKind.DateTime => 3,
        CounterKind.Decimal => 4, _ => 5,
    };

    /// <summary>Decides how every column is filled and how uniqueness is guaranteed. Returns an error if it cannot be.</summary>
    public static (InsertPlan? Plan, string? Error) Plan(string schema, string table, TableMeta meta)
    {
        var byName = meta.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var insertable = meta.Columns.Where(c => !IsSkipped(c)).ToList();

        var groups = new List<ForeignKeyInfo>();
        var fkColumns = new Dictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
        foreach (var fk in meta.ForeignKeys)
        {
            var used = false;
            for (var i = 0; i < fk.Columns.Count; i++)
            {
                if (!byName.TryGetValue(fk.Columns[i], out var col) || IsSkipped(col) || fkColumns.ContainsKey(col.Name)) continue;
                fkColumns[col.Name] = (groups.Count, i);
                used = true;
            }
            if (used) groups.Add(fk);
        }

        var counters = new Dictionary<string, CounterKind>(StringComparer.OrdinalIgnoreCase);
        var tupleSets = new List<IReadOnlyList<string>>();
        foreach (var set in meta.UniqueSets)
        {
            var cols = set.Where(byName.ContainsKey).Select(n => byName[n]).ToList();
            // Identity and computed values are unique by construction.
            if (cols.Count == 0 || cols.Any(c => c.Identity || c.Computed)) continue;
            if (cols.Any(c => counters.ContainsKey(c.Name))) continue;

            var candidate = cols.Where(c => !fkColumns.ContainsKey(c.Name))
                .Select(c => (Col: c, Kind: CounterFor(c))).Where(x => x.Kind is not null)
                .OrderBy(x => CounterRank(x.Kind!.Value)).FirstOrDefault();
            if (candidate.Col is not null) { counters[candidate.Col.Name] = candidate.Kind!.Value; continue; }

            if (cols.All(c => fkColumns.ContainsKey(c.Name))) { tupleSets.Add(cols.Select(c => c.Name).ToList()); continue; }

            return (null, $"Cannot guarantee unique values for the unique index on ({string.Join(", ", set)}) in {schema}.{table}: " +
                "it has no column of a supported type (integer, date, decimal, text of at least 7 characters, uniqueidentifier).");
        }

        foreach (var c in insertable)
        {
            if (fkColumns.ContainsKey(c.Name) || counters.ContainsKey(c.Name)) continue;
            if (!IsSupported(c) && !c.Nullable)
                return (null, $"Column '{c.Name}' has type '{c.Type}', which cannot be populated automatically.");
        }
        return (new InsertPlan(insertable, groups, fkColumns, counters, tupleSets), null);
    }

    private sealed class State
    {
        public readonly Dictionary<string, long> Seq = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, DateTime> Dates = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, decimal> Decs = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, decimal> Steps = new(StringComparer.OrdinalIgnoreCase);
    }

    private static string? InitCounters(InsertPlan plan, int count, IReadOnlyDictionary<string, string?> maxes, Random rnd, State st)
    {
        var inv = CultureInfo.InvariantCulture;
        foreach (var c in plan.Columns)
        {
            if (!plan.Counters.TryGetValue(c.Name, out var kind)) continue;
            var max = maxes.GetValueOrDefault(c.Name);
            if (string.Equals(max, "NULL", StringComparison.OrdinalIgnoreCase)) max = null;
            switch (kind)
            {
                case CounterKind.Int:
                {
                    var start = long.TryParse(max, NumberStyles.Integer, inv, out var m) ? m : 0;
                    var limit = c.Type switch { "tinyint" => 255L, "smallint" => 32767L, "int" => int.MaxValue, _ => long.MaxValue - count };
                    if (start + count > limit)
                        return $"Column '{c.Name}' must be unique but its {c.Type} range cannot fit {count} more values (current max {start}).";
                    st.Seq[c.Name] = start;
                    break;
                }
                case CounterKind.Date:
                    st.Dates[c.Name] = DateTime.TryParse(max, inv, DateTimeStyles.None, out var d) ? d.Date : DateTime.Today.AddDays(-count);
                    break;
                case CounterKind.DateTime:
                    st.Dates[c.Name] = DateTime.TryParse(max, inv, DateTimeStyles.None, out var dt) ? dt : DateTime.Today.AddYears(-1);
                    break;
                case CounterKind.Decimal:
                {
                    var money = c.Type is "money" or "smallmoney";
                    var scale = money ? 2 : c.Scale;
                    var step = scale == 0 ? 1m : 1m / (decimal)Math.Pow(10, scale);
                    var start = decimal.TryParse(max, NumberStyles.Float, inv, out var m) ? m : 0m;
                    var intDigits = c.Precision - c.Scale;
                    if (start + step * count >= (decimal)Math.Pow(10, Math.Min(intDigits, 27)))
                        return $"Column '{c.Name}' must be unique but its {c.Type}({c.Precision},{c.Scale}) range cannot fit {count} more values.";
                    st.Decs[c.Name] = start;
                    st.Steps[c.Name] = step;
                    break;
                }
                case CounterKind.Text:
                    st.Seq[c.Name] = rnd.NextInt64(0, 0x0FFFFFFFL - count);
                    break;
            }
        }
        return null;
    }

    /// <summary>Returns a script inserting <paramref name="count"/> random rows, or an error if that cannot be done safely.</summary>
    public static (string? Script, string? Error) BuildInsert(
        string schema, string table, InsertPlan plan, int count,
        IReadOnlyList<IReadOnlyList<string[]>> fkTuples, IReadOnlyList<HashSet<string>> existing,
        IReadOnlyDictionary<string, string?> maxes, Random rnd)
    {
        var name = FullName(schema, table);
        var cols = plan.Columns;
        var sb = new StringBuilder();
        sb.AppendLine("SET NOCOUNT ON;\nSET XACT_ABORT ON;\nBEGIN TRANSACTION;\nGO");

        if (cols.Count == 0)
        {
            for (var n = 0; n < count; n++)
            {
                sb.AppendLine($"INSERT INTO {name} DEFAULT VALUES;");
                if ((n + 1) % BatchSize == 0) sb.AppendLine("GO");
            }
            sb.AppendLine("GO\nCOMMIT TRANSACTION;\nGO");
            return (sb.ToString(), null);
        }

        var inTupleSet = plan.TupleSets.SelectMany(s => s).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var c in cols)
        {
            if (!plan.FkColumns.TryGetValue(c.Name, out var m) || fkTuples[m.Group].Count > 0) continue;
            if (!c.Nullable || inTupleSet.Contains(c.Name))
                return (null, $"Column '{c.Name}' references {plan.FkGroups[m.Group].RefTable}, which has no rows. Populate that table first.");
        }

        var st = new State();
        var error = InitCounters(plan, count, maxes, rnd, st);
        if (error is not null) return (null, error);

        var used = existing.Select(s => new HashSet<string>(s)).ToList();
        var colList = string.Join(", ", cols.Select(c => Q(c.Name)));
        var picks = new string[]?[plan.FkGroups.Count];

        for (var n = 0; n < count; n++)
        {
            var ok = false;
            for (var attempt = 0; attempt < TupleAttempts && !ok; attempt++)
            {
                for (var g = 0; g < picks.Length; g++)
                    picks[g] = fkTuples[g].Count == 0 ? null : fkTuples[g][rnd.Next(fkTuples[g].Count)];
                ok = true;
                var keys = new string[plan.TupleSets.Count];
                for (var s = 0; s < keys.Length; s++)
                {
                    keys[s] = string.Join('\u0001', plan.TupleSets[s].Select(cn => FkRaw(plan, picks, cn)));
                    if (used[s].Contains(keys[s])) { ok = false; break; }
                }
                if (ok) for (var s = 0; s < keys.Length; s++) used[s].Add(keys[s]);
            }
            if (!ok)
                return (null, $"Could not generate {count} distinct key combinations for a unique index made of foreign key columns. " +
                    "The referenced tables don't have enough distinct rows. Populate them further or reduce the number of rows.");

            if (n % BatchSize == 0)
            {
                if (n > 0) sb.AppendLine("GO");
                sb.AppendLine($"INSERT INTO {name} ({colList}) VALUES");
            }
            var values = cols.Select(c => Value(c, plan, picks, st, rnd)).ToList();
            var last = n == count - 1 || (n + 1) % BatchSize == 0;
            sb.AppendLine($"({string.Join(", ", values)}){(last ? ";" : ",")}");
        }
        sb.AppendLine("GO\nCOMMIT TRANSACTION;\nGO");
        return (sb.ToString(), null);
    }

    private static string? FkRaw(InsertPlan plan, string[]?[] picks, string column) =>
        plan.FkColumns.TryGetValue(column, out var m) ? picks[m.Group]?[m.Index] : null;

    private static string FkLiteral(string v, ColumnInfo c)
    {
        if (v.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return "NULL";
        if (IsNumeric(c.Type) && decimal.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return v;
        if (c.Type is "varbinary" or "binary" && v.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return v;
        return Lit(v);
    }

    private static string Value(ColumnInfo c, InsertPlan plan, string[]?[] picks, State st, Random rnd)
    {
        var inv = CultureInfo.InvariantCulture;
        if (plan.FkColumns.ContainsKey(c.Name))
        {
            var v = FkRaw(plan, picks, c.Name);
            return v is null ? "NULL" : FkLiteral(v, c);
        }

        var key = c.Name.ToLowerInvariant();
        if (plan.Counters.TryGetValue(c.Name, out var kind))
        {
            switch (kind)
            {
                case CounterKind.Int: return (st.Seq[c.Name] += 1).ToString(inv);
                case CounterKind.Date:
                    return "'" + (st.Dates[c.Name] = st.Dates[c.Name].AddDays(1)).ToString("yyyy-MM-dd", inv) + "'";
                case CounterKind.DateTime:
                {
                    var d = st.Dates[c.Name] = st.Dates[c.Name].AddMinutes(1);
                    return "'" + d.ToString("yyyy-MM-dd HH:mm:ss", inv) + (c.Type == "datetimeoffset" ? " +00:00'" : "'");
                }
                case CounterKind.Decimal:
                {
                    var d = st.Decs[c.Name] += st.Steps[c.Name];
                    return d.ToString(inv);
                }
                case CounterKind.Guid: return "'" + Guid.NewGuid() + "'";
                case CounterKind.Text:
                {
                    var suffix = (st.Seq[c.Name] += 1).ToString("x" + SuffixLength, inv);
                    var s = RandomData.Text(key, CharLength(c), suffix, rnd);
                    return (c.Type.StartsWith('n') ? "N'" : "'") + s.Replace("'", "''") + "'";
                }
            }
        }

        if (!IsSupported(c)) return "NULL";
        switch (c.Type)
        {
            case "int" or "bigint" or "smallint" or "tinyint":
                return RandomData.Integer(key, c.Type, rnd).ToString(inv);
            case "bit": return rnd.Next(2).ToString(inv);
            case "decimal" or "numeric":
                return RandomData.Decimal(key, c.Precision, c.Scale, rnd).ToString(inv);
            case "money" or "smallmoney":
                return Math.Round(rnd.NextDouble() * 5000 + 1, 2).ToString("0.00", inv);
            case "float" or "real":
                return Math.Round(rnd.NextDouble() * 1000, 3).ToString("0.###", inv);
            case "date": return "'" + RandomData.Date(key, rnd).ToString("yyyy-MM-dd", inv) + "'";
            case "datetime" or "datetime2" or "smalldatetime":
                return "'" + RandomData.Date(key, rnd).AddSeconds(rnd.Next(86400)).ToString("yyyy-MM-dd HH:mm:ss", inv) + "'";
            case "datetimeoffset":
                return "'" + RandomData.Date(key, rnd).AddSeconds(rnd.Next(86400)).ToString("yyyy-MM-dd HH:mm:ss", inv) + " +00:00'";
            case "time": return "'" + TimeSpan.FromSeconds(rnd.Next(86400)).ToString(@"hh\:mm\:ss", inv) + "'";
            case "uniqueidentifier": return "'" + Guid.NewGuid() + "'";
            case "varbinary" or "binary":
            {
                var len = Math.Clamp(c.MaxLength <= 0 ? 16 : c.MaxLength, 1, 16);
                var bytes = new byte[len];
                rnd.NextBytes(bytes);
                return "0x" + Convert.ToHexString(bytes);
            }
            case "xml": return "N'<item>" + RandomData.Words(rnd, 2) + "</item>'";
            default:
            {
                var chars = c.Type is "text" or "ntext" ? 200 : CharLength(c);
                var s = RandomData.Text(key, Math.Max(1, chars), null, rnd);
                return (c.Type.StartsWith('n') ? "N'" : "'") + s.Replace("'", "''") + "'";
            }
        }
    }
}

/// <summary>Random but plausible ASCII values, chosen from the column name.</summary>
public static class RandomData
{
    private static readonly string[] First = ["Anna", "Erik", "Maria", "Johan", "Emma", "Lars", "Sofia", "Karl", "Olivia", "Nils", "Elin", "Oskar", "Sara", "David", "Linda", "Peter", "Julia", "Daniel", "Hanna", "Alex"];
    private static readonly string[] Last = ["Andersson", "Johansson", "Karlsson", "Nilsson", "Eriksson", "Larsson", "Olsson", "Persson", "Svensson", "Gustafsson", "Smith", "Brown", "Miller", "Taylor", "Walker"];
    private static readonly string[] Cities = ["Stockholm", "Gothenburg", "Malmo", "Uppsala", "Oslo", "Copenhagen", "Helsinki", "London", "Berlin", "Paris", "Madrid", "Boston", "Seattle"];
    private static readonly string[] Countries = ["Sweden", "Norway", "Denmark", "Finland", "Germany", "France", "Spain", "United Kingdom", "United States", "Canada"];
    private static readonly string[] Streets = ["Main Street", "Park Avenue", "Oak Road", "Maple Lane", "Station Road", "Church Street", "River Way", "Hill Street"];
    private static readonly string[] Companies = ["Acme", "Globex", "Initech", "Umbrella", "Hooli", "Stark", "Wayne", "Soylent", "Vandelay", "Wonka"];
    private static readonly string[] CompanySuffix = ["AB", "Inc", "Ltd", "Group", "Systems", "Solutions"];
    private static readonly string[] Products = ["Widget", "Gadget", "Sprocket", "Gizmo", "Bracket", "Adapter", "Sensor", "Cable", "Filter", "Valve"];
    private static readonly string[] Adjectives = ["Premium", "Compact", "Classic", "Deluxe", "Basic", "Smart", "Rugged", "Portable"];
    private static readonly string[] Colors = ["Red", "Blue", "Green", "Black", "White", "Yellow", "Orange", "Purple", "Gray"];
    private static readonly string[] Statuses = ["Active", "Inactive", "Pending", "Completed", "Cancelled"];
    private static readonly string[] Lorem = ["lorem", "ipsum", "dolor", "sit", "amet", "consectetur", "adipiscing", "elit", "sed", "do", "eiusmod", "tempor", "incididunt", "ut", "labore", "et", "dolore", "magna", "aliqua", "enim", "minim", "veniam", "quis", "nostrud"];

    private static string Pick(string[] a, Random r) => a[r.Next(a.Length)];
    private static bool Has(string k, params string[] parts) => parts.Any(k.Contains);

    public static string Words(Random r, int n) => string.Join(' ', Enumerable.Range(0, n).Select(_ => Pick(Lorem, r)));

    private static string Sentence(Random r, int words)
    {
        var s = Words(r, words);
        return char.ToUpperInvariant(s[0]) + s[1..] + ".";
    }

    /// <summary>A value for a text column. A non-null <paramref name="suffix"/> makes it unique and is never truncated.</summary>
    public static string Text(string key, int maxLen, string? suffix, Random r)
    {
        string s;
        if (suffix is null) s = Raw(key, r);
        else if (Has(key, "email", "e_mail"))
        {
            s = $"{Pick(First, r).ToLowerInvariant()}{suffix}@example.com";
            if (s.Length > maxLen) s = suffix;
        }
        else
        {
            var raw = Raw(key, r);
            s = raw.Length + suffix.Length + 1 <= maxLen ? $"{raw} {suffix}" : suffix;
        }
        return s.Length <= maxLen ? s : s[..maxLen].TrimEnd();
    }

    private static string Raw(string k, Random r)
    {
        if (Has(k, "email", "e_mail")) return $"{Pick(First, r)}.{Pick(Last, r)}@example.com".ToLowerInvariant();
        if (Has(k, "firstname", "first_name", "givenname", "fname")) return Pick(First, r);
        if (Has(k, "lastname", "last_name", "surname", "familyname", "lname")) return Pick(Last, r);
        if (Has(k, "username", "login", "nick")) return (Pick(First, r) + Pick(Last, r)).ToLowerInvariant();
        if (Has(k, "company", "organization", "organisation", "employer")) return $"{Pick(Companies, r)} {Pick(CompanySuffix, r)}";
        if (Has(k, "phone", "mobile", "tel", "fax")) return $"+46 70 {r.Next(100, 1000)} {r.Next(10, 100)} {r.Next(10, 100)}";
        if (Has(k, "city", "town")) return Pick(Cities, r);
        if (Has(k, "country")) return Pick(Countries, r);
        if (Has(k, "street", "address")) return $"{r.Next(1, 200)} {Pick(Streets, r)}";
        if (Has(k, "zip", "postal", "postcode")) return r.Next(10000, 99999).ToString(CultureInfo.InvariantCulture);
        if (Has(k, "url", "website", "homepage", "link")) return $"https://www.{Pick(Companies, r).ToLowerInvariant()}.example.com/{Pick(Lorem, r)}";
        if (Has(k, "color", "colour")) return Pick(Colors, r);
        if (Has(k, "status", "state")) return Pick(Statuses, r);
        if (Has(k, "product", "item", "article")) return $"{Pick(Adjectives, r)} {Pick(Products, r)}";
        if (Has(k, "title", "subject", "headline", "summary")) return Sentence(r, r.Next(3, 7)).TrimEnd('.');
        if (Has(k, "description", "comment", "note", "body", "text", "content", "message", "remark", "bio"))
            return string.Join(' ', Enumerable.Range(0, r.Next(1, 4)).Select(_ => Sentence(r, r.Next(5, 12))));
        if (Has(k, "name")) return $"{Pick(First, r)} {Pick(Last, r)}";
        return Words(r, r.Next(1, 4));
    }

    public static long Integer(string k, string type, Random r)
    {
        var (lo, hi) = type switch { "tinyint" => (0, 255), "smallint" => (1, 32767), _ => (1, 10000) };
        if (Has(k, "age")) (lo, hi) = (18, Math.Min(hi, 90));
        else if (Has(k, "year")) (lo, hi) = (Math.Min(1950, hi), Math.Min(2025, hi));
        else if (Has(k, "quantity", "qty", "count", "stock", "amount")) (lo, hi) = (1, Math.Min(hi, 100));
        else if (Has(k, "rating", "score", "level")) (lo, hi) = (1, Math.Min(hi, 5));
        return r.NextInt64(lo, (long)hi + 1);
    }

    public static decimal Decimal(string k, int precision, int scale, Random r)
    {
        var intDigits = Math.Max(0, precision - scale);
        var max = intDigits == 0 ? 0.99m : (decimal)Math.Min(Math.Pow(10, intDigits) - 1, Has(k, "price", "cost", "amount", "total", "salary", "balance") ? 5000 : 1000);
        return Math.Round((decimal)r.NextDouble() * max, scale, MidpointRounding.ToZero);
    }

    public static DateTime Date(string k, Random r)
    {
        var today = DateTime.Today;
        DateTime from, to;
        if (Has(k, "birth", "dob")) (from, to) = (new DateTime(1950, 1, 1), new DateTime(2005, 12, 31));
        else if (Has(k, "due", "expire", "future", "end", "delivery")) (from, to) = (today, today.AddYears(1));
        else (from, to) = (today.AddYears(-5), today);
        return from.AddDays(r.Next((int)(to - from).TotalDays + 1));
    }
}
