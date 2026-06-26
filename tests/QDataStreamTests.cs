using System.Text;
using FluentAssertions;
using TafClient.Net.Io;

namespace TafClient.Tests;

/// <summary>
/// Tests for <see cref="QDataInputStream"/> and <see cref="QDataWriter"/>.
///
/// The Qt QDataStream wire format for a QString is:
///   [int32 string_byte_length, big-endian] [string_byte_length bytes UTF-16BE]
///
/// When wrapped by ServerWriter.write() / QDataWriter.appendWithSize():
///   [int32 block_size = 4 + str_len, big-endian]
///   [int32 str_len, big-endian]
///   [str_len bytes UTF-16BE]
/// </summary>
public class QDataStreamTests
{
    private static byte[] BigEndianInt32(int value) =>
    [
        (byte)((value >> 24) & 0xFF),
        (byte)((value >> 16) & 0xFF),
        (byte)((value >>  8) & 0xFF),
        (byte)(value         & 0xFF),
    ];

    // ─── QDataInputStream ────────────────────────────────────────────────────

    [Fact]
    public void ReadQString_ReturnsCorrectString()
    {
        const string text = "hello";
        byte[] utf16be = QDataInputStream.QtStringEncoding.GetBytes(text);

        using var ms = BuildQStream(utf16be);
        using var reader = new QDataInputStream(ms);

        reader.SkipBlockSize();
        string? result = reader.ReadQString();

        result.Should().Be(text);
    }

    [Fact]
    public void ReadQString_ReturnsNullForMinusOne()
    {
        // length field = -1 → null QString
        var bytes = new List<byte>();
        bytes.AddRange(BigEndianInt32(4));   // block size = 4 (just the length field)
        bytes.AddRange(BigEndianInt32(-1));  // str length = -1 (null)

        using var ms = new MemoryStream([.. bytes]);
        using var reader = new QDataInputStream(ms);

        reader.SkipBlockSize();
        string? result = reader.ReadQString();

        result.Should().BeNull();
    }

    [Fact]
    public void ReadQString_DecodesJsonCorrectly()
    {
        const string json = """{"command":"session","session":12345}""";
        byte[] utf16be = QDataInputStream.QtStringEncoding.GetBytes(json);

        using var ms = BuildQStream(utf16be);
        using var reader = new QDataInputStream(ms);

        reader.SkipBlockSize();
        string? result = reader.ReadQString();

        result.Should().Be(json);
    }

    // ─── QDataWriter ──────────────────────────────────────────────────────────

    [Fact]
    public void WriteMessage_ProducesReadableStream()
    {
        const string json = """{"command":"ask_session","version":"1.0.0"}""";

        using var ms = new MemoryStream();
        using var writer = new QDataWriter(ms);
        writer.WriteMessage(json);
        writer.Flush();

        ms.Position = 0;
        using var reader = new QDataInputStream(ms);
        reader.SkipBlockSize();
        string? result = reader.ReadQString();

        result.Should().Be(json);
    }

    [Fact]
    public void WriteMessage_BlockSizeIsCorrect()
    {
        const string text = "ab"; // 2 chars × 2 bytes UTF-16BE = 4 bytes
        byte[] expected = QDataInputStream.QtStringEncoding.GetBytes(text);

        using var ms = new MemoryStream();
        using var writer = new QDataWriter(ms);
        writer.WriteMessage(text);
        writer.Flush();

        ms.Position = 0;
        using var reader = new QDataInputStream(ms);
        // Block size should be 4 (length field) + 4 (4 bytes UTF16-BE)
        int blockSize = reader.ReadInt32BigEndian();
        blockSize.Should().Be(4 + expected.Length);
    }

    [Fact]
    public void RoundTrip_MultipleMessages()
    {
        var messages = new[] { """{"command":"ping"}""", """{"command":"game_info","uid":1}""" };

        using var ms = new MemoryStream();
        using var writer = new QDataWriter(ms);
        foreach (var m in messages)
        {
            writer.WriteMessage(m);
        }
        writer.Flush();

        ms.Position = 0;
        using var reader = new QDataInputStream(ms);
        foreach (var expected in messages)
        {
            reader.SkipBlockSize();
            string? actual = reader.ReadQString();
            actual.Should().Be(expected);
        }
    }

    // ─── Helper ───────────────────────────────────────────────────────────────

    private static MemoryStream BuildQStream(byte[] utf16beBytes)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BigEndianInt32(4 + utf16beBytes.Length)); // block size
        bytes.AddRange(BigEndianInt32(utf16beBytes.Length));     // string length
        bytes.AddRange(utf16beBytes);
        return new MemoryStream([.. bytes]);
    }
}
