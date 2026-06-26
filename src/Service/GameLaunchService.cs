using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using TafClient.Net;
using TafClient.Net.Domain;
using TafClient.Net.GpgNet;
using TafClient.Net.Ice;

namespace TafClient.Service;

/// <summary>
/// Orchestrates game launch for TAF.
///
/// Full sequence (port of TotalAnnihilationService.startGame()):
///
///   1. Kill any existing gpgnet4ta / dplaysvr processes (freePort47624)
///   2. Find free ports: rpcPort (ICE adapter RPC), gpgnetPort (ICE adapter GPGNet),
///      consolePort (gpgnet4ta console), launchServerPort (talauncher)
///   3. Launch talauncher.exe --bindport <launchServerPort>   (startLaunchServer)
///   4. Launch faf-ice-adapter.jar with rpcPort + gpgnetPort
///   5. Connect IceAdapterRpcClient → call setLobbyInitMode + setIceServers
///   6. Run talauncher.exe --registerdplay (register DirectPlay)
///   7. Launch gpgnet4ta with ALL required args including --launchserverport
///   8. Wire ICE adapter events → FAF lobby relay
/// </summary>
public sealed class GameLaunchService : IAsyncDisposable
{
    private readonly ILogger<GameLaunchService> _log;
    private readonly IFafServerAccessor         _faf;
    private readonly UserService                _us;
    private readonly PreferencesService         _prefs;
    private readonly PlayerService              _players;
    private readonly ILoggerFactory             _loggerFactory;

    private IceAdapterRpcClient? _iceRpc;
    private Process? _iceAdapterProcess;
    private Process? _gpgNet4TaProcess;
    private Process? _launchServerProcess;

    private int _launchServerPort;
    private int _consolePort;

    private readonly Subject<(string Header, List<object> Chunks)> _gpgNetMessages = new();
    public IObservable<(string Header, List<object> Chunks)> GpgNetMessages => _gpgNetMessages;

    private GameLaunchMessage? _launchInfo;

    public GameLaunchService(
        ILogger<GameLaunchService> log,
        IFafServerAccessor faf,
        UserService us,
        PreferencesService prefs,
        PlayerService players,
        ILoggerFactory loggerFactory)
    {
        _log           = log;
        _faf           = faf;
        _us            = us;
        _prefs         = prefs;
        _players       = players;
        _loggerFactory = loggerFactory;

        // NOTE: HostGame/JoinGame are NOT real top-level FAF server message types —
        // confirmed against the reference Java client: those names only appear as
        // GPGNet-relay-internal messages (com.faforever.client.fa.relay.HostGameMessage),
        // exchanged between the main client and its own local relay process, never
        // sent by the lobby server itself. Listening for them here as if they were
        // server commands meant they could never fire, so the ICE adapter was never
        // actually told to host/join — see the direct _iceRpc.HostGameAsync/
        // JoinGameAsync calls in LaunchGameAsync, which is the correct trigger point.
        faf.Router.AddListener<ConnectToPeerRelayMessage> ("ConnectToPeer",       OnFafConnectToPeer);
        faf.Router.AddListener<DisconnectFromPeerRelayMessage>("DisconnectFromPeer", OnFafDisconnectFromPeer);
        faf.Router.AddListener<IceMsgRelayMessage>        ("IceMsg",              OnFafIceMsg);
        faf.Router.AddListener<IceServersMessage>         ("ice_servers",         OnFafIceServers);
    }

    // ── Main entry: called when FAF server sends game_launch ─────────────────

    /// <summary>
    /// Set by GameService immediately before sending a host or join request, so that
    /// when the resulting game_launch arrives and LaunchGameAsync runs, the launch
    /// sequence logs to the matching hostlog.txt/joinlog.txt instead of guessing.
    /// </summary>
    public bool LastActionWasHost { get; set; } = true;

    /// <summary>
    /// Set by GameService right before launching, when joining (not hosting) —
    /// the username of the game's host, needed to resolve their peer id for the
    /// ICE adapter's joinGame(login, peerId) RPC call. Unused when hosting.
    /// </summary>
    /// <summary>
    /// Set by GameService right before sending a host request — the map name we
    /// asked the server to host. The server's game_launch response does not
    /// reliably echo "mapname" back for a self-hosted game (observed empty in
    /// practice), so this is the fallback source of truth for what to actually
    /// tell the ICE adapter to host, instead of trusting msg.Mapname alone.
    /// </summary>
    public string? LastHostedMapName { get; set; }

    /// <summary>
    /// Set by GameService right before sending a host request — the mod we
    /// asked the server to host with. The server has reliably echoed "mod"
    /// back in every game_launch observed so far (unlike mapname, which is
    /// frequently empty), but this fallback costs nothing and protects
    /// against the same class of issue if that ever changes — without it,
    /// an unset/unreliable msg.Mod would silently fall back to "tacc"
    /// regardless of what mod was actually selected in the Host dialog.
    /// </summary>
    public string? LastHostedMod { get; set; }

    public string? HostUsernameForJoin { get; set; }

    public async Task LaunchGameAsync(GameLaunchMessage msg, CancellationToken ct = default)
    {
        _launchInfo = msg;
        void Log(string s) { if (LastActionWasHost) ActionLogger.Host(s); else ActionLogger.Join(s); }

        // CRITICAL: this method is normally invoked as a fire-and-forget task
        // ("_ = gameLaunchService.LaunchGameAsync(msg)" in GameService). Any
        // exception that escapes here becomes an unobserved task exception —
        // it gets swallowed silently (or can crash the process, depending on
        // runtime config) with NOTHING printed anywhere. That would look
        // exactly like "the launcher just never starts, no error, nothing".
        // Wrapping the whole body guarantees every failure is loud.
        try
        {
            Log($"game_launch processing started: uid={msg.Uid} mod={msg.Mod} map={msg.Mapname} rated={msg.IsRated}");
            _log.LogInformation("[LAUNCH] game_launch uid={Uid} mod={Mod} map={Map} rated={Rated}",
                msg.Uid, msg.Mod, msg.Mapname, msg.IsRated);
            Log($"Wine needed={WineDetector.NeedsWine} available={WineDetector.WineAvailable} path={WineDetector.FindWine() ?? "(not found)"}");
            Log($"Wine status: {WineDetector.StatusString()}");

            await TearDownAsync();
            FreePort47624();

            string mod = !string.IsNullOrEmpty(msg.Mod) ? msg.Mod : (LastHostedMod ?? "tacc");
            if (string.IsNullOrEmpty(msg.Mod) && !string.IsNullOrEmpty(LastHostedMod))
                Log($"server's game_launch had no mod — using the mod we asked to host: '{LastHostedMod}'");
            string gamePath = FindGamePath(mod);
            string nativeDir = FindNativeDir();
            Log($"nativeDir={nativeDir}");
            Log($"gamePath={gamePath}");
            Log($"talauncher={FindTaLauncher(nativeDir)} (exists={File.Exists(FindTaLauncher(nativeDir))})");
            Log($"gpgnet4ta={FindGpgNet4TaExe(nativeDir)} (exists={File.Exists(FindGpgNet4TaExe(nativeDir))})");
            Log($"icejar={FindIceAdapterJar()} (exists={File.Exists(FindIceAdapterJar())})");
            Log($"java={FindJava()}");

            int rpcPort          = FindFreePort();
            int gpgnetPort       = FindFreePort();
            _consolePort         = FindFreePort();
            _launchServerPort    = FindFreePort();

            Log($"ports: rpc={rpcPort} gpgnet={gpgnetPort} console={_consolePort} launchServer={_launchServerPort}");
            _log.LogInformation("[LAUNCH] ports: rpc={R} gpgnet={G} console={C} launchServer={L}",
                rpcPort, gpgnetPort, _consolePort, _launchServerPort);

            // ── Step 1: Start talauncher (launch server) ──────────────────────────
            // Mirrors: startLaunchServer(modTechnical, uid)
            Log("Step 1: starting talauncher (LaunchServer)...");
            _launchServerProcess = StartLaunchServer(nativeDir, _launchServerPort, msg.Uid);
            Log($"talauncher started: pid={_launchServerProcess?.Id ?? -1}");

            // ── Step 2: Launch ICE adapter ────────────────────────────────────────
            Log("Step 2: starting ICE adapter...");
            _iceAdapterProcess = LaunchIceAdapter(rpcPort, gpgnetPort, msg.Uid, ct);
            Log($"ICE adapter started: pid={_iceAdapterProcess?.Id ?? -1}");

            // ── Step 3: Connect RPC client ────────────────────────────────────────
            if (_iceAdapterProcess is null)
                throw new InvalidOperationException("ICE adapter process failed to start (see ICE adapter log lines above for the real cause — likely java not found)");

            // Give the JVM a brief moment to actually start listening before we
            // begin connecting, and bail immediately with a clear message if it
            // already died instead of retrying blindly for the full connect timeout.
            await Task.Delay(300, ct);
            if (_iceAdapterProcess.HasExited)
                throw new InvalidOperationException(
                    $"ICE adapter process exited immediately with code {_iceAdapterProcess.ExitCode} " +
                    "— check [ICE-PROC ERR] lines above for the Java error (often: java not installed, " +
                    "or the jar path is wrong)");

            Log("Step 3: connecting to ICE adapter RPC...");
            _iceRpc = new IceAdapterRpcClient(_loggerFactory.CreateLogger<IceAdapterRpcClient>());
            WireIceAdapterEvents(_iceRpc);
            await _iceRpc.ConnectAsync(rpcPort, ct);
            await _iceRpc.SetLobbyInitModeAsync("normal", ct);
            Log("ICE adapter RPC connected");

            // ── Step 4: Register DirectPlay ───────────────────────────────────────
            // Mirrors: getRegisterDplayCommand → launch → waitFor()
            Log("Step 4: registering DirectPlay...");
            await RegisterDplayAsync(nativeDir, mod, gamePath, msg.Uid, ct);
            Log("DirectPlay registered");

            // ── Step 5: Launch gpgnet4ta ──────────────────────────────────────────
            // Mirrors: getGpgNet4TaCommand with ALL required arguments
            //
            // CRITICAL ORDERING: gpgnet4ta must be started and CONFIRMED connected
            // to the ICE adapter's GPGNet server BEFORE we tell the ICE adapter to
            // actually host/join (the next step). The ICE adapter sends its
            // HostGame/JoinGame message over that GPGNet connection the moment
            // it's told to host/join — if gpgnet4ta isn't connected yet, there's
            // no recipient for that message at all. This was confirmed as the
            // root cause of "launch sequence completes but TAForever.ini is never
            // written / the game never opens": the previous ordering told the ICE
            // adapter to host before gpgnet4ta.exe even started as a process, so
            // its HostGame/JoinGame event handler (which calls CreateTAInitFile +
            // LaunchGameAsync) never had a chance to receive anything.
            //
            // A fixed delay here is NOT reliable — gpgnet4ta.exe startup + GPGNet
            // connect time has been observed varying widely across runs (the ICE
            // adapter RPC connection alone has taken anywhere from ~2.5s to over
            // 12s in logs collected during this session). Instead we wait for the
            // ICE adapter's own onConnectionStateChanged("Connected") notification
            // — the real, observable signal that gpgnet4ta's GPGNet client has
            // actually connected — with a generous timeout as a safety net rather
            // than blocking forever if that signal is somehow never delivered.
            var gpgConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnConnState(string state)
            {
                if (state == "Connected") gpgConnectedTcs.TrySetResult();
            }
            _iceRpc.OnConnectionStateChanged += OnConnState;

            Log("Step 5: starting gpgnet4ta...");
            _gpgNet4TaProcess = LaunchGpgNet4Ta(
                nativeDir, mod, gamePath, msg.Uid,
                gpgnetPort, _consolePort, _launchServerPort,
                msg.IsRated == true, msg.Args, ct);
            Log($"gpgnet4ta started: pid={_gpgNet4TaProcess?.Id ?? -1}");

            Log("Waiting for gpgnet4ta to connect to ICE adapter's GPGNet server...");
            try
            {
                await gpgConnectedTcs.Task.WaitAsync(TimeSpan.FromSeconds(20), ct);
                Log("gpgnet4ta confirmed connected to ICE adapter");
            }
            catch (TimeoutException)
            {
                Log("WARNING — gpgnet4ta did not confirm GPGNet connection within 20s. " +
                    "Proceeding anyway, but the upcoming host/join trigger may have no " +
                    "recipient if gpgnet4ta genuinely never connected.");
            }
            finally
            {
                _iceRpc.OnConnectionStateChanged -= OnConnState;
            }

            // ── Step 6: Tell the ICE adapter to actually host or join ──────────────
            // This is the trigger that makes the ICE adapter send HostGame/
            // JoinGame to gpgnet4ta over GPGNet — nothing does that automatically
            // just from gpgnet4ta connecting. Without this call, gpgnet4ta's
            // HostGame/JoinGame event handlers (which actually launch the TA
            // exe) never fire.
            if (LastActionWasHost)
            {
                string mapName = !string.IsNullOrEmpty(msg.Mapname) ? msg.Mapname : (LastHostedMapName ?? "");
                if (string.IsNullOrEmpty(msg.Mapname) && !string.IsNullOrEmpty(LastHostedMapName))
                    Log($"Step 6: server's game_launch had no mapname — using the map we asked to host: '{LastHostedMapName}'");
                Log($"Step 6: telling ICE adapter to host game, map='{mapName}'...");
                try
                {
                    await _iceRpc.HostGameAsync(mapName, ct);
                    Log("ICE adapter hostGame RPC call sent and acknowledged");
                }
                catch (TimeoutException tex)
                {
                    // Don't abort the whole launch sequence for this — hostGame can
                    // involve real ICE/STUN/TURN negotiation that may legitimately
                    // still be in progress on the adapter's side even after our
                    // timeout gives up waiting. Continuing lets gpgnet4ta/talauncher
                    // proceed; if the adapter genuinely never processed the call,
                    // the game just won't actually launch, which is still better
                    // than blocking the rest of the sequence on this one step.
                    Log($"Step 6: WARNING — {tex.Message}. Continuing anyway since this may " +
                        "just be slow ICE negotiation rather than an outright failure.");
                }
            }
            else
            {
                string? hostLogin = HostUsernameForJoin;
                var hostPlayer = hostLogin is not null ? _players.GetPlayerForUsername(hostLogin) : null;
                if (hostPlayer is null)
                {
                    Log($"Step 6: WARNING — could not resolve peer id for host '{hostLogin ?? "(unknown)"}' " +
                        "— joinGame RPC call cannot be made, the game will not actually launch. " +
                        "This usually means the host's username wasn't found in PlayerService's roster.");
                }
                else
                {
                    Log($"Step 6: telling ICE adapter to join host '{hostPlayer.Username}' (id={hostPlayer.Id})...");
                    try
                    {
                        await _iceRpc.JoinGameAsync(hostPlayer.Username, hostPlayer.Id, ct);
                        Log("ICE adapter joinGame RPC call sent and acknowledged");
                    }
                    catch (TimeoutException tex)
                    {
                        Log($"Step 6: WARNING — {tex.Message}. Continuing anyway since this may " +
                            "just be slow ICE negotiation rather than an outright failure.");
                    }
                }
            }

            Log("Launch sequence complete — gpgnet4ta will drive talauncher to actually start the game");
            _log.LogInformation("[LAUNCH] Launch sequence complete — gpgnet4ta will trigger TA via talauncher");
        }
        catch (Exception ex)
        {
            Log($"LAUNCH SEQUENCE FAILED: {ex.GetType().Name}: {ex.Message}");
            Log($"Stack trace: {ex.StackTrace}");
            _log.LogError(ex, "[LAUNCH] Launch sequence failed");
            Console.WriteLine($"[LAUNCH] *** LAUNCH FAILED *** {ex.GetType().Name}: {ex.Message}");

            // Tell the server this game session is over — without this, the
            // server may keep treating uid={msg.Uid} as "launching" indefinitely,
            // which can affect subsequent host/join attempts (e.g. a follow-up
            // host request getting no game_launch response at all). Mirrors the
            // real client's fafService.notifyGameEnded() call from its own
            // startGame() failure handler.
            try { _faf.NotifyGameEnded(); }
            catch (Exception notifyEx) { Log($"NotifyGameEnded also failed (non-fatal): {notifyEx.Message}"); }
        }
    }

    // ── talauncher (launch server) ────────────────────────────────────────────

    private Process? StartLaunchServer(string nativeDir, int port, int gameId)
    {
        string exe = FindTaLauncher(nativeDir);
        if (!File.Exists(exe))
        {
            _log.LogWarning("[LAUNCH] talauncher not found at {Exe}", exe);
            return null;
        }

        string logPath = GetLogPath("talauncher", gameId);
        var args = new List<string>
        {
            exe,
            "--bindport",  port.ToString(),
            "--logfile",   logPath,
            // Defense in depth alongside TaLaunch.LaunchClient's 3s keepalive
            // loop (started right after connecting) — talauncher's default is
            // 10s, which was confirmed too tight in practice: our own launch
            // sequence (DirectPlay registration, ICE adapter connect, etc.)
            // can take longer than that before the keepalive loop even gets a
            // chance to send its first ping, let alone if it ever stalls
            // briefly (GC pause, etc.). 30s gives real margin either way.
            "--keepalivetimeout", "30",
        };

        args = MaybeWine(args, exe);
        _log.LogInformation("[LAUNCH] StartLaunchServer: {Args}", string.Join(" ", args));
        Console.WriteLine($"[LAUNCH] talauncher: {string.Join(" ", args)}");
        return Launch(nativeDir, args);
    }

    // ── Register DirectPlay ───────────────────────────────────────────────────

    private async Task RegisterDplayAsync(string nativeDir, string mod,
        string gamePath, int uid, CancellationToken ct)
    {
        // Check the real registry first. --registerdplay's elevation path
        // (RunAs) was confirmed unreliable from real logs: every single
        // call returned in well under a second — far too fast for an actual
        // UAC dialog to have appeared and been answered — and the user
        // separately confirmed the dialog often doesn't show up at all on
        // fresh launches or after inactivity. Skipping straight to a
        // registry check avoids depending on that unreliable native call on
        // every single launch; once registered (via the standalone .reg
        // file generated in Settings, or a prior successful run), there's
        // nothing further to do here.
        if (DPlayRegFileGenerator.IsRegistered(mod))
        {
            string skipMsg = $"DirectPlay already registered for {mod} — skipping --registerdplay";
            if (LastActionWasHost) ActionLogger.Host(skipMsg); else ActionLogger.Join(skipMsg);
            Console.WriteLine($"[LAUNCH] DirectPlay already registered for {mod}, skipping registration step");
            return;
        }

        string attemptMsg = $"DirectPlay not yet registered for {mod} — attempting --registerdplay " +
            "(this may show a UAC prompt; if it doesn't appear or this keeps failing, " +
            "use the \"Gen .reg\" button in Settings instead and approve that file once)";
        if (LastActionWasHost) ActionLogger.Host(attemptMsg); else ActionLogger.Join(attemptMsg);

        string exe     = FindTaLauncher(nativeDir);
        string gameExe = Path.GetFileName(FindGameExe(gamePath));
        string logPath = GetLogPath("registerdplay", uid);

        var args = new List<string>
        {
            exe,
            "--registerdplay",
            "--gamemod",  mod,
            "--gamepath", gamePath,
            "--gameexe",  gameExe,
            "--logfile",  logPath,
        };

        args = MaybeWine(args, exe);
        _log.LogInformation("[LAUNCH] RegisterDplay: {Args}", string.Join(" ", args));
        Console.WriteLine($"[LAUNCH] registerdplay: {string.Join(" ", args)}");

        try
        {
            var psi = BuildPsi(nativeDir, args);
            using var p = Process.Start(psi)!;

            // talauncher may now be waiting on a REAL Windows UAC elevation
            // prompt (RunAs) — possible the user hasn't seen/approved it yet,
            // or it appeared on a different desktop/session context. Without
            // a bound here, this could hang indefinitely with the launch
            // sequence stuck silently at "registering DirectPlay..." and no
            // indication why. 30s gives a reasonable window to click through
            // a UAC prompt without making every launch needlessly slow if
            // it's already elevated/pre-approved.
            Console.WriteLine("[LAUNCH] Waiting for registerdplay to complete (may be showing a UAC prompt — check for it if this takes a while)...");
            try
            {
                await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(30), ct);
            }
            catch (TimeoutException)
            {
                _log.LogWarning("[LAUNCH] RegisterDplay did not exit within 30s — likely waiting on an unanswered UAC prompt. Continuing without waiting further; check for a UAC dialog if the game doesn't launch correctly.");
                Console.WriteLine("[LAUNCH] WARNING: registerdplay timed out after 30s — it's likely still waiting on a UAC prompt you haven't approved yet. Check your taskbar/other monitors for it. Continuing the launch sequence anyway.");
                return; // don't try to read p.ExitCode below — the process is still running
            }
            // NOTE: exit code 0 here does NOT reliably confirm the DirectPlay
            // registration actually succeeded. talauncher's --registerdplay
            // first checks CheckDplayLobbyableApplication; if that fails (the
            // normal case the first time), it calls Windows' RunAs() to
            // re-launch itself elevated for the actual registry write. That
            // outer process exits 0 regardless of whether the elevated
            // re-launch (and the registration inside it) succeeded — a
            // dismissed UAC prompt, a failed RunAs, or (under Wine, where
            // RunAs is a genuine Windows-only mechanism not meaningfully
            // implemented) the elevation simply not happening at all can
            // each produce exit code 0 with no actual registration having
            // occurred. The only genuine confirmation would come from
            // talauncher's own logfile (registerdplay_{uid}.log) showing the
            // registration actually completing, or from the subsequent
            // launchGame call succeeding rather than hitting
            // DPERR_UNKNOWNAPPLICATION.
            _log.LogInformation("[LAUNCH] RegisterDplay process exited {Code} " +
                "(NOTE: this does not confirm the DirectPlay registration itself succeeded — " +
                "check registerdplay_{Uid}.log for the real outcome)", p.ExitCode, uid);
            Console.WriteLine($"[LAUNCH] RegisterDplay process exited {p.ExitCode} " +
                $"(check registerdplay_{uid}.log for whether registration actually succeeded)");

            // Re-check the real registry now — this gives an honest,
            // immediate answer instead of making the user dig through a
            // logfile to find out whether the unreliable RunAs() elevation
            // actually worked this time.
            if (DPlayRegFileGenerator.IsRegistered(mod))
            {
                string confirmMsg = $"Confirmed: DirectPlay registration for {mod} now exists in the registry.";
                if (LastActionWasHost) ActionLogger.Host(confirmMsg); else ActionLogger.Join(confirmMsg);
            }
            else
            {
                string warnMsg = $"WARNING — DirectPlay registration for {mod} still does not exist after --registerdplay. " +
                    "Use the \"Gen .reg\" button in Settings and approve the UAC prompt for that file directly — " +
                    "this is the reliable path when --registerdplay's own elevation doesn't work.";
                if (LastActionWasHost) ActionLogger.Host(warnMsg); else ActionLogger.Join(warnMsg);
                Console.WriteLine($"[LAUNCH] WARNING: {mod} is still not registered. Use Settings > \"Gen .reg\" instead.");
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "[LAUNCH] RegisterDplay failed"); }
    }

    // ── gpgnet4ta ─────────────────────────────────────────────────────────────

    private Process? LaunchGpgNet4Ta(
        string nativeDir, string mod, string gamePath, int uid,
        int gpgnetPort, int consolePort, int launchServerPort,
        bool isRated, List<string>? extraArgs, CancellationToken _ct)
    {
        string exe     = FindGpgNet4TaExe(nativeDir);
        string logPath = GetLogPath("game", uid);
        string gpgUrl  = $"127.0.0.1:{gpgnetPort}";

        // API token for --hashtoken (used for server-side file verification)
        string apiBase  = "https://api.taforever.com";
        string apiToken = _us.AccessToken ?? "";

        // Demo compiler URL (TAF specific)
        string demoUrl  = "http://taforever.com:18765";

        var args = new List<string>
        {
            exe,
            "--lobbybindaddress", "127.0.0.1",
            "--consoleport",      consolePort.ToString(),
            "--gamemod",          mod,
            "--gameid",           uid.ToString(),
            "--gamepath",         gamePath,
            "--gpgnet",           gpgUrl,
            "--logfile",          logPath,
            "--launchserverport", launchServerPort.ToString(),
            "--democompilerurl",  demoUrl,
            "--maxpacketsize",    "992",
            "--hashendpoint",     $"{apiBase}/game/launch_codes",
            "--hashtoken",        apiToken,
            "--autolaunch",
        };

        if (isRated)  args.Add("--israted");

        // Extra args from game_launch message (e.g. "/ratingcolor", "d8d8d8d8",
        // "/numgames", "236") are arguments for the TA game executable itself,
        // not flags for gpgnet4ta's own command line — passing them through as
        // "--ratingcolor"/"--numgames" was wrong, since gpgnet4ta doesn't define
        // those options and would just silently ignore them, and the TA exe never
        // received them at all. Pass them through verbatim as a single delimited
        // string via --gameargs; GpgNetApp forwards them straight to the TA exe.
        if (extraArgs is not null && extraArgs.Count > 0)
        {
            args.Add("--gameargs");
            args.Add(string.Join(' ', extraArgs));
        }

        if (IsManagedDll(exe))
        {
            // GpgNetApp (C# port) — run via `dotnet exec gpgnet4ta.dll <args>`.
            // No Wine needed: this is a managed assembly, not a Windows binary.
            args.Insert(0, "exec");
            args.Insert(0, FindDotnetExe());
        }
        else
        {
            // Native binary path (Linux ELF or a Windows .exe override) — only
            // reaches here via TAF_GPGNET4TA_EXE or on native Linux.
            args = MaybeWine(args, exe);
        }

        _log.LogInformation("[LAUNCH] gpgnet4ta: {Args}", string.Join(" ", args));
        Console.WriteLine($"[LAUNCH] gpgnet4ta: {string.Join(" ", args)}");
        return Launch(nativeDir, args, label: "gpgnet4ta");
    }

    // ── ICE adapter ───────────────────────────────────────────────────────────

    private Process? LaunchIceAdapter(int rpcPort, int gpgnetPort, int gameUid, CancellationToken _)
    {
        string iceJar = FindIceAdapterJar();
        if (!File.Exists(iceJar))
        {
            _log.LogWarning("[LAUNCH] faf-ice-adapter not found at {Path}", iceJar);
            Console.WriteLine($"[LAUNCH] ICE adapter jar not found: {iceJar}");
            return null;
        }

        string java = FindJava();
        var args = new List<string>
        {
            // -D system properties MUST come before -jar — java only treats
            // them as JVM args in that position; after -jar they'd just be
            // ordinary program arguments instead, and LOG_DIR would never
            // actually reach Logback's ${LOG_DIR} substitution in the jar's
            // bundled logback.xml. Without this, faf-ice-adapter falls back
            // to writing ice-adapter.log under a literal
            // "LOG_DIR_IS_UNDEFINED" folder, which is its own default when
            // the property is unset — not a path we ever configured.
            $"-DLOG_DIR={LogPaths.IceAdapterLogDir}",
            "-jar", iceJar,
            "--id",           (_us.UserId?.ToString() ?? "0"),
            "--login",        (_us.Username ?? "unknown"),
            "--rpc-port",     rpcPort.ToString(),
            "--gpgnet-port",  gpgnetPort.ToString(),
            "--game-id",      gameUid.ToString(),
        };

        _log.LogInformation("[LAUNCH] ice-adapter: {Java} {Args}", java, string.Join(" ", args));
        Console.WriteLine($"[LAUNCH] ice-adapter: {java} {string.Join(" ", args)}");

        var psi = new ProcessStartInfo(java)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Deliberately NOT caught here — if java can't be found or the process
        // can't start, that needs to propagate up to LaunchGameAsync's try/catch
        // so it gets logged loudly to hostlog.txt/joinlog.txt with the real
        // exception. Swallowing it here and returning null was exactly what
        // produced "ICE adapter started: pid=-1" with zero explanation of why.
        var p = Process.Start(psi)
            ?? throw new InvalidOperationException($"Process.Start returned null for java at '{java}'");
        Console.WriteLine($"[LAUNCH] ICE adapter process started: pid={p.Id}");
        var pOut = p.StandardOutput;
        var pErr = p.StandardError;
        Task.Run(() => Drain(pOut, "[ICE-PROC]"));
        Task.Run(() => Drain(pErr, "[ICE-PROC ERR]"));
        return p;
    }

    private void WireIceAdapterEvents(IceAdapterRpcClient rpc)
    {
        rpc.OnGpgNetMessageReceived += (header, chunks) =>
        {
            // Log the actual content, not just the fact that *a* message
            // arrived — this is the GameState/GameOption value the client
            // is reading, e.g. "GameState [Lobby]" or "GameOption [SubState,
            // Battleroom]". Without this, every log only ever showed the
            // generic "[ICE-RPC] notification: onGpgNetMessageReceived"
            // line with no indication of which state was actually received,
            // even though the value itself was always being correctly
            // relayed to the FAF server via _faf.SendGpgMessage below.
            string chunksStr = string.Join(", ", chunks);
            _log.LogInformation("[GPGNET] {Header} [{Chunks}]", header, chunksStr);
            Console.WriteLine($"[GPGNET] {header} [{chunksStr}]");

            _gpgNetMessages.OnNext((header, chunks));
            _faf.SendGpgMessage(new GpgGameMessage(header, chunks));
        };
        rpc.OnIceMsg += (srcId, dstId, candidates) =>
            _faf.SendIceMsg(srcId, dstId, candidates);
        rpc.OnConnectionStateChanged += state =>
            Console.WriteLine($"[ICE] adapter state: {state}");
        rpc.OnIceConnectionStateChanged += (l, r, s) =>
            _log.LogInformation("[ICE] {L}↔{R}: {S}", l, r, s);
        rpc.OnConnected += (l, r, c) =>
            _log.LogInformation("[ICE] connected {L}↔{R}: {C}", l, r, c);
    }

    // ── FAF lobby → ICE adapter ───────────────────────────────────────────────

    private void OnFafConnectToPeer(ConnectToPeerRelayMessage msg)
        => _ = SafeRpc(() => _iceRpc?.ConnectToPeerAsync(msg.Username, msg.PeerUid, msg.Offer));

    private void OnFafDisconnectFromPeer(DisconnectFromPeerRelayMessage msg)
        => _ = SafeRpc(() => _iceRpc?.DisconnectFromPeerAsync(msg.Uid));

    private void OnFafIceMsg(IceMsgRelayMessage msg)
        => _ = SafeRpc(() => _iceRpc?.IceMsgAsync(msg.Sender, msg.Record));

    private void OnFafIceServers(IceServersMessage msg)
    {
        if (msg.Servers is null) return;
        var servers = msg.Servers.Select(s => new Dictionary<string, object>
        {
            ["urls"]       = (object)(s.Urls?.ToList() ?? (s.Url != null ? new List<string> { s.Url } : new List<string>())),
            ["username"]   = s.Username   ?? string.Empty,
            ["credential"] = s.Credential ?? string.Empty,
        }).ToList();
        _ = SafeRpc(() => _iceRpc?.SetIceServersAsync(servers));
    }

    // ── Path finders ─────────────────────────────────────────────────────────

    private static string FindNativeDir()
    {
        // Mirrors: getNativeGpgnet4taDir() = Paths.get(nativeDir, "lib").resolve("bin")
        foreach (var candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "natives", "bin"),
            Path.Combine(AppContext.BaseDirectory, "bin"),
            AppContext.BaseDirectory,
        })
            if (Directory.Exists(candidate)) return candidate;
        return AppContext.BaseDirectory;
    }

    private static string FindGpgNet4TaExe(string nativeDir)
    {
        // PRIORITY 1: the C# GpgNetApp port (gpgnet4ta.dll), run via `dotnet exec`.
        // The native gpgnet4ta binary shipped in natives/bin is a Linux x86-64 ELF
        // executable — it cannot run on macOS (Intel or Apple Silicon) under any
        // circumstance, Wine included (Wine only runs Windows PE binaries, not
        // Linux ELF). There is also no Windows .exe build of gpgnet4ta bundled,
        // so on non-Linux platforms the native binary path is always a dead end.
        // GpgNetApp is a full cross-platform C# rewrite of the same program —
        // use it everywhere except when an explicit override is given.
        string? env = Environment.GetEnvironmentVariable("TAF_GPGNET4TA_EXE");
        if (env is not null && File.Exists(env)) return env;

        // PRIORITY 1: a self-contained published gpgnet4ta.exe (Windows) or
        // gpgnet4ta (macOS/Linux self-contained build) — carries its own .NET
        // runtime, so it never depends on a system-wide dotnet install being
        // present. This is what PublishGpgNetAppSelfContained produces.
        string selfContainedName = OperatingSystem.IsWindows() ? "gpgnet4ta.exe" : "gpgnet4ta";
        foreach (var dir in new[] { AppContext.BaseDirectory, nativeDir })
        {
            string sc = Path.Combine(dir, selfContainedName);
            // Distinguish our self-contained C# build from the bundled native
            // Linux ELF (which lives only in nativeDir and is never our output) —
            // AppContext.BaseDirectory is the safe/preferred location to check first.
            if (dir == AppContext.BaseDirectory && File.Exists(sc)) return sc;
        }

        // PRIORITY 2: the managed gpgnet4ta.dll, run via `dotnet exec`. Only
        // useful on a machine that actually has a dotnet runtime installed —
        // which is guaranteed for a framework-dependent TafClient deployment,
        // but NOT for a self-contained one (see PRIORITY 1 above, which should
        // normally be found first in that case).
        foreach (var dir in new[]
        {
            AppContext.BaseDirectory,
            nativeDir,
            Path.Combine(AppContext.BaseDirectory, "..", "GpgNetApp"),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "GpgNetApp", "bin", "Release", "net10.0"),
        })
        {
            string dll = Path.Combine(dir, "gpgnet4ta.dll");
            if (File.Exists(dll)) return dll;
        }

        // PRIORITY 2 (Linux only): the native ELF binary actually works on Linux.
        if (OperatingSystem.IsLinux())
        {
            string nativeElf = Path.Combine(nativeDir, "gpgnet4ta");
            if (File.Exists(nativeElf)) return nativeElf;
        }

        // Fall back to whatever's configured, even if missing — diagnostics will
        // show the path so the cause is obvious rather than silently no-op'ing.
        return Path.Combine(AppContext.BaseDirectory, "gpgnet4ta.dll");
    }

    /// <summary>True if the resolved gpgnet4ta path is a managed .dll that must be run via `dotnet exec`.</summary>
    private static bool IsManagedDll(string exe) =>
        exe.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

    private static string FindTaLauncher(string nativeDir)
    {
        string? env = Environment.GetEnvironmentVariable("TAF_TALAUNCHER_EXE");
        if (env is not null && File.Exists(env)) return env;

        foreach (var dir in new[] { nativeDir, AppContext.BaseDirectory })
        {
            string p = Path.Combine(dir, "talauncher.exe");
            if (File.Exists(p)) return p;
        }
        return Path.Combine(nativeDir, "talauncher.exe");
    }

    private static string FindIceAdapterJar()
    {
        string? env = Environment.GetEnvironmentVariable("TAF_ICE_ADAPTER_JAR");
        if (env is not null && File.Exists(env)) return env;

        foreach (var candidate in new[]
        {
            Path.Combine(AppContext.BaseDirectory, "faf-ice-adapter.jar"),
            Path.Combine(AppContext.BaseDirectory, "natives", "faf-ice-adapter.jar"),
        })
            if (File.Exists(candidate)) return candidate;
        return Path.Combine(AppContext.BaseDirectory, "faf-ice-adapter.jar");
    }

    private string FindGamePath(string mod)
    {
        // 1. User-set path from Settings tab
        string savedExe = _prefs.GetMod(mod).ExePath;
        if (!string.IsNullOrEmpty(savedExe))
        {
            string gameRoot = Directory.Exists(savedExe)
                ? savedExe
                : Path.GetDirectoryName(savedExe) ?? savedExe;

            if (Directory.Exists(gameRoot))
            {
                Console.WriteLine($"[LAUNCH] Using user-set game path for {mod}: {gameRoot}");
                return gameRoot;
            }
            Console.WriteLine($"[LAUNCH] User-set path for {mod} invalid, falling back: {gameRoot}");
        }

        // 2. Env vars
        string? env = Environment.GetEnvironmentVariable($"TAF_GAME_PATH_{mod.ToUpper()}")
                   ?? Environment.GetEnvironmentVariable("TAF_GAME_PATH");
        if (env is not null) return env;

        // 3. Default install path: ~/TotalAnnihilation/{mod}
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "TotalAnnihilation", mod);
    }

    private static string FindGameExe(string gamePath)
    {
        foreach (var name in new[] { "TotalA.exe", "totala.exe", "TotalAnnihilation.exe" })
        {
            string p = Path.Combine(gamePath, name);
            if (File.Exists(p)) return p;
        }
        return Path.Combine(gamePath, "TotalA.exe");
    }

    private void CacheJavaPath(string path)
    {
        if (_prefs.Preferences.JavaPath == path) return; // already cached, avoid redundant saves
        _prefs.Preferences.JavaPath = path;
        try { _prefs.Save(); }
        catch (Exception ex) { Console.WriteLine($"[LAUNCH] Could not persist Java path to preferences: {ex.Message}"); }
    }

    private string FindJava()
    {
        // Cached from a previous successful launch — checked first since search
        // paths (PATH, common install dirs) aren't always reliable across runs,
        // e.g. if the app is started in a different context with a different
        // PATH than when Java was last successfully found.
        string? cached = _prefs.Preferences.JavaPath;
        if (!string.IsNullOrEmpty(cached) && File.Exists(cached))
        {
            Console.WriteLine($"[LAUNCH] Using cached Java path: {cached}");
            return cached;
        }

        string? javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (javaHome is not null)
        {
            string java = Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
            if (File.Exists(java)) { CacheJavaPath(java); return java; }
        }

        string exeName = OperatingSystem.IsWindows() ? "java.exe" : "java";

        // Search PATH explicitly — Process.Start with UseShellExecute=false does NOT
        // reliably resolve bare command names against PATH on every platform/config,
        // and a failed resolution throws Win32Exception with no useful message,
        // which is exactly what was happening here (ICE adapter silently failing
        // to start because "java.exe" alone couldn't be found).
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is not null)
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator))
            {
                if (string.IsNullOrEmpty(dir)) continue;
                string candidate = Path.Combine(dir, exeName);
                if (File.Exists(candidate)) { CacheJavaPath(candidate); return candidate; }
            }
        }

        // Common install locations as a last resort
        var candidates = OperatingSystem.IsWindows()
            ? new[]
              {
                  @"C:\Program Files\Java",
                  @"C:\Program Files (x86)\Java",
                  @"C:\Program Files\Eclipse Adoptium",
              }
            : new[]
              {
                  "/usr/lib/jvm",
                  "/Library/Java/JavaVirtualMachines",
                  "/opt/homebrew/opt/openjdk/bin",
                  "/usr/local/opt/openjdk/bin",
              };

        foreach (var baseDir in candidates)
        {
            try
            {
                if (!Directory.Exists(baseDir)) continue;
                // Direct bin/java(.exe) under this dir
                string direct = Path.Combine(baseDir, exeName);
                if (File.Exists(direct)) { CacheJavaPath(direct); return direct; }
                // One level down (versioned JDK folders), e.g. .../jdk-21/bin/java.exe
                foreach (var sub in Directory.GetDirectories(baseDir))
                {
                    string candidate = Path.Combine(sub, "bin", exeName);
                    if (File.Exists(candidate)) { CacheJavaPath(candidate); return candidate; }
                    // macOS JDKs often nest under Contents/Home/bin
                    string macCandidate = Path.Combine(sub, "Contents", "Home", "bin", exeName);
                    if (File.Exists(macCandidate)) { CacheJavaPath(macCandidate); return macCandidate; }
                }
            }
            catch { /* directory enumeration is best-effort */ }
        }

        Console.WriteLine($"[LAUNCH] WARNING: Java not found anywhere searched (JAVA_HOME, PATH, " +
            $"{string.Join(", ", candidates)}) — falling back to bare '{exeName}', which will fail " +
            "to start unless it happens to resolve via the OS's own PATH search at process-start time. " +
            "If Java is installed, set JAVA_HOME or add its bin directory to PATH.");
        return exeName; // give up — let Process.Start try, the error will at least be loud now
    }

    // ── Wine helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Prepend Wine if on macOS/Linux and exe is a .exe file.
    /// Mirrors: if (Platform.isLinux()) command.add("wine")
    /// </summary>
    private static List<string> MaybeWine(List<string> args, string exe)
    {
        if (!WineDetector.NeedsWine) return args;
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return args;
        return WineDetector.PrependWine(args);
    }

    // ── Process helpers ───────────────────────────────────────────────────────

    /// <summary>Locates the dotnet executable used to run managed .dll ports like GpgNetApp.</summary>
    private static string FindDotnetExe()
    {
        string? env = Environment.GetEnvironmentVariable("TAF_DOTNET_EXE");
        if (env is not null && File.Exists(env)) return env;

        // The currently-running process IS dotnet (or a self-contained apphost that
        // embeds it) — reuse its directory first, since that's guaranteed to have a
        // matching runtime version for the gpgnet4ta.dll we're about to run.
        string? procPath = Environment.ProcessPath;
        if (procPath is not null)
        {
            string name = Path.GetFileNameWithoutExtension(procPath);
            if (string.Equals(name, "dotnet", StringComparison.OrdinalIgnoreCase))
                return procPath;
        }

        string exeName = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrEmpty(dir)) continue;
            string full = Path.Combine(dir, exeName);
            if (File.Exists(full)) return full;
        }

        // Common install locations as a last resort — platform-specific, since a
        // Unix-style absolute path like "/usr/local/share/dotnet/dotnet" resolves
        // on Windows to a path under the CURRENT DRIVE ROOT (e.g. "Z:\usr\local\..."),
        // not "doesn't exist". On a mapped network drive that coincidentally has a
        // matching directory structure, File.Exists can return true for a path that
        // is NOT a real dotnet install, which is exactly what happened here:
        // Process.Start was handed a bogus resolved path and returned null instead
        // of throwing. Only check paths that are valid for the current platform.
        string? dotnetRootEnv = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var candidates = OperatingSystem.IsWindows()
            ? new[]
              {
                  dotnetRootEnv is not null ? Path.Combine(dotnetRootEnv, "dotnet.exe") : null,
                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe"),
                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet", "dotnet.exe"),
                  @"C:\Program Files\dotnet\dotnet.exe",
              }
            : new[]
              {
                  dotnetRootEnv is not null ? Path.Combine(dotnetRootEnv, "dotnet") : null,
                  "/usr/local/share/dotnet/dotnet",
                  "/usr/local/bin/dotnet",
                  "/opt/homebrew/bin/dotnet",
                  "/usr/bin/dotnet",
              };

        foreach (var candidate in candidates)
            if (candidate is not null && File.Exists(candidate)) return candidate;

        return exeName; // let Process.Start try PATH resolution as a fallback
    }

    private Process Launch(string workingDir, List<string> args, string? label = null)
    {
        if (args.Count == 0)
            throw new ArgumentException("Launch called with an empty args list — nothing to execute");
        if (args[0] is null)
            throw new ArgumentException("Launch called with args[0] == null — the executable path was never resolved");

        Console.WriteLine($"[LAUNCH] Launch(): workingDir='{workingDir}', exe='{args[0]}', argCount={args.Count}");

        ProcessStartInfo psi;
        try
        {
            psi = BuildPsi(workingDir, args);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LAUNCH] BuildPsi FAILED: {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        Process? p;
        try
        {
            p = Process.Start(psi);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[LAUNCH] Process.Start FAILED for '{args[0]}': {ex.GetType().Name}: {ex.Message}");
            throw;
        }

        if (p is null)
        {
            // Per .NET docs this should be unreachable with UseShellExecute=false,
            // but guard it explicitly anyway — silently dereferencing a null Process
            // is exactly what produced an unexplained NullReferenceException here
            // before this rewrite. Now it's a clear, specific message instead.
            throw new InvalidOperationException(
                $"Process.Start returned null for '{args[0]}' with no exception thrown — " +
                "this is unexpected with UseShellExecute=false and may indicate a Windows " +
                "process-reuse edge case or an antivirus/security product intercepting the launch.");
        }

        string tag = label ?? Path.GetFileName(args[0]) ?? "process";
        Console.WriteLine($"[LAUNCH] Started '{tag}', pid={p.Id}");

        try
        {
            _ = Task.Run(() => Drain(p.StandardOutput, $"[{tag}]"));
            _ = Task.Run(() => Drain(p.StandardError,  $"[{tag} ERR]"));
        }
        catch (Exception ex)
        {
            // Don't let a stdout/stderr drain wiring failure take down the whole
            // launch — the process is already running at this point.
            Console.WriteLine($"[LAUNCH] Could not wire up output draining for '{tag}': {ex.Message}");
        }

        _log?.LogInformation("[LAUNCH] PID={Pid} {Exe}", p.Id, tag);
        return p;
    }

    private static ProcessStartInfo BuildPsi(string workingDir, List<string> args)
    {
        var psi = new ProcessStartInfo(args[0])
        {
            WorkingDirectory       = workingDir,
            UseShellExecute        = false,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            CreateNoWindow         = true,
        };
        for (int i = 1; i < args.Count; i++) psi.ArgumentList.Add(args[i]);

        if (WineDetector.NeedsWine)
            foreach (var (k, v) in WineDetector.GetWineEnvironment())
                psi.Environment[k] = v;

        if (OperatingSystem.IsMacOS())
            psi.Environment.Remove("DISPLAY");

        return psi;
    }

    private void Drain(System.IO.StreamReader r, string prefix)
    {
        try { string? l; while ((l = r.ReadLine()) is not null) { Console.WriteLine($"{prefix} {l}"); } }
        catch { }
    }

    private static string GetLogPath(string name, int uid) => LogPaths.ForGameProcess(name, uid);

    private static int FindFreePort()
    {
        using var s = new TcpListener(IPAddress.Loopback, 0);
        s.Start();
        int port = ((IPEndPoint)s.LocalEndpoint).Port;
        s.Stop();
        return port;
    }

    /// <summary>
    /// Mirrors: freePort47624() — kills existing gpgnet4ta/dplaysvr processes
    /// so port 47624 is available for DirectPlay enumeration.
    /// </summary>
    private void FreePort47624()
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                foreach (var exe in new[] { "dplaysvr.exe", "gpgnet4ta" })
                    RunAndForget("killall", [exe]);
            }
            else if (OperatingSystem.IsWindows())
            {
                RunAndForget("taskkill", ["/F", "/IM", "gpgnet4ta.exe"]);
            }
        }
        catch { }
    }

    private static void RunAndForget(string exe, string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.WaitForExit(2000);
        }
        catch { }
    }

    private async Task SafeRpc(Func<Task?> fn)
    {
        try { var t = fn(); if (t is not null) await t; }
        catch (Exception ex) { _log.LogWarning(ex, "[LAUNCH] RPC call failed"); }
    }

    // ── Teardown ──────────────────────────────────────────────────────────────

    /// <summary>
    /// User-initiated leave — tears down talauncher/ICE adapter/gpgnet4ta
    /// for the current session and tells the server this game session is
    /// over, without disposing the whole service (unlike DisposeAsync,
    /// which is only called on app shutdown). Used by the staging/lobby
    /// screen's "Leave" button.
    /// </summary>
    /// <summary>
    /// Sends "/launch" to gpgnet4ta's console port — the actual trigger
    /// that makes it proceed past the staging/lobby room and start the
    /// real game (ported from taflib::ConsoleReader / the real client's
    /// "/launch" extended message). Called by the staging screen's "Start"
    /// button. Safe to call even if gpgnet4ta isn't listening yet or has
    /// already exited — failures are logged but not thrown, since this is
    /// a best-effort UI action, not something the rest of the launch
    /// sequence depends on.
    /// </summary>
    public async Task StartGameAsync()
    {
        if (_consolePort <= 0)
        {
            _log.LogWarning("[LAUNCH] StartGameAsync called but no console port is set — was a game ever launched?");
            Console.WriteLine("[LAUNCH] Cannot send /launch — no console port set (no active game launch?)");
            return;
        }
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            await tcp.ConnectAsync(System.Net.IPAddress.Loopback, _consolePort);
            await using var stream = tcp.GetStream();
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes("/launch");
            await stream.WriteAsync(bytes);
            await stream.FlushAsync();
            Console.WriteLine($"[LAUNCH] Sent /launch to gpgnet4ta console port {_consolePort}");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[LAUNCH] StartGameAsync failed to reach gpgnet4ta's console port {Port}", _consolePort);
            Console.WriteLine($"[LAUNCH] Failed to send /launch: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task LeaveGameAsync()
    {
        await TearDownAsync();
        try { _faf.NotifyGameEnded(); }
        catch (Exception ex) { _log.LogWarning(ex, "[LAUNCH] NotifyGameEnded failed during LeaveGameAsync"); }
    }

    private async Task TearDownAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        if (_iceRpc is not null)
        {
            try { await _iceRpc.QuitAsync(); } catch { }
            try { await _iceRpc.DisposeAsync(); } catch (Exception ex) { _log.LogWarning(ex, "[LAUNCH] iceRpc dispose failed"); }
            _iceRpc = null;
        }

        // Kill all three processes IN PARALLEL rather than sequentially — each
        // Kill() call can block for up to 3s in WaitForExit, and running them
        // one after another could compound to ~9s of pure waiting before the
        // next launch attempt even starts, which was confirmed happening in
        // practice (a 10+ second gap appeared between "processing started"
        // and "nativeDir=" on the second/third host attempt in a session,
        // exactly matching 3x WaitForExit(3000) back to back).
        var killTasks = new[]
        {
            Task.Run(() => Kill(_gpgNet4TaProcess,    "gpgnet4ta")),
            Task.Run(() => Kill(_iceAdapterProcess,   "faf-ice-adapter")),
            Task.Run(() => Kill(_launchServerProcess, "talauncher")),
        };
        await Task.WhenAll(killTasks);
        _gpgNet4TaProcess = _iceAdapterProcess = _launchServerProcess = null;

        if (sw.ElapsedMilliseconds > 1000)
        {
            _log.LogWarning("[LAUNCH] TearDownAsync took {Ms}ms — a prior process may not have exited cleanly", sw.ElapsedMilliseconds);
            Console.WriteLine($"[LAUNCH] TearDownAsync took {sw.ElapsedMilliseconds}ms (slower than expected — check for a hung talauncher/gpgnet4ta/ICE adapter process)");
        }
    }

    private void Kill(Process? p, string name)
    {
        if (p is null) return;
        try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(3000); } }
        catch (Exception ex) { _log.LogWarning(ex, "[LAUNCH] Could not kill {Name}", name); }
        finally { p.Dispose(); }
    }

    public async ValueTask DisposeAsync()
    {
        await TearDownAsync();
        _gpgNetMessages.Dispose();
    }
}

// ─── Relay message types ──────────────────────────────────────────────────────

public class GpgGameMessage(string header, List<object> chunks)
{
    public string       Header { get; } = header;
    public List<object> Chunks { get; } = chunks;
}


public class ConnectToPeerRelayMessage : TafClient.Net.Domain.FafServerMessage { public string Username { get; set; } = ""; public int PeerUid { get; set; } public bool Offer { get; set; } }
public class DisconnectFromPeerRelayMessage : TafClient.Net.Domain.FafServerMessage { public int Uid { get; set; } }
public class IceMsgRelayMessage        : TafClient.Net.Domain.FafServerMessage { public int Sender { get; set; } public string Record { get; set; } = ""; }
public class IceServersMessage         : TafClient.Net.Domain.FafServerMessage
{
    public List<IceServer>? Servers { get; set; }
    public class IceServer
    {
        public string?   Url        { get; set; }
        public string[]? Urls       { get; set; }
        public string?   Username   { get; set; }
        public string?   Credential { get; set; }
    }
}
