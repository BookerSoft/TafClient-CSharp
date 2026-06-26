namespace GpgNet.Protocol;

// ─── Port of GpgNetServerMessages.h / GpgNetServerMessages.cpp ───────────────
//
// Messages the FAF server (via ICE adapter) sends TO gpgnet4ta.
// Each mirrors the C++ struct + Set(QVariantList command) factory.
//
// playerName wire format: "alias/realName" — split on '/' per SplitAliasAndRealName()
// e.g. "BILLYIDOL/Joshua" → alias="BILLYIDOL", realName="Joshua"
//      "BILLYIDOL"        → alias="BILLYIDOL", realName=""

public static class ServerMsgIds
{
    public const string CreateLobby        = "CreateLobby";
    public const string HostGame           = "HostGame";
    public const string JoinGame           = "JoinGame";
    public const string ConnectToPeer      = "ConnectToPeer";
    public const string DisconnectFromPeer = "DisconnectFromPeer";
    public const string Ping               = "Ping";
}

/// <summary>
/// Port of CreateLobbyCommand.
/// args (params only, NO leading command name) = [protocol, localPort, playerName, playerId, natTraversal]
/// Confirmed against real wire data: a logged CreateLobby message
/// ["CreateLobby", [0, 61542, "bigman", 3585, 1]] arrives here as args =
/// [0, 61542, "bigman", 3585, 1] — the dispatcher already strips the command
/// name before calling FromArgs, so there is no args[0]=cmd to skip.
/// </summary>
public sealed record CreateLobbyCommand(
    int    Protocol,
    int    LocalPort,
    string PlayerAlias,
    string PlayerRealName,
    int    PlayerId,
    int    NatTraversal)
{
    public static CreateLobbyCommand FromArgs(List<object> args)
    {
        string playerName = H.Str(args, 2);
        (string alias, string real) = H.SplitName(playerName);
        return new CreateLobbyCommand(
            Protocol:       H.Int(args, 0),
            LocalPort:      H.Int(args, 1),
            PlayerAlias:    alias,
            PlayerRealName: real,
            PlayerId:       H.Int(args, 3),
            NatTraversal:   H.Int(args, 4));
    }
}

/// <summary>
/// Port of HostGameCommand.
/// args (params only) = [mapName]
/// </summary>
public sealed record HostGameCommand(string MapName)
{
    public static HostGameCommand FromArgs(List<object> args) =>
        new(H.Str(args, 0));
}

/// <summary>
/// Port of JoinGameCommand.
/// args (params only) = [remoteHost, playerName("alias/real"), playerId]
/// </summary>
public sealed record JoinGameCommand(
    string RemoteHost,
    string RemotePlayerAlias,
    string RemotePlayerRealName,
    int    RemotePlayerId)
{
    public static JoinGameCommand FromArgs(List<object> args)
    {
        (string alias, string real) = H.SplitName(H.Str(args, 1));
        return new JoinGameCommand(
            RemoteHost:           H.Str(args, 0),
            RemotePlayerAlias:    alias,
            RemotePlayerRealName: real,
            RemotePlayerId:       H.Int(args, 2));
    }
}

/// <summary>
/// Port of ConnectToPeerCommand.
/// args (params only) = [host, playerName("alias/real"), playerId]
/// </summary>
public sealed record ConnectToPeerCommand(
    string Host,
    string PlayerAlias,
    string PlayerRealName,
    int    PlayerId)
{
    public static ConnectToPeerCommand FromArgs(List<object> args)
    {
        (string alias, string real) = H.SplitName(H.Str(args, 1));
        return new ConnectToPeerCommand(
            Host:           H.Str(args, 0),
            PlayerAlias:    alias,
            PlayerRealName: real,
            PlayerId:       H.Int(args, 2));
    }
}

/// <summary>
/// Port of DisconnectFromPeerCommand.
/// args (params only) = [playerId]
/// </summary>
public sealed record DisconnectFromPeerCommand(int PlayerId)
{
    public static DisconnectFromPeerCommand FromArgs(List<object> args) =>
        new(H.Int(args, 0));
}

// ─── Helpers ─────────────────────────────────────────────────────────────────

internal static class H
{
    /// <summary>
    /// Port of SplitAliasAndRealName(QString aliasAndReal, ...).
    /// Splits "alias/realName" on the first '/'.
    /// </summary>
    internal static (string alias, string real) SplitName(string raw)
    {
        int idx = raw.IndexOf('/');
        return idx < 0 ? (raw, "") : (raw[..idx], raw[(idx + 1)..]);
    }

    internal static int Int(List<object> args, int i)
    {
        if (i >= args.Count) return 0;
        return args[i] switch
        {
            int    v => v,
            long   v => (int)v,
            uint   v => (int)v,
            string s => int.TryParse(s, out int n) ? n : 0,
            _        => 0,
        };
    }

    internal static string Str(List<object> args, int i) =>
        i < args.Count ? args[i]?.ToString() ?? "" : "";
}
