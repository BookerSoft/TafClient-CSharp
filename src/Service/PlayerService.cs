using System.Collections.Concurrent;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using TafClient.Domain;
using TafClient.Net;
using TafClient.Net.Domain;

namespace TafClient.Service;

/// <summary>
/// Port of <c>com.faforever.client.player.PlayerService</c>.
///
/// Java used two ObservableMaps (playersByName, playersById) synchronised with
/// FXCollections. Here we use ConcurrentDictionary for thread-safety and
/// System.Reactive Subject&lt;T&gt; for player online/offline events.
///
/// The afterPropertiesSet() listener registrations are done in the constructor
/// (no Spring lifecycle needed).
/// </summary>
public class PlayerService
{
    private readonly ILogger<PlayerService> _log;
    private readonly IFafServerAccessor _faf;

    // Mirrors playersByName / playersById
    private readonly ConcurrentDictionary<string, Player> _playersByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<int, Player>    _playersById   = new();

    // Mirrors usersOfflineById (based on IRC disconnect)
    private readonly ConcurrentDictionary<int, Player> _usersOfflineById = new();

    private readonly List<int> _friendList = [];
    private readonly List<int> _foeList    = [];

    private Player? _currentPlayer;

    // ── Reactive event streams — mirrors Guava EventBus posts ─────────────────
    private readonly Subject<Player> _playerOnline  = new();
    private readonly Subject<Player> _playerOffline = new();

    public IObservable<Player> PlayerOnline  => _playerOnline;
    public IObservable<Player> PlayerOffline => _playerOffline;

    public Player? CurrentPlayer => _currentPlayer;
    public IReadOnlyDictionary<string, Player> Players => _playersByName;

    public PlayerService(ILogger<PlayerService> log, IFafServerAccessor faf)
    {
        _log = log;
        _faf = faf;

        // Mirrors afterPropertiesSet():
        //   fafService.addOnMessageListener(PlayersMessage.class, this::onPlayersInfo)
        //   fafService.addOnMessageListener(SocialMessage.class, this::onFoeList)
        faf.Router.AddListener<PlayersMessage>("player_info", OnPlayersInfo);
        faf.Router.AddListener<SocialMessage> ("social",      OnSocialMessage);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public bool IsOnline(int playerId) => _playersById.ContainsKey(playerId);

    public Player? GetPlayerForUsername(string? username) =>
        username != null && _playersByName.TryGetValue(username, out var p) ? p : null;

    public Player? GetPlayerById(int id) =>
        _playersById.TryGetValue(id, out var p) ? p : null;

    /// <summary>
    /// Sets the current player after login.
    /// Mirrors <c>onLoginSuccess(LoginSuccessEvent)</c>.
    /// </summary>
    public void SetCurrentPlayer(int userId, string username)
    {
        Player player = CreateOrGetPlayer(username);
        player.Id = userId;
        player.SocialStatus = SocialStatus.Self;
        _currentPlayer = player;
    }

    public void AddFriend(Player player)
    {
        if (_playersByName.TryGetValue(player.Username, out var p))
            p.SocialStatus = SocialStatus.Friend;
        _friendList.Add(player.Id);
        _foeList.Remove(player.Id);
    }

    public void RemoveFriend(Player player)
    {
        if (_playersByName.TryGetValue(player.Username, out var p))
            p.SocialStatus = SocialStatus.Other;
        _friendList.Remove(player.Id);
    }

    public void AddFoe(Player player)
    {
        if (_playersByName.TryGetValue(player.Username, out var p))
            p.SocialStatus = SocialStatus.Foe;
        _foeList.Add(player.Id);
        _friendList.Remove(player.Id);
    }

    public void RemoveFoe(Player player)
    {
        if (_playersByName.TryGetValue(player.Username, out var p))
            p.SocialStatus = SocialStatus.Other;
        _foeList.Remove(player.Id);
    }

    public bool IsFriend(int pid) => _friendList.Contains(pid);
    public bool IsFoe(int pid)    => _foeList.Contains(pid);

    // ── Server message handlers ───────────────────────────────────────────────

    /// <summary>
    /// Mirrors <c>onPlayersInfo(PlayersMessage)</c>:
    ///   playersMessage.getPlayers().forEach(dto -> JavaFxUtil.runLater(() -> onPlayerInfo(dto)))
    /// </summary>
    private void OnPlayersInfo(PlayersMessage msg)
    {
        if (msg.Players is null) return;
        foreach (var dto in msg.Players)
            OnPlayerInfo(dto);
    }

    /// <summary>
    /// Faithful port of <c>PlayerService.onPlayerInfo(Player dto)</c>.
    /// </summary>
    private void OnPlayerInfo(PlayerDto dto)
    {
        bool wasAlreadyOnline = IsOnline(dto.Id) && !_usersOfflineById.ContainsKey(dto.Id);
        _usersOfflineById.TryRemove(dto.Id, out _);

        if (string.Equals(dto.Login, _currentPlayer?.Username, StringComparison.OrdinalIgnoreCase))
        {
            // Current player info update
            _currentPlayer!.UpdateFromDto(dto);
            _currentPlayer.SocialStatus = SocialStatus.Self;
        }
        else
        {
            Player player = CreateOrGetPlayer(dto.Login);

            player.SocialStatus = _friendList.Contains(dto.Id) ? SocialStatus.Friend
                                : _foeList.Contains(dto.Id)    ? SocialStatus.Foe
                                :                                SocialStatus.Other;

            player.UpdateFromDto(dto);

            if (!wasAlreadyOnline && PlayerStatusExtensions.Parse(dto.State) == PlayerStatus.Idle)
                _playerOnline.OnNext(player);
        }
    }

    /// <summary>
    /// Port of <c>onFoeList(SocialMessage)</c>.
    /// </summary>
    private void OnSocialMessage(SocialMessage msg)
    {
        if (msg.Foes    is not null) UpdateSocialList(_foeList,    msg.Foes,    SocialStatus.Foe);
        if (msg.Friends is not null) UpdateSocialList(_friendList, msg.Friends, SocialStatus.Friend);
    }

    private void UpdateSocialList(List<int> list, List<int> newValues, SocialStatus status)
    {
        list.Clear();
        list.AddRange(newValues);

        foreach (int id in list)
            if (_playersById.TryGetValue(id, out var p))
                p.SocialStatus = status;
    }

    // ── Internal helpers ──────────────────────────────────────────────────────

    /// <summary>
    /// Port of <c>createAndGetPlayerForUsername(String username)</c>.
    /// Thread-safe creation.
    /// </summary>
    private Player CreateOrGetPlayer(string username)
    {
        return _playersByName.GetOrAdd(username, name =>
        {
            var p = new Player(name);
            // When id is set later, also register in playersById
            p.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Player.Id) && p.Id != 0)
                    _playersById.TryAdd(p.Id, p);
            };
            return p;
        });
    }
}
