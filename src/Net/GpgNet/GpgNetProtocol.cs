using System.Buffers.Binary;
using System.Text;

namespace TafClient.Net.GpgNet;

// ─── Wire format ──────────────────────────────────────────────────────────────
//
// Port of:
//   java-ice-adapter: FaDataInputStream.java / FaDataOutputStream.java
//   gpgnet4ta:        GpgNetParse.cpp / GpgNetSend.cpp
//
// ALL integers are LITTLE-ENDIAN — QDataStream is set LittleEndian in GpgNetClient.
//
// Client → Server (game → adapter / adapter → game):
//   [uint32 LE: str_len] [str_len bytes UTF-8]   ← command header
//   [uint32 LE: num_args]
//   per arg:
//     [uint8 type]   0 = int32,  1 = string
//     if int:    [uint32 LE]
//     if string: [uint32 LE: str_len] [str_len bytes UTF-8]
//
// gpgnet4ta GpgNetSend uses the same format (QDataStream LE):
//   sendCommand(cmd, argCount) → writes [cmd bytes][argCount]
//   sendArgument(QByteArray)   → writes [0x01][len][bytes]
//   sendArgument(int)          → writes [0x00][uint32 LE]

public sealed class GpgNetReader : IDisposable
{
    private readonly Stream _s;
    private bool _disposed;

    public GpgNetReader(Stream stream) => _s = stream;

    // ── Read a full message: returns (command, args) ──────────────────────────

    public async Task<(string Command, List<object> Args)> ReadMessageAsync(CancellationToken ct = default)
    {
        string command = await ReadStringAsync(ct);
        uint   numArgs = await ReadUInt32Async(ct);

        var args = new List<object>((int)numArgs);
        for (uint i = 0; i < numArgs; i++)
        {
            byte type = await ReadByteAsync(ct);
            if (type == 0)          // int
                args.Add((int)await ReadUInt32Async(ct));
            else                    // string (type == 1 or anything else)
                args.Add(await ReadStringAsync(ct));
        }
        return (command, args);
    }

    // ── Primitives ────────────────────────────────────────────────────────────

    private async Task<string> ReadStringAsync(CancellationToken ct)
    {
        uint len = await ReadUInt32Async(ct);
        if (len == 0) return string.Empty;
        if (len > 65536) throw new InvalidDataException($"GPGNet string length {len} too large");

        byte[] buf  = new byte[len];
        int    read = 0;
        while (read < (int)len)
        {
            int n = await _s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException("EOF reading GPGNet string");
            read += n;
        }
        return Encoding.UTF8.GetString(buf);
    }

    private async Task<uint> ReadUInt32Async(CancellationToken ct)
    {
        byte[] buf  = new byte[4];
        int    read = 0;
        while (read < 4)
        {
            int n = await _s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException("EOF reading GPGNet uint32");
            read += n;
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(buf);
    }

    private async Task<byte> ReadByteAsync(CancellationToken ct)
    {
        byte[] buf = new byte[1];
        int    n   = await _s.ReadAsync(buf.AsMemory(0, 1), ct);
        if (n == 0) throw new EndOfStreamException("EOF reading GPGNet byte");
        return buf[0];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Do NOT dispose the underlying stream — caller owns it
    }
}

public sealed class GpgNetWriter
{
    private readonly Stream          _s;
    private readonly SemaphoreSlim   _lock = new(1, 1);

    public GpgNetWriter(Stream stream) => _s = stream;

    // ── Write a full message ──────────────────────────────────────────────────

    public async Task WriteMessageAsync(string command, IReadOnlyList<object> args,
        CancellationToken ct = default)
    {
        // Build into a buffer to send atomically
        using var ms = new MemoryStream(256);
        WriteString(ms, command);
        WriteUInt32(ms, (uint)args.Count);

        foreach (var arg in args)
        {
            switch (arg)
            {
                case int i:
                    ms.WriteByte(0);
                    WriteUInt32(ms, (uint)i);
                    break;
                case long l:
                    ms.WriteByte(0);
                    WriteUInt32(ms, (uint)(int)l);
                    break;
                case string str:
                    ms.WriteByte(1);
                    WriteString(ms, str);
                    break;
                default:
                    ms.WriteByte(1);
                    WriteString(ms, arg?.ToString() ?? string.Empty);
                    break;
            }
        }

        byte[] data = ms.ToArray();

        await _lock.WaitAsync(ct);
        try
        {
            await _s.WriteAsync(data, ct);
            await _s.FlushAsync(ct);
        }
        finally { _lock.Release(); }
    }

    // ── Convenience overloads ─────────────────────────────────────────────────

    public Task WriteAsync(string cmd, CancellationToken ct = default)
        => WriteMessageAsync(cmd, [], ct);

    public Task WriteAsync(string cmd, object arg1, CancellationToken ct = default)
        => WriteMessageAsync(cmd, [arg1], ct);

    public Task WriteAsync(string cmd, object arg1, object arg2, CancellationToken ct = default)
        => WriteMessageAsync(cmd, [arg1, arg2], ct);

    public Task WriteAsync(string cmd, object arg1, object arg2, object arg3, CancellationToken ct = default)
        => WriteMessageAsync(cmd, [arg1, arg2, arg3], ct);

    // ── Primitives ────────────────────────────────────────────────────────────

    private static void WriteString(Stream s, string str)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(str);
        WriteUInt32(s, (uint)bytes.Length);
        s.Write(bytes);
    }

    private static void WriteUInt32(Stream s, uint v)
    {
        Span<byte> buf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, v);
        s.Write(buf);
    }
}

// ─── Message types ─────────────────────────────────────────────────────────────
// Commands the server (ICE adapter / TAF lobby) sends TO gpgnet4ta:

public record CreateLobbyCommand(int Protocol, int LocalPort, string PlayerName,
                                 int PlayerId, int NatTraversal)
{
    public const string Id = "CreateLobby";
    public static CreateLobbyCommand From(List<object> args) => new(
        (int)args[0], (int)args[1], (string)args[2], (int)args[3], (int)args[4]);
}

public record HostGameCommand(string MapName)
{
    public const string Id = "HostGame";
    public static HostGameCommand From(List<object> args) => new((string)args[0]);
}

public record JoinGameCommand(string RemoteHost, string RemotePlayerName, int RemotePlayerId)
{
    public const string Id = "JoinGame";
    public static JoinGameCommand From(List<object> args) => new(
        (string)args[0], (string)args[1], (int)args[2]);
}

public record ConnectToPeerCommand(string Host, string PlayerName, int PlayerId)
{
    public const string Id = "ConnectToPeer";
    public static ConnectToPeerCommand From(List<object> args) => new(
        (string)args[0], (string)args[1], (int)args[2]);
}

public record DisconnectFromPeerCommand(int PlayerId)
{
    public const string Id = "DisconnectFromPeer";
    public static DisconnectFromPeerCommand From(List<object> args) => new((int)args[0]);
}

// Commands gpgnet4ta sends TO us (game events):
// GameState(state, substate), PlayerOption(id, key, value), AIOption(name, key, value)
// GameOption(key, value), GameResult(army, scoreStr), GameEnded(), GameMetrics(key, value)
