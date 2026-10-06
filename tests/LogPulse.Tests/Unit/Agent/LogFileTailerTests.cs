using System.Text;
using LogPulse.Agent.Logs;
using Microsoft.Extensions.Logging.Abstractions;

namespace LogPulse.Tests.Unit.Agent;

public sealed class LogFileTailerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("logpulse-tailer-").FullName;

    private string LogPath => Path.Combine(_dir, "app.log");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static List<string> Texts(IEnumerable<TailedLine> lines) => lines.Select(l => l.Text).ToList();

    private void Append(string text) => File.AppendAllText(LogPath, text);

    [Fact]
    public void Without_a_checkpoint_existing_content_is_skipped_and_new_lines_are_read()
    {
        Append("old line\n");
        var tailer = new LogFileTailer(LogPath, checkpoint: null, readExisting: false);

        Assert.Empty(tailer.ReadNewLines());
        Append("new 1\nnew 2\n");

        Assert.Equal(["new 1", "new 2"], Texts(tailer.ReadNewLines()));
        Assert.Empty(tailer.ReadNewLines());
    }

    [Fact]
    public void ReadExisting_reads_the_content_already_in_the_file()
    {
        Append("old line\n");

        Assert.Equal(["old line"], Texts(new LogFileTailer(LogPath, null, readExisting: true).ReadNewLines()));
    }

    [Fact]
    public void A_file_that_appears_later_is_read_from_its_first_line()
    {
        var tailer = new LogFileTailer(LogPath, null, readExisting: false);
        Assert.Empty(tailer.ReadNewLines()); // missing: nothing, no error

        Append("first\nsecond\n");

        Assert.Equal(["first", "second"], Texts(tailer.ReadNewLines()));
    }

    [Fact]
    public void A_line_without_its_newline_waits_until_it_is_complete()
    {
        var tailer = new LogFileTailer(LogPath, null, readExisting: true);
        Append("complete\npart");

        Assert.Equal(["complete"], Texts(tailer.ReadNewLines()));
        Append("ial\n");
        Assert.Equal(["partial"], Texts(tailer.ReadNewLines()));
    }

    [Fact]
    public void Byte_order_mark_carriage_returns_and_blank_lines_are_removed()
    {
        File.WriteAllBytes(LogPath, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("first\r\n\r\n   \nsecond é\r\n")]);

        Assert.Equal(["first", "second é"], Texts(new LogFileTailer(LogPath, null, readExisting: true).ReadNewLines()));
    }

    [Fact]
    public void Positions_point_right_after_each_line()
    {
        var tailer = new LogFileTailer(LogPath, null, readExisting: true);
        Append("ab\ncd\n");

        var lines = tailer.ReadNewLines();

        Assert.Equal([3L, 6L], lines.Select(l => l.Position.Offset));
        Assert.All(lines, l => Assert.Equal(LogPath, l.Position.FilePath));
    }

    [Fact]
    public void A_truncated_file_is_read_again_from_the_start()
    {
        var tailer = new LogFileTailer(LogPath, null, readExisting: true);
        Append("line one is long\nline two is long\n");
        tailer.ReadNewLines();

        File.WriteAllText(LogPath, "after truncation\n"); // copytruncate: same file, shorter

        Assert.Equal(["after truncation"], Texts(tailer.ReadNewLines()));
    }

    [Fact]
    public void A_replaced_file_is_read_from_the_start_even_when_already_longer()
    {
        var tailer = new LogFileTailer(LogPath, null, readExisting: true);
        Append("old 1\nold 2\n");
        tailer.ReadNewLines();

        File.Delete(LogPath);
        File.WriteAllText(LogPath, "new file line 1\nnew file line 2\nnew file line 3\n");

        Assert.Equal(["new file line 1", "new file line 2", "new file line 3"], Texts(tailer.ReadNewLines()));
    }

    [Fact]
    public void A_restart_resumes_after_the_last_confirmed_line()
    {
        var first = new LogFileTailer(LogPath, null, readExisting: true);
        Append("sent\n");
        var checkpoint = first.ReadNewLines()[^1].Position;
        Append("written while stopped\n");

        var second = new LogFileTailer(LogPath, checkpoint, readExisting: false);

        Assert.Equal(["written while stopped"], Texts(second.ReadNewLines()));
    }

    [Fact]
    public void A_checkpoint_of_a_file_rotated_while_stopped_reads_the_new_file_from_the_start()
    {
        var first = new LogFileTailer(LogPath, null, readExisting: true);
        Append("before rotation\n");
        var checkpoint = first.ReadNewLines()[^1].Position;
        File.WriteAllText(LogPath, "rotated content that is longer than before\n");

        var second = new LogFileTailer(LogPath, checkpoint, readExisting: false);

        Assert.Equal(["rotated content that is longer than before"], Texts(second.ReadNewLines()));
    }

    [Fact]
    public void A_big_backlog_is_read_in_chunks()
    {
        var line = new string('x', 999) + "\n"; // 1000 bytes
        File.WriteAllText(LogPath, string.Concat(Enumerable.Repeat(line, 600))); // 600 KB: three chunks
        var tailer = new LogFileTailer(LogPath, null, readExisting: true);

        var total = tailer.ReadNewLines().Count;
        var calls = 1;
        while (tailer.MoreAvailable)
        {
            total += tailer.ReadNewLines().Count;
            calls++;
        }

        Assert.Equal(600, total);
        Assert.Equal(3, calls);
    }

    [Fact]
    public void A_line_longer_than_a_chunk_is_returned_instead_of_blocking_the_file()
    {
        File.WriteAllText(LogPath, new string('x', LogFileTailer.ChunkBytes + 10) + "\nnext\n");
        var tailer = new LogFileTailer(LogPath, null, readExisting: true);

        var lines = Texts(tailer.ReadNewLines());
        while (tailer.MoreAvailable)
        {
            lines.AddRange(Texts(tailer.ReadNewLines()));
        }

        Assert.Equal(3, lines.Count); // the long line in two pieces, then "next"
        Assert.Equal("next", lines[^1]);
    }

    [Fact]
    public void Starting_at_the_end_reports_the_start_position_once()
    {
        Append("old line\n");
        var tailer = new LogFileTailer(LogPath, null, readExisting: false);

        tailer.ReadNewLines();

        Assert.Equal(9, tailer.TakeStartPosition()?.Offset);
        Assert.Null(tailer.TakeStartPosition());
    }

    [Fact]
    public void A_tailer_resuming_from_a_checkpoint_reports_no_start_position()
    {
        Append("line\n");
        var checkpoint = new LogFileTailer(LogPath, null, readExisting: true).ReadNewLines()[^1].Position;
        var tailer = new LogFileTailer(LogPath, checkpoint, readExisting: false);

        tailer.ReadNewLines();

        Assert.Null(tailer.TakeStartPosition());
    }

    [Fact]
    public void A_restart_before_any_confirmed_line_resumes_from_the_saved_start()
    {
        // First run: the API is unreachable, so nothing gets confirmed; only the start position is saved.
        Append("before the agent\n");
        var first = new LogFileTailer(LogPath, null, readExisting: false);
        first.ReadNewLines();
        var start = first.TakeStartPosition();
        Append("written while the API was down\n");
        first.ReadNewLines();

        var second = new LogFileTailer(LogPath, start, readExisting: false);

        Assert.Equal(["written while the API was down"], Texts(second.ReadNewLines()));
    }

    [Fact]
    public void A_missing_file_saves_a_start_at_its_first_byte_so_a_restart_still_reads_it_whole()
    {
        var first = new LogFileTailer(LogPath, null, readExisting: false);
        first.ReadNewLines();
        var start = first.TakeStartPosition();
        Append("appeared while stopped\n");

        var second = new LogFileTailer(LogPath, start, readExisting: false);

        Assert.Equal(0, start?.Offset);
        Assert.Equal(["appeared while stopped"], Texts(second.ReadNewLines()));
    }

    [Fact]
    public void A_missing_folder_decides_nothing_until_the_file_can_be_opened()
    {
        // e.g. a volume not mounted yet at boot: the file may already hold old content when it appears.
        var path = Path.Combine(_dir, "not-mounted", "app.log");
        var tailer = new LogFileTailer(path, null, readExisting: false);
        Assert.Empty(tailer.ReadNewLines());
        Assert.Null(tailer.TakeStartPosition());

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "old content" + "\n");

        Assert.Empty(tailer.ReadNewLines());
        Assert.Equal(12, tailer.TakeStartPosition()?.Offset);
    }

    [Fact]
    public void Starting_at_the_end_does_not_cut_a_line_that_is_being_written()
    {
        Append("complete\nhalf-wri");
        var tailer = new LogFileTailer(LogPath, null, readExisting: false);

        Assert.Empty(tailer.ReadNewLines());
        Append("tten\n");

        Assert.Equal(["half-written"], Texts(tailer.ReadNewLines()));
    }

    [Fact]
    public void Checkpoints_survive_a_restart_and_the_last_position_of_each_file_wins()
    {
        var state = Path.Combine(_dir, "state");
        var fingerprint = new FileFingerprint(3, "ABC");
        new CheckpointStore(state, NullLogger<CheckpointStore>.Instance).Save(
        [
            new LogPosition(LogPath, fingerprint, 10),
            new LogPosition(LogPath, fingerprint, 25),
        ]);

        var reloaded = new CheckpointStore(state, NullLogger<CheckpointStore>.Instance);

        Assert.Equal(new LogPosition(LogPath, fingerprint, 25), reloaded.Find(LogPath));
        Assert.Null(reloaded.Find(Path.Combine(_dir, "other.log")));
    }

    [Fact]
    public void A_known_position_is_not_replaced_by_a_start_position()
    {
        var state = Path.Combine(_dir, "state");
        var store = new CheckpointStore(state, NullLogger<CheckpointStore>.Instance);
        store.Save([new LogPosition(LogPath, FileFingerprint.Empty, 40)]);

        store.SaveIfMissing(new LogPosition(LogPath, FileFingerprint.Empty, 0));

        Assert.Equal(40, new CheckpointStore(state, NullLogger<CheckpointStore>.Instance).Find(LogPath)?.Offset);
    }

    [Fact]
    public void Inconsistent_entries_of_a_tampered_checkpoint_file_are_dropped()
    {
        var state = Path.Combine(_dir, "state");
        Directory.CreateDirectory(state);
        var escaped = LogPath.Replace("\\", "\\\\", StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(state, CheckpointStore.FileName), $$"""
            [ { "filePath": "{{escaped}}", "fingerprint": { "headLength": -1, "headHash": "" }, "offset": 5 } ]
            """);

        Assert.Null(new CheckpointStore(state, NullLogger<CheckpointStore>.Instance).Find(LogPath));
    }

    [Fact]
    public void A_damaged_checkpoint_file_is_ignored()
    {
        var state = Path.Combine(_dir, "state");
        Directory.CreateDirectory(state);
        File.WriteAllText(Path.Combine(state, CheckpointStore.FileName), "{ not json");

        var store = new CheckpointStore(state, NullLogger<CheckpointStore>.Instance);

        Assert.Null(store.Find(LogPath));
    }
}
