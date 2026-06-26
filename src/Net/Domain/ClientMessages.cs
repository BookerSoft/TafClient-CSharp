using System.Text.Json;
using System.Text.Json.Serialization;

namespace TafClient.Net.Domain;

// ─── Base ─────────────────────────────────────────────────────────────────────
// Port of com.faforever.client.remote.domain.ClientMessage
// The "command" field is the ClientMessageType string (e.g. "game_host").

/// <summary>
/// Base class for all client→server messages.
/// <c>command</c> is serialized as the type string (snake_case) that the server expects.
/// Mirrors <c>ClientMessage</c> + Gson LOWER_CASE_WITH_UNDERSCORES + ClientMessageTypeTypeAdapter.
/// </summary>
public abstract class ClientMessage
{
    [JsonPropertyName("command")]
    public string Command { get; }

    protected ClientMessage(ClientMessageType type)
    {
        Command = type.GetString();
    }
}

// ─── Session / Login ──────────────────────────────────────────────────────────

/// <summary>Port of InitSessionMessage (asks server for a session id).</summary>
public sealed class InitSessionMessage : ClientMessage
{
    [JsonPropertyName("version")]
    public string Version { get; }

    [JsonPropertyName("user_agent")]
    public string UserAgent { get; } = "downlords-taf-client";

    public InitSessionMessage(string version) : base(ClientMessageType.AskSession)
        => Version = version;
}

/// <summary>
/// Port of LoginClientMessage.
/// Sent after session is established; contains SHA-256 hashed password + UID.
/// </summary>
public sealed class LoginClientMessage : ClientMessage
{
    [JsonPropertyName("login")]
    public string Login { get; }

    [JsonPropertyName("password")]
    public string Password { get; }   // SHA-256 hex of the plaintext password

    [JsonPropertyName("session")]
    public long Session { get; }

    [JsonPropertyName("unique_id")]
    public string UniqueId { get; }

    [JsonPropertyName("local_ip")]
    public string LocalIp { get; }

    public LoginClientMessage(string login, string password, long session,
                              string uniqueId, string localIp)
        : base(ClientMessageType.Login)
    {
        Login = login; Password = password; Session = session;
        UniqueId = uniqueId; LocalIp = localIp;
    }
}

// ─── Games ────────────────────────────────────────────────────────────────────

/// <summary>
/// Port of GameEndedMessage (com.faforever.client.remote.domain.GameEndedMessage,
/// via GpgGameMessage). Sent when the local launch process fails or the game
/// process exits, telling the server this game session is over so it doesn't
/// leave it in a stuck "launching" state. The real client calls this from its
/// .exceptionally() handler whenever startGame()'s process chain throws —
/// our equivalent is the catch block in GameLaunchService.LaunchGameAsync.
/// Wire shape: {"command":"GameState","args":["Ended"],"target":"game"}
/// (note: NOT the same shape as our other ClientMessage-derived classes, which
/// use {"command":&lt;type&gt;,&lt;fields&gt;} — this one is GpgGameMessage's own format).
/// </summary>
public sealed class GameEndedMessage
{
    [JsonPropertyName("command")]
    public string Command { get; } = "GameState";

    [JsonPropertyName("args")]
    public List<string> Args { get; } = ["Ended"];

    [JsonPropertyName("target")]
    public string Target { get; } = "game";
}

/// <summary>Port of HostGameMessage.</summary>
public sealed class HostGameMessage : ClientMessage
{
    [JsonPropertyName("access")]
    public string Access { get; }          // "public" / "password"

    [JsonPropertyName("mapname")]
    public string Mapname { get; }

    [JsonPropertyName("title")]
    public string Title { get; }

    [JsonPropertyName("mod")]
    public string Mod { get; }

    [JsonPropertyName("options")]
    public bool[] Options { get; }

    [JsonPropertyName("password")]
    public string? Password { get; }

    [JsonPropertyName("mod_version")]
    public string? ModVersion { get; }

    [JsonPropertyName("visibility")]
    public string Visibility { get; }

    [JsonPropertyName("rating_min")]
    public int? RatingMin { get; }

    [JsonPropertyName("rating_max")]
    public int? RatingMax { get; }

    [JsonPropertyName("enforce_rating_range")]
    public bool? EnforceRatingRange { get; }

    [JsonPropertyName("replay_delay_seconds")]
    public int? ReplayDelaySeconds { get; }

    [JsonPropertyName("rating_type")]
    public string? RatingType { get; }

    [JsonPropertyName("galactic_war_planet_name")]
    public string? GalacticWarPlanetName { get; }

    [JsonPropertyName("max_players")]
    public int? MaxPlayers { get; }

    public HostGameMessage(
        bool passwordProtected, string mapName, string title, bool[] options,
        string mod, string? password, string? modVersion,
        GameVisibility visibility, int? ratingMin, int? ratingMax,
        bool? enforceRatingRange, int? replayDelaySeconds,
        string? ratingType, string? galacticWarPlanetName, int? maxPlayers)
        : base(ClientMessageType.HostGame)
    {
        Access            = passwordProtected ? "password" : "public";
        Mapname           = mapName;
        Title             = title;
        Options           = options;
        Mod               = mod;
        Password          = password;
        ModVersion        = modVersion;
        Visibility        = visibility.GetString();
        RatingMin         = ratingMin;
        RatingMax         = ratingMax;
        EnforceRatingRange = enforceRatingRange;
        ReplayDelaySeconds = replayDelaySeconds;
        RatingType        = ratingType;
        GalacticWarPlanetName = galacticWarPlanetName;
        MaxPlayers        = maxPlayers;
    }
}

/// <summary>Port of JoinGameMessage.</summary>
public sealed class JoinGameMessage : ClientMessage
{
    [JsonPropertyName("uid")]
    public int Uid { get; }

    [JsonPropertyName("password")]
    public string? Password { get; }

    public JoinGameMessage(int uid, string? password)
        : base(ClientMessageType.JoinGame)
    { Uid = uid; Password = password; }
}

// ─── Social ───────────────────────────────────────────────────────────────────

/// <summary>Port of AddFriendMessage.</summary>
public sealed class AddFriendMessage : ClientMessage
{
    [JsonPropertyName("friend")]
    public int Friend { get; }

    public AddFriendMessage(int playerId) : base(ClientMessageType.SocialAdd)
        => Friend = playerId;
}

/// <summary>Port of AddFoeMessage.</summary>
public sealed class AddFoeMessage : ClientMessage
{
    [JsonPropertyName("foe")]
    public int Foe { get; }

    public AddFoeMessage(int playerId) : base(ClientMessageType.SocialAdd)
        => Foe = playerId;
}

/// <summary>Port of RemoveFriendMessage.</summary>
public sealed class RemoveFriendMessage : ClientMessage
{
    [JsonPropertyName("friend_id")]
    public int FriendId { get; }

    public RemoveFriendMessage(int playerId) : base(ClientMessageType.SocialRemove)
        => FriendId = playerId;
}

/// <summary>Port of RemoveFoeMessage.</summary>
public sealed class RemoveFoeMessage : ClientMessage
{
    [JsonPropertyName("foe_id")]
    public int FoeId { get; }

    public RemoveFoeMessage(int playerId) : base(ClientMessageType.SocialRemove)
        => FoeId = playerId;
}

// ─── Misc ─────────────────────────────────────────────────────────────────────

/// <summary>Port of PongMessage — sent in response to a server PING bare-string command.</summary>
public sealed class PongMessage : ClientMessage
{
    public PongMessage() : base(ClientMessageType.Pong) { }
}

/// <summary>Port of PingMessage — client-initiated ping carrying afk seconds.</summary>
public sealed class PingMessage : ClientMessage
{
    [JsonPropertyName("afk_seconds")]
    public long AfkSeconds { get; }

    public PingMessage(long afkSeconds) : base(ClientMessageType.Ping)
        => AfkSeconds = afkSeconds;
}

/// <summary>Port of GameMatchmakingMessage.</summary>
public sealed class GameMatchmakingMessage : ClientMessage
{
    [JsonPropertyName("queue_name")]
    public string QueueName { get; }

    [JsonPropertyName("state")]
    public string State { get; }

    public GameMatchmakingMessage(string queueName, MatchmakingState state)
        : base(ClientMessageType.GameMatchmaking)
    { QueueName = queueName; State = state.GetString(); }
}

/// <summary>Port of RestoreGameSessionMessage.</summary>
public sealed class RestoreGameSessionMessage : ClientMessage
{
    [JsonPropertyName("game_id")]
    public int GameId { get; }

    public RestoreGameSessionMessage(int gameId)
        : base(ClientMessageType.RestoreGameSession)
        => GameId = gameId;
}

/// <summary>Port of SelectAvatarMessage.</summary>
public sealed class SelectAvatarMessage : ClientMessage
{
    [JsonPropertyName("url")]
    public string? Url { get; }

    public SelectAvatarMessage(string? url) : base(ClientMessageType.Avatar)
        => Url = url;
}

/// <summary>Port of SetPlayerAliasMessage.</summary>
public sealed class SetPlayerAliasMessage : ClientMessage
{
    [JsonPropertyName("alias")]
    public string Alias { get; }

    public SetPlayerAliasMessage(string alias) : base(ClientMessageType.SetPlayerAlias)
        => Alias = alias;
}

/// <summary>Port of SetGamePasswordMessage.</summary>
public sealed class SetGamePasswordMessage : ClientMessage
{
    [JsonPropertyName("password")]
    public string Password { get; }

    public SetGamePasswordMessage(string password) : base(ClientMessageType.SetGamePassword)
        => Password = password;
}

/// <summary>Port of SetGameMapDetailsMessage.</summary>
public sealed class SetGameMapDetailsMessage : ClientMessage
{
    [JsonPropertyName("map_name")]
    public string MapName { get; }

    [JsonPropertyName("hpi_archive")]
    public string HpiArchive { get; }

    [JsonPropertyName("crc")]
    public string Crc { get; }

    public SetGameMapDetailsMessage(string mapName, string hpiArchive, string crc)
        : base(ClientMessageType.SetGameMapDetails)
    { MapName = mapName; HpiArchive = hpiArchive; Crc = crc; }
}

/// <summary>Port of UploadReplayToTadaMessage.</summary>
public sealed class UploadReplayToTadaMessage : ClientMessage
{
    [JsonPropertyName("replay_id")]
    public int? ReplayId { get; }

    public UploadReplayToTadaMessage(int? replayId)
        : base(ClientMessageType.UploadReplayToTada)
        => ReplayId = replayId;
}

// ─── Party ────────────────────────────────────────────────────────────────────

public sealed class InviteToPartyMessage : ClientMessage
{
    [JsonPropertyName("recipient_id")]
    public int RecipientId { get; }
    public InviteToPartyMessage(int id) : base(ClientMessageType.InviteToParty) => RecipientId = id;
}

public sealed class AcceptPartyInviteMessage : ClientMessage
{
    [JsonPropertyName("sender_id")]
    public int SenderId { get; }
    public AcceptPartyInviteMessage(int id) : base(ClientMessageType.AcceptPartyInvite) => SenderId = id;
}

public sealed class KickPlayerFromPartyMessage : ClientMessage
{
    [JsonPropertyName("kicked_player_id")]
    public int KickedPlayerId { get; }
    public KickPlayerFromPartyMessage(int id) : base(ClientMessageType.KickPlayerFromParty) => KickedPlayerId = id;
}

public sealed class ReadyPartyMessage    : ClientMessage { public ReadyPartyMessage()    : base(ClientMessageType.ReadyParty)    {} }
public sealed class UnreadyPartyMessage  : ClientMessage { public UnreadyPartyMessage()  : base(ClientMessageType.UnreadyParty)  {} }
public sealed class LeavePartyMessage    : ClientMessage { public LeavePartyMessage()    : base(ClientMessageType.LeaveParty)    {} }
public sealed class MatchmakerInfoMessage : ClientMessage { public MatchmakerInfoMessage() : base(ClientMessageType.MatchmakerInfo) {} }

// ── GPGNet relay messages (game events forwarded back to FAF server) ───────────

/// <summary>
/// Sends a GPGNet game message to the FAF lobby server.
/// Mirrors FafService.sendGpgGameMessage() used when the ICE adapter reports
/// a game event (GameState, PlayerOption, GameResult, etc.) via onGpgNetMessageReceived.
/// The server uses these to track game lifecycle and update ratings.
/// </summary>
public sealed class GpgGameClientMessage : ClientMessage
{
    [System.Text.Json.Serialization.JsonPropertyName("args")]
    public List<object> Args { get; }

    public GpgGameClientMessage(string header, List<object> args)
        : base(ClientMessageType.GpgGame)
    {
        // The command field IS the header (GameState, GameEnded, etc.)
        // but for GpgGame the "command" field holds "gpgGame" and header goes in a separate field
        Args = args;
        // Note: base sets Command = "gpg_game"; the FAF server routes on this
    }
}

/// <summary>
/// Relays ICE candidate messages between peers via the FAF lobby server.
/// Mirrors the IceMsg flow: ice adapter → client → FAF server → remote peer.
/// </summary>
public sealed class IceMsgClientMessage : ClientMessage
{
    [System.Text.Json.Serialization.JsonPropertyName("target")]
    public int Target { get; }

    [System.Text.Json.Serialization.JsonPropertyName("candidates")]
    public string Candidates { get; }

    public IceMsgClientMessage(int srcId, int dstId, string candidatesJson)
        : base(ClientMessageType.IceMsg)
    {
        Target     = dstId;
        Candidates = candidatesJson;
    }
}
