using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace TafClient.Net.Ice;

/// <summary>
/// JSON-RPC 2.0 TCP client that connects to the faf-ice-adapter process.
///
/// Port of com.faforever.client.fa.relay.ice.IceAdapterImpl — specifically the
/// TcpClient / JJsonPeer usage (nbarraille/jjsonrpc library in Java).
///
/// Protocol: newline-delimited JSON-RPC 2.0 over plain TCP.
///
/// Calls FROM us TO adapter (RPC methods):
///   hostGame(mapName)
///   joinGame(remotePlayerLogin, remotePlayerId)
///   connectToPeer(remotePlayerLogin, remotePlayerId, offer)
///   disconnectFromPeer(remotePlayerId)
///   iceMsg(remotePlayerId, candidatesJson)
///   setIceServers([{urls, username, credential}])
///   setLobbyInitMode(mode)   // "normal" or "auto"
///   status()
///   quit()
///
/// Notifications FROM adapter TO us (events):
///   onConnectionStateChanged(state)
///   onGpgNetMessageReceived(header, chunks)  ← relay these to FAF lobby server
///   onIceMsg(srcId, destId, candidatesJson)  ← relay to remote peer via FAF lobby
///   onIceConnectionStateChanged(localId, remoteId, state)
///   onConnected(localId, remoteId, connected)
/// </summary>
public sealed class IceAdapterRpcClient : IAsyncDisposable
{
    private readonly ILogger<IceAdapterRpcClient> _log;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource _cts = new();
    private int _nextId = 1;

    // Pending RPC call completions keyed by id
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();

    // ── Events from adapter to us ─────────────────────────────────────────────
    public event Action<string>?                        OnConnectionStateChanged;
    public event Action<string, List<object>>?          OnGpgNetMessageReceived;
    public event Action<int, int, string>?              OnIceMsg;
    public event Action<int, int, string>?              OnIceConnectionStateChanged;
    public event Action<int, int, bool>?                OnConnected;

    public bool IsConnected => _tcp?.Connected == true;

    public IceAdapterRpcClient(ILogger<IceAdapterRpcClient> log) => _log = log;

    // ── Connect to running ice adapter ────────────────────────────────────────

    public async Task ConnectAsync(int rpcPort, CancellationToken ct = default)
    {
        _log.LogInformation("[ICE-RPC] Connecting to ice adapter on port {Port}", rpcPort);
        Console.WriteLine($"[ICE-RPC] Connecting on port {rpcPort}");

        // Retry loop — adapter takes a moment to start listening
        // Mirrors: IceAdapterImpl CONNECTION_ATTEMPTS=100, DELAY=200ms
        for (int attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                // IMPORTANT: connect via TcpClient's own ConnectAsync, not by
                // creating a raw Socket and assigning it to TcpClient.Client
                // afterward. The old pattern (`new TcpClient { Client = sock }`)
                // leaves TcpClient's internal "Active" flag unset, since that
                // flag is only set by TcpClient's own connect methods — never by
                // the Client property setter. GetStream() doesn't hard-fail in
                // that state, but the resulting NetworkStream can behave
                // unreliably for reads: writes go out fine (confirmed by the ICE
                // adapter actually receiving and responding to our calls), but
                // incoming data is never observed by ReadLineAsync, which is
                // exactly the symptom seen — the adapter responds in under a
                // second, our read loop never logs receiving anything, and the
                // call times out regardless.
                var tcp = new TcpClient();
                await tcp.ConnectAsync("127.0.0.1", rpcPort, ct);
                _tcp    = tcp;
                _stream = _tcp.GetStream();
                _ = Task.Run(() => ReadLoopAsync(_cts.Token));
                _log.LogInformation("[ICE-RPC] Connected on attempt {N}", attempt + 1);
                Console.WriteLine($"[ICE-RPC] Connected on attempt {attempt + 1}");
                return;
            }
            catch (SocketException) when (attempt < 99)
            {
                await Task.Delay(200, ct);
            }
        }
        throw new InvalidOperationException($"Could not connect to ice adapter on port {rpcPort} after 100 attempts");
    }

    // ── JSON-RPC read loop ────────────────────────────────────────────────────

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        _log.LogDebug("[ICE-RPC] Read loop started");
        using var reader = new System.IO.StreamReader(_stream!, Encoding.UTF8, leaveOpen: true);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                _log.LogDebug("[ICE-RPC] ← {Line}", line);
                Console.WriteLine($"[ICE-RPC] ← {line}");

                try { ProcessMessage(line); }
                catch (Exception ex) { _log.LogWarning(ex, "[ICE-RPC] Error processing message"); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "[ICE-RPC] Read loop error"); }
        finally { _log.LogInformation("[ICE-RPC] Read loop ended"); }
    }

    private void ProcessMessage(string json)
    {
        var node = JsonNode.Parse(json);
        if (node is not JsonObject obj) return;

        // ── Response to a call we made ────────────────────────────────────────
        // IMPORTANT: System.Text.Json.Nodes' indexer returns C# null both when a
        // key is absent AND when a key is present with a JSON `null` value —
        // there is no separate "JsonNode wrapping null" the way Newtonsoft's
        // JValue works. The ICE adapter's JSON-RPC responses for void-returning
        // methods (e.g. setLobbyInitMode, hostGame) are exactly
        // {"result":null,"id":N,"jsonrpc":"2.0"} — a real, valid response that
        // must resolve the pending call. The old check `node["result"] is not
        // null || node["error"] is not null` evaluated false for this shape
        // (since both indexers return null), so every such response silently
        // fell through to the notification-handling code below, found no
        // "method" key, and returned with zero logging — exactly matching the
        // observed symptom: the adapter responds in milliseconds, but the call
        // always times out after the full 10s with no error anywhere.
        // ContainsKey checks PRESENCE, not value-nullness, so it correctly
        // identifies this as a response regardless of whether result is null,
        // a value, or absent in favor of "error".
        bool isResponse = obj.ContainsKey("result") || obj.ContainsKey("error");
        if (isResponse)
        {
            if (node["id"]?.GetValue<int>() is int id && _pending.TryRemove(id, out var tcs))
            {
                if (obj.ContainsKey("error") && node["error"] is JsonNode err)
                    tcs.TrySetException(new InvalidOperationException(err.ToJsonString()));
                else
                    tcs.TrySetResult(node["result"]); // may legitimately be null
            }
            else
            {
                _log.LogWarning("[ICE-RPC] Received response for unknown/already-resolved id: {Json}", json);
            }
            return;
        }

        // ── Notification from adapter ─────────────────────────────────────────
        string? method = node["method"]?.GetValue<string>();
        var     parms  = node["params"]?.AsArray();
        if (method is null) return;

        _log.LogInformation("[ICE-RPC] notification: {Method}", method);

        switch (method)
        {
            case "onConnectionStateChanged":
                OnConnectionStateChanged?.Invoke(parms?[0]?.GetValue<string>() ?? "");
                break;

            case "onGpgNetMessageReceived":
                string header = parms?[0]?.GetValue<string>() ?? "";
                var chunks = new List<object>();
                if (parms?.Count > 1 && parms[1] is JsonArray arr)
                {
                    foreach (var el in arr)
                    {
                        if (el is JsonValue jv)
                        {
                            if (jv.TryGetValue<int>(out int iv)) chunks.Add(iv);
                            else chunks.Add(jv.GetValue<string>());
                        }
                    }
                }
                OnGpgNetMessageReceived?.Invoke(header, chunks);
                break;

            case "onIceMsg":
                int srcId  = parms?[0]?.GetValue<int>()    ?? 0;
                int dstId  = parms?[1]?.GetValue<int>()    ?? 0;
                string msg = parms?[2]?.GetValue<string>() ?? "";
                OnIceMsg?.Invoke(srcId, dstId, msg);
                break;

            case "onIceConnectionStateChanged":
                int lid1  = parms?[0]?.GetValue<int>()    ?? 0;
                int rid1  = parms?[1]?.GetValue<int>()    ?? 0;
                string st = parms?[2]?.GetValue<string>() ?? "";
                OnIceConnectionStateChanged?.Invoke(lid1, rid1, st);
                break;

            case "onConnected":
                int lid2   = parms?[0]?.GetValue<int>()    ?? 0;
                int rid2   = parms?[1]?.GetValue<int>()    ?? 0;
                bool conn  = parms?[2]?.GetValue<bool>()   ?? false;
                OnConnected?.Invoke(lid2, rid2, conn);
                break;

            default:
                _log.LogDebug("[ICE-RPC] Unhandled notification: {Method}", method);
                break;
        }
    }

    // ── RPC call methods ──────────────────────────────────────────────────────

    /// <summary>Mirrors: iceAdapterProxy.hostGame(mapName)</summary>
    public Task<JsonNode?> HostGameAsync(string mapName, CancellationToken ct = default)
        => CallAsync("hostGame", [mapName], ct, timeoutSeconds: 30);

    /// <summary>Mirrors: iceAdapterProxy.joinGame(login, playerId)</summary>
    public Task<JsonNode?> JoinGameAsync(string remoteLogin, int remoteId, CancellationToken ct = default)
        => CallAsync("joinGame", [remoteLogin, remoteId], ct, timeoutSeconds: 30);

    /// <summary>Mirrors: iceAdapterProxy.connectToPeer(login, playerId, offer)</summary>
    public Task<JsonNode?> ConnectToPeerAsync(string remoteLogin, int remoteId, bool offer,
        CancellationToken ct = default)
        => CallAsync("connectToPeer", [remoteLogin, remoteId, offer], ct);

    /// <summary>Mirrors: iceAdapterProxy.disconnectFromPeer(playerId)</summary>
    public Task<JsonNode?> DisconnectFromPeerAsync(int remoteId, CancellationToken ct = default)
        => CallAsync("disconnectFromPeer", [remoteId], ct);

    /// <summary>
    /// Mirrors: iceAdapterProxy.iceMsg(remotePlayerId, record)
    /// candidatesJson is the raw JSON string received in onIceMsg from the FAF lobby.
    /// </summary>
    public Task<JsonNode?> IceMsgAsync(int remoteId, string candidatesJson, CancellationToken ct = default)
        => CallAsync("iceMsg", [remoteId, candidatesJson], ct);

    /// <summary>Mirrors: iceAdapterProxy.setIceServers(servers)</summary>
    public Task<JsonNode?> SetIceServersAsync(List<Dictionary<string, object>> servers,
        CancellationToken ct = default)
        => CallAsync("setIceServers", [servers], ct);

    /// <summary>Mirrors: iceAdapterProxy.setLobbyInitMode("normal" | "auto")</summary>
    public Task<JsonNode?> SetLobbyInitModeAsync(string mode, CancellationToken ct = default)
        => CallAsync("setLobbyInitMode", [mode], ct);

    /// <summary>Mirrors: iceAdapterProxy.status()</summary>
    public Task<JsonNode?> StatusAsync(CancellationToken ct = default)
        => CallAsync("status", [], ct);

    /// <summary>Mirrors: iceAdapterProxy.quit()</summary>
    public Task<JsonNode?> QuitAsync(CancellationToken ct = default)
        => CallAsync("quit", [], ct);

    // ── Core send ─────────────────────────────────────────────────────────────

    private async Task<JsonNode?> CallAsync(string method, object[] paramArray, CancellationToken ct, int timeoutSeconds = 10)
    {
        int id  = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var request = new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = paramArray
        };

        string json = JsonSerializer.Serialize(request) + "\n";
        _log.LogDebug("[ICE-RPC] → {Json}", json.TrimEnd());
        Console.WriteLine($"[ICE-RPC] → {json.TrimEnd()}");

        if (_stream is null)
        {
            _pending.TryRemove(id, out _);
            throw new InvalidOperationException("ICE adapter not connected");
        }

        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _stream.WriteAsync(bytes, ct);
        await _stream.FlushAsync(ct);

        // Wait for response with timeout
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        using var linked  = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            return await tcs.Task.WaitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            _pending.TryRemove(id, out _);
            // A timeout here is NOT necessarily a failure on the adapter's side —
            // hostGame/joinGame can involve real ICE/STUN/TURN candidate gathering,
            // which can legitimately take longer than a simple metadata call. But
            // silently returning null (the old behavior) meant LaunchGameAsync had
            // zero visibility into whether this actually failed or just ran long —
            // it would log "RPC call sent" regardless either way. Throwing here
            // makes that distinction visible in hostlog/joinlog instead of hidden.
            _log.LogWarning("[ICE-RPC] Call {Method} timed out after {Timeout}s", method, timeoutSeconds);
            throw new TimeoutException($"ICE adapter RPC call '{method}' did not respond within {timeoutSeconds}s");
        }
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try { _tcp?.Close(); } catch { }
        _tcp = null;
        _cts.Dispose();
    }
}
