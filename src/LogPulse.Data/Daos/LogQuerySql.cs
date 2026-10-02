using System.Text;
using Dapper;
using LogPulse.Core.Queries;

namespace LogPulse.Data.Daos;

/// <summary>Builds the parameterized WHERE clause for <see cref="LogQuery"/>. Kept separate so it can be unit tested.</summary>
internal static class LogQuerySql
{
    public static (string Where, DynamicParameters Parameters) BuildWhere(LogQuery query)
    {
        var conditions = new List<string>();
        var parameters = new DynamicParameters();

        if (query.ServerId is { } serverId)
        {
            conditions.Add("ServerId = @ServerId");
            parameters.Add("ServerId", serverId);
        }

        if (query.MinSeverity is { } minSeverity)
        {
            conditions.Add("Severity >= @MinSeverity");
            parameters.Add("MinSeverity", (byte)minSeverity);
        }

        if (query.From is { } from)
        {
            conditions.Add("Timestamp >= @From");
            parameters.Add("From", from);
        }

        if (query.To is { } to)
        {
            conditions.Add("Timestamp <= @To");
            parameters.Add("To", to);
        }

        if (query.Search is { } search)
        {
            conditions.Add(@"(Message LIKE @Search ESCAPE '\' OR Source LIKE @Search ESCAPE '\')");
            parameters.Add("Search", $"%{EscapeLike(search)}%");
        }

        var where = conditions.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", conditions);
        return (where, parameters);
    }

    /// <summary>Escapes LIKE wildcards so user input is matched literally.</summary>
    public static string EscapeLike(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            if (c is '\\' or '%' or '_' or '[')
            {
                builder.Append('\\');
            }

            builder.Append(c);
        }

        return builder.ToString();
    }
}
