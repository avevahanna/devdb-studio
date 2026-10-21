using System.Text.RegularExpressions;

namespace DevDbStudio.Services.Dbms;

/// <summary>
/// Rewrites psql's aligned output into the tab-separated layout that <see cref="SqlcmdOutputParser"/> understands,
/// so the shared result grid works for every DBMS. Everything that is not a result table is passed through.
/// </summary>
public static partial class PsqlOutputConverter
{
    [GeneratedRegex(@"^-+(\+-+)*$")]
    private static partial Regex DashLine();

    [GeneratedRegex(@"^\((\d+) rows?\)$")]
    private static partial Regex RowCount();

    public static string ToSqlcmdStyle(string output)
    {
        if (string.IsNullOrEmpty(output)) return output;
        var lines = output.Replace("\r\n", "\n").Split('\n');
        var res = new List<string>();
        var i = 0;
        while (i < lines.Length)
        {
            if (i + 1 < lines.Length && lines[i].Length > 0 && DashLine().IsMatch(lines[i + 1]))
            {
                var segs = lines[i + 1].Split('+');
                var header = Cells(lines[i], segs, isHeader: true);
                if (header.Length == segs.Length)
                {
                    res.Add(string.Join('\t', header));
                    res.Add(string.Join('\t', segs.Select(s => new string('-', Math.Max(1, s.Length - 2)))));
                    i += 2;
                    while (i < lines.Length && !RowCount().IsMatch(lines[i]) && lines[i].Length > 0)
                    {
                        res.Add(string.Join('\t', Cells(lines[i], segs, isHeader: false).Select(Flatten)));
                        i++;
                    }
                    res.Add("");
                    if (i < lines.Length && RowCount().Match(lines[i]) is { Success: true } m)
                    {
                        var n = m.Groups[1].Value;
                        res.Add($"({n} row{(n == "1" ? "" : "s")} affected)");
                        i++;
                    }
                    continue;
                }
            }
            res.Add(lines[i]);
            i++;
        }
        return string.Join('\n', res);
    }

    private static string Flatten(string s) => s.Replace('\t', ' ');

    private static string[] Cells(string line, string[] segs, bool isHeader)
    {
        // The runner trims the output, so the very first header line may have lost its leading space and cannot be sliced by position.
        if (isHeader && segs.Length > 1)
        {
            var parts = line.Split('|');
            if (parts.Length == segs.Length) return parts.Select(p => p.Trim()).ToArray();
        }
        if (segs.Length == 1) return [line.Trim()];

        var cells = new string[segs.Length];
        var pos = 0;
        for (var k = 0; k < segs.Length; k++)
        {
            var width = segs[k].Length;
            var start = Math.Min(pos, line.Length);
            var len = Math.Min(width, line.Length - start);
            cells[k] = line.Substring(start, len).Trim();
            pos += width + 1;
        }
        return cells;
    }
}
