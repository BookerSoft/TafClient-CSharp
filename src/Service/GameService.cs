using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using TafClient.Domain;
using TafClient.Net;
using TafClient.Net.Domain;

namespace TafClient.Service;

public class GameService
{
    public const string CustomGameChannelRegex = @"^#.+\[.+\]$";

    private readonly ILogger<GameService> _log;
    private readonly IFafServerAccessor _faf;
    private readonly PlayerService _playerService;
    private readonly MapService _mapService;  // for auto-download
    private readonly GameLaunchService _gameLaunchService;

    private readonly ConcurrentDictionary<int, Game> _uidToGame = new();

    private readonly Subject<Game> _gameAdded   = new();
    private readonly Subject<Game> _gameUpdated = new();
    private readonly Subject<Game> _gameRemoved = new();

    public ObservableCollection<Game> Games { get; } = [];
    public IObservable<Game> GameAdded   => _gameAdded;
    public IObservable<Game> GameUpdated => _gameUpdated;
    public IObservable<Game> GameRemoved => _gameRemoved;

    private Game? _currentGame;
    private readonly object _currentGameLock = new();
    private readonly Subject<Game?> _currentGameChanged = new();
    public IObservable<Game?> CurrentGameChanged => _currentGameChanged;

    public Game? CurrentGame
    {
        get { lock (_currentGameLock) return _currentGame; }
        private set
        {
            lock (_currentGameLock) _currentGame = value;
            _currentGameChanged.OnNext(value);
        }
    }

    public GameService(ILogger<GameService> log, IFafServerAccessor faf,
                       PlayerService playerService, MapService mapService,
                       GameLaunchService gameLaunchService)
    {
        _log = log;
        _faf = faf;
        _playerService = playerService;
        _mapService = mapService;
        _gameLaunchService = gameLaunchService;

        // Wire game_launch → GameLaunchService (ICE adapter + gpgnet4ta)
        faf.Router.AddListener<GameLaunchMessage>("game_launch", msg =>
        {
            _log.LogInformation("[GameService] game_launch received uid={Uid}", msg.Uid);
            Console.WriteLine($"[GameService] game_launch received uid={msg.Uid} — starting launch sequence");
            _ = gameLaunchService.LaunchGameAsync(msg).ContinueWith(t =>
            {
                if (t.Exception is not null)
                {
                    // LaunchGameAsync already catches everything internally and logs
                    // to hostlog/joinlog — this is a last-resort safety net so a
                    // fire-and-forget task can never silently vanish if some future
                    // change reintroduces an unguarded throw.
                    _log.LogError(t.Exception, "[GameService] LaunchGameAsync threw unexpectedly");
                    Console.WriteLine($"[GameService] *** LaunchGameAsync threw unexpectedly: {t.Exception.GetBaseException().Message}");
                }
            }, TaskScheduler.Default);
        });

        faf.Router.AddListener<GameInfoMessage>("game_info", OnGameInfo);

        faf.ConnectionStateChanged.Subscribe(state =>
        {
            if (state == ConnectionState.Disconnected)
            {
                _uidToGame.Clear();
                Games.Clear();
            }
        });
    }

    /// <summary>The server's stated reason for the most recent host/join rejection, if any.</summary>
    public string? LastGameLaunchFailReason => _faf.LastGameLaunchFailReason;

    public async Task<GameLaunchMessage?> HostGameAsync(NewGameInfo info)
    {
        _gameLaunchService.LastActionWasHost = true;
        _gameLaunchService.LastHostedMapName = info.Map;
        _gameLaunchService.LastHostedMod     = info.FeaturedModTechnicalName;
        ActionLogger.Host($"=== Host request: title='{info.Title}' map='{info.Map}' mod='{info.FeaturedModTechnicalName}' " +
                           $"maxPlayers={info.MaxPlayers} ===");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await _faf.RequestHostGame(info).WaitAsync(TimeSpan.FromSeconds(20));
            if (result is null)
                ActionLogger.Host($"Server rejected host request after {sw.ElapsedMilliseconds}ms — reason: {_faf.LastGameLaunchFailReason ?? "(server sent no game_launch and no rejection notice — possible connection issue)"}");
            else
                ActionLogger.Host($"game_launch received after {sw.ElapsedMilliseconds}ms: uid={result.Uid} mod={result.Mod} map={result.Mapname}");
            return result;
        }
        catch (TimeoutException)
        {
            ActionLogger.Host($"TIMEOUT after {sw.ElapsedMilliseconds}ms waiting for game_launch — server may not have responded");
            return null;
        }
        catch (Exception ex)
        {
            ActionLogger.Host($"ERROR after {sw.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
    /// <summary>
    /// Mirrors GameService.requestJoinGame() which calls:
    ///   mapService.optionalEnsureMap(game.getFeaturedMod(), game.getMapName(),
    ///       game.getMapCrc(), game.getMapArchiveName(), null, null)
    /// before requesting the join.
    /// </summary>
    public async Task<GameLaunchMessage?> JoinGameAsync(int gameId, string? password)
    {
        _gameLaunchService.LastActionWasHost = false;
        ActionLogger.Join($"=== Join request: gameId={gameId} ===");

        // Find the game in our list to get map details
        var game = _uidToGame.TryGetValue(gameId, out var g) ? g : null;
        _gameLaunchService.HostUsernameForJoin = game?.Host;
        if (game is not null && !string.IsNullOrEmpty(game.MapArchiveName))
        {
            ActionLogger.Join($"Game found: title='{game.Title}' map={game.MapName} archive={game.MapArchiveName} mod={game.FeaturedMod}");
            _log.LogInformation("Ensuring map {Archive} before joining game {Id}",
                game.MapArchiveName, gameId);

            // Auto-download if not installed — mirrors optionalEnsureMap.
            // A real CancellationToken is threaded through (not just WaitAsync, which
            // only stops awaiting from here — it doesn't cancel the HTTP call itself,
            // so a stuck download would otherwise keep running and the join would
            // never actually proceed even though this method "returned").
            bool mapOk;
            ActionLogger.Join($"Ensuring map archive='{game.MapArchiveName}' mod={game.FeaturedMod}...");
            var swMap = System.Diagnostics.Stopwatch.StartNew();
            using var mapCts = CancellationTokenSource.CreateLinkedTokenSource(default(CancellationToken));
            mapCts.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                mapOk = await _mapService.EnsureMapAsync(
                    game.FeaturedMod,
                    game.MapName,
                    game.MapCrc,
                    game.MapArchiveName.Contains('/') ? game.MapArchiveName.Split('/')[0] : game.MapArchiveName,
                    progress: null,
                    ct: mapCts.Token);
                ActionLogger.Join($"EnsureMap completed in {swMap.ElapsedMilliseconds}ms, ok={mapOk}");
            }
            catch (OperationCanceledException)
            {
                _log.LogWarning("Map download timed out after {Elapsed}ms for {Archive} — joining anyway",
                    swMap.ElapsedMilliseconds, game.MapArchiveName);
                ActionLogger.Join($"EnsureMap TIMED OUT after {swMap.ElapsedMilliseconds}ms — proceeding to join anyway");
                mapOk = false;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Map download failed for {Archive} — joining anyway", game.MapArchiveName);
                ActionLogger.Join($"EnsureMap FAILED after {swMap.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message} — proceeding to join anyway");
                mapOk = false;
            }

            if (!mapOk)
                _log.LogWarning("Could not ensure map {Archive} — joining anyway", game.MapArchiveName);
        }
        else
        {
            ActionLogger.Join($"Game id={gameId} not found in local game list, or has no map archive — joining without map check");
        }

        ActionLogger.Join($"Sending join_game request to server for game id={gameId}...");
        var swServer = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var result = await _faf.RequestJoinGame(gameId, password).WaitAsync(TimeSpan.FromSeconds(20));
            ActionLogger.Join($"Server responded in {swServer.ElapsedMilliseconds}ms: " +
                               (result is null
                                   ? $"null — reason: {_faf.LastGameLaunchFailReason ?? "(no rejection notice received)"}"
                                   : $"uid={result.Uid} mod={result.Mod} map={result.Mapname}"));
            return result;
        }
        catch (TimeoutException)
        {
            ActionLogger.Join($"Server did not respond within 20s (waited {swServer.ElapsedMilliseconds}ms total) — " +
                               "the request was sent (see hostlog/joinlog 'Sending' line above) but no game_launch or " +
                               "game_join_fail notice ever arrived. This points to a server-side or connection issue, " +
                               "not the map download.");
            return null;
        }
        catch (Exception ex)
        {
            ActionLogger.Join($"ERROR after {swServer.ElapsedMilliseconds}ms: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
    }
    public Game? GetByUid(int uid) => _uidToGame.TryGetValue(uid, out var g) ? g : null;

    private void OnGameInfo(GameInfoMessage msg)
    {
        if (msg.Games is not null) { foreach (var sub in msg.Games) OnGameInfo(sub); return; }
        if (msg.Uid is null) return;

        Player? currentPlayer = _playerService.CurrentPlayer;
        Game game = CreateOrUpdateGame(msg);

        if (currentPlayer is not null)
        {
            bool currentPlayerInGame = msg.Uid == currentPlayer.CurrentGameUid;
            if (currentPlayerInGame && game.Status.IsOpen())
                CurrentGame = game;
            else if (CurrentGame?.Id == game.Id && !currentPlayerInGame)
                CurrentGame = null;
        }

        if (game.Status == GameStatus.Ended)
        {
            RemoveGame(msg.Uid.Value);
            if (CurrentGame?.Id == game.Id) CurrentGame = null;
        }
    }

    private Game CreateOrUpdateGame(GameInfoMessage msg)
    {
        int uid = msg.Uid!.Value;
        bool isNew = !_uidToGame.TryGetValue(uid, out var game);
        if (isNew) { game = new Game(); _uidToGame[uid] = game; }
        UpdateFromGameInfo(msg, game!);
        if (isNew) { Games.Add(game!); _gameAdded.OnNext(game!); }
        else _gameUpdated.OnNext(game!);
        return game!;
    }

    private static void UpdateFromGameInfo(GameInfoMessage msg, Game game)
    {
        game.Id = msg.Uid!.Value;
        Console.WriteLine($"[GAME_INFO] uid={msg.Uid} host={msg.Host} num_players={msg.NumPlayers} " +
                          $"teams={System.Text.Json.JsonSerializer.Serialize(msg.Teams)} " +
                          $"state={msg.State}");

        // Note: do NOT early-return on null Host — server sends partial updates
        // (e.g. just num_players changing) that still need to be applied
        if (msg.Host    is not null) game.Host             = msg.Host;
        if (msg.Title   is not null) game.Title            = System.Web.HttpUtility.HtmlDecode(msg.Title);
        if (msg.MapName is not null) game.MapName          = msg.MapName;
        if (msg.FeaturedMod        is not null) game.FeaturedMod         = msg.FeaturedMod;
        if (msg.FeaturedModVersion is not null) game.FeaturedModVersion  = msg.FeaturedModVersion;
        if (msg.NumPlayers  is not null) game.NumPlayers      = msg.NumPlayers.Value;
        if (msg.MaxPlayers  is not null) game.MaxPlayers      = msg.MaxPlayers.Value;
        if (msg.PasswordProtected is not null) game.PasswordProtected = msg.PasswordProtected.Value;
        if (msg.State       is not null) game.Status          = GameStatusExtensions.Parse(msg.State);
        if (msg.RatingType  is not null) game.RatingType       = msg.RatingType;
        if (msg.RatingMin.HasValue) game.MinRating = (int)msg.RatingMin.Value;
        if (msg.RatingMax.HasValue) game.MaxRating = (int)msg.RatingMax.Value;
        if (msg.EnforceRatingRange is not null) game.EnforceRating = msg.EnforceRatingRange.Value;
        if (msg.ReplayDelaySeconds is not null) game.ReplayDelaySeconds = msg.ReplayDelaySeconds.Value;
        if (msg.GalacticWarPlanetName is not null) game.GalacticWarPlanetName = msg.GalacticWarPlanetName;
        if (msg.Visibility  is not null) game.Visibility      = GameVisibilityExtensions.Parse(msg.Visibility);
        if (msg.GameType    is not null) game.GameType         = ParseGameType(msg.GameType);

        if (msg.MapFilePath is not null)
        {
            string[] parts = msg.MapFilePath.Split('/');
            if (parts.Length >= 3) { game.MapArchiveName = parts[0]; game.MapCrc = parts[2]; }
        }

        if (msg.LaunchedAt.HasValue)
            game.StartTime = DateTimeOffset.FromUnixTimeSeconds((long)msg.LaunchedAt.Value);

        if (msg.Teams is not null)
        {
            game.Teams.Clear();
            foreach (var (k, v) in msg.Teams) game.Teams[k] = v;
        }

        // Always derive NumPlayers from Teams when teams is populated —
        // the server's num_players can lag behind the actual team membership
        if (game.Teams.Count > 0)
        {
            int fromTeams = game.Teams.Values.Sum(players => players.Count);
            if (fromTeams > game.NumPlayers)
                game.NumPlayers = fromTeams;
        }

        if (msg.SimMods is not null)
        {
            game.SimMods.Clear();
            foreach (var (k, v) in msg.SimMods) game.SimMods[k] = v;
        }

        if (msg.Pings is not null)
        {
            game.Pings.Clear();
            foreach (var (k, v) in msg.Pings) game.Pings[k] = v;
        }
    }

    private void RemoveGame(int uid)
    {
        if (_uidToGame.TryRemove(uid, out var game))
        {
            Games.Remove(game);
            _gameRemoved.OnNext(game);
        }
    }

    private static GameType ParseGameType(string? s) => s?.ToLowerInvariant() switch
    {
        "matchmaker" => GameType.Matchmaker,
        "coop"       => GameType.Coop,
        "ladder"     => GameType.Ladder,
        _            => GameType.Normal,
    };
}
