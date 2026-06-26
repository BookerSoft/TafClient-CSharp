using System.Diagnostics;
using System.Linq;
using System.Net;
using GpgNet.Client;
using GpgNet.Protocol;
using Microsoft.Extensions.Logging;

// ─── gpgnet4ta.cs ─────────────────────────────────────────────────────────────
//
// C# port of gpgnet4ta.cpp doMain().
//
// WHAT IS PORTED:
//   - GpgNetClient (TCP connect + command dispatch)       ✓ GpgNet.Client
//   - ForwardGameEventsToGpgNet (game events → FAF)       ✓ below (PlayerOption, GameState, GameResult)
//   - GpgNetSend (all send* methods)                      ✓ GpgNet.Protocol
//   - Command-line interface matching gpgnet4ta --help     ✓ below
//
// WHAT IS NOT PORTED (Windows-only DirectPlay components):
//   - TaLobby / TafnetNode / TafnetGameNode               ✗ DirectPlay UDP relay (Windows only)
//   - GameMonitor2 / TAPacketParser                        ✗ DirectPlay packet sniffing (Windows only)
//   - DPlayReg / JDPlay / DPlayWrapper                     ✗ DirectPlay COM registration (Windows only)
//   - GpgNetGameLauncher (DirectPlay game launch)          ✗ Windows only
//   - IrcForward                                           ✗ IRC relay (low priority)
//
// On Linux/macOS the ICE adapter provides the UDP relay; gpgnet4ta's job is
// purely to: connect to GPGNet, receive commands, launch the game, and forward
// game state events back. The UDP tunnelling is handled by the ICE adapter.
//
// Usage (matches C++ version):
//   gpgnet4ta --gpgnet 127.0.0.1:50123
//             --gamepath /path/to/TotalAnnihilation/taesc
//             --gameid   12345
//             --gamemod  taesc
//             --logfile  /tmp/game_12345.log
//             [--autolaunch]
//             [--players 10]
//             [--israted]
//             [--lobbybindaddress 127.0.0.1]
//             [--loglevel 5]

using var loggerFactory = LoggerFactory.Create(b =>
    b.AddConsole().SetMinimumLevel(
        int.TryParse(GetArg(args, "--loglevel", "3"), out int ll)
            ? (LogLevel)Math.Clamp(5 - ll, 0, 5)
            : LogLevel.Information));

var log = loggerFactory.CreateLogger("gpgnet4ta");

// ── Parse command line (mirrors QCommandLineParser options) ───────────────────
string gpgnetAddr   = GetArg(args, "--gpgnet",      "127.0.0.1:50000");
string gamePath     = GetArg(args, "--gamepath",    "");
string gameMod      = GetArg(args, "--gamemod",     "tacc");
int    gameId       = int.Parse(GetArg(args, "--gameid", "0"));
string logFile      = GetArg(args, "--logfile",     "");
bool   autoLaunch   = args.Contains("--autolaunch");
bool   isRated      = args.Contains("--israted");
int    maxPlayers   = int.Parse(GetArg(args, "--players", "10"));
bool   lockOptions  = args.Contains("--lockoptions");
string bindAddr     = GetArg(args, "--lobbybindaddress", "127.0.0.1");
int    launchServerPort = int.Parse(GetArg(args, "--launchserverport", "0"));
int    consolePort      = int.Parse(GetArg(args, "--consoleport", "0"));
string gameArgsRaw  = GetArg(args, "--gameargs", "");
// Args are passed through as a single string, space-separated, since each
// individual game_launch arg (e.g. "/team", "2") is itself a plain token.
List<string> gameArgs = string.IsNullOrEmpty(gameArgsRaw)
    ? []
    : gameArgsRaw.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

if (!string.IsNullOrEmpty(logFile))
    Console.SetOut(new StreamWriter(logFile, append: true) { AutoFlush = true });

Console.WriteLine($"[gpgnet4ta] version 2026.1-cs  gpgnet={gpgnetAddr} mod={gameMod} id={gameId} autolaunch={autoLaunch}");

if (!string.IsNullOrEmpty(gamePath) && !Directory.Exists(gamePath))
{
    Console.Error.WriteLine($"[gpgnet4ta] Game path not found: {gamePath}");
    return 1;
}

// ── Connect to GPGNet server ──────────────────────────────────────────────────
(string gpgHost, int gpgPort) = ParseHostPort(gpgnetAddr);
var client = new GpgNetClient(loggerFactory.CreateLogger<GpgNet.Client.GpgNetClient>());

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

try
{
    await client.ConnectAsync(gpgHost, gpgPort, cts.Token);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[gpgnet4ta] Cannot connect to GPGNet {gpgnetAddr}: {ex.Message}");
    return 1;
}

// Mirrors GpgNetGameLauncher's constructor: m_gpgNetClient.sendGameState("Idle", "Idle")
// is sent IMMEDIATELY on construction — i.e. right after the GPGNet connection
// is established — with no dependency on CreateLobby having arrived yet. This
// is the actual trigger that causes the ICE adapter to send CreateLobby back to
// us; our previous code had this backwards (sent Idle only after receiving
// CreateLobby), which deadlocks since CreateLobby never arrives without Idle
// being sent first. Confirmed via a correlated working ice-adapter log showing
// the exact sequence: GPGNetClient connected → game sends "GameState Idle" →
// adapter sends "CreateLobby" → game (us) sends "GameState Lobby".
Console.WriteLine("[gpgnet4ta] Sending initial GameState: Idle");
_ = client.Sender!.SendGameStateAsync("Idle", "Idle", cts.Token);

// ── Connect to talauncher (LaunchServer) via TaLaunch.LaunchClient ────────────
// Mirrors GpgNetGameLauncher owning a talaunch::LaunchClient — this is the
// missing piece that actually drives talauncher.exe to set up the DirectPlay
// session and launch the TA game process. Without this connection, talauncher
// just sits idle and the game never actually starts, leaving peers stuck on
// "Joining..." forever since LaunchServer never receives /host or /join.
TaLaunch.LaunchClient? launchClient = null;
// Tracks whether the game process was ever actually running, so we can
// detect the transition away from Running/Launched and send GameEnded —
// mirrors pollJdplayStillActive's isApplicationRunning() check exactly.
// Without this distinction, a transition TO Idle is ambiguous: it happens
// both right after talauncher first connects (nothing has happened yet)
// AND when a previously-running game exits cleanly (exit code 0) — only
// the second case should trigger GameEnded.
bool wasRunning = false;
if (launchServerPort > 0)
{
    launchClient = new TaLaunch.LaunchClient(loggerFactory.CreateLogger<TaLaunch.LaunchClient>());
    bool connected = await launchClient.ConnectAsync(
        System.Net.IPAddress.Loopback, launchServerPort, cts.Token);
    if (!connected)
    {
        Console.Error.WriteLine($"[gpgnet4ta] Could not connect to talauncher on port {launchServerPort} — game launch will not work");
    }
    else
    {
        Console.WriteLine($"[gpgnet4ta] Connected to talauncher LaunchServer on port {launchServerPort}");
        launchClient.StateChanged += (_, state) =>
        {
            Console.WriteLine($"[gpgnet4ta] LaunchClient state -> {state}");
            log.LogInformation("[gpgnet4ta] LaunchClient state -> {State}", state);
            // Forward state transitions to GPGNet so the lobby UI reflects real progress
            // instead of being stuck on whatever state was last sent.
            switch (state)
            {
                case TaLaunch.LaunchClientState.Running:
                    wasRunning = true;
                    _ = client.Sender!.SendGameStateAsync("Launching", "Launching", cts.Token);
                    break;
                case TaLaunch.LaunchClientState.Launched:
                    wasRunning = true;
                    _ = client.Sender!.SendGameStateAsync("Launched", "Launched", cts.Token);
                    break;
                case TaLaunch.LaunchClientState.Idle:
                    // Mirrors pollJdplayStillActive: talauncher sends IDLE
                    // both right after first connecting (nothing has run
                    // yet — wasRunning is still false, so this is a no-op)
                    // AND when a previously-running game exits cleanly
                    // (exit code 0) — only the latter should report
                    // GameEnded to the server.
                    if (wasRunning)
                    {
                        Console.WriteLine("[gpgnet4ta] Game process exited cleanly (talauncher reported IDLE after running) — sending GameEnded");
                        _ = client.Sender!.SendGameEndedAsync(cts.Token);
                        wasRunning = false;
                    }
                    break;
                case TaLaunch.LaunchClientState.Fail:
                    // talauncher reports FAIL when its internal JDPlay
                    // (DirectPlay COM) session setup fails to initialize or
                    // launch — see LaunchServer::launchGame() in the original
                    // C++ source. This is the ONLY place the direct exe
                    // launch fallback fires on real Windows now — it used to
                    // fire unconditionally alongside every talauncher
                    // attempt, which meant our own direct launch almost
                    // always won the race against talauncher's TCP
                    // round-trip + JDPlay setup, starting TotalA.exe with no
                    // real DirectPlay session before talauncher ever got a
                    // chance to do it properly. Now we wait to see if
                    // talauncher's own attempt actually fails before falling
                    // back.
                    //
                    // If the game had already been running and THEN failed
                    // (e.g. it crashed), this also means the session ended —
                    // send GameEnded the same way the Idle case does, since
                    // talauncher's real "FAIL {exitcode}" for a non-zero
                    // exit after running is functionally the same "the
                    // game is no longer running" event as a clean exit.
                    if (wasRunning)
                    {
                        Console.WriteLine("[gpgnet4ta] Game process exited with an error (talauncher reported FAIL after running) — sending GameEnded");
                        _ = client.Sender!.SendGameEndedAsync(cts.Token);
                        wasRunning = false;
                    }
                    Console.Error.WriteLine("[gpgnet4ta] talauncher reported FAIL (DirectPlay/JDPlay " +
                        "init or launch failure) — falling back to direct exe launch");
                    if (autoLaunch)
                        _ = LaunchGameAsync(gamePath, gameMod, gameArgs, log, cts.Token, client);
                    break;
            }
        };
    }
}
else
{
    Console.Error.WriteLine("[gpgnet4ta] No --launchserverport supplied — cannot drive talauncher, falling back to direct exe launch (no multiplayer session setup)");
}

// ── Game state tracking (mirrors gpgnet4ta.cpp local state) ──────────────────
string localAlias    = "";
int    localPlayerId = 0;

// Mirrors GpgNetGameLauncher's m_readyToStart / m_autoStart exactly. Per
// the real C++ source: onHostGame/onJoinGame set readyToStart = true, and
// only call the actual launch immediately if autoStart was ALREADY true
// (the deferred-join-order case — a /launch arrived before HostGame/
// JoinGame did). Otherwise the game waits, fully staged, until an explicit
// "/launch" arrives over the console port — driven by the host clicking
// "Start" in the lobby client's staging screen. This is the actual
// missing piece behind "didn't go to staging before launching" — our
// previous --autolaunch flag conflated "ready to start" with "actually
// start now", launching the instant HostGame/JoinGame arrived with no way
// for the staging screen to ever appear first.
bool readyToStart = false;
bool autoStartRequested = false;
string pendingMapName = "";

void TryStartApplication()
{
    if (!readyToStart)
    {
        Console.WriteLine($"[gpgnet4ta] /launch received but game not ready to start yet (pending map='{pendingMapName}') — deferring (will auto-start once HostGame/JoinGame arrives)");
        autoStartRequested = true;
        return;
    }
    if (launchClient != null)
    {
        launchClient.GameGuid = $"{{{ComputeDplayGuid(gameMod.ToUpperInvariant())}}}";
        _ = StartViaLaunchClientAsync(launchClient, log, client, cts.Token);
    }
    else if (autoLaunch)
    {
        _ = LaunchGameAsync(gamePath, gameMod, gameArgs, log, cts.Token, client);
    }
}

// Console listener — the missing piece behind the staging/lobby room never
// appearing before launch. See ConsoleListener.cs and TryStartApplication
// above for the full explanation. Only started if a real port was supplied
// (GameLaunchService always passes one); skipped entirely if consolePort is
// 0, which would otherwise mean no listener at all and a /launch from
// TafClient would have nowhere to go. Constructed/started here rather than
// right after consolePort is parsed, since its callback references
// TryStartApplication and other variables (client, cts, launchClient,
// readyToStart, etc.) that must already be definitely-assigned at this
// point in top-level statement order — starting it earlier caused CS0165
// "use of unassigned local variable" for every one of them, since the C#
// compiler's definite-assignment analysis for top-level statements doesn't
// hoist local functions the way it does inside a regular method body.
GpgNetApp.ConsoleListener? consoleListener = null;
if (consolePort > 0)
{
    consoleListener = new GpgNetApp.ConsoleListener(consolePort);
    consoleListener.TextReceived += text =>
    {
        // Mirrors GpgNetGameLauncher::onExtendedMessage — only /launch is
        // implemented for now (the actual missing piece); the other
        // commands (/set_hash_api_token, /map, /title, /max_players, etc.)
        // aren't ported since nothing in this codebase sends them yet.
        if (text.StartsWith("/launch", StringComparison.Ordinal))
        {
            Console.WriteLine("[gpgnet4ta] /launch received — starting the game now");
            TryStartApplication();
        }
        else
        {
            Console.WriteLine($"[gpgnet4ta] Unrecognized console command (ignored): {text}");
        }
    };
    consoleListener.Start();
}

// Slot tracking for ForwardGameEventsToGpgNet (mirrors m_playerNames vector)
// Maps slot → (playerAlias, isAI, isWatcher, armyNumber, teamNumber, side)
var slots = new Dictionary<int, SlotInfo>();

// ── CreateLobby handler ───────────────────────────────────────────────────────
// Mirrors: emit createLobby → TaLobby::onCreateLobby + GpgNetGameLauncher::onCreateLobby
// This arrives in RESPONSE to the "GameState Idle" we send right after
// connecting (see above) — not the other way around.
client.CreateLobby += (protocol, localPort, alias, realName, playerId, natTraversal) =>
{
    try
    {
        localAlias    = alias;
        localPlayerId = playerId;
        log.LogInformation("[gpgnet4ta] CreateLobby alias={Alias} id={Id} port={Port}",
            alias, playerId, localPort);
        Console.WriteLine($"[gpgnet4ta] CreateLobby alias={alias} id={playerId} localPort={localPort}");

        // Mirrors GpgNetGameLauncher::onCreateLobby — transitions to Lobby/Staging,
        // NOT a re-send of Idle (which was this codebase's previous, backwards logic).
        _ = client.Sender!.SendGameStateAsync("Lobby", "Staging", cts.Token);
    }
    catch (Exception ex)
    {
        // IMPORTANT: without this catch, an exception here propagates up
        // into GpgNetClient.ReadLoopAsync's own catch block, which only
        // logs via the structured ILogger (invisible in the plain
        // Console.WriteLine-based game_*.log files) and then fires
        // Disconnected — tearing down the entire GPGNet connection with
        // zero visible explanation in the one log file actually being
        // checked after a failed launch. Confirmed as a real, observed
        // symptom: a session where "<- CreateLobby [...]" printed (the
        // raw wire receive, logged unconditionally before Dispatch) but
        // "[gpgnet4ta] CreateLobby alias=..." (this handler's own log
        // line) never appeared at all, with nothing else happening after.
        Console.WriteLine($"[gpgnet4ta] CreateLobby handler FAILED: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        log.LogError(ex, "[gpgnet4ta] CreateLobby handler failed");
    }
};

// ── HostGame handler ──────────────────────────────────────────────────────────
// Mirrors: connect(&gpgNetClient, &GpgNetClient::hostGame, [&launcher, &parser] ...)
//
// Real DirectPlay-based session setup (talauncher/JDPlay) is Windows-only and
// not portable — see header comment. The ICE adapter already provides the
// cross-platform UDP relay, so all this needs to do is start the actual game
// executable with the right arguments. talauncher is still notified (if
// connected) as a best-effort side channel for logging/diagnostics, but the
// game launch itself no longer waits on or depends on that connection.
client.HostGame += mapName =>
{
    try
    {
        log.LogInformation("[gpgnet4ta] HostGame: {Map}", mapName);
        Console.WriteLine($"[gpgnet4ta] HostGame map={mapName}");
        pendingMapName = mapName;

        if (!string.IsNullOrEmpty(gamePath))
            CreateTAInitFile(gamePath, localAlias, mapName, maxPlayers, lockOptions, log);

        // Set up launchClient's properties now (cheap, no side effects) so
        // they're ready the moment /launch actually arrives — but DO NOT
        // trigger the launch itself here. Mirrors the real onHostGame:
        // m_readyToStart = true; if (m_autoStart) onStartApplication();
        // The launch only fires immediately if autoStartRequested was
        // already set by an earlier /launch (the deferred-join-order
        // case) — otherwise it waits for the host to click "Start" in the
        // staging screen, which sends /launch over the console port.
        if (launchClient != null)
        {
            launchClient.IsHost      = true;
            launchClient.PlayerName  = localAlias;
            launchClient.GameId      = gameId;
            launchClient.GameAddress = "127.0.0.1";
        }

        readyToStart = true;
        if (autoStartRequested)
        {
            Console.WriteLine("[gpgnet4ta] HostGame: executing deferred launch (an earlier /launch was already received)");
            TryStartApplication();
        }
        else
        {
            Console.WriteLine("[gpgnet4ta] HostGame: staged and ready — waiting for /launch (host must click Start)");
        }

        // Mirrors the real onHostGame exactly: tell the server the host is
        // on team 1, and report the slot count / map details. THIS is what
        // actually makes the server add the host to the game's team
        // roster — confirmed as the missing piece behind the staging
        // screen's player list staying empty even after everything else
        // in the launch chain worked correctly. Without this, the server
        // has no basis to ever include the host in game_info's "teams"
        // field, regardless of how long the client waits or retries.
        _ = client.Sender!.SendPlayerOptionAsync(localPlayerId.ToString(), "Team", 1, cts.Token);
        _ = client.Sender!.SendGameOptionAsync("Slots", maxPlayers, cts.Token);

        // Build the REAL structured MapDetails string (mapname\x1Farchive\x1F
        // hash\x1Fdescription\x1F...), confirmed against a real Java client
        // log — previously this just sent the bare map name, nowhere close
        // to the actual expected format. Called in-process via MapTool's
        // BuildMapDetails (same pattern MapService already uses), not by
        // shelling out to a separate maptool executable.
        string? mapDetails = TafToolbox.MapTool.Program.BuildMapDetails(gamePath, mapName);
        if (mapDetails is not null)
        {
            Console.WriteLine($"[gpgnet4ta] MapDetails: {mapDetails}");
            _ = client.Sender!.SendGameOptionAsync("MapDetails", mapDetails, cts.Token);
        }
        else
        {
            Console.WriteLine($"[gpgnet4ta] Could not build MapDetails for '{mapName}' in '{gamePath}' — sending bare map name as a fallback");
            _ = client.Sender!.SendGameOptionAsync("MapDetails", mapName, cts.Token);
        }
    }
    catch (Exception ex)
    {
        // See CreateLobby's handler above for why this matters: an
        // unguarded exception here would silently tear down the entire
        // GPGNet connection via ReadLoopAsync's catch -> Disconnected,
        // with no trace in game_*.log (which is built from Console.WriteLine,
        // not the structured ILogger that catch block uses).
        Console.WriteLine($"[gpgnet4ta] HostGame handler FAILED: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        log.LogError(ex, "[gpgnet4ta] HostGame handler failed");
    }
};

// ── JoinGame handler ──────────────────────────────────────────────────────────
// Mirrors: connect(&gpgNetClient, &GpgNetClient::joinGame, [&launcher, &parser] ...)
client.JoinGame += (host, alias, realName, playerId) =>
{
    try
    {
        log.LogInformation("[gpgnet4ta] JoinGame: {Alias} ({Id}) host={Host}", alias, playerId, host);
        Console.WriteLine($"[gpgnet4ta] JoinGame alias={alias} id={playerId} host={host}");

        // Set up launchClient's properties now, but defer the actual
        // launch trigger to TryStartApplication — see the matching comment
        // in the HostGame handler above for the full explanation.
        if (launchClient != null)
        {
            launchClient.IsHost      = false;
            launchClient.PlayerName  = localAlias;
            launchClient.GameId      = gameId;
            launchClient.GameAddress = host;
        }

        readyToStart = true;
        if (autoStartRequested)
        {
            Console.WriteLine("[gpgnet4ta] JoinGame: executing deferred launch (an earlier /launch was already received)");
            TryStartApplication();
        }
        else
        {
            Console.WriteLine("[gpgnet4ta] JoinGame: staged and ready — waiting for /launch (host must click Start)");
        }

        // Mirrors the real onJoinGame — tell the server this player is on
        // team 1, so the server actually adds them to the game's team
        // roster (same reasoning as HostGame's matching call above).
        _ = client.Sender!.SendPlayerOptionAsync(localPlayerId.ToString(), "Team", 1, cts.Token);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[gpgnet4ta] JoinGame handler FAILED: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
        log.LogError(ex, "[gpgnet4ta] JoinGame handler failed");
    }
};

// ── ConnectToPeer / DisconnectFromPeer ────────────────────────────────────────
client.ConnectToPeer += (host, alias, realName, id) =>
{
    Console.WriteLine($"[gpgnet4ta] ConnectToPeer alias={alias} id={id} host={host}");
    log.LogInformation("[gpgnet4ta] ConnectToPeer {Alias} ({Id}) at {Host}", alias, id, host);
};

client.DisconnectFromPeer += id =>
{
    Console.WriteLine($"[gpgnet4ta] DisconnectFromPeer id={id}");
    log.LogInformation("[gpgnet4ta] DisconnectFromPeer {Id}", id);
};

// ── Disconnected ──────────────────────────────────────────────────────────────
client.Disconnected += () =>
{
    Console.WriteLine("[gpgnet4ta] GPGNet server disconnected — exiting");
    cts.Cancel();
};

// ── Wait for shutdown ─────────────────────────────────────────────────────────
try { await Task.Delay(Timeout.Infinite, cts.Token); }
catch (OperationCanceledException) { }

if (launchClient != null) await launchClient.DisposeAsync();
if (consoleListener != null) await consoleListener.DisposeAsync();
await client.DisposeAsync();
Console.WriteLine("[gpgnet4ta] Exited cleanly");
return 0;

// ─── Helpers ──────────────────────────────────────────────────────────────────

static string GetArg(string[] a, string flag, string def)
{
    int i = Array.IndexOf(a, flag);
    return i >= 0 && i + 1 < a.Length ? a[i + 1] : def;
}

static (string host, int port) ParseHostPort(string s)
{
    int colon = s.LastIndexOf(':');
    if (colon < 0) return (s, 50000);
    return (s[..colon], int.TryParse(s[(colon + 1)..], out int p) ? p : 50000);
}

static async Task LaunchGameAsync(string gamePath, string gameMod, List<string> gameArgs,
    ILogger log, CancellationToken ct, GpgNet.Client.GpgNetClient client)
{
    if (string.IsNullOrEmpty(gamePath))
    {
        log.LogWarning("[gpgnet4ta] No --gamepath supplied — cannot launch game");
        return;
    }

    string exe = FindGameExe(gamePath);
    if (!File.Exists(exe))
    {
        log.LogWarning("[gpgnet4ta] Game exe not found: {Exe}", exe);
        return;
    }

    bool needsWine = !OperatingSystem.IsWindows() && exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    // CRITICAL: Wine cannot resolve a raw macOS/Linux filesystem path the
    // way it resolves a real Windows path. Convert to whatever Wine can
    // actually use BEFORE building cmd — C:\... if the install lives
    // inside the prefix's own drive_c (e.g. via the main client's Install
    // Mod feature), or Z:\... (the standard Wine convention mapping the
    // real filesystem root) for any other install location. This was the
    // actual missing piece behind the game not launching correctly via
    // wine on macOS — we were previously passing wine the bare Unix path
    // as-is, which it can't resolve as a Windows path at all.
    string exeForCmd = needsWine ? ToWineArgPathLocal(exe) : exe;
    if (needsWine)
        Console.WriteLine($"[gpgnet4ta] Converted exe path for Wine: {exe} -> {exeForCmd}");

    // "-c TAForever.ini" is REQUIRED — confirmed from the real C++ source
    // (talauncher.cpp's handleRegisterDplay: ADDITIONAL_GAME_ARGS = "-c
    // TAForever.ini", always appended to whatever game args are configured).
    // Without this flag TA has no way to know to read the session config file
    // CreateTAInitFile() just wrote — the game opens with no session info at
    // all and has nothing to do, which plausibly explains it exiting
    // immediately with code 0 and no error.
    var cmd = new List<string> { exeForCmd, "-c", "TAForever.ini" };
    cmd.AddRange(gameArgs);

    if (needsWine)
    {
        string? wine = FindWine();
        if (wine != null)
        {
            cmd.Insert(0, wine);
            log.LogInformation("[gpgnet4ta] Using wine: {Wine}", wine);
        }
        else
        {
            log.LogWarning("[gpgnet4ta] Wine not found — attempting direct launch (will likely fail)");
        }
    }

    log.LogInformation("[gpgnet4ta] Launching: {Cmd}", string.Join(" ", cmd));
    Console.WriteLine($"[gpgnet4ta] Launching: {string.Join(" ", cmd)}");

    try
    {
        var psi = new ProcessStartInfo(cmd[0])
        {
            UseShellExecute  = false,
            WorkingDirectory = gamePath,
        };
        for (int i = 1; i < cmd.Count; i++) psi.ArgumentList.Add(cmd[i]);

        // Set WINEPREFIX for a dedicated TAF Wine bottle, only when actually using wine
        if (needsWine && cmd[0].Contains("wine"))
        {
            string prefix = GetWinePrefixLocal();
            psi.Environment["WINEPREFIX"] = prefix;
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("TAF_WINE_DEBUG")))
                psi.Environment["WINEDEBUG"] = "-all";
        }

        using var p = Process.Start(psi)!;
        log.LogInformation("[gpgnet4ta] Game process started, pid={Pid}", p.Id);
        Console.WriteLine($"[gpgnet4ta] Game process started, pid={p.Id}");
        await p.WaitForExitAsync(ct);
        log.LogInformation("[gpgnet4ta] Game exited with code {Code}", p.ExitCode);
        Console.WriteLine($"[gpgnet4ta] Game exited with code {p.ExitCode}");
        // Same gap as the talauncher path's StateChanged handler — nothing
        // was telling the server this game session ended once the direct-
        // launched process actually exited.
        _ = client.Sender!.SendGameEndedAsync(ct);
    }
    catch (Exception ex)
    {
        log.LogError(ex, "[gpgnet4ta] Launch failed");
        Console.WriteLine($"[gpgnet4ta] Launch FAILED: {ex.GetType().Name}: {ex.Message}");
    }
}

/// <summary>
/// Sends "/host" or "/join" to talauncher, then — on success — sends
/// GameState Lobby/Battleroom, mirroring GpgNetGameLauncher::doLaunchGame()
/// in the real C++ source exactly: m_launchClient.startApplication() is
/// followed immediately by m_gpgNetClient.sendGameState("Lobby",
/// "Battleroom"). This is a SEPARATE state from the earlier Lobby/Staging
/// sent right after CreateLobby (see client.CreateLobby's handler above) —
/// Staging means "we've created the lobby and are about to launch",
/// Battleroom means "the launch command was actually sent and the game
/// should now be opening into its in-game lobby screen". Without this second
/// transition, the server/client UI has no signal that launch progressed
/// past the initial CreateLobby step at all.
/// </summary>
static async Task StartViaLaunchClientAsync(TaLaunch.LaunchClient launchClient, ILogger log, GpgNet.Client.GpgNetClient client, CancellationToken ct)
{
    Console.WriteLine($"[gpgnet4ta] Sending {(launchClient.IsHost ? "/host" : "/join")} to talauncher...");
    bool sent = await launchClient.StartApplicationAsync();
    if (!sent)
    {
        log.LogWarning("[gpgnet4ta] Failed to send start command to talauncher");
        return;
    }

    Console.WriteLine("[gpgnet4ta] Sending GameState: Lobby/Battleroom");
    _ = client.Sender!.SendGameStateAsync("Lobby", "Battleroom", ct);
}

/// <summary>
/// Port of GpgNetGameLauncher::createTAInitFile (GpgNetGameLauncher.cpp).
///
/// This is the actual hand-off mechanism between gpgnet4ta and TA's own game
/// engine: TotalA.exe reads TAForever.ini at startup to learn the session name,
/// map/mission, player limit, unit cap, locked-options flag, and starting
/// position mode. Without this file, the exe launches with no session
/// configuration at all — it has no way to know what game it's supposed to
/// set up, which is a very plausible explanation for "the game starts but
/// doesn't actually do anything host/join-related" even after the launch
/// process chain itself (talauncher, ICE adapter, gpgnet4ta) is all working.
///
/// Reads taforever.ini.template (shipped alongside the natives, same template
/// used by the original C++ client) and substitutes:
///   {session}      -> "{playerName}'s Game"
///   {mission}      -> the map/mission name
///   {playerlimit}  -> clamped to [2, 10]
///   {maxunits}     -> clamped to [20, 1500] (hardcoded 1000 in the original)
///   {lockoptions}  -> "1" or "0"
///   {location}     -> "2" (random) or "1" (fixed) — we default to random
///                      since our simplified architecture has no equivalent
///                      of the original's "/launch &lt;order&gt;" join-order signal.
/// </summary>
static void CreateTAInitFile(
    string gamePath, string playerName, string mission, int playerLimit,
    bool lockOptions, ILogger log)
{
    // Defensive: the whole body is wrapped because this runs inside the
    // HostGame event handler lambda — an unguarded exception here (e.g. from
    // File.ReadAllText, or .Replace on a null mission) would propagate out of
    // the event invocation and could silently abort everything queued after
    // it in that handler, INCLUDING the actual LaunchGameAsync call that
    // starts TotalA.exe. That would look exactly like "TAForever.ini doesn't
    // get created" with no other visible symptom, since nothing downstream
    // would run either, but with no exception ever printed anywhere.
    try
    {
        mission ??= "";

        string templatePath = Path.Combine(AppContext.BaseDirectory, "taforever.ini.template");
        Console.WriteLine($"[gpgnet4ta] Looking for ini template at: {templatePath}");
        if (!File.Exists(templatePath))
        {
            // Also check alongside the natives, matching where it's actually shipped
            string altPath = Path.Combine(AppContext.BaseDirectory, "natives", "bin", "taforever.ini.template");
            Console.WriteLine($"[gpgnet4ta] Not found, trying: {altPath}");
            if (File.Exists(altPath)) templatePath = altPath;
        }

        if (!File.Exists(templatePath))
        {
            log.LogWarning("[gpgnet4ta] taforever.ini.template not found (checked {Path}) — " +
                "TAForever.ini will NOT be generated, the game may not start with correct session config", templatePath);
            Console.WriteLine($"[gpgnet4ta] WARNING: taforever.ini.template not found at {templatePath}");
            return;
        }

        Console.WriteLine($"[gpgnet4ta] Found template at: {templatePath}");
        string text = File.ReadAllText(templatePath);
        string session     = $"{playerName}'s Game";
        int    clampedLimit = Math.Max(2, Math.Min(playerLimit, 10));
        const int maxUnits   = 1000; // matches the hardcoded value at the original's call site
        int    clampedUnits = Math.Max(20, Math.Min(maxUnits, 1500));

        text = text.Replace("{session}", session)
                   .Replace("{mission}", mission)
                   .Replace("{playerlimit}", clampedLimit.ToString())
                   .Replace("{maxunits}", clampedUnits.ToString())
                   .Replace("{lockoptions}", lockOptions ? "1" : "0")
                   .Replace("{location}", "2"); // random starting positions (default)

        if (!Directory.Exists(gamePath))
        {
            log.LogWarning("[gpgnet4ta] Game path does not exist, cannot write TAForever.ini: {Path}", gamePath);
            Console.WriteLine($"[gpgnet4ta] WARNING: game path does not exist: {gamePath}");
            return;
        }

        string iniPath = Path.Combine(gamePath, "TAForever.ini");
        File.WriteAllText(iniPath, text);
        log.LogInformation("[gpgnet4ta] Wrote {Path} (session='{Session}', mission='{Mission}', playerLimit={Limit})",
            iniPath, session, mission, clampedLimit);
        Console.WriteLine($"[gpgnet4ta] Wrote TAForever.ini: session='{session}' mission='{mission}' playerLimit={clampedLimit}");
    }
    catch (Exception ex)
    {
        log.LogError(ex, "[gpgnet4ta] CreateTAInitFile failed");
        Console.WriteLine($"[gpgnet4ta] CreateTAInitFile FAILED: {ex.GetType().Name}: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
    }
}

/// <summary>
/// Computes the same per-mod DirectPlay GUID that talauncher's
/// --registerdplay derives and registers (QUuid::createUuidV5 in the real
/// C++ source) — RFC 4122 UUIDv5, namespace + SHA-1. Duplicated from
/// TafClient's DPlayRegFileGenerator.cs rather than shared via a project
/// reference, since GpgNetApp is a separate standalone Exe that TafClient
/// references with ReferenceOutputAssembly="false" specifically so its
/// assembly is never loaded into TafClient's own process — the reverse
/// reference (GpgNetApp -> TafClient) isn't viable either, so this small,
/// self-contained piece of pure math is duplicated instead.
///
/// THIS WAS THE ACTUAL MISSING PIECE for "Unable to launch game — no
/// DirectPlay registry entry for game with guid=...": talauncher's /host
/// command (sent via TaLaunch.LaunchClient.StartApplicationAsync) was
/// always using GameGuid's hardcoded default, the FIXED Total Annihilation
/// game GUID ({99797420-F5F5-11CF-9827-00A0241496C8}) — never the per-mod
/// GUID that --registerdplay (or the standalone .reg file) actually
/// registered. talauncher looks up the registration by whatever GUID /host
/// gives it, found nothing under the fixed GUID (since nothing was ever
/// registered under that GUID — only under the per-mod derived one), and
/// showed exactly the dialog observed.
/// </summary>
static Guid ComputeDplayGuid(string modTechnicalUppercase)
{
    var ns = Guid.Parse("1336f32e-d116-4633-b853-4fee1ec91ea5");
    Span<byte> nsBytes = stackalloc byte[16];
    ns.TryWriteBytes(nsBytes);
    Span<byte> nsBytesBE = stackalloc byte[16];
    nsBytesBE[0] = nsBytes[3]; nsBytesBE[1] = nsBytes[2]; nsBytesBE[2] = nsBytes[1]; nsBytesBE[3] = nsBytes[0];
    nsBytesBE[4] = nsBytes[5]; nsBytesBE[5] = nsBytes[4];
    nsBytesBE[6] = nsBytes[7]; nsBytesBE[7] = nsBytes[6];
    for (int i = 8; i < 16; i++) nsBytesBE[i] = nsBytes[i];

    byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(modTechnicalUppercase);
    byte[] toHash = new byte[16 + nameBytes.Length];
    nsBytesBE.CopyTo(toHash);
    nameBytes.CopyTo(toHash, 16);

    byte[] hash = System.Security.Cryptography.SHA1.HashData(toHash);
    Span<byte> result = stackalloc byte[16];
    hash.AsSpan(0, 16).CopyTo(result);
    result[6] = (byte)((result[6] & 0x0F) | 0x50);
    result[8] = (byte)((result[8] & 0x3F) | 0x80);

    Span<byte> resultLE = stackalloc byte[16];
    resultLE[0] = result[3]; resultLE[1] = result[2]; resultLE[2] = result[1]; resultLE[3] = result[0];
    resultLE[4] = result[5]; resultLE[5] = result[4];
    resultLE[6] = result[7]; resultLE[7] = result[6];
    for (int i = 8; i < 16; i++) resultLE[i] = result[i];

    return new Guid(resultLE);
}

static string FindGameExe(string gamePath)
{
    foreach (var name in new[] { "TotalA.exe", "totala.exe", "TotalAnnihilation.exe" })
    {
        string p = Path.Combine(gamePath, name);
        if (File.Exists(p)) return p;
    }
    return Path.Combine(gamePath, "TotalA.exe");
}

// Mirror WineDetector from main client (kept self-contained in this exe)

/// <summary>
/// Mirrors WineDetector.GetWinePrefix() exactly — TAF_WINEPREFIX env var,
/// then the bundle-contained prefix if running from inside a .app
/// (Contents/Resources/prefix), then ~/.wine-taf as the final fallback.
/// This was a real, separate bug: the WINEPREFIX-setting code in
/// LaunchGameAsync only ever checked TAF_WINEPREFIX / ~/.wine-taf, with no
/// awareness of the bundle-contained prefix at all — meaning gpgnet4ta's
/// own direct-launch path was using a completely different (and likely
/// uninitialized — no wineboot --init, no DirectPlay registration) prefix
/// than the rest of the app whenever running from a bundled .app.
/// </summary>
static string GetWinePrefixLocal()
{
    string? env = Environment.GetEnvironmentVariable("TAF_WINEPREFIX");
    if (!string.IsNullOrEmpty(env)) return env;

    if (OperatingSystem.IsMacOS())
    {
        string baseDir = AppContext.BaseDirectory.TrimEnd('/', '\\');
        if (baseDir.EndsWith(Path.Combine(".app", "Contents", "MacOS"), StringComparison.Ordinal))
        {
            string? contentsDir = Path.GetDirectoryName(baseDir);
            if (!string.IsNullOrEmpty(contentsDir))
                return Path.Combine(contentsDir, "Resources", "prefix");
        }
    }

    return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wine-taf");
}

/// <summary>
/// Converts a real macOS/Linux filesystem path into whatever Wine can
/// actually resolve when passed as a launch argument — works both ways,
/// as requested: if the path is already inside the prefix's own drive_c
/// (e.g. a game installed via the main client's "Install Mod" feature,
/// which copies into {prefix}/drive_c/TAF/{mod}), returns the C:\...
/// equivalent. Otherwise falls back to the standard Wine convention that
/// Z:\ maps to the real filesystem root / by default, covering an
/// arbitrary install location that was never copied into the prefix at
/// all. This was the actual missing piece behind "wine doesn't launch the
/// game correctly on macOS" — we were passing wine a raw Unix path as its
/// argument, which it can't resolve the same way a real Windows path does.
/// </summary>
static string ToWineArgPathLocal(string realPath)
{
    string fullReal = Path.GetFullPath(realPath);
    string driveC = Path.Combine(GetWinePrefixLocal(), "drive_c");
    string fullDriveC = Path.GetFullPath(driveC);

    if (fullReal.StartsWith(fullDriveC, StringComparison.Ordinal))
    {
        string relative = fullReal[fullDriveC.Length..].TrimStart('/', '\\');
        string windowsRelative = relative.Replace('/', '\\');
        return windowsRelative.Length > 0 ? $@"C:\{windowsRelative}" : "C:\\";
    }

    string zRelative = fullReal.TrimStart('/').Replace('/', '\\');
    return $@"Z:\{zRelative}";
}

static string? FindWine()
{
    if (OperatingSystem.IsMacOS())
    {
        string[] macCandidates = {
            "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine64",
            "/Applications/CrossOver.app/Contents/SharedSupport/CrossOver/bin/wine",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library/Application Support/Whisky/Libraries/Wine/bin/wine64"),
            "/opt/homebrew/bin/wine64", "/opt/homebrew/bin/wine",
            "/usr/local/bin/wine64",    "/usr/local/bin/wine",
            "/opt/local/bin/wine64",    "/opt/local/bin/wine",
        };
        foreach (var c in macCandidates)
            if (File.Exists(c)) return c;
    }
    foreach (var name in new[] { "wine64", "wine", "wine-stable" })
    {
        string? found = FindInPath(name);
        if (found != null) return found;
    }
    return null;
}

static string? FindInPath(string exe)
{
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
    {
        if (string.IsNullOrEmpty(dir)) continue;
        string full = Path.Combine(dir, exe);
        if (File.Exists(full)) return full;
    }
    return null;
}

// ─── Slot state record ────────────────────────────────────────────────────────
// Mirrors m_playerNames / m_isAI / m_isWatcher vectors in HandleGameStatus

record SlotInfo(
    string  Alias,
    bool    IsAI,
    bool    IsWatcher,
    int     ArmyNumber,
    int     TeamNumber,
    int     Side);
