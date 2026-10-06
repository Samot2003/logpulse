using System.Security.Cryptography;
using System.Text;

namespace LogPulse.Agent.Logs;

/// <summary>A complete line read from a log file, and the position right after it.</summary>
public readonly record struct TailedLine(string Text, LogPosition Position);

/// <summary>
/// Follows one log file like <c>tail -F</c>: returns the complete lines added since the last read, resumes from a
/// checkpoint after a restart, and starts over when the file is truncated or replaced (log rotation).
/// </summary>
/// <remarks>
/// Known limits: lines written to the old file between the last read and its rotation are not read, a new file
/// whose first bytes are identical to the old one (and already longer) is not recognized as new, and every
/// physical line is one entry (a multi-line stack trace becomes several entries).
/// </remarks>
public sealed class LogFileTailer
{
    /// <summary>Bytes read per call; <see cref="MoreAvailable"/> tells the caller to read again.</summary>
    public const int ChunkBytes = 256 * 1024;

    /// <summary>How far back to look for the start of the current line when starting at the end of a file.</summary>
    private const int LineStartSearchBytes = 64 * 1024;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private LogPosition? _checkpoint;
    private bool _startAtBeginning;
    private bool _startReported;
    private LogPosition? _startPosition;
    private FileFingerprint? _fingerprint; // null until the file is first opened by this tailer
    private long _offset;

    /// <param name="path">Full path of the file.</param>
    /// <param name="checkpoint">Where a previous run stopped, if anywhere.</param>
    /// <param name="readExisting">Without a checkpoint, read the content already in the file instead of skipping it.</param>
    public LogFileTailer(string path, LogPosition? checkpoint, bool readExisting)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        FilePath = path;
        _checkpoint = checkpoint;
        _startAtBeginning = readExisting;
        _startReported = checkpoint is not null;
    }

    public string FilePath { get; }

    /// <summary>True when the last read stopped at <see cref="ChunkBytes"/> and the file has more data.</summary>
    public bool MoreAvailable { get; private set; }

    /// <summary>
    /// For a file without a checkpoint: where reading starts, known after the first read. The caller saves it right
    /// away, so a restart before the API confirms any line resumes here instead of skipping to the file's new end.
    /// Returned once.
    /// </summary>
    public LogPosition? TakeStartPosition()
    {
        var start = _startPosition;
        _startPosition = null;
        return start;
    }

    /// <summary>Returns the new complete lines. A last line without its newline is left for the next call.</summary>
    public IReadOnlyList<TailedLine> ReadNewLines()
    {
        MoreAvailable = false;

        FileStream stream;
        try
        {
            // Share everything: the application keeps writing, and rotation may rename or delete the file.
            stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (FileNotFoundException)
        {
            // The folder exists but the file does not (yet, or between two rotation steps): whatever the
            // application writes at this path later is all new.
            if (_fingerprint is null && _checkpoint is null)
            {
                _startAtBeginning = true;
                ReportStart(FileFingerprint.Empty, 0);
            }

            return [];
        }
        catch (DirectoryNotFoundException)
        {
            // The folder itself is missing, e.g. a volume not mounted yet at boot: when it appears, the file may
            // hold old content, so nothing is decided until it can be opened.
            return [];
        }

        using (stream)
        {
            var length = stream.Length;
            var head = ReadHead(stream, length);
            var fingerprint = new FileFingerprint(head.Length, Hash(head));

            if (_fingerprint is null)
            {
                // First open in this run.
                if (_checkpoint is { } checkpoint)
                {
                    // Same file as before the restart: resume. Otherwise it was rotated meanwhile: all of it is new.
                    var same = Matches(head, checkpoint.Fingerprint) && checkpoint.Offset <= length;
                    _offset = same ? checkpoint.Offset : 0;
                    _checkpoint = null;
                }
                else
                {
                    _offset = _startAtBeginning ? 0 : StartOfLastLine(stream, length);
                    ReportStart(fingerprint, _offset);
                }
            }
            else if (length < _offset || !Matches(head, _fingerprint))
            {
                // Truncated in place (copytruncate) or replaced by a new file: start over from its first byte.
                _offset = 0;
            }

            // While the file is shorter than MaxHeadLength, the fingerprint grows with it.
            _fingerprint = fingerprint;
            return ReadLines(stream, length, fingerprint);
        }
    }

    private void ReportStart(FileFingerprint fingerprint, long offset)
    {
        if (!_startReported)
        {
            _startPosition = new LogPosition(FilePath, fingerprint, offset);
            _startReported = true;
        }
    }

    /// <summary>
    /// Skipping existing content starts at the end, but the application may be halfway through writing a line:
    /// start at the beginning of that line so it is read whole once complete, not as a fragment.
    /// </summary>
    private static long StartOfLastLine(FileStream stream, long length)
    {
        var size = (int)Math.Min(LineStartSearchBytes, length);
        if (size == 0)
        {
            return 0;
        }

        var tail = new byte[size];
        stream.Position = length - size;
        var read = stream.ReadAtLeast(tail, size, throwOnEndOfStream: false);
        var lastNewline = tail.AsSpan(0, read).LastIndexOf((byte)'\n');

        if (lastNewline < 0)
        {
            // The whole (small) file is one unfinished line, or the line start is out of reach.
            return length <= size ? 0 : length;
        }

        // A file ending with a newline has nothing half-written: start at its very end.
        return lastNewline == read - 1 ? length : length - size + lastNewline + 1;
    }

    private List<TailedLine> ReadLines(FileStream stream, long length, FileFingerprint fingerprint)
    {
        if (_offset >= length)
        {
            return [];
        }

        var start = _offset;
        var buffer = new byte[(int)Math.Min(ChunkBytes, length - start)];
        stream.Position = start;
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

        var lines = new List<TailedLine>();
        var lineStart = 0;
        for (var i = 0; i < read; i++)
        {
            if (buffer[i] == (byte)'\n')
            {
                Add(lines, buffer.AsSpan(lineStart, i - lineStart), start + lineStart, new LogPosition(FilePath, fingerprint, start + i + 1));
                lineStart = i + 1;
            }
        }

        // A single line longer than a whole chunk: send it as it is (it gets truncated) instead of waiting forever.
        if (lineStart == 0 && read == ChunkBytes)
        {
            Add(lines, buffer.AsSpan(0, read), start, new LogPosition(FilePath, fingerprint, start + read));
            lineStart = read;
        }

        _offset = start + lineStart;
        MoreAvailable = start + read < length;
        return lines;
    }

    private static void Add(List<TailedLine> lines, ReadOnlySpan<byte> bytes, long lineOffset, LogPosition end)
    {
        // Files written by some Windows tools start with a UTF-8 byte order mark.
        if (lineOffset == 0 && bytes.StartsWith(Utf8Bom))
        {
            bytes = bytes[Utf8Bom.Length..];
        }

        if (bytes is [.., (byte)'\r'])
        {
            bytes = bytes[..^1];
        }

        var text = Encoding.UTF8.GetString(bytes);
        if (!string.IsNullOrWhiteSpace(text))
        {
            lines.Add(new TailedLine(text, end));
        }
    }

    private static byte[] ReadHead(FileStream stream, long length)
    {
        var head = new byte[(int)Math.Min(FileFingerprint.MaxHeadLength, length)];
        stream.Position = 0;
        var read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        return read == head.Length ? head : head[..read];
    }

    private static bool Matches(byte[] head, FileFingerprint fingerprint) =>
        fingerprint.HeadLength <= head.Length
        && string.Equals(Hash(head.AsSpan(0, fingerprint.HeadLength)), fingerprint.HeadHash, StringComparison.Ordinal);

    private static string Hash(ReadOnlySpan<byte> bytes) => bytes.IsEmpty ? string.Empty : Convert.ToHexString(SHA256.HashData(bytes));
}
