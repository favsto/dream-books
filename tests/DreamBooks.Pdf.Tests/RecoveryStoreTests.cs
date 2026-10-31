using DreamBooks.Core.Documents;
using DreamBooks.Core.Journal;

namespace DreamBooks.Pdf.Tests;

public sealed class RecoveryStoreTests : IDisposable
{
    private static readonly DocumentFingerprint _fp = new(100, 200, new string((char)97, 64));
    private readonly string _root = Path.Combine(Path.GetTempPath(), "db-recovery-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static JournalHeader Header(string path) => new(JournalCodec.CurrentFormatVersion, path, _fp);

    private static string ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void DocumentKey_is_stable_case_insensitive_and_differs_per_path()
    {
        var a = RecoveryStore.DocumentKey(@"C:\Books\One.pdf");
        Assert.Equal(32, a.Length);
        Assert.Matches("^[0-9a-f]{32}$", a);
        Assert.Equal(a, RecoveryStore.DocumentKey(@"C:\Books\One.pdf"));
        Assert.Equal(a, RecoveryStore.DocumentKey(@"c:\books\one.PDF"));
        Assert.Equal(a, RecoveryStore.DocumentKey(@"C:\Books\Sub\..\One.pdf"));
        Assert.NotEqual(a, RecoveryStore.DocumentKey(@"C:\Books\Two.pdf"));
    }

    [Fact]
    public async Task Begin_creates_a_journal_with_the_header_line()
    {
        var store = new RecoveryStore(_root);
        var path = @"C:\Books\One.pdf";
        await using var session = store.Begin(path, _fp);

        Assert.Equal(Path.Combine(_root, RecoveryStore.DocumentKey(path), "journal.jsonl"), session.FilePath);
        var text = ReadShared(session.FilePath);
        Assert.EndsWith("\n", text);
        var header = JournalCodec.ParseHeader(text.TrimEnd('\n'));
        Assert.NotNull(header);
        Assert.Equal(Path.GetFullPath(path), header.Path);
        Assert.Equal(_fp, header.Fingerprint);
    }

    [Fact]
    public async Task Appended_lines_are_readable_by_a_second_reader_immediately()
    {
        await using var session = new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        session.Append("{\"k\":\"x\"}");
        session.Append("{\"k\":\"y\"}");

        using var reader = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(reader);
        var lines = sr.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("{\"k\":\"y\"}", lines[2]);
    }

    [Fact]
    public async Task Truncate_leaves_only_the_new_header()
    {
        await using var session = new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        session.Append("{\"k\":\"x\"}");
        var newHeader = Header(@"C:\Books\Other.pdf");
        session.Truncate(newHeader);

        var lines = ReadShared(session.FilePath).Split((char)10, StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines);
        Assert.Equal(newHeader, JournalCodec.ParseHeader(lines[0]));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(session.FilePath)!));
    }

    [Fact]
    public async Task Truncate_with_lines_replaces_the_content_with_the_header_and_the_lines_at_once()
    {
        await using var session = new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        session.Append("{\"k\":\"old\"}");
        var newHeader = Header(@"C:\Books\One.pdf");

        session.Truncate(newHeader, ["{\"k\":\"a\"}", "{\"k\":\"b\"}"]);
        session.Append("{\"k\":\"c\"}");

        var lines = ReadShared(session.FilePath).Split((char)10, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(newHeader, JournalCodec.ParseHeader(lines[0]));
        Assert.Equal(["{\"k\":\"a\"}", "{\"k\":\"b\"}", "{\"k\":\"c\"}"], lines[1..]);
    }

    [Fact]
    public async Task Truncate_with_a_line_break_in_a_line_changes_nothing()
    {
        await using var session = new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        session.Append("{\"k\":\"old\"}");
        var before = ReadBytesShared(session.FilePath);

        Assert.Throws<ArgumentException>(() => session.Truncate(Header(@"C:\Books\One.pdf"), ["{\"k\":\n1}"]));

        Assert.Equal(before, ReadBytesShared(session.FilePath));
        session.Append("{\"k\":\"after\"}"); // still usable
    }

    [Fact]
    public async Task Truncate_with_lines_replace_failure_keeps_the_old_content()
    {
        await using var session = new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        session.Append("{\"k\":\"before\"}");
        var before = ReadBytesShared(session.FilePath);

        using (new FileStream(session.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.ThrowsAny<IOException>(() => session.Truncate(Header(@"C:\Books\One.pdf"), ["{\"k\":\"new\"}"]));
        }

        Assert.Equal(before, ReadBytesShared(session.FilePath));
    }

    [Fact]
    public async Task Append_after_Truncate_continues_the_new_journal()
    {
        await using var session = new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        session.Append("{\"k\":\"old\"}");
        session.Truncate(Header(@"C:\Books\One.pdf"));
        session.Append("{\"k\":\"new\"}");

        var lines = ReadShared(session.FilePath).Split((char)10, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal("{\"k\":\"new\"}", lines[1]);
    }

    [Fact]
    public async Task Delete_removes_file_and_folder_and_is_idempotent()
    {
        var session = new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        var folder = Path.GetDirectoryName(session.FilePath)!;
        session.Delete();
        session.Delete();

        Assert.False(File.Exists(session.FilePath));
        Assert.False(Directory.Exists(folder));
        Assert.Throws<ObjectDisposedException>(() => session.Append("{}"));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_flushes_pending_fsync_and_does_not_throw_twice()
    {
        var session = new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        for (var i = 0; i < 200; i++)
        {
            session.Append("{\"k\":\"x\"}");
        }

        await session.DisposeAsync();
        await session.DisposeAsync();
        Assert.Equal(201, ReadShared(session.FilePath).Split((char)10, StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Throws<ObjectDisposedException>(() => session.Append("{}"));
    }

    [Fact]
    public async Task FindAll_returns_most_recent_first_and_flags_unreadable_headers()
    {
        var store = new RecoveryStore(_root);
        Assert.Empty(store.FindAll());

        await using (var a = store.Begin(@"C:\Books\A.pdf", _fp))
        {
        }

        await using (var b = store.Begin(@"C:\Books\B.pdf", _fp))
        {
        }

        var badDir = Path.Combine(_root, "badbadbad");
        Directory.CreateDirectory(badDir);
        File.WriteAllText(Path.Combine(badDir, "journal.jsonl"), "garbage\n");

        File.SetLastWriteTimeUtc(store.Find(@"C:\Books\A.pdf")!.FilePath, new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(store.Find(@"C:\Books\B.pdf")!.FilePath, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(Path.Combine(badDir, "journal.jsonl"), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var all = store.FindAll();
        Assert.Equal(3, all.Count);
        Assert.Equal(Path.GetFullPath(@"C:\Books\A.pdf"), all[0].Contents.Header!.Path);
        Assert.Equal(Path.GetFullPath(@"C:\Books\B.pdf"), all[1].Contents.Header!.Path);
        Assert.Null(all[2].Contents.Header);
        Assert.Null(store.Find(@"C:\Books\Missing.pdf"));
    }

    [Fact]
    public async Task FindAll_reads_a_journal_that_is_open_in_this_process()
    {
        var store = new RecoveryStore(_root);
        await using var session = store.Begin(@"C:\Books\A.pdf", _fp);
        session.Append("{\"k\":\"x\"}");
        Assert.Single(store.FindAll());
    }

    [Fact]
    public async Task Delete_by_path_removes_the_journal_and_is_idempotent()
    {
        var store = new RecoveryStore(_root);
        await store.Begin(@"C:\Books\A.pdf", _fp).DisposeAsync();
        store.Delete(@"C:\Books\A.pdf");
        store.Delete(@"C:\Books\A.pdf");
        Assert.Null(store.Find(@"C:\Books\A.pdf"));
    }

    [Fact]
    public void A_file_with_a_torn_last_line_is_read_back_without_it()
    {
        var store = new RecoveryStore(_root);
        var path = @"C:\Books\One.pdf";
        var file = Path.Combine(_root, RecoveryStore.DocumentKey(path), "journal.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, JournalCodec.SerializeHeader(Header(Path.GetFullPath(path))) + "\n{\"k\":\"ad");

        var found = store.Find(path);
        Assert.NotNull(found);
        Assert.True(found.Contents.HadTornTail);
        Assert.Empty(found.Contents.Entries);
    }

    [Fact]
    public async Task Reopening_a_torn_journal_terminates_the_torn_line_before_appending()
    {
        var store = new RecoveryStore(_root);
        var path = @"C:\Books\One.pdf";
        var file = Path.Combine(_root, RecoveryStore.DocumentKey(path), "journal.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var header = JournalCodec.SerializeHeader(Header(Path.GetFullPath(path)));
        File.WriteAllText(file, header + "\n{\"k\":\"ad");

        await using (var session = store.Begin(path, _fp))
        {
            session.Append("{\"k\":\"good\"}");
        }

        var lines = ReadShared(file).Split((char)10, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal(header, lines[0]);
        Assert.Equal("{\"k\":\"ad", lines[1]);
        Assert.Equal("{\"k\":\"good\"}", lines[2]);
    }

    [Fact]
    public async Task Begin_keeps_an_existing_header_and_appends()
    {
        var store = new RecoveryStore(_root);
        await using (var s1 = store.Begin(@"C:\Books\One.pdf", _fp))
        {
            s1.Append("{\"k\":\"one\"}");
        }

        await using (var s2 = store.Begin(@"C:\Books\One.pdf", new DocumentFingerprint(1, 2, new string((char)98, 64))))
        {
            s2.Append("{\"k\":\"two\"}");
        }

        var lines = ReadShared(store.Find(@"C:\Books\One.pdf")!.FilePath).Split((char)10, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal(_fp, JournalCodec.ParseHeader(lines[0])!.Fingerprint);
    }

    private string JournalFile(string docPath)
    {
        var file = Path.Combine(_root, RecoveryStore.DocumentKey(docPath), "journal.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        return file;
    }

    private static string[] DamagedFiles(string journalFile)
        => Directory.GetFiles(Path.GetDirectoryName(journalFile)!, "journal.damaged-*.jsonl");

    [Theory]
    [InlineData("")]
    [InlineData("  \n\n \n")]
    [InlineData("{\"k\":\"hdr\",\"fv\":1,\"pa")]
    [InlineData("OLDFV")]
    public async Task Begin_moves_a_journal_without_a_valid_header_aside_and_starts_fresh(string content)
    {
        var path = @"C:\Books\One.pdf";
        var file = JournalFile(path);
        if (content == "OLDFV")
        {
            content = JournalCodec.SerializeHeader(new JournalHeader(0, Path.GetFullPath(path), _fp)) + "\n{\"k\":\"x\"}\n";
        }

        File.WriteAllText(file, content);

        await using (var session = new RecoveryStore(_root).Begin(path, _fp))
        {
            session.Append("{\"k\":\"good\"}");
        }

        var lines = ReadShared(file).Split((char)10, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(JournalCodec.CurrentFormatVersion, JournalCodec.ParseHeader(lines[0])!.FormatVersion);
        Assert.Equal("{\"k\":\"good\"}", lines[1]);
        var damaged = Assert.Single(DamagedFiles(file));
        Assert.Equal(content, File.ReadAllText(damaged));
        Assert.Matches(@"^journal\.damaged-\d{14}-[0-9a-f]{4}\.jsonl$", Path.GetFileName(damaged));
    }

    [Fact]
    public async Task Begin_keeps_a_valid_header_without_trailing_newline_and_repairs_it()
    {
        var path = @"C:\Books\One.pdf";
        var file = JournalFile(path);
        var header = JournalCodec.SerializeHeader(Header(Path.GetFullPath(path)));
        File.WriteAllText(file, header);

        await using (var session = new RecoveryStore(_root).Begin(path, _fp))
        {
            session.Append("{\"k\":\"good\"}");
        }

        Assert.Equal(header + "\n{\"k\":\"good\"}\n", File.ReadAllText(file));
        Assert.Empty(DamagedFiles(file));
    }

    [Fact]
    public async Task Begin_keeps_a_valid_header_with_a_different_fingerprint_and_path()
    {
        var path = @"C:\Books\One.pdf";
        var file = JournalFile(path);
        var other = new JournalHeader(JournalCodec.CurrentFormatVersion, @"C:\Elsewhere\Other.pdf", new DocumentFingerprint(1, 2, new string((char)99, 64)));
        File.WriteAllText(file, JournalCodec.SerializeHeader(other) + "\n");

        await using (var session = new RecoveryStore(_root).Begin(path, _fp))
        {
            session.Append("{}");
        }

        Assert.Equal(other, JournalCodec.ParseHeader(ReadShared(file).Split((char)10)[0]));
        Assert.Empty(DamagedFiles(file));
    }

    [Fact]
    public async Task FindAll_ignores_damaged_files_and_Delete_by_path_removes_them_with_the_folder()
    {
        var store = new RecoveryStore(_root);
        var path = @"C:\Books\One.pdf";
        var file = JournalFile(path);
        File.WriteAllText(file, "garbage");
        await store.Begin(path, _fp).DisposeAsync();
        Assert.Single(DamagedFiles(file));
        Assert.Single(store.FindAll());

        store.Delete(path);
        Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
    }

    [Fact]
    public async Task Session_Delete_failure_surfaces_and_leaves_the_journal()
    {
        var path = @"C:\Books\One.pdf";
        var session = new RecoveryStore(_root).Begin(path, _fp);
        using var holder = new FileStream(session.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var ex = Assert.Throws<IOException>(session.Delete);
        Assert.DoesNotContain("One.pdf", ex.Message);
        Assert.True(File.Exists(session.FilePath));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Store_Delete_failure_surfaces()
    {
        var store = new RecoveryStore(_root);
        var path = @"C:\Books\One.pdf";
        await store.Begin(path, _fp).DisposeAsync();
        using var holder = new FileStream(store.Find(path)!.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        Assert.Throws<IOException>(() => store.Delete(path));
        Assert.NotNull(store.Find(path));
    }

    [Fact]
    public async Task FindAll_flags_a_locked_journal_as_unreadable_not_damaged()
    {
        var store = new RecoveryStore(_root);
        var path = @"C:\Books\One.pdf";
        await store.Begin(path, _fp).DisposeAsync();
        var file = store.Find(path)!.FilePath;

        using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var locked = Assert.Single(store.FindAll());
            Assert.True(locked.Unreadable);
            Assert.Null(locked.Contents.Header);
        }

        var ok = Assert.Single(store.FindAll());
        Assert.False(ok.Unreadable);
        Assert.NotNull(ok.Contents.Header);

        File.WriteAllText(file, "garbage\nmore");
        var damaged = Assert.Single(store.FindAll());
        Assert.False(damaged.Unreadable);
        Assert.Null(damaged.Contents.Header);
    }

    [Fact]
    public async Task Truncate_replace_failure_keeps_the_old_file_and_the_session_usable()
    {
        await using var session = new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        session.Append("{\"k\":\"before\"}");
        var before = ReadBytesShared(session.FilePath);

        using (new FileStream(session.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.ThrowsAny<IOException>(() => session.Truncate(Header(@"C:\Books\Other.pdf")));
            Assert.Equal(before, ReadBytesShared(session.FilePath));
        }

        Assert.False(File.Exists(session.FilePath + ".tmp"));
        session.Append("{\"k\":\"after\"}");
        var lines = ReadShared(session.FilePath).Split((char)10, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length);
        Assert.Equal("{\"k\":\"after\"}", lines[2]);
    }

    [Fact]
    public async Task Truncate_after_the_journal_was_deleted_externally_recreates_it_with_the_new_header()
    {
        var session = (JournalSession)new RecoveryStore(_root).Begin(@"C:BooksOne.pdf", _fp);
        await using var disposer = session;
        session.Append("{}");
        session.AfterStreamClosedForTest = () => File.Delete(session.FilePath);
        var newHeader = Header(@"C:\Books\Other.pdf");
        session.Truncate(newHeader);
        session.Append("{\"k\":\"x\"}");

        var lines = ReadShared(session.FilePath).Split((char)10, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Equal(newHeader, JournalCodec.ParseHeader(lines[0]));
    }

    [Fact]
    public async Task Truncate_replace_that_removed_the_old_file_without_moving_the_new_one_keeps_the_new_content()
    {
        // Windows error 1176 (ERROR_UNABLE_TO_MOVE_REPLACEMENT): the old journal is gone, the temp still holds the new content.
        var session = (JournalSession)new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        await using var disposer = session;
        session.Append("{\"k\":\"old\"}");
        session.ReplaceForTest = (temp, file) =>
        {
            File.Delete(file);
            throw new IOException("Unable to move the replacement file to the file to be replaced.");
        };
        var newHeader = Header(@"C:\Books\One.pdf");

        session.Truncate(newHeader, ["{\"k\":\"catch-up\"}"]); // succeeds: the new content is in place
        session.Append("{\"k\":\"next\"}");

        var lines = ReadShared(session.FilePath).Split((char)10, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(newHeader, JournalCodec.ParseHeader(lines[0]));
        Assert.Equal(["{\"k\":\"catch-up\"}", "{\"k\":\"next\"}"], lines[1..]);
        Assert.False(File.Exists(session.FilePath + ".tmp"));
    }

    [Fact]
    public async Task Appends_racing_a_Truncate_are_never_corrupted_and_the_tail_is_complete()
    {
        await using var session = new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        const int count = 400;
        var writer = Task.Run(() =>
        {
            for (var i = 0; i < count; i++)
            {
                session.Append("{\"i\":" + i + "}");
            }
        });

        for (var t = 0; t < 5; t++)
        {
            session.Truncate(Header(@"C:\Books\One.pdf"));
            Thread.Sleep(1);
        }

        await writer;
        var lines = ReadShared(session.FilePath).Split((char)10, StringSplitOptions.RemoveEmptyEntries);
        Assert.NotNull(JournalCodec.ParseHeader(lines[0]));
        var numbers = lines.Skip(1).Select(l => int.Parse(l.AsSpan(5, l.Length - 6), System.Globalization.CultureInfo.InvariantCulture)).ToList();
        for (var i = 1; i < numbers.Count; i++)
        {
            Assert.Equal(numbers[i - 1] + 1, numbers[i]);
        }

        if (numbers.Count > 0)
        {
            Assert.Equal(count - 1, numbers[^1]);
        }
    }

    [Fact]
    public async Task An_open_journal_cannot_be_deleted_or_moved_from_outside()
    {
        await using var session = new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        session.Append("{\"k\":\"x\"}");
        Assert.ThrowsAny<IOException>(() => File.Delete(session.FilePath));
        Assert.ThrowsAny<IOException>(() => File.Move(session.FilePath, session.FilePath + ".moved"));
        session.Append("{\"k\":\"y\"}");
        Assert.Equal(3, ReadShared(session.FilePath).Split((char)10, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task Store_Delete_while_a_session_is_open_throws_and_leaves_the_journal_usable()
    {
        var store = new RecoveryStore(_root);
        var path = @"C:\Books\One.pdf";
        await using var session = store.Begin(path, _fp);
        session.Append("{\"k\":\"x\"}");
        Assert.Throws<IOException>(() => store.Delete(path));
        session.Append("{\"k\":\"y\"}");
        Assert.Equal(3, ReadShared(session.FilePath).Split((char)10, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public async Task A_second_Begin_on_a_journal_without_a_header_fails_while_the_first_session_is_open()
    {
        var store = new RecoveryStore(_root);
        await using var first = store.Begin(@"C:\Books\One.pdf", _fp);
        Assert.ThrowsAny<IOException>(() => store.Begin(@"C:\Books\One.pdf", _fp));
    }

    [Fact]
    public async Task Session_Delete_keeps_damaged_journals_and_the_folder_and_Store_Delete_removes_them()
    {
        var store = new RecoveryStore(_root);
        var path = @"C:\Books\One.pdf";
        var file = JournalFile(path);
        File.WriteAllText(file, "garbage");
        var session = store.Begin(path, _fp);
        session.Delete();
        Assert.False(File.Exists(file));
        Assert.Single(DamagedFiles(file));
        Assert.True(Directory.Exists(Path.GetDirectoryName(file)));
        await session.DisposeAsync();

        store.Delete(path);
        Assert.False(Directory.Exists(Path.GetDirectoryName(file)));
    }

    [Fact]
    public async Task Session_Delete_removes_the_folder_when_nothing_else_is_left()
    {
        var session = new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        var folder = Path.GetDirectoryName(session.FilePath)!;
        session.Delete();
        Assert.False(Directory.Exists(folder));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Append_rejects_line_breaks()
    {
        await using var session = new RecoveryStore(_root).Begin(@"C:\Books\One.pdf", _fp);
        Assert.Throws<ArgumentException>(() => session.Append("a\nb"));
        Assert.Throws<ArgumentException>(() => session.Append("a\rb"));
    }

    private static byte[] ReadBytesShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Journal_files_never_appear_next_to_the_document()
    {
        var docDir = Path.Combine(Path.GetTempPath(), "db-doc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(docDir);
        try
        {
            var doc = Path.Combine(docDir, "book.pdf");
            File.WriteAllText(doc, "x");
            var before = Directory.GetFileSystemEntries(docDir, "*", SearchOption.AllDirectories);

            await using (var session = new RecoveryStore(_root).Begin(doc, _fp))
            {
                session.Append("{}");
                session.Truncate(Header(doc));
            }

            Assert.Equal(before, Directory.GetFileSystemEntries(docDir, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(docDir, recursive: true);
        }
    }
}
