using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TaLaunch;

// ─── State ────────────────────────────────────────────────────────────────────
// Mirrors LaunchClient's private State enum exactly
public enum LaunchClientState
{
    Connecting,
    Idle,
    Running,   // game process launched, DirectPlay session open
    Launched,  // DPSESSION_JOINDISABLED set → game progressed past lobby
    Fail,
}

// ─── Player status event ──────────────────────────────────────────────────────
// Mirrors the PLAYER_STATUS message from LaunchServer → LaunchClient
public sealed class PlayerStatusEventArgs : EventArgs
{
    /// <summary>allyFlags[i*10+j] — whether player i considers player j an ally</summary>
    public int[] AllyFlags   { get; init; } = new int[100];
    public int[] Actives     { get; init; } = new int[10];
    public int[] UnitCounts  { get; init; } = new int[10];
    public int[] AllyTeams   { get; init; } = new int[10];
    public int[] PropertyMasks { get; init; } = new int[10];
    public int[] DplayIds    { get; init; } = new int[10];
}

// ─── Client ───────────────────────────────────────────────────────────────────

/// <summary>
/// Port of talaunch::LaunchClient.
///
/// Connects to the talauncher LaunchServer over TCP and sends game-launch
/// commands (/host or /join). Receives state update messages back
/// (IDLE, RUNNING, LAUNCHED, FAIL, PLAYER_STATUS ...).
///
/// Wire protocol: plain UTF-8 text, space-separated, newline terminated.
///
/// Commands TO server:
///   /host     {gameId} {guid} {playerName} {ipAddr} {hashEndpoint} {hashToken}
///   /join     {gameId} {guid} {playerName} {ipAddr} {hashEndpoint} {hashToken}
///   /searchjoin {gameId} {guid} {playerName} {ipAddr}
///   /keepalive
///   /failversion {filename} {reason}
///
/// Responses FROM server:
///   IDLE
///   RUNNING
///   LAUNCHED
///   FAIL [exitCodeHex]
///   PLAYER_STATUS {slot0} {slot1} ... {slot9}
///     where each slot = "f0,...,f9:active:unitCount:team:propertyMask:dplayId"
/// </summary>
public sealed class LaunchClient : IAsyncDisposable
{
    private readonly ILogger<LaunchClient> _log;

    private TcpClient?     _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource _cts = new();

    // Serializes every write to talauncher. CRITICAL: talauncher's own
    // onReadyReadTcp (LaunchServer.cpp) has NO message framing whatsoever —
    // it treats every readyRead event as exactly one message, stripping
    // newlines and splitting on spaces with no length/delimiter-based
    // buffering at all. TCP itself has no message boundaries: if our
    // /keepalive (sent every 3s by KeepAliveLoopAsync) and a /host or /join
    // command happen to land in the same read on talauncher's end — entirely
    // possible for two close-together writes over a loopback socket — the
    // combined string becomes garbage like "/keepalive /host 178340 {guid}
    // ..." and talauncher's parser reads args[0] as "/keepalive", silently
    // swallowing the ENTIRE /host command. No error, no log line — exactly
    // matching a launchGame() call that never happens at all, confirmed from
    // a real talauncher log showing the server starting and accepting the
    // connection but never reaching even its own "[LaunchServer::launchGame]
    // host ..." log line, let alone "Initialising JDPlay...". This lock
    // prevents KeepAliveLoopAsync and StartApplicationAsync from ever
    // writing concurrently; pairing it with a brief keepalive pause around
    // the critical send (see StartApplicationAsync) gives the timing gap
    // needed to avoid the two writes coalescing into the same TCP segment.
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public LaunchClientState State { get; private set; } = LaunchClientState.Connecting;

    public bool IsApplicationRunning =>
        State is LaunchClientState.Running or LaunchClientState.Launched;

    public bool IsGameLaunched => State == LaunchClientState.Launched;

    // ── Events ────────────────────────────────────────────────────────────────
    public event EventHandler<PlayerStatusEventArgs>? PlayerStatusReceived;
    public event EventHandler<LaunchClientState>?     StateChanged;

    // ── Game launch parameters (mirrors LaunchClient fields) ──────────────────
    public string  PlayerName                   { get; set; } = "BILLYIDOL";
    public string  GameGuid                     { get; set; } = "{99797420-F5F5-11CF-9827-00A0241496C8}";
    public int     GameId                       { get; set; }
    public string  GameAddress                  { get; set; } = "127.0.0.1";
    public bool    IsHost                       { get; set; } = true;
    public bool    RequireSearch                { get; set; }  // for replay join
    public string  SubmitGameFileHashesEndpoint { get; set; } = "";
    public string  SubmitGameFileHashesToken    { get; set; } = "";

    public LaunchClient(ILogger<LaunchClient>? log = null)
        => _log = log ?? NullLogger<LaunchClient>.Instance;

    // ── Connect ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors LaunchClient constructor's connection retry loop (30 attempts × 1s).
    /// </summary>
    public async Task<bool> ConnectAsync(IPAddress host, int port, CancellationToken ct = default)
    {
        _log.LogInformation("[LaunchClient] Connecting to {Host}:{Port}", host, port);
        for (int attempt = 0; attempt < 30; attempt++)
        {
            try
            {
                // Use TcpClient's own ConnectAsync rather than constructing a raw
                // Socket and assigning it via the Client property afterward — that
                // pattern leaves TcpClient's internal "Active" state unset (only
                // TcpClient's own connect methods set it), which can make the
                // resulting NetworkStream unreliable for reads even though writes
                // succeed normally. See the identical fix in IceAdapterRpcClient.cs
                // for the full diagnosis — this was confirmed as a real bug there
                // via correlated client/server logs showing the remote side
                // responding in under a second while our read loop never observed
                // any incoming data at all.
                var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port, ct);
                _tcp    = tcp;
                _stream = _tcp.GetStream();
                State   = LaunchClientState.Idle;
                _log.LogInformation("[LaunchClient] Connected on attempt {N}", attempt + 1);
                _ = Task.Run(() => ReadLoopAsync(_cts.Token));

                // Start a periodic keepalive loop. talauncher's LaunchServer
                // self-terminates after --keepalivetimeout seconds (default 10)
                // unless it receives a "/keepalive" message, which resets its
                // countdown — see LaunchServer::onReadyReadTcp in the original
                // C++ source. Without this, talauncher would silently die
                // before we ever get around to sending /host or /join if our
                // own launch sequence (DirectPlay registration, ICE adapter
                // connect, etc.) takes longer than that timeout — which it
                // routinely does. Confirmed via a real talauncher log showing
                // exactly this: it accepted our connection, then logged
                // "shutdown counter expired, terminating" ~7s later with no
                // /host command ever having arrived.
                _ = Task.Run(() => KeepAliveLoopAsync(_cts.Token));
                return true;
            }
            catch (SocketException)
            {
                if (attempt < 29) await Task.Delay(1000, ct);
            }
        }
        _log.LogWarning("[LaunchClient] Could not connect after 30 attempts");
        State = LaunchClientState.Fail;
        return false;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Mirrors LaunchClient::startApplication().
    /// Sends /host or /join (or /searchjoin) to the LaunchServer.
    /// </summary>
    public async Task<bool> StartApplicationAsync(CancellationToken ct = default)
    {
        string cmd;
        if (IsHost)
            cmd = "/host";
        else if (RequireSearch)
            cmd = "/searchjoin";
        else
            cmd = "/join";

        string message = string.Join(' ', new[]
        {
            cmd,
            GameId.ToString(),
            GameGuid,
            PlayerName,
            GameAddress,
            SubmitGameFileHashesEndpoint,
            SubmitGameFileHashesToken,
        });

        _log.LogInformation("[LaunchClient] StartApplication: {Cmd}", cmd);
        bool sent = await SendAsync(message, ct);

        // Brief pause after the critical send, still before returning —
        // this doesn't block the keepalive loop's OWN internal 3s wait, but
        // ensures that if the keepalive loop's timer happens to fire right
        // around now, it sees this method (and the _sendLock it shares)
        // already clear, with comfortable separation from when these bytes
        // actually went out — reducing (not eliminating, since TCP timing
        // is never fully deterministic) the chance of the two writes
        // coalescing into the same read on talauncher's end.
        if (sent) await Task.Delay(250, ct);
        return sent;
    }

    /// <summary>
    /// Mirrors LaunchClient::failGameFileVersions(filename, reason).
    /// Tells the LaunchServer the game file version check failed.
    /// </summary>
    public Task<bool> FailVersionAsync(string filename, string reason, CancellationToken ct = default)
        => SendAsync($"/failversion {filename} {reason}", ct);

    /// <summary>Sends a keepalive to reset the LaunchServer's shutdown timer.</summary>
    public Task<bool> KeepAliveAsync(CancellationToken ct = default)
        => SendAsync("/keepalive", ct);

    /// <summary>
    /// Sends "/keepalive" every 3 seconds — comfortably inside talauncher's
    /// default 10-second --keepalivetimeout — for as long as this LaunchClient
    /// is connected. Runs until cancelled (normally via DisposeAsync's _cts).
    /// </summary>
    private async Task KeepAliveLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(3000, ct);
                if (ct.IsCancellationRequested) break;
                bool ok = await KeepAliveAsync(ct);
                if (!ok)
                {
                    _log.LogWarning("[LaunchClient] Keepalive send failed — connection may be down");
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[LaunchClient] Keepalive loop ended unexpectedly");
        }
    }

    private async Task<bool> SendAsync(string message, CancellationToken ct)
    {
        if (_stream is null)
        {
            _log.LogWarning("[LaunchClient] Cannot send — not connected");
            return false;
        }
        await _sendLock.WaitAsync(ct);
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(message);
            await _stream.WriteAsync(bytes, ct);
            await _stream.FlushAsync(ct);
            _log.LogDebug("[LaunchClient] → {Msg}", message);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[LaunchClient] Send failed");
            return false;
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // ── Read loop ─────────────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        _log.LogDebug("[LaunchClient] Read loop started");
        var reader = new StreamReader(_stream!, Encoding.UTF8, leaveOpen: true);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                _log.LogDebug("[LaunchClient] ← {Line}", line);
                ProcessMessage(line.Trim());
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "[LaunchClient] Read loop error"); }
        finally
        {
            SetState(LaunchClientState.Fail);
            _log.LogInformation("[LaunchClient] Read loop ended");
        }
    }

    private void ProcessMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var parts = message.Split(' ');

        switch (parts[0])
        {
            case "IDLE":    SetState(LaunchClientState.Idle);    break;
            case "RUNNING": SetState(LaunchClientState.Running); break;
            case "LAUNCHED":SetState(LaunchClientState.Launched); break;
            case "FAIL":    SetState(LaunchClientState.Fail);    break;

            case "PLAYER_STATUS" when parts.Length >= 11:
                ParsePlayerStatus(parts);
                break;

            default:
                _log.LogDebug("[LaunchClient] Unknown message: {Msg}", message);
                break;
        }
    }

    private void SetState(LaunchClientState s)
    {
        if (State == s) return;
        State = s;
        _log.LogInformation("[LaunchClient] State → {State}", s);
        StateChanged?.Invoke(this, s);
    }

    private void ParsePlayerStatus(string[] parts)
    {
        // Format: PLAYER_STATUS slot0 slot1 ... slot9
        // Each slot: "f0,...,f9:active:unitCount:team:propertyMask:dplayId"
        var e = new PlayerStatusEventArgs();
        for (int i = 0; i < 10 && i + 1 < parts.Length; i++)
        {
            var tok = parts[i + 1].Split(':');
            if (tok.Length < 6) continue;

            var flags = tok[0].Split(',');
            for (int j = 0; j < 10 && j < flags.Length; j++)
                if (int.TryParse(flags[j], out int f)) e.AllyFlags[i * 10 + j] = f;

            if (int.TryParse(tok[1], out int a))  e.Actives[i]      = a;
            if (int.TryParse(tok[2], out int u))  e.UnitCounts[i]   = u;
            if (int.TryParse(tok[3], out int t))  e.AllyTeams[i]    = t;
            if (int.TryParse(tok[4], out int pm)) e.PropertyMasks[i] = pm;
            if (int.TryParse(tok[5], out int d))  e.DplayIds[i]     = d;
        }
        PlayerStatusReceived?.Invoke(this, e);
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _tcp?.Close();
        _cts.Dispose();
        _sendLock.Dispose();
    }
}
