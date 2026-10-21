using DbServeApp.Services.Dbms;

namespace DbServeApp.Tests;

public class SqlcmdOutputParserTests
{
    [Fact]
    public void ParsesTableWithFooter()
    {
        var blocks = SqlcmdOutputParser.Parse("id\tname\n--\t----\n1\talice\n2\tbob\n\n(2 rows affected)");

        var table = Assert.Single(blocks).Table;
        Assert.NotNull(table);
        Assert.Equal(["id", "name"], table!.Columns);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["2", "bob"], table.Rows[1]);
        Assert.Equal("(2 rows affected)", table.Footer);
    }

    [Fact]
    public void ParsesUnnamedSingleColumn_EvenWhenHeaderLineWasTrimmed()
    {
        var blocks = SqlcmdOutputParser.Parse("-----------\n1\n\n(1 rows affected)");

        var table = Assert.Single(blocks).Table!;
        Assert.Equal([""], table.Columns);
        Assert.Equal("1", table.Rows[0][0]);
    }

    [Fact]
    public void PlainText_IsKeptAsText()
    {
        var block = Assert.Single(SqlcmdOutputParser.Parse("Msg 102, Level 15\nIncorrect syntax"));

        Assert.Null(block.Table);
        Assert.Contains("Incorrect syntax", block.Text);
    }

    [Fact]
    public void TextAndTable_AreSplitIntoBlocks()
    {
        var blocks = SqlcmdOutputParser.Parse("hello\na\tb\n-\t-\n1\t2\n\n(1 rows affected)");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("hello", blocks[0].Text);
        Assert.NotNull(blocks[1].Table);
    }

    [Fact]
    public void ShortRow_IsPaddedToColumnCount()
    {
        var table = Assert.Single(SqlcmdOutputParser.Parse("a\tb\tc\n-\t-\t-\n1\t2")).Table!;

        Assert.Equal(["1", "2", ""], table.Rows[0]);
    }

    [Fact]
    public void EmptyInput_GivesNoBlocks() => Assert.Empty(SqlcmdOutputParser.Parse(""));

    [Fact]
    public void WindowsLineEndings_AreHandled()
    {
        var table = Assert.Single(SqlcmdOutputParser.Parse("a\tb\r\n-\t-\r\n1\t2\r\n")).Table!;

        Assert.Equal(["1", "2"], table.Rows[0]);
    }
}

public class MySqlOutputConverterTests
{
    private const string Box =
        "+----+-------+\n| id | name  |\n+----+-------+\n|  1 | alice |\n|  2 | bob   |\n+----+-------+";

    [Fact]
    public void ConvertsBoxTable_ToParsableLayout()
    {
        var table = Assert.Single(SqlcmdOutputParser.Parse(MySqlOutputConverter.ToSqlcmdStyle(Box))).Table!;

        Assert.Equal(["id", "name"], table.Columns);
        Assert.Equal(["1", "alice"], table.Rows[0]);
        Assert.Equal(["2", "bob"], table.Rows[1]);
        Assert.Equal("(2 rows affected)", table.Footer);
    }

    [Fact]
    public void SingleRow_UsesSingularFooterText()
    {
        var text = MySqlOutputConverter.ToSqlcmdStyle("+---+\n| a |\n+---+\n| 1 |\n+---+");

        Assert.Contains("(1 row affected)", text);
    }

    [Fact]
    public void NonTableOutput_IsPassedThrough() =>
        Assert.Equal("ERROR 1064 (42000): syntax error", MySqlOutputConverter.ToSqlcmdStyle("ERROR 1064 (42000): syntax error"));

    [Fact]
    public void EmptyInput_IsReturnedAsIs() => Assert.Equal("", MySqlOutputConverter.ToSqlcmdStyle(""));
}

public class PsqlOutputConverterTests
{
    private const string Aligned = " id | name  \n----+-------\n  1 | alice\n  2 | bob\n(2 rows)";

    [Fact]
    public void ConvertsAlignedTable_ToParsableLayout()
    {
        var table = Assert.Single(SqlcmdOutputParser.Parse(PsqlOutputConverter.ToSqlcmdStyle(Aligned))).Table!;

        Assert.Equal(["id", "name"], table.Columns);
        Assert.Equal(["1", "alice"], table.Rows[0]);
        Assert.Equal(["2", "bob"], table.Rows[1]);
        Assert.Equal("(2 rows affected)", table.Footer);
    }

    [Fact]
    public void LeadingSpaceTrimmedFromHeader_IsStillParsed()
    {
        // The command runner trims output, so the first header line loses its leading space.
        var table = Assert.Single(SqlcmdOutputParser.Parse(PsqlOutputConverter.ToSqlcmdStyle(Aligned.TrimStart()))).Table!;

        Assert.Equal(["id", "name"], table.Columns);
    }

    [Fact]
    public void NonTableOutput_IsPassedThrough() =>
        Assert.Equal("CREATE TABLE", PsqlOutputConverter.ToSqlcmdStyle("CREATE TABLE"));
}
