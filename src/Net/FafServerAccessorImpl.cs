using System.Net.Sockets;
using System.Reactive.Subjects;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TafClient.Config;
using TafClient.Domain;
using TafClient.Net.Domain;
using TafClient.Net.Io;

namespace TafClient.Net;

// ─── Interface ────────────────────────────────────────────────────────────────

public interface IFafServerAccessor
{
    IObservable<ConnectionState> ConnectionStateChanged { get; }
    ConnectionState ConnectionState { get; }
    ServerMessageRouter Router { get; }
    Task<LoginMessage> ConnectAndLogIn(string username, string password, CancellationToken ct = default);
    Task<GameLaunchMessage?> RequestHostGame(NewGameInfo info);
    Task<GameLaunchMessage?> RequestJoinGame(int gameId, string? password);

    /// <summary>
    /// Tells the server this game session is over — call whenever the local
    /// launch process fails or the game exits, so the server doesn't leave the
    /// game registered as "launching" indefinitely. Mirrors the real client's
    /// fafService.notifyGameEnded(), called from its startGame() failure handler.
    /// Safe to call even if not connected (silently does nothing in that case).
    /// </summary>
    void NotifyGameEnded();

    /// <summary>
    /// The server-provided text from the most recent "game_join_fail" notice,
    /// if any. Set right before the pending host/join task resolves to null,
    /// so callers can immediately surface the real reason instead of just
    /// "null/cancelled". Cleared at the start of each new host/join request.
    /// </summary>
    string? LastGameLaunchFailReason { get; }
    Task<GameLaunchMessage?> StartSearchMatchmaker();
    void StopSearchMatchmaker();
    void Disconnect();
    void Reconnect();
    void AddFriend(int playerId);
    void AddFoe(int playerId);
    void RemoveFriend(int playerId);
    void RemoveFoe(int playerId);
    void RequestMatchmakerInfo();
    void SendGpgMessage(object message);
    void SendIceMsg(int srcId, int dstId, string candidatesJson);
    void SelectAvatar(string? url);
    void BanPlayer(int playerId, int duration, string periodType, string reason);
    void ClosePlayersGame(int playerId);
    void ClosePlayersLobby(int playerId);
    void BroadcastMessage(string message);
    void RestoreGameSession(int id);
    void Ping(long afkSeconds);
    void GameMatchmaking(string queueName, MatchmakingState state);
    void InviteToParty(int recipientId);
    void AcceptPartyInvite(int senderId);
    void KickPlayerFromParty(int kickedPlayerId);
    void ReadyParty();
    void UnreadyParty();
    void LeaveParty();
    void SetPlayerAlias(string alias);
    void SetGamePassword(string password);
    void SetGameMapDetails(string mapName, string hpiArchive, string crc);
    void UploadReplayToTada(int? replayId);
    List<string> GetLocalIps();
}

// ─── Implementation ───────────────────────────────────────────────────────────

public sealed class FafServerAccessorImpl : IFafServerAccessor, IAsyncDisposable
{
    private readonly ILogger<FafServerAccessorImpl> _log;
    private readonly ServerProperties _server;
    private readonly ServerWriter _writer;
    private readonly ReconnectTimerService _reconnect;

    private readonly BehaviorSubject<ConnectionState> _state = new(ConnectionState.Disconnected);

    private TcpClient?   _tcp;
    private CancellationTokenSource? _cts;
    private bool _stopped;

    private TaskCompletionSource<LoginMessage>?       _loginTcs;
    private TaskCompletionSource<GameLaunchMessage?>? _gameLaunchTcs;
    private long    _sessionId;
    private string? _username;
    private string? _password;
    private List<string> _localIps = [];

    public IObservable<ConnectionState> ConnectionStateChanged => _state;
    public ConnectionState ConnectionState => _state.Value;
    public ServerMessageRouter Router { get; }

    public FafServerAccessorImpl(
        ILogger<FafServerAccessorImpl> log,
        IOptions<ServerProperties> serverOpts,
        ServerWriter writer,
        ServerMessageRouter router,
        ReconnectTimerService reconnect)
    {
        _log      = log;
        _server   = serverOpts.Value;
        _writer   = writer;
        Router    = router;
        _reconnect = reconnect;

        Router.AddListener<NoticeMessage>              ("notice",               OnNotice);
        Router.AddListener<SessionMessage>             ("session",              OnSessionInitiated);
        Router.AddListener<LoginMessage>               ("welcome",              OnFafLoginSucceeded);
        Router.AddListener<GameLaunchMessage>          ("game_launch",          OnGameLaunchInfo);
        Router.AddListener<AuthenticationFailedMessage>("authentication_failed", OnAuthenticationFailed);
    }

    // ── Public: connect and log in ────────────────────────────────────────────

    public async Task<LoginMessage> ConnectAndLogIn(
        string username, string password, CancellationToken ct = default)
    {
        _loginTcs = new TaskCompletionSource<LoginMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _username = username;
        _password = password;
        _stopped  = false;

        // Internal CTS — NOT linked to caller's token so the connection
        // loop lives beyond the login wait.
        _cts = new CancellationTokenSource();
        _ = Task.Run(() => ConnectionLoopAsync(_cts.Token), CancellationToken.None);

        // Wait up to 30 s for the welcome message
        using var to = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            return await _loginTcs.Task.WaitAsync(to.Token);
        }
        catch (OperationCanceledException) when (to.IsCancellationRequested)
        {
            _loginTcs.TrySetException(new LoginFailedException("Login timed out (30 s)"));
            throw new LoginFailedException("Login timed out (30 s)");
        }
    }

    // ── Connection loop ───────────────────────────────────────────────────────

    private async Task ConnectionLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_stopped)
        {
            _state.OnNext(ConnectionState.Connecting);
            _log.LogInformation("[CONN] Connecting to {Host}:{Port}", _server.Host, _server.Port);
            Console.WriteLine($"[CONN] Connecting to {_server.Host}:{_server.Port}");

            // macOS ARM fix: after a failed ConnectAsync the socket is permanently
            // poisoned. We must create a brand-new Socket (not just TcpClient) and
            // explicitly dispose it before each retry. Using AddressFamily.InterNetwork
            // avoids the dual-stack quirk that triggers the exception on macOS 26.
            TcpClient? tcpLocal = null;
            try
            {
                // Connect via TcpClient's own ConnectAsync rather than constructing
                // a raw Socket and assigning it via the Client property afterward.
                // That pattern leaves TcpClient's internal "Active" flag unset
                // (only TcpClient's own connect methods set it), which can make the
                // resulting NetworkStream unreliable for reads even though writes
                // go out fine — confirmed as a real, reproducible bug via the
                // identical pattern in IceAdapterRpcClient.cs (correlated adapter
                // logs showed it responding in under a second while our read loop
                // never observed any incoming data at all). KeepAlive can be set
                // on TcpClient.Client AFTER connecting just as well as before.
                tcpLocal = new TcpClient();
                _tcp = tcpLocal;

                await tcpLocal.ConnectAsync(_server.Host, _server.Port, ct);
                Console.WriteLine("[CONN] TCP connected");

                // KeepAlive via socket option can throw on macOS — set it safely
                try
                {
                    tcpLocal.Client.SetSocketOption(
                        System.Net.Sockets.SocketOptionLevel.Socket,
                        System.Net.Sockets.SocketOptionName.KeepAlive, true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[CONN] KeepAlive not supported: {ex.Message}");
                }

                // Discover local IP in background — does NOT block login
                _ = DiscoverLocalIpsAsync(_tcp).ContinueWith(t =>
                {
                    if (!t.IsFaulted) _localIps = t.Result;
                }, CancellationToken.None);

                var stream = _tcp.GetStream();
                _writer.SetStream(stream);

                _state.OnNext(ConnectionState.Connected);
                _reconnect.ResetConnectionFailures();

                // ── Send ask_session immediately ──────────────────────────
                // Server is deprecated on ask_session but still supports it.
                // We send it first, wait for "session" response, then send "hello".
                var initMsg = new InitSessionMessage(ClientVersion.Current);
                Console.WriteLine($"[CONN] → ask_session version={ClientVersion.Current}");
                _writer.Write(initMsg);

                // ── Read loop ─────────────────────────────────────────────
                await ReadLoopAsync(stream, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                Console.WriteLine($"[CONN] Exception: {ex.GetType().Name}: {ex.Message}");
                _log.LogWarning(ex, "[CONN] Connection error");

                if (_loginTcs is { Task.IsCompleted: false })
                {
                    _loginTcs.TrySetException(
                        new LoginFailedException($"Connection error: {ex.Message}"));
                    _loginTcs = null;
                    _stopped  = true;
                }
                else
                {
                    _reconnect.IncrementConnectionFailures();
                    await _reconnect.WaitForReconnectAsync(ct);
                }
            }
            finally
            {
                _state.OnNext(ConnectionState.Disconnected);

                // Any pending host/join request sent on this connection can never
                // get a real response now — the socket is gone. Without this,
                // RequestHostGame/RequestJoinGame would just sit there until the
                // caller's own 20s WaitAsync timeout fires, with zero indication
                // that the actual cause was "the connection dropped out from under
                // the request", not a slow/unresponsive server.
                if (_gameLaunchTcs is { Task.IsCompleted: false })
                {
                    Console.WriteLine("[CONN] Connection dropped with a pending host/join request — cancelling it now instead of waiting for timeout");
                    LastGameLaunchFailReason = "Connection to the server was lost while waiting for a response.";
                    _gameLaunchTcs.TrySetResult(null);
                    _gameLaunchTcs = null;
                }

                // Null the stream first so any in-flight Write() returns cleanly
                _writer.ClearStream();
                // Explicitly dispose the socket so macOS releases the fd before retry
                try { tcpLocal?.Client?.Dispose(); } catch { }
                try { tcpLocal?.Dispose(); } catch { }
                _tcp = null;
                tcpLocal = null;
            }
        }
        Console.WriteLine("[CONN] Loop exited");
    }

    // ── Read loop — exact port of QDataStreamProtocol.read_message() ─────────
    //
    // Server wire format per message (from server source):
    //   [uint32 block_len, big-endian]     — length of everything that follows
    //   [uint32 qstring_len, big-endian]   — byte length of the UTF-16BE string
    //   [qstring_len bytes, UTF-16BE]      — JSON payload  (or "PING"/"PONG")
    //
    // read_message():
    //   length = readexactly(4)     → block_len
    //   block  = readexactly(length) → qstring_len + qstring_bytes
    //   decode_message(block):
    //     _, action = read_qstring(block)  → reads [4-byte len][len bytes]
    //     if action in ("PING","PONG"): return {"command":"ping"/"pong"}
    //     else: return json.loads(action)

    private async Task ReadLoopAsync(Stream stream, CancellationToken ct)
    {
        Console.WriteLine("[READ] Read loop started");
        try
        {
            while (!ct.IsCancellationRequested && !_stopped)
            {
                // Step 1: read block_len (4 bytes)
                byte[] lenBuf = await ReadExactAsync(stream, 4, ct);
                uint blockLen = (uint)((lenBuf[0] << 24) | (lenBuf[1] << 16) |
                                       (lenBuf[2] <<  8) |  lenBuf[3]);

                Console.WriteLine($"[READ] block_len={blockLen}");

                if (blockLen == 0)
                {
                    Console.WriteLine("[READ] block_len=0, skipping");
                    continue;
                }

                if (blockLen > 4_000_000)
                {
                    Console.WriteLine($"[READ] block_len={blockLen} too large, aborting");
                    break;
                }

                // Step 2: read the whole block
                byte[] block = await ReadExactAsync(stream, (int)blockLen, ct);

                // Step 3: decode — read qstring from start of block
                // read_qstring(block, pos=0):
                //   size = unpack("!I", block[0:4])
                //   return block[4:4+size].decode("UTF-16BE")
                if (block.Length < 4)
                {
                    Console.WriteLine("[READ] block too short for qstring header");
                    continue;
                }

                uint qstrLen = (uint)((block[0] << 24) | (block[1] << 16) |
                                      (block[2] <<  8) |  block[3]);
                Console.WriteLine($"[READ] qstr_len={qstrLen}");

                if (qstrLen > blockLen - 4)
                {
                    Console.WriteLine($"[READ] qstr_len={qstrLen} exceeds block payload");
                    continue;
                }

                string action = Encoding.BigEndianUnicode.GetString(block, 4, (int)qstrLen);
                Console.WriteLine($"[READ] action=[{action}]");

                // Decode exactly as server decode_message() does:
                // if action in ("PING", "PONG"): return {"command": action.lower()}
                string message;
                if (action == "PING" || action == "PONG")
                    message = $"{{\"command\":\"{action.ToLower()}\"}}";
                else
                    message = action;

                // Dispatch
                if (!string.IsNullOrWhiteSpace(message))
                {
                    if (message.Contains("\"command\":\"ping\""))
                    {
                        Console.WriteLine("[READ] Server PING → sending PONG");
                        WriteToServer(new PongMessage());
                    }
                    else if (message.Contains("\"command\":\"pong\""))
                    {
                        Console.WriteLine("[READ] Server PONG");
                    }
                    else
                    {
                        Console.WriteLine($"[READ] Dispatching: {message}");
                        Router.Dispatch(message);
                    }
                }
            }
        }
        catch (OperationCanceledException) { Console.WriteLine("[READ] Cancelled"); }
        catch (EndOfStreamException ex)    { Console.WriteLine($"[READ] EOF: {ex.Message}"); }
        catch (IOException ex)             { Console.WriteLine($"[READ] IO: {ex.Message}"); }
        finally { Console.WriteLine("[READ] Loop ended"); }
    }

    // ── Exact-read helper ─────────────────────────────────────────────────────

    private static async Task<byte[]> ReadExactAsync(Stream stream, int count, CancellationToken ct)
    {
        var buf  = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buf.AsMemory(read, count - read), ct);
            if (n == 0) throw new EndOfStreamException($"EOF after {read}/{count} bytes");
            read += n;
        }
        return buf;
    }

    // ── Listener callbacks ────────────────────────────────────────────────────

    private void OnSessionInitiated(SessionMessage msg)
    {
        Console.WriteLine($"[AUTH] Session id={msg.Session} → sending hello");
        _sessionId = msg.Session;
        SendHello(_username!, _password!);
    }

    private void SendHello(string username, string password)
    {
        string hashedPw = HashPassword(password);
        string uid      = UidServiceHelper.Generate(_sessionId.ToString());
        string localIp  = _localIps.Count > 0 ? _localIps[0] : "127.0.0.1";

        var msg = new LoginClientMessage(username, hashedPw, _sessionId, uid, localIp);
        Console.WriteLine($"[AUTH] → hello login={username} session={_sessionId}");
        WriteToServer(msg);
    }

    private void OnFafLoginSucceeded(LoginMessage msg)
    {
        Console.WriteLine($"[AUTH] Welcome! id={msg.Id} login={msg.Login}");
        _loginTcs?.TrySetResult(msg);
        _loginTcs = null;
    }

    private void OnAuthenticationFailed(AuthenticationFailedMessage msg)
    {
        Console.WriteLine($"[AUTH] FAILED: {msg.Text}");
        _loginTcs?.TrySetException(new LoginFailedException(msg.Text ?? "Authentication failed"));
        _loginTcs = null;
        _stopped  = true;
        _cts?.Cancel();
    }

    public string? LastGameLaunchFailReason { get; private set; }

    private void OnNotice(NoticeMessage msg)
    {
        Console.WriteLine($"[AUTH] Notice style={msg.Style} text={msg.Text}");
        if (msg.Style == "game_join_fail")
        {
            LastGameLaunchFailReason = string.IsNullOrEmpty(msg.Text) ? "(no reason given by server)" : msg.Text;
            Console.WriteLine($"[JOIN] Server sent game_join_fail — resolving pending join/host as null. Reason: {LastGameLaunchFailReason}");
            _gameLaunchTcs?.TrySetResult(null);
            _gameLaunchTcs = null;
        }
    }

    private void OnGameLaunchInfo(GameLaunchMessage msg)
    {
        Console.WriteLine($"[JOIN] game_launch received: uid={msg.Uid} mod={msg.Mod} map={msg.Mapname}");
        _gameLaunchTcs?.TrySetResult(msg);
        _gameLaunchTcs = null;
    }

    // ── Public interface ──────────────────────────────────────────────────────

    public void NotifyGameEnded()
    {
        if (_tcp?.Connected != true)
        {
            Console.WriteLine("[SEND] NotifyGameEnded: not connected — skipping");
            return;
        }
        try
        {
            _writer.WriteRaw(new GameEndedMessage());
            Console.WriteLine("[SEND] Sent GameEnded notification to server");
        }
        catch (Exception ex)
        {
            // This is a best-effort cleanup notification — never let it throw
            // back into a launch-failure handler that's already mid-cleanup.
            Console.WriteLine($"[SEND] NotifyGameEnded failed (non-fatal): {ex.Message}");
        }
    }

    public Task<GameLaunchMessage?> RequestHostGame(NewGameInfo info)
    {
        LastGameLaunchFailReason = null;
        _gameLaunchTcs = new TaskCompletionSource<GameLaunchMessage?>();
        WriteToServer(new HostGameMessage(
            !string.IsNullOrEmpty(info.Password), info.Map, info.Title, [],
            info.FeaturedModTechnicalName, info.Password, info.FeaturedModVersionKey,
            info.Visibility, info.RatingMin, info.RatingMax,
            info.EnforceRatingRange, info.ReplayDelaySeconds,
            info.RatingType, info.GalacticWarPlanetName, info.MaxPlayers));
        return _gameLaunchTcs.Task;
    }

    public Task<GameLaunchMessage?> RequestJoinGame(int gameId, string? password)
    {
        LastGameLaunchFailReason = null;
        _gameLaunchTcs = new TaskCompletionSource<GameLaunchMessage?>();
        WriteToServer(new JoinGameMessage(gameId, password));
        return _gameLaunchTcs.Task;
    }

    public Task<GameLaunchMessage?> StartSearchMatchmaker()
    {
        _gameLaunchTcs = new TaskCompletionSource<GameLaunchMessage?>();
        return _gameLaunchTcs.Task;
    }

    public void StopSearchMatchmaker() { _gameLaunchTcs?.TrySetCanceled(); }
    public void Disconnect()
    {
        _stopped = true;
        _cts?.Cancel();
        if (_gameLaunchTcs is { Task.IsCompleted: false })
        {
            Console.WriteLine("[CONN] Disconnect() called with a pending host/join request — cancelling it now");
            LastGameLaunchFailReason = "Disconnected from the server.";
            _gameLaunchTcs.TrySetResult(null);
            _gameLaunchTcs = null;
        }
        try { _tcp?.Client?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _tcp = null;
    }

    public void Reconnect()
    {
        try { _tcp?.Client?.Dispose(); } catch { }
        try { _tcp?.Dispose(); } catch { }
        _tcp = null;
        _reconnect.SkipWait();
    }
    public void AddFriend(int id)      => WriteToServer(new AddFriendMessage(id));
    public void AddFoe(int id)         => WriteToServer(new AddFoeMessage(id));
    public void RemoveFriend(int id)   => WriteToServer(new RemoveFriendMessage(id));
    public void RemoveFoe(int id)      => WriteToServer(new RemoveFoeMessage(id));
    public void RequestMatchmakerInfo()    => WriteToServer(new MatchmakerInfoMessage());
    public void SendGpgMessage(object message)
    {
        // Port of FafService.sendGpgGameMessage — sends GPGNet message back to FAF lobby server
        // The FAF server uses this to track game state (players, results, etc.)
        if (message is TafClient.Service.GpgGameMessage gpg)
        {
            // Serialize as a JSON message to the server
            var payload = new Dictionary<string, object>
            {
                ["command"] = gpg.Header,
                ["args"]    = gpg.Chunks,
            };
            WriteToServer(new GpgGameClientMessage(gpg.Header, gpg.Chunks));
        }
    }

    public void SendIceMsg(int srcId, int dstId, string candidatesJson)
    {
        // Relay ICE candidates to the remote peer via FAF lobby server
        // The server forwards this to the remote player who passes it to their ice adapter
        WriteToServer(new IceMsgClientMessage(srcId, dstId, candidatesJson));
    }
    public void SelectAvatar(string? url)  => WriteToServer(new SelectAvatarMessage(url));
    public void BanPlayer(int id, int dur, string p, string r) { }
    public void ClosePlayersGame(int id)   { }
    public void ClosePlayersLobby(int id)  { }
    public void BroadcastMessage(string m) { }
    public void RestoreGameSession(int id) => WriteToServer(new RestoreGameSessionMessage(id));
    public void Ping(long afk)             => WriteToServer(new PingMessage(afk));
    public void GameMatchmaking(string q, MatchmakingState s) => WriteToServer(new GameMatchmakingMessage(q, s));
    public void InviteToParty(int id)      => WriteToServer(new InviteToPartyMessage(id));
    public void AcceptPartyInvite(int id)  => WriteToServer(new AcceptPartyInviteMessage(id));
    public void KickPlayerFromParty(int id)=> WriteToServer(new KickPlayerFromPartyMessage(id));
    public void ReadyParty()   => WriteToServer(new ReadyPartyMessage());
    public void UnreadyParty() => WriteToServer(new UnreadyPartyMessage());
    public void LeaveParty()   => WriteToServer(new LeavePartyMessage());
    public void SetPlayerAlias(string a)             => WriteToServer(new SetPlayerAliasMessage(a));
    public void SetGamePassword(string p)            => WriteToServer(new SetGamePasswordMessage(p));
    public void SetGameMapDetails(string m, string h, string c) => WriteToServer(new SetGameMapDetailsMessage(m, h, c));
    public void UploadReplayToTada(int? id)=> WriteToServer(new UploadReplayToTadaMessage(id));
    public List<string> GetLocalIps()      => _localIps;

    private void WriteToServer(ClientMessage message)
    {
        if (_tcp?.Connected != true)
        {
            _log.LogWarning("[SEND] Not connected — dropping {Cmd}", message.Command);
            Console.WriteLine($"[SEND] Not connected — dropping {message.Command}");
            return;
        }
        try
        {
            Console.WriteLine($"[SEND] command={message.Command}");
            _writer.Write(message);
        }
        catch (ObjectDisposedException)
        {
            _log.LogWarning("[SEND] Stream disposed — dropping {Cmd}", message.Command);
            Console.WriteLine($"[SEND] Stream disposed — dropping {message.Command}");
        }
        catch (IOException ex)
        {
            _log.LogWarning(ex, "[SEND] IO error writing {Cmd}", message.Command);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string HashPassword(string password)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(password));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task<List<string>> DiscoverLocalIpsAsync(TcpClient tcp)
    {
        var ips = new List<string>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        foreach (var svc in new[] { "http://checkip.amazonaws.com", "https://api.ipify.org/?format=txt" })
        {
            try
            {
                string ip = (await http.GetStringAsync(svc)).Trim();
                if (System.Net.IPAddress.TryParse(ip, out _)) { ips.Add(ip); break; }
            }
            catch { }
        }
        if (tcp.Client.LocalEndPoint is System.Net.IPEndPoint ep)
            ips.Add(ep.Address.ToString());
        return ips;
    }

    public async ValueTask DisposeAsync()
    {
        Disconnect();
        if (_cts is not null) { await _cts.CancelAsync(); _cts.Dispose(); }
        _tcp?.Dispose();
        _writer.Dispose();
        _state.Dispose();
    }
}

// ─── Support types ─────────────────────────────────────────────────────────────

public class LoginFailedException : Exception
{
    public LoginFailedException(string? message) : base(message) { }
}

public static class UidServiceHelper
{
    public static string Generate(string sessionId) =>
        $"{Environment.MachineName}-placeholder-{sessionId}";
}

public class ReconnectTimerService
{
    private int _failures;
    private readonly SemaphoreSlim _skip = new(0, 1);
    public void ResetConnectionFailures() => _failures = 0;
    public void IncrementConnectionFailures() => _failures++;
    public void SkipWait() { try { _skip.Release(); } catch { } }
    public async Task WaitForReconnectAsync(CancellationToken ct)
    {
        double secs = Math.Min(5 * Math.Pow(2, _failures - 1), 60);
        await Task.WhenAny(
            Task.Delay(TimeSpan.FromSeconds(secs), ct),
            _skip.WaitAsync(ct).ContinueWith(_ => { }));
    }
}

public static class ClientVersion { public static string Current => "2026.1.0"; }
