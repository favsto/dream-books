using DreamBooks.Core.Documents;

namespace DreamBooks.Core.Tests.Documents;

public sealed class DocumentFingerprintTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbfp").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static byte[] Bytes(int n) => Enumerable.Repeat((byte)7, n).ToArray();

    private string Write(string name, byte[] bytes, DateTime? when = null)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, bytes);
        File.SetLastWriteTimeUtc(path, when ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return path;
    }

    [Fact]
    public async Task Fingerprint_changes_when_length_or_edge_bytes_or_write_time_change()
    {
        var baseBytes = Bytes(300_000);
        var baseline = await DocumentFingerprint.ComputeAsync(Write("a.pdf", baseBytes));

        Assert.Equal(baseline, await DocumentFingerprint.ComputeAsync(Write("same.pdf", baseBytes)));
        Assert.NotEqual(baseline, await DocumentFingerprint.ComputeAsync(Write("len.pdf", Bytes(300_001))));

        var head = (byte[])baseBytes.Clone();
        head[0] ^= 1;
        Assert.NotEqual(baseline.EdgeHash, (await DocumentFingerprint.ComputeAsync(Write("head.pdf", head))).EdgeHash);

        var tail = (byte[])baseBytes.Clone();
        tail[^1] ^= 1;
        Assert.NotEqual(baseline.EdgeHash, (await DocumentFingerprint.ComputeAsync(Write("tail.pdf", tail))).EdgeHash);

        var middle = (byte[])baseBytes.Clone();
        middle[150_000] ^= 1;
        Assert.Equal(baseline.EdgeHash, (await DocumentFingerprint.ComputeAsync(Write("mid.pdf", middle))).EdgeHash);

        var later = await DocumentFingerprint.ComputeAsync(Write("time.pdf", baseBytes, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
        Assert.NotEqual(baseline.LastWriteUtcTicks, later.LastWriteUtcTicks);
        Assert.NotEqual(baseline, later);
    }

    [Fact]
    public async Task Middle_change_keeping_length_and_write_time_is_caught_in_the_sampled_blocks()
    {
        // 300 000 bytes: a 168 928-byte middle, sampled in 16 blocks of 4 KB (the first starts right after the head).
        var baseBytes = Bytes(300_000);
        var baseline = await DocumentFingerprint.ComputeAsync(Write("a.pdf", baseBytes));
        Assert.NotNull(baseline.MiddleHash);

        foreach (var offset in new[] { 64 * 1024, (64 * 1024) + 4095, 300_000 - (64 * 1024) - 1 })
        {
            var middle = (byte[])baseBytes.Clone();
            middle[offset] ^= 1;
            var changed = await DocumentFingerprint.ComputeAsync(Write($"mid{offset}.pdf", middle));
            Assert.Equal(baseline.EdgeHash, changed.EdgeHash);
            Assert.NotEqual(baseline, changed);
        }

        // A byte between two sampled blocks stays invisible: the documented limit of sampling.
        var gap = (byte[])baseBytes.Clone();
        gap[(64 * 1024) + 4096] ^= 1;
        Assert.Equal(baseline, await DocumentFingerprint.ComputeAsync(Write("gap.pdf", gap)));
    }

    [Fact]
    public async Task A_middle_up_to_64_KB_is_hashed_whole()
    {
        var baseBytes = Bytes((128 * 1024) + (64 * 1024));
        var baseline = await DocumentFingerprint.ComputeAsync(Write("a.pdf", baseBytes));

        for (var offset = 64 * 1024; offset < (128 * 1024); offset += 997)
        {
            var middle = (byte[])baseBytes.Clone();
            middle[offset] ^= 1;
            Assert.NotEqual(baseline, await DocumentFingerprint.ComputeAsync(Write($"m{offset}.pdf", middle)));
        }
    }

    [Fact]
    public async Task Small_files_have_no_middle_sample_and_keep_the_three_part_token()
    {
        var small = await DocumentFingerprint.ComputeAsync(Write("s.pdf", Bytes(128 * 1024)));

        Assert.Null(small.MiddleHash);
        Assert.Equal(3, small.ToToken().Split(':').Length);
    }

    [Fact]
    public async Task An_old_token_without_the_middle_sample_still_matches_the_same_file()
    {
        // Journals written before the sample existed must not raise a mismatch question after an upgrade.
        var path = Write("a.pdf", Bytes(300_000));
        var fresh = await DocumentFingerprint.ComputeAsync(path);
        Assert.Equal(4, fresh.ToToken().Split(':').Length);
        var oldToken = string.Join(':', fresh.ToToken().Split(':')[..3]);

        Assert.True(DocumentFingerprint.TryParse(oldToken, out var old));
        Assert.Null(old.MiddleHash);
        Assert.Equal(old, fresh);
        Assert.Equal(fresh, old);
        Assert.True(old == fresh);
        Assert.Equal(old.GetHashCode(), fresh.GetHashCode());

        // The old fields still decide: another write time is a mismatch.
        Assert.NotEqual(old, fresh with { LastWriteUtcTicks = fresh.LastWriteUtcTicks + 1 });
        Assert.NotEqual(old, fresh with { EdgeHash = new string('0', 64) });
    }

    [Fact]
    public async Task Fingerprint_of_a_file_smaller_than_the_edge_window_works()
    {
        var empty = await DocumentFingerprint.ComputeAsync(Write("e.pdf", []));
        var small = await DocumentFingerprint.ComputeAsync(Write("s.pdf", Bytes(100)));
        var exact = await DocumentFingerprint.ComputeAsync(Write("x.pdf", Bytes(128 * 1024)));

        Assert.Equal(0, empty.Length);
        Assert.Equal(100, small.Length);
        Assert.Equal(128 * 1024, exact.Length);
        Assert.Matches("^[0-9a-f]{64}$", small.EdgeHash);
        Assert.Matches("^[0-9a-f]{64}$", empty.EdgeHash);
        Assert.NotEqual(empty.EdgeHash, small.EdgeHash);
    }

    [Fact]
    public async Task Fingerprint_does_not_lock_the_file_and_honours_cancellation()
    {
        var path = Write("l.pdf", Bytes(1000));
        using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            await DocumentFingerprint.ComputeAsync(path);
            writer.WriteByte(1);
        }

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DocumentFingerprint.ComputeAsync(path, cts.Token));
    }

    [Fact]
    public async Task Fingerprint_token_round_trips_and_rejects_garbage()
    {
        var f = await DocumentFingerprint.ComputeAsync(Write("t.pdf", Bytes(5000)));
        var large = await DocumentFingerprint.ComputeAsync(Write("l.pdf", Bytes(300_000)));

        Assert.True(DocumentFingerprint.TryParse(f.ToToken(), out var back));
        Assert.Equal(f, back);
        Assert.True(DocumentFingerprint.TryParse(large.ToToken(), out var largeBack));
        Assert.Equal(large.MiddleHash, largeBack.MiddleHash);
        Assert.Equal(large, largeBack);

        string[] bad =
        [
            "", "garbage", "1:2", "a:2:abc", "1:b:abc", "-1:2:" + f.EdgeHash, "1:2:XYZ", "1:2:3:4",
            "1:2:" + f.EdgeHash.ToUpperInvariant(), "1:2:" + f.EdgeHash[..10],
            "1:2:" + f.EdgeHash + ":", "1:2:" + f.EdgeHash + ":XYZ", "1:2:" + f.EdgeHash + ":" + f.EdgeHash + ":" + f.EdgeHash,
        ];
        foreach (var token in bad)
        {
            Assert.False(DocumentFingerprint.TryParse(token, out _), token);
        }

        Assert.False(DocumentFingerprint.TryParse(null!, out _));
    }
}
