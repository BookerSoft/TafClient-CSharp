using System.Text;

namespace TafClient.Net.Io;

/// <summary>
/// Port of com.faforever.client.remote.io.QDataInputStream.
///
/// Wire framing (Qt QDataStream):
///   [int32 block_size, big-endian]   — skipped; tells total payload size
///   [int32 str_len,    big-endian]   — byte count of the UTF-16BE string (-1 = null)
///   [str_len bytes,    UTF-16BE]     — JSON payload
///
/// Changes from earlier version:
///   • Added <paramref name="leaveOpen"/> constructor parameter (default false).
///     The read loop passes leaveOpen:true so disposing the reader does NOT
///     close the underlying NetworkStream and kill the TcpClient.
///   • Added async overloads (SkipBlockSizeAsync / ReadQStringAsync) so the
///     CancellationToken can interrupt a stalled read instead of blocking forever.
/// </summary>
public sealed class QDataInputStream : IDisposable
{
    public static readonly Encoding QtStringEncoding =
        new UnicodeEncoding(bigEndian: true, byteOrderMark: false);

    private readonly Stream _stream;
    private readonly bool   _leaveOpen;
    private bool _disposed;

    public QDataInputStream(Stream stream, bool leaveOpen = false)
    {
        _stream    = stream;
        _leaveOpen = leaveOpen;
    }

    // ── Async API (used by the read loop) ────────────────────────────────────

    public async Task SkipBlockSizeAsync(CancellationToken ct) =>
        await ReadInt32BigEndianAsync(ct);

    public async Task<string?> ReadQStringAsync(CancellationToken ct)
    {
        int stringSize = await ReadInt32BigEndianAsync(ct);
        if (stringSize == -1) return null;
        if (stringSize == 0)  return string.Empty;
        if (stringSize < 0)   throw new InvalidDataException($"Invalid QString length: {stringSize}");

        byte[] buffer = new byte[stringSize];
        int    read   = 0;
        while (read < stringSize)
        {
            int chunk = await _stream.ReadAsync(buffer, read, stringSize - read, ct);
            if (chunk == 0) throw new EndOfStreamException(
                $"Expected {stringSize} bytes for QString, got {read}");
            read += chunk;
        }
        return QtStringEncoding.GetString(buffer);
    }

    // ── Sync API (kept for tests) ─────────────────────────────────────────────

    public void SkipBlockSize() => ReadInt32BigEndian();

    public string? ReadQString()
    {
        int stringSize = ReadInt32BigEndian();
        if (stringSize == -1) return null;
        if (stringSize == 0)  return string.Empty;

        byte[] buffer = new byte[stringSize];
        int    read   = 0;
        while (read < stringSize)
        {
            int chunk = _stream.Read(buffer, read, stringSize - read);
            if (chunk == 0) throw new EndOfStreamException(
                $"Expected {stringSize} bytes for QString, got {read}");
            read += chunk;
        }
        return QtStringEncoding.GetString(buffer);
    }

    public int ReadInt32BigEndian()
    {
        Span<byte> buf = stackalloc byte[4];
        int total = 0;
        while (total < 4)
        {
            int n = _stream.Read(buf[total..]);
            if (n == 0) throw new EndOfStreamException("Stream ended reading int32");
            total += n;
        }
        return (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
    }

    public async Task<int> ReadInt32BigEndianAsync(CancellationToken ct)
    {
        byte[] buf   = new byte[4];
        int    total = 0;
        while (total < 4)
        {
            int n = await _stream.ReadAsync(buf, total, 4 - total, ct);
            if (n == 0) throw new EndOfStreamException("Stream ended reading int32");
            total += n;
        }
        return (buf[0] << 24) | (buf[1] << 16) | (buf[2] << 8) | buf[3];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_leaveOpen) _stream.Dispose();
    }
}
