using System.Text.RegularExpressions;

namespace DevDbStudio.Services.Dbms;

/// <summary>
/// Rewrites the <c>mariadb --table</c> / <c>mysql --table</c> box output into the tab-separated layout that
/// <see cref="SqlcmdOutputParser"/> understands. Everything that is not a result table is passed through.
/// </summary>
public static partial class MySqlOutputConverter
{
    [GeneratedRegex(@"^\+(-+\+)+$")]
    private static partial Regex Border();

    public static string ToSqlcmdStyle(string output)
    {
        if (string.IsNullOrEmpty(output)) return output;
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var res = new List<string>();
        var i = 0;
        while (i < lines.Length)
        {
            if (!Border().IsMatch(lines[i]))
            {
                res.Add(lines[i]);
                i++;
                continue;
            }

            // +----+-----+ / | a | b | / +----+-----+ / rows / +----+-----+
            var segs = lines[i].Trim('+').Split('+');
            i++;
            var header = ReadRow(lines, ref i, segs);
            if (header is null) break;
            res.Add(string.Join('\t', header));
            res.Add(string.Join('\t', segs.Select(s => new string('-', Math.Max(1, s.Length - 2)))));
            if (i < lines.Length && Border().IsMatch(lines[i])) i++;

            var count = 0;
            while (i < lines.Length && !Border().IsMatch(lines[i]) && lines[i].StartsWith('|'))
            {
                var row = ReadRow(lines, ref i, segs);
                if (row is null) break;
                res.Add(string.Join('\t', row));
                count++;
            }
            if (i < lines.Length && Border().IsMatch(lines[i])) i++;
            res.Add("");
            res.Add($"({count} row{(count == 1 ? "" : "s")} affected)");
        }
        return string.Join('\n', res);
    }

    /// <summary>Reads one table row; a value that contains a line break spans several physical lines.</summary>
    private static string[]? ReadRow(string[] lines, ref int i, string[] segs)
    {
        if (i >= lines.Length) return null;
        var line = lines[i++];
        while (!line.TrimEnd().EndsWith('|') && i < lines.Length && !Border().IsMatch(lines[i]))
            line += " " + lines[i++];

        var cells = new string[segs.Length];
        var pos = 1;
        for (var k = 0; k < segs.Length; k++)
        {
            var start = Math.Min(pos, line.Length);
            var len = Math.Min(segs[k].Length, line.Length - start);
            cells[k] = line.Substring(start, len).Trim().Replace('\t', ' ');
            pos += segs[k].Length + 1;
        }
        return cells;
    }
}
