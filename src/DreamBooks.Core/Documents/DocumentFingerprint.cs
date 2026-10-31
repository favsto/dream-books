using System.Globalization;
using System.Security.Cryptography;

namespace DreamBooks.Core.Documents;

/// <summary>
/// Cheap identity of a file on disk: length, last write time, a SHA-256 of its first and last 64 KB and, for files
/// over 128 KB, a SHA-256 of their middle: whole up to 64 KB, otherwise 16 evenly spaced 4 KB blocks of it.
/// Used to detect that the original changed since it was opened (Save) or since a recovery journal was written.
/// </summary>
/// <remarks>
/// <para>A change that keeps the length and the last write time and falls between two sampled blocks of a middle
/// over 64 KB is not seen: the price of not reading the whole file.</para>
/// <para><see cref="MiddleHash"/> is null for files up to 128 KB (the edges cover them) and in tokens written before
/// it existed. Equality compares it only when both sides have one, so a journal header written by an older version
/// still matches the unchanged file after an upgrade.</para>
/// </remarks>
public readonly record struct DocumentFingerprint(long Length, long LastWriteUtcTicks, string EdgeHash, string? MiddleHash = null)
{
    private const int _edgeBytes = 64 * 1024;
    private const int _sampleBlocks = 16;
    private const int _sampleBlockBytes = 4 * 1024;

    public static async Task<DocumentFingerprint> ComputeAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ct.ThrowIfCancellationRequested();

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            useAsync: true);

        var length = stream.Length;
        var lastWrite = File.GetLastWriteTimeUtc(path).Ticks;

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[_edgeBytes];

        if (length <= 2L * _edgeBytes)
        {
            await HashRangeAsync(stream, hash, buffer, 0, (int)length, ct);
            return new DocumentFingerprint(length, lastWrite, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }

        await HashRangeAsync(stream, hash, buffer, 0, _edgeBytes, ct);
        await HashRangeAsync(stream, hash, buffer, length - _edgeBytes, _edgeBytes, ct);
        var edgeHash = Convert.ToHexStringLower(hash.GetHashAndReset());

        var middleStart = (long)_edgeBytes;
        var middleLength = length - (2L * _edgeBytes);
        if (middleLength <= (long)_sampleBlocks * _sampleBlockBytes)
        {
            await HashRangeAsync(stream, hash, buffer, middleStart, (int)middleLength, ct);
        }
        else
        {
            // The first block starts where the head ends, the last one ends where the tail starts.
            var span = middleLength - _sampleBlockBytes;
            for (var i = 0; i < _sampleBlocks; i++)
            {
                var offset = middleStart + (span * i / (_sampleBlocks - 1));
                await HashRangeAsync(stream, hash, buffer, offset, _sampleBlockBytes, ct);
            }
        }

        return new DocumentFingerprint(length, lastWrite, edgeHash, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    /// <summary>Same length, write time and edges; the middle too when both sides have its hash.</summary>
    public bool Equals(DocumentFingerprint other) =>
        Length == other.Length
        && LastWriteUtcTicks == other.LastWriteUtcTicks
        && string.Equals(EdgeHash, other.EdgeHash, StringComparison.Ordinal)
        && (MiddleHash is null || other.MiddleHash is null || string.Equals(MiddleHash, other.MiddleHash, StringComparison.Ordinal));

    /// <summary>Leaves <see cref="MiddleHash"/> out, as <see cref="Equals(DocumentFingerprint)"/> may.</summary>
    public override int GetHashCode() => HashCode.Combine(Length, LastWriteUtcTicks, EdgeHash);

    /// <summary><c>length:ticks:edges</c>, followed by <c>:middle</c> when there is a middle hash.</summary>
    public string ToToken()
        => MiddleHash is null
            ? string.Create(CultureInfo.InvariantCulture, $"{Length}:{LastWriteUtcTicks}:{EdgeHash}")
            : string.Create(CultureInfo.InvariantCulture, $"{Length}:{LastWriteUtcTicks}:{EdgeHash}:{MiddleHash}");

    /// <summary>Reads a token of either form (with or without the middle hash).</summary>
    public static bool TryParse(string token, out DocumentFingerprint fingerprint)
    {
        fingerprint = default;
        if (token is null)
        {
            return false;
        }

        var parts = token.Split(':');
        if (parts.Length is not (3 or 4)
            || !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var length)
            || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
            || !IsLowerHexSha256(parts[2])
            || (parts.Length == 4 && !IsLowerHexSha256(parts[3])))
        {
            return false;
        }

        fingerprint = new DocumentFingerprint(length, ticks, parts[2], parts.Length == 4 ? parts[3] : null);
        return true;
    }

    private static bool IsLowerHexSha256(string s)
        => s.Length == 64 && s.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static async Task HashRangeAsync(FileStream stream, IncrementalHash hash, byte[] buffer, long offset, int count, CancellationToken ct)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var remaining = count;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(remaining, buffer.Length)), ct);
            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
    }
}
