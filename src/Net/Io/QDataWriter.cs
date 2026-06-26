using System.Text;

namespace TafClient.Net.Io;

/// <summary>
/// Port of <c>com.faforever.client.remote.io.QDataWriter</c>.
///
/// Wire format for a client message:
///   [int32 block_size]        — total byte count of payload (str_len_field + str_bytes)
///   [int32 str_len]           — byte length of the UTF-16BE string
///   [str_len bytes]           — UTF-16BE encoded JSON
///
/// Corresponds to <c>ServerWriter.write()</c> calling
/// <c>QDataWriter.appendWithSize(byte[])</c> then flush.
/// </summary>
public sealed class QDataWriter : IDisposable
{
    // Java QDataWriter uses UTF_16BE; same as Qt's QString encoding.
    public static readonly Encoding QtStringEncoding = QDataInputStream.QtStringEncoding;

    private readonly Stream _stream;
    private bool _disposed;

    public QDataWriter(Stream stream) => _stream = stream;

    /// <summary>
    /// Writes [int32 length][bytes] and prepends a block-size header.
    /// Mirrors <c>QDataWriter.appendWithSize(byte[])</c> wrapped by
    /// <c>ServerWriter.write()</c> which first serialises to a ByteArrayOutputStream.
    /// </summary>
    public void AppendWithSize(byte[] bytes)
    {
        // block_size = sizeof(length_field) + sizeof(bytes) = 4 + bytes.Length
        WriteInt32BigEndian(4 + bytes.Length);
        WriteInt32BigEndian(bytes.Length);
        _stream.Write(bytes, 0, bytes.Length);
    }

    /// <summary>
    /// Encodes <paramref name="json"/> as UTF-16BE then writes it with the block/string
    /// size headers. Convenience wrapper used by <see cref="ServerWriter"/>.
    /// </summary>
    public void WriteMessage(string json)
    {
        byte[] bytes = QtStringEncoding.GetBytes(json);
        AppendWithSize(bytes);
    }

    public void Flush() => _stream.Flush();

    private void WriteInt32BigEndian(int value)
    {
        Span<byte> buf = stackalloc byte[4];
        buf[0] = (byte)((value >> 24) & 0xFF);
        buf[1] = (byte)((value >> 16) & 0xFF);
        buf[2] = (byte)((value >> 8) & 0xFF);
        buf[3] = (byte)(value & 0xFF);
        _stream.Write(buf);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream.Dispose();
    }
}
