using System.Net;
using System.Net.Sockets;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;

namespace TafClient.Net.GpgNet;

/// <summary>
/// Port of com.faforever.iceadapter.gpgnet.GPGNetServer.
///
/// Listens on a free TCP port. gpgnet4ta connects to it as a client.
/// Once connected, relays GPGNet messages:
///   gpgnet4ta → us  (GameState, PlayerOption, GameResult, GameEnded, etc.)
///   us → gpgnet4ta  (CreateLobby, HostGame, JoinGame, ConnectToPeer, etc.)
///
/// The ICE adapter also acts as its own GPGNet server; when using the real
/// faf-ice-adapter the adapter opens GPGNET_PORT and gpgnet4ta connects there.
/// In our C# port we open our own GPGNet server for when faf-ice-adapter is NOT
/// used (direct integration path) and we forward messages ourselves.
/// </summary>
public sealed class GpgNetServer : IAsyncDisposable
{
    private readonly ILogger<GpgNetServer> _log;

    private TcpListener?   _listener;
    private TcpClient?     _client;
    private GpgNetWriter?  _writer;
    private CancellationTokenSource _cts = new();

    // Events raised when gpgnet4ta sends us messages
    public event Action<string, List<object>>? MessageReceived;
    public event Action?                       Connected;
    public event Action?                       Disconnected;

    public bool IsConnected => _client?.Connected == true;
    public int  Port        { get; private set; }

    // Observable game state string ("Idle", "Lobby", "Launching", "Live", "Ended")
    private readonly Subject<string> _gameState = new();
    public IObservable<string> GameStateChanged => _gameState;
    public string CurrentGameState { get; private set; } = "None";

    public GpgNetServer(ILogger<GpgNetServer> log) => _log = log;

    // ── Start listening ───────────────────────────────────────────────────────

    public void Start(int port = 0)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _log.LogInformation("[GpgNetServer] Listening on port {Port}", Port);
        Console.WriteLine($"[GPGNET] Server listening on port {Port}");
        _ = Task.Run(AcceptLoopAsync);
    }

    // ── Accept loop ───────────────────────────────────────────────────────────

    private async Task AcceptLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var incoming = await _listener!.AcceptTcpClientAsync(_cts.Token);
                Console.WriteLine($"[GPGNET] gpgnet4ta connected from {incoming.Client.RemoteEndPoint}");
                _log.LogInformation("[GpgNetServer] gpgnet4ta connected");

                // Close previous connection if any
                await DisconnectClientAsync();

                _client = incoming;
                _writer = new GpgNetWriter(_client.GetStream());

                Connected?.Invoke();
                _ = Task.Run(() => ReadLoopAsync(_client, _cts.Token));
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[GpgNetServer] Accept error");
                await Task.Delay(1000);
            }
        }
    }

    // ── Read loop — mirrors GPGNetClient.listenerThread() ────────────────────

    private async Task ReadLoopAsync(TcpClient client, CancellationToken ct)
    {
        _log.LogDebug("[GpgNetServer] Read loop started");
        using var reader = new GpgNetReader(client.GetStream());
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (command, args) = await reader.ReadMessageAsync(ct);
                Console.WriteLine($"[GPGNET] ← {command} [{string.Join(", ", args)}]");
                _log.LogInformation("[GpgNetServer] ← {Cmd} {Args}", command, string.Join(", ", args));

                // Handle GameState internally (mirrors Java switch on "GameState")
                if (command == "GameState" && args.Count > 0)
                {
                    string state = args[0].ToString()!;
                    CurrentGameState = state;
                    _gameState.OnNext(state);
                    _log.LogInformation("[GpgNetServer] GameState = {State}", state);
                }

                MessageReceived?.Invoke(command, args);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException ex)
        {
            _log.LogInformation("[GpgNetServer] gpgnet4ta disconnected: {Msg}", ex.Message);
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "[GpgNetServer] IO error reading from gpgnet4ta");
        }
        finally
        {
            Console.WriteLine("[GPGNET] Read loop ended");
            Disconnected?.Invoke();
        }
    }

    // ── Send methods (to gpgnet4ta) ───────────────────────────────────────────

    /// <summary>
    /// Sends CreateLobby to gpgnet4ta.
    /// Mirrors: gpgNetClient.sendGpgnetMessage("CreateLobby", lobbyInitMode.getId(),
    ///          LOBBY_PORT, login, id, 1)
    /// </summary>
    public Task SendCreateLobbyAsync(int protocol, int lobbyPort, string playerLogin,
        int playerId, int natTraversal = 1, CancellationToken ct = default)
        => SendAsync("CreateLobby", [protocol, lobbyPort, playerLogin, playerId, natTraversal], ct);

    /// <summary>Mirrors: gpgNetClient.sendGpgnetMessage("HostGame", mapName)</summary>
    public Task SendHostGameAsync(string mapName, CancellationToken ct = default)
        => SendAsync("HostGame", [mapName], ct);

    /// <summary>Mirrors: gpgNetClient.sendGpgnetMessage("JoinGame", "127.0.0.1:"+port, login, id)</summary>
    public Task SendJoinGameAsync(string hostAndPort, string playerLogin,
        int playerId, CancellationToken ct = default)
        => SendAsync("JoinGame", [hostAndPort, playerLogin, playerId], ct);

    /// <summary>Mirrors: gpgNetClient.sendGpgnetMessage("ConnectToPeer", "127.0.0.1:"+port, login, id)</summary>
    public Task SendConnectToPeerAsync(string hostAndPort, string playerLogin,
        int playerId, CancellationToken ct = default)
        => SendAsync("ConnectToPeer", [hostAndPort, playerLogin, playerId], ct);

    /// <summary>Mirrors: gpgNetClient.sendGpgnetMessage("DisconnectFromPeer", playerId)</summary>
    public Task SendDisconnectFromPeerAsync(int playerId, CancellationToken ct = default)
        => SendAsync("DisconnectFromPeer", [playerId], ct);

    private async Task SendAsync(string command, List<object> args, CancellationToken ct)
    {
        if (_writer is null)
        {
            _log.LogWarning("[GpgNetServer] Cannot send {Cmd} — no client connected", command);
            return;
        }
        Console.WriteLine($"[GPGNET] → {command} [{string.Join(", ", args)}]");
        _log.LogInformation("[GpgNetServer] → {Cmd} {Args}", command, string.Join(", ", args));
        await _writer.WriteMessageAsync(command, args, ct);
    }

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    private async Task DisconnectClientAsync()
    {
        if (_client is null) return;
        try { _client.Close(); } catch { }
        _client  = null;
        _writer  = null;
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        await DisconnectClientAsync();
        _listener?.Stop();
        _gameState.Dispose();
        _cts.Dispose();
        _log.LogInformation("[GpgNetServer] Disposed");
    }
}
