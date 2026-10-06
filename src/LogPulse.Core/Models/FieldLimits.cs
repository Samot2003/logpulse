namespace LogPulse.Core.Models;

/// <summary>Maximum lengths of stored text fields. They mirror the column sizes in the SQL schema.</summary>
public static class FieldLimits
{
    public const int ServerName = 128;

    /// <summary>Host-name-like server names: they appear in URLs (e.g. DELETE /api/agents/{serverName}).</summary>
    public const string ServerNamePattern = "^[A-Za-z0-9][A-Za-z0-9._-]*$";
    public const int UserName = 64;
    public const int LogSource = 256;
    public const int LogMessage = 4000;

    /// <summary>The column is NVARCHAR(MAX); this cap keeps a single runaway stack trace from filling the disk.</summary>
    public const int LogException = 32_000;

    /// <summary>
    /// Shortens the value to at most <paramref name="maxLength"/> characters, ending in an ellipsis. Used by the
    /// agent before sending and by the API before storing, so both cut a long field the same way.
    /// </summary>
    public static string Truncate(string value, int maxLength)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 2);
        if (value.Length <= maxLength)
        {
            return value;
        }

        var cut = maxLength - 1;
        // Never split a surrogate pair (emoji and other non-BMP characters).
        if (char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return string.Concat(value.AsSpan(0, cut), "…");
    }
}
