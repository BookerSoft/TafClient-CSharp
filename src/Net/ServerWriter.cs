using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using TafClient.Net.Domain;

namespace TafClient.Net;

/// <summary>
/// Port of QDataStreamProtocol.encode_message() + pack_message() + pack_qstring() + pack_block().
///
/// Exact server Python source:
///
/// pack_qstring(s): struct.pack("!i", len(s.encode("UTF-16BE"))) + s.encode("UTF-16BE")
/// pack_block(b):   struct.pack("!I", len(b)) + b
/// pack_message(*args): pack_block( concat(pack_qstring(a) for a in args) )
/// encode_message(msg):
///   if command == "ping": return PING_MSG  # = pack_message("PING")
///   if command == "pong": return PONG_MSG  # = pack_message("PONG")
///   return pack_message(json.encode(msg))
///
/// So the wire format for any non-ping/pong message is:
///   [uint32 block_len]   = 4 + utf16be_len
///   [uint32 qstr_len]    = utf16be_len
///   [utf16be_len bytes]  = UTF-16BE encoded JSON
///
/// For PING/PONG, the JSON field is replaced with the bare string "PING"/"PONG".
/// </summary>
public sealed class ServerWriter : IDisposable
{
    private readonly ILogger<ServerWriter> _log;
    private Stream? _stream;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    private static readonly Encoding Utf16Be =
        new UnicodeEncoding(bigEndian: true, byteOrderMark: false);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy         = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition       = JsonIgnoreCondition.WhenWritingNull,
    };

    // Pre-built constants matching server PING_MSG / PONG_MSG
    private static readonly byte[] PingBytes = EncodeMessage("PING");
    private static readonly byte[] PongBytes = EncodeMessage("PONG");

    public ServerWriter(ILogger<ServerWriter> log) => _log = log;

    public void SetStream(Stream stream)
    {
        _stream = stream;
    }

    /// <summary>Call when the connection drops so Write returns cleanly instead of throwing.</summary>
    public void ClearStream()
    {
        _stream = null;
    }

    public void Write(ClientMessage message)
    {
        if (message.Command == "ping")      { SendEncoded(PingBytes); return; }
        if (message.Command == "pong")      { SendEncoded(PongBytes); return; }
        WriteJson(message, message.GetType());
    }

    /// <summary>
    /// Sends a JSON message that doesn't fit the ClientMessage hierarchy's shape
    /// (e.g. GameEndedMessage, which mirrors GpgGameMessage's own
    /// {"command","args","target"} format rather than our ClientMessage's
    /// {"command",&lt;fields&gt;} pattern). Same wire framing as Write(ClientMessage).
    /// </summary>
    public void WriteRaw<T>(T message) where T : notnull => WriteJson(message, typeof(T));

    private void WriteJson(object message, Type type)
    {
        string json = JsonSerializer.Serialize(message, type, JsonOpts);
        Console.WriteLine($"[SEND] json={json}");
        SendEncoded(EncodeMessage(json));
    }

    private void SendEncoded(byte[] encoded)
    {
        if (_stream is null)
        {
            _log.LogWarning("Write called before SetStream or after disconnect");
            return;
        }

        _lock.Wait();
        try
        {
            _stream.Write(encoded);
            _stream.Flush();
        }
        catch (ObjectDisposedException)
        {
            _stream = null;   // prevent further writes on dead stream
            throw;
        }
        catch (IOException)
        {
            _stream = null;
            throw;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Produces: [uint32 block_len][uint32 qstr_len][UTF-16BE bytes]
    /// Mirrors: pack_message(s) = pack_block(pack_qstring(s))
    /// </summary>
    private static byte[] EncodeMessage(string s)
    {
        byte[] strBytes = Utf16Be.GetBytes(s);
        // pack_qstring: [int32 len][bytes]
        // pack_block:   [uint32 len][content]
        // total = 4 (block len field) + 4 (qstr len field) + strBytes.Length
        var buf = new byte[8 + strBytes.Length];

        uint blockLen = (uint)(4 + strBytes.Length);
        // block_len (big-endian)
        buf[0] = (byte)((blockLen >> 24) & 0xFF);
        buf[1] = (byte)((blockLen >> 16) & 0xFF);
        buf[2] = (byte)((blockLen >>  8) & 0xFF);
        buf[3] = (byte)( blockLen        & 0xFF);
        // qstr_len (big-endian, signed int32 per pack_qstring "!i")
        uint qstrLen = (uint)strBytes.Length;
        buf[4] = (byte)((qstrLen >> 24) & 0xFF);
        buf[5] = (byte)((qstrLen >> 16) & 0xFF);
        buf[6] = (byte)((qstrLen >>  8) & 0xFF);
        buf[7] = (byte)( qstrLen        & 0xFF);
        // payload
        Buffer.BlockCopy(strBytes, 0, buf, 8, strBytes.Length);
        return buf;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _stream?.Dispose();
    }
}
