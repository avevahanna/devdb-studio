using System.Text.RegularExpressions;

namespace DevDbStudio.Services.Dbms;

public sealed record ResultTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, string? Footer);

/// <summary>A block of sqlcmd output: either a parsed table or free text (messages, PRINT output, errors).</summary>
public sealed record ResultBlock(ResultTable? Table, string? Text);

/// <summary>Parses sqlcmd output produced with <c>-W -s "\t"</c> into tables. Unparseable parts stay as text.</summary>
public static partial class SqlcmdOutputParser
{
    [GeneratedRegex(@"^-+(\t-+)*$")]
    private static partial Regex DashLine();

    [GeneratedRegex(@"^\(\d+ rows? affected\)$")]
    private static partial Regex Affected();

    public static IReadOnlyList<ResultBlock> Parse(string output)
    {
        var lines = output.Replace("\r\n", "\n").Split('\n');
        // The runner trims output, which drops the empty header line of an unnamed single column.
        if (lines.Length > 0 && DashLine().IsMatch(lines[0]) && !lines[0].Contains('\t'))
            lines = ["", .. lines];
        var blocks = new List<ResultBlock>();
        var text = new List<string>();

        void FlushText()
        {
            var t = string.Join("\n", text).Trim('\n');
            if (t.Length > 0) blocks.Add(new ResultBlock(null, t));
            text.Clear();
        }

        var i = 0;
        while (i < lines.Length)
        {
            // An unnamed single column (SELECT 1, COUNT(*)) has an empty header line, so only require non-empty for multi-column sets.
            if (i + 1 < lines.Length && DashLine().IsMatch(lines[i + 1])
                && lines[i].Split('\t').Length == lines[i + 1].Split('\t').Length
                && (lines[i].Length > 0 || !lines[i + 1].Contains('\t')))
            {
                FlushText();
                var cols = lines[i].Split('\t');
                var rows = new List<IReadOnlyList<string>>();
                i += 2;
                while (i < lines.Length && lines[i].Length > 0 && !Affected().IsMatch(lines[i]))
                {
                    var cells = lines[i].Split('\t');
                    // Pad/truncate defensively so a stray tab or newline in a value cannot break the grid.
                    rows.Add(cells.Length == cols.Length ? cells : Normalize(cells, cols.Length));
                    i++;
                }
                string? footer = null;
                // sqlcmd -W puts a blank line between the rows and the "(N rows affected)" footer.
                var f = i < lines.Length && lines[i].Length == 0 ? i + 1 : i;
                if (f < lines.Length && Affected().IsMatch(lines[f])) { footer = lines[f]; i = f + 1; }
                blocks.Add(new ResultBlock(new ResultTable(cols, rows, footer), null));
                continue;
            }
            text.Add(lines[i]);
            i++;
        }
        FlushText();
        return blocks;
    }

    private static string[] Normalize(string[] cells, int n)
    {
        var r = new string[n];
        for (var k = 0; k < n; k++) r[k] = k < cells.Length ? cells[k] : "";
        return r;
    }
}
