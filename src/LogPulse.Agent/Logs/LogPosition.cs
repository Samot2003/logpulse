namespace LogPulse.Agent.Logs;

/// <summary>
/// Identifies the content of a file by a hash of its first bytes (up to <see cref="MaxHeadLength"/>). When a log
/// is rotated the path stays the same but the content starts over, and the hash no longer matches.
/// </summary>
public sealed record FileFingerprint(int HeadLength, string HeadHash)
{
    public const int MaxHeadLength = 256;

    /// <summary>An empty file: matches anything.</summary>
    public static FileFingerprint Empty { get; } = new(0, string.Empty);
}

/// <summary>A position in a log file: everything before <see cref="Offset"/> (in bytes) has been read.</summary>
public sealed record LogPosition(string FilePath, FileFingerprint Fingerprint, long Offset);

public static class FilePaths
{
    /// <summary>Windows paths are case-insensitive; Linux paths are not.</summary>
    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
