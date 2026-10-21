using System.Text;

namespace DbServeApp.Services.Dbms;

/// <summary>DBMS-specific rules and SQL generation for the table designer and table listing.</summary>
public abstract class TableDialect
{
    public static readonly TableDialect Mssql = new MssqlDialect();
    public static readonly TableDialect Postgres = new PostgresDialect();

    public static readonly TableDialect MariaDb = new MariaDbDialect();

    public static TableDialect For(string? dbmsKey) =>
        string.Equals(dbmsKey, PostgresProvider.ProviderKey, StringComparison.OrdinalIgnoreCase) ? Postgres
        : string.Equals(dbmsKey, MariaDbProvider.ProviderKey, StringComparison.OrdinalIgnoreCase)
          || string.Equals(dbmsKey, MySqlProvider.ProviderKey, StringComparison.OrdinalIgnoreCase) ? MariaDb
        : Mssql;

    /// <summary>False when a schema is the same thing as a database (MariaDB/MySQL), so no separate schema is entered.</summary>
    public virtual bool HasSchemas => true;
    public virtual string LengthHint => "MAX";

    public abstract string Name { get; }
    public abstract string DefaultSchema { get; }
    public abstract string DefaultDatabase { get; }
    public abstract IReadOnlyList<string> Types { get; }
    public abstract string IntType { get; }
    public abstract string TextType { get; }
    public abstract int MaxLength { get; }
    public abstract string DefaultHint { get; }
    public abstract string ListTablesSql { get; }

    public abstract bool UsesLength(string type);
    public abstract bool UsesPrecision(string type);
    public abstract bool CanIdentity(string type);
    public abstract string Quote(string id);
    public abstract IReadOnlyList<string> Validate(TableDef t);
    public abstract string Build(TableDef t, string? useDatabase = null);
    /// <summary>Statement(s) that switch the session to another database, including a trailing newline.</summary>
    public abstract string Use(string? database);

    private sealed class MssqlDialect : TableDialect
    {
        public override string Name => "T-SQL";
        public override string DefaultSchema => "dbo";
        public override string DefaultDatabase => "master";
        public override IReadOnlyList<string> Types => MssqlTableScript.Types;
        public override string IntType => "int";
        public override string TextType => "nvarchar";
        public override int MaxLength => 8000;
        public override string DefaultHint => "e.g. 0 or GETDATE()";
        public override string ListTablesSql => "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM INFORMATION_SCHEMA.TABLES ORDER BY 1, 2;";
        public override bool UsesLength(string t) => MssqlTableScript.UsesLength(t);
        public override bool UsesPrecision(string t) => MssqlTableScript.UsesPrecision(t);
        public override bool CanIdentity(string t) => MssqlTableScript.CanIdentity(t);
        public override string Quote(string id) => MssqlTableScript.Quote(id);
        public override IReadOnlyList<string> Validate(TableDef t) => MssqlTableScript.Validate(t);
        public override string Build(TableDef t, string? useDatabase = null) => MssqlTableScript.Build(t, useDatabase);
        public override string Use(string? database) => MssqlDataScript.Use(database);
    }

    private sealed class PostgresDialect : TableDialect
    {
        private static readonly string[] AllTypes =
        [
            "smallint", "integer", "bigint", "boolean",
            "numeric", "real", "double precision", "money",
            "date", "timestamp", "timestamptz", "time",
            "uuid", "varchar", "char", "text", "bytea", "jsonb",
        ];

        public override string Name => "PostgreSQL";
        public override string DefaultSchema => "public";
        public override string DefaultDatabase => "postgres";
        public override IReadOnlyList<string> Types => AllTypes;
        public override string IntType => "integer";
        public override string TextType => "varchar";
        public override int MaxLength => 10485760;
        public override string DefaultHint => "e.g. 0 or now()";
        public override string LengthHint => "none";
        public override string ListTablesSql =>
            "SELECT table_schema, table_name, table_type FROM information_schema.tables WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY 1, 2;";
        public override bool UsesLength(string t) => t is "varchar" or "char";
        public override bool UsesPrecision(string t) => t is "numeric";
        public override bool CanIdentity(string t) => t is "smallint" or "integer" or "bigint";
        public override string Quote(string id) => "\"" + id.Replace("\"", "\"\"") + "\"";
        public override string Use(string? database) =>
            string.IsNullOrWhiteSpace(database) ? "" : $"\\set QUIET on\n\\connect {Quote(database.Trim())}\n\\set QUIET off\n";

        private string TypeSql(ColumnDef c)
        {
            var t = c.Type.ToLowerInvariant();
            if (UsesLength(t)) return c.Length is null or <= 0 ? t : $"{t}({c.Length})";
            if (UsesPrecision(t)) return $"{t}({c.Precision ?? 18},{c.Scale ?? 0})";
            return t;
        }

        public override IReadOnlyList<string> Validate(TableDef t)
        {
            var e = new List<string>();
            if (string.IsNullOrWhiteSpace(t.Schema)) e.Add("Schema is required.");
            if (string.IsNullOrWhiteSpace(t.Name)) e.Add("Table name is required.");
            if (t.Name.Length > 63) e.Add("Table name is too long (max 63).");
            if (t.Columns.Count == 0) e.Add("Add at least one column.");

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var n = 0;
            foreach (var c in t.Columns)
            {
                n++;
                var label = string.IsNullOrWhiteSpace(c.Name) ? $"Column {n}" : $"Column '{c.Name}'";
                if (string.IsNullOrWhiteSpace(c.Name)) { e.Add($"{label}: name is required."); continue; }
                if (c.Name.Length > 63) e.Add($"{label}: name is too long (max 63).");
                if (!seen.Add(c.Name.Trim())) e.Add($"{label}: duplicate column name.");
                if (!AllTypes.Contains(c.Type)) e.Add($"{label}: unsupported type.");
                if (UsesLength(c.Type) && c.Length is not null && (c.Length <= 0 || c.Length > MaxLength))
                    e.Add($"{label}: length must be 1-{MaxLength} (leave empty for no limit).");
                if (UsesPrecision(c.Type))
                {
                    if (c.Precision is null or < 1 or > 38) e.Add($"{label}: precision must be 1-38.");
                    else if (c.Scale is null or < 0 || c.Scale > c.Precision) e.Add($"{label}: scale must be between 0 and the precision.");
                }
                if (c.Identity && !CanIdentity(c.Type)) e.Add($"{label}: identity requires an integer type.");
                if (c.PrimaryKey && c.Nullable) e.Add($"{label}: primary key columns cannot be nullable.");
                if (c.Identity && c.Nullable) e.Add($"{label}: identity columns cannot be nullable.");
                if (c.Identity && !string.IsNullOrWhiteSpace(c.Default)) e.Add($"{label}: identity columns cannot have a default.");
            }
            if (t.Columns.Count(c => c.Identity) > 1) e.Add("Only one identity column is allowed per table.");
            return e;
        }

        public override string Build(TableDef t, string? useDatabase = null)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(useDatabase)) sb.Append(Use(useDatabase));

            sb.AppendLine($"CREATE TABLE {Quote(t.Schema)}.{Quote(t.Name)} (");
            var lines = new List<string>();
            foreach (var c in t.Columns)
            {
                var l = $"    {Quote(c.Name.Trim())} {TypeSql(c)}";
                if (c.Identity) l += " GENERATED BY DEFAULT AS IDENTITY";
                l += c.Nullable && !c.PrimaryKey ? " NULL" : " NOT NULL";
                if (!string.IsNullOrWhiteSpace(c.Default)) l += $" DEFAULT ({c.Default.Trim()})";
                lines.Add(l);
            }
            var pk = t.Columns.Where(c => c.PrimaryKey).Select(c => Quote(c.Name.Trim())).ToList();
            if (pk.Count > 0)
                lines.Add($"    CONSTRAINT {Quote("PK_" + t.Name)} PRIMARY KEY ({string.Join(", ", pk)})");
            sb.AppendLine(string.Join(",\n", lines));
            sb.Append(");");
            return sb.ToString();
        }
    }

    private sealed class MariaDbDialect : TableDialect
    {
        private static readonly string[] AllTypes =
        [
            "tinyint", "smallint", "int", "bigint", "boolean",
            "decimal", "float", "double",
            "date", "datetime", "timestamp", "time",
            "varchar", "char", "text", "longtext", "blob", "json",
        ];

        public override string Name => "MariaDB SQL";
        public override string DefaultSchema => "";
        public override string DefaultDatabase => "appdb";
        public override bool HasSchemas => false;
        public override IReadOnlyList<string> Types => AllTypes;
        public override string IntType => "int";
        public override string TextType => "varchar";
        // Row size limit is 65,535 bytes; utf8mb4 uses up to 4 per character.
        public override int MaxLength => 16383;
        public override string DefaultHint => "e.g. 0 or now()";
        public override string LengthHint => "255";
        public override string ListTablesSql =>
            "SELECT TABLE_SCHEMA, TABLE_NAME, TABLE_TYPE FROM information_schema.TABLES WHERE TABLE_SCHEMA = DATABASE() ORDER BY 1, 2;";
        public override bool UsesLength(string t) => t is "varchar" or "char";
        public override bool UsesPrecision(string t) => t is "decimal";
        public override bool CanIdentity(string t) => t is "tinyint" or "smallint" or "int" or "bigint";
        public override string Quote(string id) => "`" + id.Replace("`", "``") + "`";
        public override string Use(string? database) =>
            string.IsNullOrWhiteSpace(database) ? "" : $"USE {Quote(database.Trim())};\n";

        private string TypeSql(ColumnDef c)
        {
            var t = c.Type.ToLowerInvariant();
            if (t == "varchar") return $"varchar({(c.Length is null or <= 0 ? 255 : c.Length)})";
            if (t == "char") return c.Length is null or <= 0 ? "char(1)" : $"char({c.Length})";
            if (UsesPrecision(t)) return $"decimal({c.Precision ?? 18},{c.Scale ?? 0})";
            return t;
        }

        public override IReadOnlyList<string> Validate(TableDef t)
        {
            var e = new List<string>();
            // The schema of a MariaDB table is the database it lives in.
            if (string.IsNullOrWhiteSpace(t.Schema)) e.Add("Database is required.");
            if (string.IsNullOrWhiteSpace(t.Name)) e.Add("Table name is required.");
            if (t.Name.Length > 64) e.Add("Table name is too long (max 64).");
            if (t.Columns.Count == 0) e.Add("Add at least one column.");

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var n = 0;
            foreach (var c in t.Columns)
            {
                n++;
                var label = string.IsNullOrWhiteSpace(c.Name) ? $"Column {n}" : $"Column '{c.Name}'";
                if (string.IsNullOrWhiteSpace(c.Name)) { e.Add($"{label}: name is required."); continue; }
                if (c.Name.Length > 64) e.Add($"{label}: name is too long (max 64).");
                if (!seen.Add(c.Name.Trim())) e.Add($"{label}: duplicate column name.");
                if (!AllTypes.Contains(c.Type)) e.Add($"{label}: unsupported type.");
                if (UsesLength(c.Type) && c.Length is not null && (c.Length <= 0 || c.Length > MaxLength))
                    e.Add($"{label}: length must be 1-{MaxLength}.");
                if (UsesPrecision(c.Type))
                {
                    if (c.Precision is null or < 1 or > 65) e.Add($"{label}: precision must be 1-65.");
                    else if (c.Scale is null or < 0 || c.Scale > c.Precision || c.Scale > 30) e.Add($"{label}: scale must be between 0 and the precision (max 30).");
                }
                if (c.Identity && !CanIdentity(c.Type)) e.Add($"{label}: auto increment requires an integer type.");
                if (c.Identity && !c.PrimaryKey) e.Add($"{label}: auto increment columns must be part of the primary key.");
                if (c.PrimaryKey && c.Nullable) e.Add($"{label}: primary key columns cannot be nullable.");
                if (c.Identity && c.Nullable) e.Add($"{label}: auto increment columns cannot be nullable.");
                if (c.Identity && !string.IsNullOrWhiteSpace(c.Default)) e.Add($"{label}: auto increment columns cannot have a default.");
                if (c.PrimaryKey && c.Type is "text" or "longtext" or "blob" or "json") e.Add($"{label}: this type cannot be a primary key.");
            }
            if (t.Columns.Count(c => c.Identity) > 1) e.Add("Only one auto increment column is allowed per table.");
            return e;
        }

        public override string Build(TableDef t, string? useDatabase = null)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(useDatabase)) sb.Append(Use(useDatabase));

            sb.AppendLine($"CREATE TABLE {Quote(t.Schema)}.{Quote(t.Name)} (");
            var lines = new List<string>();
            foreach (var c in t.Columns)
            {
                var l = $"    {Quote(c.Name.Trim())} {TypeSql(c)}";
                l += c.Nullable && !c.PrimaryKey ? " NULL" : " NOT NULL";
                if (c.Identity) l += " AUTO_INCREMENT";
                if (!string.IsNullOrWhiteSpace(c.Default)) l += $" DEFAULT ({c.Default.Trim()})";
                lines.Add(l);
            }
            var pk = t.Columns.Where(c => c.PrimaryKey).Select(c => Quote(c.Name.Trim())).ToList();
            if (pk.Count > 0) lines.Add($"    PRIMARY KEY ({string.Join(", ", pk)})");
            sb.AppendLine(string.Join(",\n", lines));
            sb.Append(");");
            return sb.ToString();
        }
    }
}
