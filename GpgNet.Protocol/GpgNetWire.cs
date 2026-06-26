using System.Buffers.Binary;
using System.Text;

namespace GpgNet.Protocol;

// ─── Wire format (LittleEndian QDataStream) ───────────────────────────────────
//
// Port of GpgNetSend.cpp / GpgNetParse.cpp
//
// Both directions:
//   [uint32 LE: str_len][str_len bytes UTF-8]   ← command
//   [uint32 LE: num_args]
//   per arg:
//     [uint8 type]   0=int32, 1=string
//     int:    [uint32 LE value]
//     string: [uint32 LE len][len bytes UTF-8]

public static class Wire
{
    // ── Read ──────────────────────────────────────────────────────────────────

    public static async Task<(string Command, List<object> Args)> ReadMessageAsync(
        Stream s, CancellationToken ct = default)
    {
        string command = await ReadStringAsync(s, ct);
        uint numArgs   = await ReadUInt32Async(s, ct);
        if (numArgs > 10)
            throw new InvalidDataException($"Too many args: {numArgs}");

        var args = new List<object>((int)numArgs);
        for (uint i = 0; i < numArgs; i++)
        {
            byte type = await ReadByteAsync(s, ct);
            if (type == 0)
                args.Add((int)await ReadUInt32Async(s, ct));
            else
                args.Add(await ReadStringAsync(s, ct));
        }
        return (command, args);
    }

    private static async Task<string> ReadStringAsync(Stream s, CancellationToken ct)
    {
        uint len = await ReadUInt32Async(s, ct);
        if (len == 0) return string.Empty;
        if (len > 10_000) throw new InvalidDataException($"String too long: {len}");
        byte[] buf  = new byte[len];
        int    read = 0;
        while (read < (int)len)
        {
            int n = await s.ReadAsync(buf.AsMemory(read), ct);
            if (n == 0) throw new EndOfStreamException();
            read += n;
        }
        return Encoding.UTF8.GetString(buf);
    }

    private static async Task<uint> ReadUInt32Async(Stream s, CancellationToken ct)
    {
        byte[] b = new byte[4]; int r = 0;
        while (r < 4) { int n = await s.ReadAsync(b.AsMemory(r), ct); if (n == 0) throw new EndOfStreamException(); r += n; }
        return BinaryPrimitives.ReadUInt32LittleEndian(b);
    }

    private static async Task<byte> ReadByteAsync(Stream s, CancellationToken ct)
    {
        byte[] b = new byte[1];
        if (await s.ReadAsync(b.AsMemory(), ct) == 0) throw new EndOfStreamException();
        return b[0];
    }

    // ── Write ─────────────────────────────────────────────────────────────────

    private static readonly SemaphoreSlim _wlock = new(1, 1);

    public static async Task WriteMessageAsync(Stream s, string command,
        IReadOnlyList<object> args, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(256);
        WriteString(ms, command);
        WriteUInt32(ms, (uint)args.Count);
        foreach (var arg in args)
            switch (arg)
            {
                case int   i:   ms.WriteByte(0); WriteUInt32(ms, (uint)i); break;
                case long  l:   ms.WriteByte(0); WriteUInt32(ms, (uint)(int)l); break;
                case string str: ms.WriteByte(1); WriteString(ms, str); break;
                default:         ms.WriteByte(1); WriteString(ms, arg?.ToString() ?? ""); break;
            }

        byte[] data = ms.ToArray();
        await _wlock.WaitAsync(ct);
        try { await s.WriteAsync(data, ct); await s.FlushAsync(ct); }
        finally { _wlock.Release(); }
    }

    private static void WriteString(Stream s, string str)
    {
        byte[] b = Encoding.UTF8.GetBytes(str);
        WriteUInt32(s, (uint)b.Length);
        s.Write(b);
    }

    private static void WriteUInt32(Stream s, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        s.Write(b);
    }
}
