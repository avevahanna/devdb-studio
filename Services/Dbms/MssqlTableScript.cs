using System.Text;

namespace DevDbStudio.Services.Dbms;

public sealed class ColumnDef
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "int";
    public int? Length { get; set; }
    public int? Precision { get; set; } = 18;
    public int? Scale { get; set; } = 2;
    public bool Nullable { get; set; } = true;
    public bool PrimaryKey { get; set; }
    public bool Identity { get; set; }
    public string Default { get; set; } = "";
}

public sealed record TableDef(string Schema, string Name, IReadOnlyList<ColumnDef> Columns);

/// <summary>Builds and validates T-SQL CREATE TABLE scripts for the visual table designer.</summary>
public static class MssqlTableScript
{
    public static readonly IReadOnlyList<string> Types =
    [
        "int", "bigint", "smallint", "tinyint", "bit",
        "decimal", "float", "money",
        "date", "datetime2", "datetimeoffset", "time",
        "uniqueidentifier",
        "nvarchar", "varchar", "nchar", "char", "varbinary",
    ];

    public static bool UsesLength(string t) => t is "nvarchar" or "varchar" or "nchar" or "char" or "varbinary";
    public static bool UsesPrecision(string t) => t is "decimal";
    public static bool CanIdentity(string t) => t is "int" or "bigint" or "smallint" or "tinyint" or "decimal";

    public static string Quote(string id) => "[" + id.Replace("]", "]]") + "]";

    public static string TypeSql(ColumnDef c)
    {
        var t = c.Type.ToLowerInvariant();
        if (UsesLength(t)) return c.Length is null or <= 0 ? $"{t}(MAX)" : $"{t}({c.Length})";
        if (UsesPrecision(t)) return $"{t}({c.Precision ?? 18},{c.Scale ?? 0})";
        return t;
    }

    public static IReadOnlyList<string> Validate(TableDef t)
    {
        var e = new List<string>();
        if (string.IsNullOrWhiteSpace(t.Schema)) e.Add("Schema is required.");
        if (string.IsNullOrWhiteSpace(t.Name)) e.Add("Table name is required.");
        if (t.Name.Length > 128) e.Add("Table name is too long (max 128).");
        if (t.Columns.Count == 0) e.Add("Add at least one column.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var n = 0;
        foreach (var c in t.Columns)
        {
            n++;
            var label = string.IsNullOrWhiteSpace(c.Name) ? $"Column {n}" : $"Column '{c.Name}'";
            if (string.IsNullOrWhiteSpace(c.Name)) { e.Add($"{label}: name is required."); continue; }
            if (c.Name.Length > 128) e.Add($"{label}: name is too long (max 128).");
            if (!seen.Add(c.Name.Trim())) e.Add($"{label}: duplicate column name.");
            if (!Types.Contains(c.Type)) e.Add($"{label}: unsupported type.");
            if (UsesLength(c.Type) && c.Length is not null && (c.Length <= 0 || c.Length > 8000))
                e.Add($"{label}: length must be 1-8000 (leave empty for MAX).");
            if (UsesPrecision(c.Type))
            {
                if (c.Precision is null or < 1 or > 38) e.Add($"{label}: precision must be 1-38.");
                else if (c.Scale is null or < 0 || c.Scale > c.Precision) e.Add($"{label}: scale must be between 0 and the precision.");
            }
            if (c.Identity && !CanIdentity(c.Type)) e.Add($"{label}: identity requires an integer type.");
            if (c.Identity && c.Type == "decimal" && c.Scale != 0) e.Add($"{label}: identity decimal needs scale 0.");
            if (c.PrimaryKey && c.Nullable) e.Add($"{label}: primary key columns cannot be nullable.");
            if (c.Identity && c.Nullable) e.Add($"{label}: identity columns cannot be nullable.");
            if (c.Identity && !string.IsNullOrWhiteSpace(c.Default)) e.Add($"{label}: identity columns cannot have a default.");
            if (c.PrimaryKey && UsesLength(c.Type) && c.Length is null)
                e.Add($"{label}: MAX-length columns cannot be part of a primary key.");
        }
        if (t.Columns.Count(c => c.Identity) > 1) e.Add("Only one identity column is allowed per table.");
        return e;
    }

    public static string Build(TableDef t, string? useDatabase = null)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(useDatabase))
            sb.AppendLine($"USE {Quote(useDatabase)};\nGO");

        sb.AppendLine($"CREATE TABLE {Quote(t.Schema)}.{Quote(t.Name)} (");
        var lines = new List<string>();
        foreach (var c in t.Columns)
        {
            var l = $"    {Quote(c.Name.Trim())} {TypeSql(c).ToUpperInvariant()}";
            if (c.Identity) l += " IDENTITY(1,1)";
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
