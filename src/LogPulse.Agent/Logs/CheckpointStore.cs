using System.Text.Json;

namespace LogPulse.Agent.Logs;

/// <summary>
/// Remembers, per log file, up to which position the API has confirmed the lines. It is saved after every
/// confirmed batch, so after a restart the agent resumes there: every line is sent at least once (a crash between
/// the API storing a batch and this save means that batch is sent again).
/// </summary>
public sealed partial class CheckpointStore
{
    public const string FileName = "log-positions.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _file;
    private readonly object _gate = new();
    private readonly Dictionary<string, LogPosition> _positions;

    public CheckpointStore(string directory, ILogger<CheckpointStore> logger)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        ArgumentNullException.ThrowIfNull(logger);

        Directory.CreateDirectory(directory);
        _file = Path.Combine(directory, FileName);
        _positions = Load(_file, logger);
    }

    public LogPosition? Find(string filePath)
    {
        lock (_gate)
        {
            return _positions.GetValueOrDefault(filePath);
        }
    }

    /// <summary>Records confirmed positions, in read order (the last one of each file wins), and writes them to disk.</summary>
    public void Save(IEnumerable<LogPosition> confirmed)
    {
        ArgumentNullException.ThrowIfNull(confirmed);

        lock (_gate)
        {
            var changed = false;
            foreach (var position in confirmed)
            {
                _positions[position.FilePath] = position;
                changed = true;
            }

            if (changed)
            {
                Write();
            }
        }
    }

    /// <summary>Records where reading of a file starts, unless a position is already known for it.</summary>
    public void SaveIfMissing(LogPosition start)
    {
        ArgumentNullException.ThrowIfNull(start);

        lock (_gate)
        {
            if (_positions.TryAdd(start.FilePath, start))
            {
                Write();
            }
        }
    }

    private void Write()
    {
        // Write a new file, then rename it over the old one: a crash mid-write leaves the previous file intact.
        // CreateNew after deleting never follows a link planted under the temporary name.
        var temporary = _file + ".tmp";
        File.Delete(temporary);
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, _positions.Values, Json);
            // On disk before the rename: after a power cut the file is the old one or the new one, never empty.
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, _file, overwrite: true);
    }

    private static Dictionary<string, LogPosition> Load(string file, ILogger logger)
    {
        var positions = new Dictionary<string, LogPosition>(FilePaths.Comparer);
        if (!File.Exists(file))
        {
            return positions;
        }

        try
        {
            foreach (var position in JsonSerializer.Deserialize<List<LogPosition>>(File.ReadAllText(file), Json) ?? [])
            {
                // Anything inconsistent (a damaged or tampered file) is dropped rather than trusted.
                if (position is { FilePath.Length: > 0, Offset: >= 0, Fingerprint: { HeadLength: >= 0 and <= FileFingerprint.MaxHeadLength } fingerprint }
                    && (fingerprint.HeadLength == 0) == string.IsNullOrEmpty(fingerprint.HeadHash))
                {
                    positions[position.FilePath] = position;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged or unreadable file must not stop the agent: start without checkpoints (new lines only).
            LogUnreadableCheckpoints(logger, file, ex);
            positions.Clear();
        }

        return positions;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring unreadable log positions file {File}")]
    private static partial void LogUnreadableCheckpoints(ILogger logger, string file, Exception exception);
}
