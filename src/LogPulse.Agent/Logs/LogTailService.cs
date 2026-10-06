using LogPulse.Agent.Options;
using LogPulse.Agent.Shipping;
using LogPulse.Core.Contracts;
using LogPulse.Core.Models;
using Microsoft.Extensions.Options;

namespace LogPulse.Agent.Logs;

/// <summary>Polls the configured log files and queues their new lines for sending.</summary>
public sealed partial class LogTailService(
    AgentBuffers buffers,
    CheckpointStore checkpoints,
    IOptions<AgentOptions> options,
    IHostEnvironment environment,
    TimeProvider time,
    ILogger<LogTailService> logger) : BackgroundService
{
    private sealed class FollowedFile(LogFileTailer tailer, string source)
    {
        public LogFileTailer Tailer { get; } = tailer;
        public string Source { get; } = source;
        public bool Failing { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        var files = settings.LogFiles
            .Select(f => (File: f, Path: Path.GetFullPath(f.Path, environment.ContentRootPath)))
            .DistinctBy(f => f.Path, FilePaths.Comparer)
            .Select(f => new FollowedFile(
                new LogFileTailer(f.Path, checkpoints.Find(f.Path), settings.ReadExistingLogs),
                FieldLimits.Truncate(f.File.Source is { Length: > 0 } source ? source : Path.GetFileName(f.Path), FieldLimits.LogSource)))
            .ToList();

        if (files.Count == 0)
        {
            LogNoFiles(logger);
            return;
        }

        using var timer = new PeriodicTimer(settings.LogPollInterval, time);
        do
        {
            // Round-robin: while any file still has a backlog, go round again right away instead of waiting for
            // the next tick, so the per-poll cap shares the reading between files without limiting its speed.
            bool backlog;
            do
            {
                backlog = false;
                foreach (var file in files)
                {
                    backlog |= await PollAsync(file, stoppingToken);
                }
            }
            while (backlog);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Chunks of <see cref="LogFileTailer.ChunkBytes"/> read from one file before moving on to the next (1 MB): a
    /// big backlog in one file is worked through in turns instead of keeping the other files waiting.
    /// </summary>
    public const int MaxChunksPerPoll = 4;

    /// <summary>Reads up to <see cref="MaxChunksPerPoll"/> chunks of the file. Returns true if more is waiting.</summary>
    private async Task<bool> PollAsync(FollowedFile file, CancellationToken cancellationToken)
    {
        try
        {
            var chunks = 0;
            do
            {
                var lines = file.Tailer.ReadNewLines();
                if (file.Tailer.TakeStartPosition() is { } start)
                {
                    SaveStart(start);
                }

                foreach (var line in lines)
                {
                    // The line's own timestamp is not parsed (every format differs). The read time is within a second
                    // in normal operation; lines read late (a backlog after an outage) get the time they are read.
                    var entry = new IngestLogEntry
                    {
                        Timestamp = time.GetUtcNow(),
                        Severity = LogSeverityParser.Detect(line.Text),
                        Source = file.Source,
                        Message = FieldLimits.Truncate(line.Text, FieldLimits.LogMessage),
                    };

                    // Waits while the buffer is full (API down or slow); the rest of the lines stay in the file.
                    await buffers.Logs.Writer.WriteAsync(new PendingLog(entry, line.Position), cancellationToken);
                }
            }
            while (file.Tailer.MoreAvailable && ++chunks < MaxChunksPerPoll);

            file.Failing = false;
            return file.Tailer.MoreAvailable;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One unreadable file (permissions, I/O error, a tampered checkpoint) must not stop the other files or
            // the agent. Logged once per failure streak, not every second.
            if (!file.Failing)
            {
                LogFileUnreadable(logger, file.Tailer.FilePath, ex);
                file.Failing = true;
            }

            return false;
        }
    }

    // Saved before any line is sent, so a restart before the first confirmed batch resumes from here instead of
    // skipping what was written meanwhile. Not fatal: the lines already read must still be queued.
    private void SaveStart(LogPosition start)
    {
        try
        {
            checkpoints.SaveIfMissing(start);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogStartNotSaved(logger, start.FilePath, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not save the start position of {Path}; a restart before the first sent batch may skip lines")]
    private static partial void LogStartNotSaved(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "No log files configured (Agent:LogFiles); only metrics will be sent")]
    private static partial void LogNoFiles(ILogger logger);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Cannot read log file {Path}; will keep trying")]
    private static partial void LogFileUnreadable(ILogger logger, string path, Exception exception);
}
