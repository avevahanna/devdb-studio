namespace DevDbStudio.Services.Dbms;

public static class ConnectionStrings
{
    /// <summary>
    /// Quotes a value for a key=value;key=value connection string (ADO.NET, Npgsql, MySqlConnector) when it
    /// contains characters that would otherwise end or corrupt the value.
    /// </summary>
    public static string Value(string value)
    {
        if (value.Length == 0) return value;
        var needsQuotes = value.AsSpan().IndexOfAny(";\"'=") >= 0 || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]);
        return needsQuotes ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}
