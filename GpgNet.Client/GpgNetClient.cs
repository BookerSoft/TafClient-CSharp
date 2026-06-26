using System.Net.Sockets;
using GpgNet.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace GpgNet.Client;

/// <summary>
/// Port of gpgnet::GpgNetClient.
///
/// Connects (as a TCP client) to the GPGNet server at host:port.
/// The server is typically the FAF ICE adapter's GPGNet server.
///
/// On connection it:
///   1. Starts a read loop waiting for server commands
///   2. Dispatches them as C# events (CreateLobby, HostGame, JoinGame, etc.)
///   3. Exposes a GpgNetSender for sending game events back to the server
///
/// Mirrors the Qt signal/slot wiring in GpgNetClient exactly:
///   emit createLobby(...)    → event CreateLobby
///   emit hostGame(...)       → event HostGame
///   emit joinGame(...)       → event JoinGame
///   emit connectToPeer(...)  → event ConnectToPeer
///   emit disconnectFromPeer  → event DisconnectFromPeer
/// </summary>
public sealed class GpgNetClient : IAsyncDisposable
{
    private readonly ILogger<GpgNetClient> _log;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource _cts = new();

    // playerName → playerId map (mirrors m_gpgnetPlayerIds QMap)
    private readonly Dictionary<string, int> _playerIds = new();

    /// <summary>Sender for game events → FAF server.</summary>
    public GpgNetSender? Sender { get; private set; }

    public bool IsConnected => _tcp?.Connected == true;

    // ── Events (mirrors Qt signals) ───────────────────────────────────────────

    public event Action<int, int, string, string, int, int>? CreateLobby;
    // (protocol, localPort, playerAlias, playerRealName, playerId, natTraversal)

    public event Action<string>? HostGame;                              // (mapName)
    public event Action<string, string, string, int>? JoinGame;         // (host, alias, real, id)
    public event Action<string, string, string, int>? ConnectToPeer;    // (host, alias, real, id)
    public event Action<int>? DisconnectFromPeer;                       // (playerId)
    public event Action? Disconnected;

    public GpgNetClient(ILogger<GpgNetClient>? log = null)
        => _log = log ?? NullLogger<GpgNetClient>.Instance;

    // ── Connect ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors GpgNetClient(QString gpgnetHostAndPort) constructor.
    /// Connects synchronously with a 3-second timeout, then starts async read loop.
    /// </summary>
    public async Task ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        _log.LogInformation("[GpgNetClient] Connecting to {Host}:{Port}", host, port);
        // Use TcpClient's own ConnectAsync rather than constructing a raw Socket
        // and assigning it via the Client property afterward. That pattern leaves
        // TcpClient's internal "Active" flag unset (only TcpClient's own connect
        // methods set it), which can make the resulting NetworkStream unreliable
        // for reads even though writes go out fine. This connection is what
        // gpgnet4ta uses to receive HostGame/JoinGame from the ICE adapter — the
        // same bug pattern, fixed identically in IceAdapterRpcClient.cs and
        // FafServerAccessorImpl.cs, was confirmed via correlated logs showing the
        // remote side responding promptly while our own read loop never observed
        // any incoming data at all.
        var tcp = new TcpClient();
        await tcp.ConnectAsync(host, port, ct);
        _tcp    = tcp;
        _stream = _tcp.GetStream();
        Sender  = new GpgNetSender(_stream);
        _log.LogInformation("[GpgNetClient] Connected");

        _ = Task.Run(() => ReadLoopAsync(_cts.Token));
    }

    // ── Read loop — mirrors GpgNetClient::onReadyRead ─────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        _log.LogDebug("[GpgNetClient] Read loop started");
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (cmd, args) = await Wire.ReadMessageAsync(_stream!, ct);
                _log.LogInformation("[GpgNetClient] ← {Cmd} [{Args}]",
                    cmd, string.Join(", ", args));
                Console.WriteLine($"[GPGNET-CLIENT] ← {cmd} [{string.Join(", ", args)}]");
                Dispatch(cmd, args);
            }
        }
        catch (OperationCanceledException) { }
        catch (EndOfStreamException ex)
        {
            _log.LogInformation("[GpgNetClient] Server closed connection: {Msg}", ex.Message);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[GpgNetClient] Read loop error");
            Console.WriteLine($"[GPGNET-CLIENT] Read loop error: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _log.LogInformation("[GpgNetClient] Disconnected");
            Disconnected?.Invoke();
        }
    }

    private void Dispatch(string cmd, List<object> args)
    {
        switch (cmd)
        {
            case ServerMsgIds.CreateLobby:
            {
                var c = CreateLobbyCommand.FromArgs(args);
                _playerIds[c.PlayerAlias] = c.PlayerId;
                CreateLobby?.Invoke(c.Protocol, c.LocalPort, c.PlayerAlias,
                    c.PlayerRealName, c.PlayerId, c.NatTraversal);
                break;
            }
            case ServerMsgIds.HostGame:
            {
                var c = HostGameCommand.FromArgs(args);
                HostGame?.Invoke(c.MapName);
                break;
            }
            case ServerMsgIds.JoinGame:
            {
                var c = JoinGameCommand.FromArgs(args);
                _playerIds[c.RemotePlayerAlias] = c.RemotePlayerId;
                JoinGame?.Invoke(c.RemoteHost, c.RemotePlayerAlias,
                    c.RemotePlayerRealName, c.RemotePlayerId);
                break;
            }
            case ServerMsgIds.ConnectToPeer:
            {
                var c = ConnectToPeerCommand.FromArgs(args);
                _playerIds[c.PlayerAlias] = c.PlayerId;
                ConnectToPeer?.Invoke(c.Host, c.PlayerAlias,
                    c.PlayerRealName, c.PlayerId);
                break;
            }
            case ServerMsgIds.DisconnectFromPeer:
            {
                var c = DisconnectFromPeerCommand.FromArgs(args);
                DisconnectFromPeer?.Invoke(c.PlayerId);
                break;
            }
            case ServerMsgIds.Ping:
                // Nothing to do — gpgnet4ta doesn't respond to Ping
                _log.LogDebug("[GpgNetClient] Ping from server");
                break;
            default:
                _log.LogWarning("[GpgNetClient] Unknown command: {Cmd}", cmd);
                break;
        }
    }

    // ── lookupPlayerId ────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors GpgNetClient::lookupPlayerId().
    /// Returns 0 for AI players (name starts with "AI:") or unknown players.
    /// </summary>
    public int LookupPlayerId(string playerName)
    {
        if (playerName.StartsWith("AI:", StringComparison.OrdinalIgnoreCase)) return 0;
        return _playerIds.TryGetValue(playerName, out int id) ? id : 0;
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _tcp?.Close();
        _tcp = null;
        _cts.Dispose();
    }
}
