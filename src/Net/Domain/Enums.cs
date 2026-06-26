using System.Text.Json;
using System.Text.Json.Serialization;

namespace TafClient.Net.Domain;

// ─── MessageTarget ───────────────────────────────────────────────────────────
// Port of com.faforever.client.remote.domain.MessageTarget

public enum MessageTarget
{
    /// <summary>"game" — GPGNet messages routed to the game process.</summary>
    Game,
    /// <summary>"connectivity" — ICE/connectivity messages.</summary>
    Connectivity,
    /// <summary>null/absent — normal lobby client messages.</summary>
    Client
}

// ─── FafServerMessageType ────────────────────────────────────────────────────
// Port of com.faforever.client.remote.domain.FafServerMessageType

public enum FafServerMessageType
{
    Unknown,
    Welcome,         // "welcome"
    Session,         // "session"
    GameInfo,        // "game_info"
    PlayerInfo,      // "player_info"
    GameLaunch,      // "game_launch"
    MatchmakerInfo,  // "matchmaker_info"
    MatchFound,      // "match_found"
    MatchCancelled,  // "match_cancelled"
    Social,          // "social"
    AuthenticationFailed, // "authentication_failed"
    ChatBanNotice,   // "chat_ban_notice"
    Notice,          // "notice"
    IceServers,      // "ice_servers"
    Avatar,          // "avatar"
    PartyUpdate,     // "update_party"
    PartyInvite,     // "party_invite"
    PartyKicked,     // "kicked_from_party"
    SearchInfo,      // "search_info"
    NewTadaReplay,   // "new_tada_replay"
    GalacticWarUpdate, // "galactic_war_update"
}

public static class FafServerMessageTypeExtensions
{
    private static readonly Dictionary<string, FafServerMessageType> FromString = new()
    {
        ["welcome"]            = FafServerMessageType.Welcome,
        ["session"]            = FafServerMessageType.Session,
        ["game_info"]          = FafServerMessageType.GameInfo,
        ["player_info"]        = FafServerMessageType.PlayerInfo,
        ["game_launch"]        = FafServerMessageType.GameLaunch,
        ["matchmaker_info"]    = FafServerMessageType.MatchmakerInfo,
        ["match_found"]        = FafServerMessageType.MatchFound,
        ["match_cancelled"]    = FafServerMessageType.MatchCancelled,
        ["social"]             = FafServerMessageType.Social,
        ["authentication_failed"] = FafServerMessageType.AuthenticationFailed,
        ["chat_ban_notice"]    = FafServerMessageType.ChatBanNotice,
        ["notice"]             = FafServerMessageType.Notice,
        ["ice_servers"]        = FafServerMessageType.IceServers,
        ["avatar"]             = FafServerMessageType.Avatar,
        ["update_party"]       = FafServerMessageType.PartyUpdate,
        ["party_invite"]       = FafServerMessageType.PartyInvite,
        ["kicked_from_party"]  = FafServerMessageType.PartyKicked,
        ["search_info"]        = FafServerMessageType.SearchInfo,
        ["new_tada_replay"]    = FafServerMessageType.NewTadaReplay,
        ["galactic_war_update"]= FafServerMessageType.GalacticWarUpdate,
    };

    public static FafServerMessageType Parse(string command) =>
        FromString.TryGetValue(command, out var t) ? t : FafServerMessageType.Unknown;
}

// ─── ClientMessageType ───────────────────────────────────────────────────────
// Port of com.faforever.client.remote.domain.ClientMessageType

public enum ClientMessageType
{
    HostGame,         // "game_host"
    JoinGame,         // "game_join"
    AskSession,       // "ask_session"
    SocialAdd,        // "social_add"
    SocialRemove,     // "social_remove"
    Login,            // "hello"
    GameMatchmaking,  // "game_matchmaking"
    Avatar,           // "avatar"
    IceServers,       // "ice_servers"
    RestoreGameSession, // "restore_game_session"
    Ping,             // "ping"
    Pong,             // "pong"
    Admin,            // "admin"
    InviteToParty,    // "invite_to_party"
    AcceptPartyInvite,  // "accept_party_invite"
    KickPlayerFromParty,// "kick_player_from_party"
    ReadyParty,       // "ready_party"
    UnreadyParty,     // "unready_party"
    LeaveParty,       // "leave_party"
    SetPartyFactions, // "set_party_factions"
    SetPlayerAlias,   // "set_player_alias"
    SetGameMapDetails, // "set_game_map_details"
    SetGamePassword,  // "set_game_password"
    MatchmakerInfo,   // "matchmaker_info"
    UploadReplayToTada, // "upload_replay_to_tada"
    GpgGame,          // "gpg_game"  — relay game events to FAF server
    IceMsg,           // "ice_msg"   — relay ICE candidates between peers
}

public static class ClientMessageTypeExtensions
{
    private static readonly Dictionary<ClientMessageType, string> StringMap = new()
    {
        [ClientMessageType.HostGame]           = "game_host",
        [ClientMessageType.JoinGame]           = "game_join",
        [ClientMessageType.AskSession]         = "ask_session",
        [ClientMessageType.SocialAdd]          = "social_add",
        [ClientMessageType.SocialRemove]       = "social_remove",
        [ClientMessageType.Login]              = "hello",
        [ClientMessageType.GameMatchmaking]    = "game_matchmaking",
        [ClientMessageType.Avatar]             = "avatar",
        [ClientMessageType.IceServers]         = "ice_servers",
        [ClientMessageType.RestoreGameSession] = "restore_game_session",
        [ClientMessageType.Ping]               = "ping",
        [ClientMessageType.Pong]               = "pong",
        [ClientMessageType.Admin]              = "admin",
        [ClientMessageType.InviteToParty]      = "invite_to_party",
        [ClientMessageType.AcceptPartyInvite]  = "accept_party_invite",
        [ClientMessageType.KickPlayerFromParty]= "kick_player_from_party",
        [ClientMessageType.ReadyParty]         = "ready_party",
        [ClientMessageType.UnreadyParty]       = "unready_party",
        [ClientMessageType.LeaveParty]         = "leave_party",
        [ClientMessageType.SetPartyFactions]   = "set_party_factions",
        [ClientMessageType.SetPlayerAlias]     = "set_player_alias",
        [ClientMessageType.SetGameMapDetails]  = "set_game_map_details",
        [ClientMessageType.SetGamePassword]    = "set_game_password",
        [ClientMessageType.MatchmakerInfo]     = "matchmaker_info",
        [ClientMessageType.UploadReplayToTada] = "upload_replay_to_tada",
        [ClientMessageType.GpgGame]            = "gpg_game",
        [ClientMessageType.IceMsg]             = "ice_msg",
    };

    public static string GetString(this ClientMessageType t) => StringMap[t];
}

// ─── Server-side enum types ───────────────────────────────────────────────────

/// <summary>Port of com.faforever.client.remote.domain.GameStatus</summary>
public enum GameStatus
{
    Unknown,
    Spawning,    // "spawning" — OS spawned exe
    Staging,     // "staging"  — chat room open, game not launched
    Battleroom,  // "battleroom" — players in battle room
    Launching,   // "launching" — started, no new joins
    Live,        // "live" — teams finalised
    Ended        // "ended"
}

public static class GameStatusExtensions
{
    private static readonly Dictionary<string, GameStatus> FromString = new()
    {
        ["unknown"]     = GameStatus.Unknown,
        ["spawning"]    = GameStatus.Spawning,
        ["staging"]     = GameStatus.Staging,
        ["battleroom"]  = GameStatus.Battleroom,
        ["launching"]   = GameStatus.Launching,
        ["live"]        = GameStatus.Live,
        ["ended"]       = GameStatus.Ended,
    };

    public static GameStatus Parse(string? s) =>
        s != null && FromString.TryGetValue(s.ToLowerInvariant(), out var v) ? v : GameStatus.Unknown;

    public static bool IsOpen(this GameStatus s) =>
        s == GameStatus.Staging || s == GameStatus.Battleroom;

    public static bool IsInProgress(this GameStatus s) =>
        s == GameStatus.Launching || s == GameStatus.Live;
}

/// <summary>Port of com.faforever.client.remote.domain.PlayerStatus</summary>
public enum PlayerStatus
{
    Idle,     // "idle"
    Hosting,  // "hosting"
    Hosted,   // "hosted"
    Joining,  // "joining"
    Joined,   // "joined"
    Playing   // "playing"
}

public static class PlayerStatusExtensions
{
    private static readonly Dictionary<string, PlayerStatus> FromString = new()
    {
        ["idle"]    = PlayerStatus.Idle,
        ["hosting"] = PlayerStatus.Hosting,
        ["hosted"]  = PlayerStatus.Hosted,
        ["joining"] = PlayerStatus.Joining,
        ["joined"]  = PlayerStatus.Joined,
        ["playing"] = PlayerStatus.Playing,
    };

    public static PlayerStatus Parse(string? s) =>
        s != null && FromString.TryGetValue(s.ToLowerInvariant(), out var v) ? v : PlayerStatus.Idle;
}

/// <summary>Port of com.faforever.client.remote.domain.GameType</summary>
public enum GameType { Normal, Ladder, Coop, Matchmaker }

/// <summary>Port of com.faforever.client.game.GameVisibility</summary>
public enum GameVisibility { Public, Private }

public static class GameVisibilityExtensions
{
    public static GameVisibility Parse(string? s) =>
        s == "friends" ? GameVisibility.Private : GameVisibility.Public;
    public static string GetString(this GameVisibility v) =>
        v == GameVisibility.Private ? "friends" : "public";
}

/// <summary>Port of com.faforever.client.remote.domain.MatchmakingState</summary>
public enum MatchmakingState { Start, Stop }

public static class MatchmakingStateExtensions
{
    public static string GetString(this MatchmakingState s) =>
        s == MatchmakingState.Start ? "start" : "stop";
}
