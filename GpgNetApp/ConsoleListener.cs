using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GpgNetApp;

/// <summary>
/// Port of taflib::ConsoleReader — a plain-text TCP listener that accepts
/// "less-privileged commands" than LaunchServer (talauncher's own port).
/// This is the missing piece behind gpgnet4ta auto-launching the instant
/// HostGame/JoinGame arrived: the real client never auto-launches on its
/// own. It waits, fully staged, until an explicit "/launch" line arrives
/// here — sent by the lobby client when the host clicks "Start" in the
/// staging/lobby room screen.
///
/// Wire format: same as LaunchServer's — a single line of plain text per
/// command, no length prefix, no JSON. Confirmed from the real
/// ConsoleReader::onReadyReadTcp: it just calls sender->readAll() and emits
/// the whole buffer as one message, same "no real framing" caveat that
/// applies to LaunchServer's own protocol (see TaLaunch/LaunchClient.cs's
/// keepalive-coalescing fix for the full explanation of why that matters).
/// Since this listener only needs to receive "/launch" (a short, standalone
/// command with no other traffic expected on this port), the coalescing
/// risk that affected LaunchServer's /keepalive + /host interaction doesn't
/// apply here — there's nothing else being sent on this connection to
/// collide with.
/// </summary>
public sealed class ConsoleListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptLoop;

    public event Action<string>? TextReceived;

    public ConsoleListener(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        _listener.Start();
        Console.WriteLine($"[ConsoleListener] Listening on port {((IPEndPoint)_listener.LocalEndpoint).Port}");
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[ConsoleListener] Accept loop ended: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        Console.WriteLine("[ConsoleListener] Client connected");
        try
        {
            using (client)
            await using (var stream = client.GetStream())
            {
                var buffer = new byte[4096];
                while (!ct.IsCancellationRequested)
                {
                    int n = await stream.ReadAsync(buffer, ct);
                    if (n == 0) break; // peer closed
                    string text = Encoding.UTF8.GetString(buffer, 0, n).Trim();
                    if (text.Length == 0) continue;
                    Console.WriteLine($"[ConsoleListener] received: {text}");
                    TextReceived?.Invoke(text);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine($"[ConsoleListener] Client handler error: {ex.GetType().Name}: {ex.Message}");
        }
        Console.WriteLine("[ConsoleListener] Client disconnected");
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { _listener.Stop(); } catch { }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop; } catch { }
        }
        _cts.Dispose();
    }
}
