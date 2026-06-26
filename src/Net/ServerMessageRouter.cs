using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using TafClient.Net.Domain;

namespace TafClient.Net;

/// <summary>
/// Port of <c>com.faforever.client.remote.gson.ServerMessageTypeAdapter</c>.
///
/// The server sends JSON objects where the "command" field identifies the concrete type
/// and the optional "target" field routes to game ("game"), connectivity ("connectivity"),
/// or lobby client (null/"client") messages.
///
/// In Java this was a Gson <c>JsonDeserializer&lt;ServerMessage&gt;</c>.
/// Here it is a dispatcher that registers typed <c>Action&lt;T&gt;</c> handlers per
/// command string — mirrors the listener maps in <c>FafServerAccessorImpl</c>.
/// </summary>
public class ServerMessageRouter
{
    private readonly ILogger<ServerMessageRouter> _log;

    // Mirrors FafServerAccessorImpl.messageListeners:
    //   HashMap<Class<? extends ServerMessage>, Collection<Consumer<ServerMessage>>>
    private readonly Dictionary<string, List<Action<FafServerMessage>>> _listeners = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        // Server sends some numeric fields as floats (e.g. rating_min: 1500.0)
        // AllowReadingFromString also handles quoted numbers
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                       | System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public ServerMessageRouter(ILogger<ServerMessageRouter> log) => _log = log;

    /// <summary>
    /// Mirrors <c>FafServerAccessorImpl.addOnMessageListener</c>.
    /// </summary>
    public void AddListener<T>(string command, Action<T> handler)
        where T : FafServerMessage
    {
        if (!_listeners.TryGetValue(command, out var list))
            _listeners[command] = list = [];

        list.Add(raw =>
        {
            if (raw is T typed)
                handler(typed);
        });
    }

    public void RemoveListeners(string command) => _listeners.Remove(command);

    /// <summary>
    /// Parses a raw JSON string received from the server and dispatches it.
    /// Mirrors <c>FafServerAccessorImpl.parseServerObject()</c> +
    /// <c>ServerMessageTypeAdapter.deserialize()</c>.
    /// </summary>
    public void Dispatch(string json)
    {
        JsonObject? obj;
        try { obj = JsonNode.Parse(json)?.AsObject(); }
        catch (JsonException ex)
        {
            _log.LogWarning(ex, "Failed to parse server JSON: {Json}", json);
            return;
        }
        if (obj is null) return;

        string? command = obj["command"]?.GetValue<string>();
        if (command is null)
        {
            _log.LogDebug("Server message missing 'command': {Json}", json);
            return;
        }

        // Determine MessageTarget from "target" field (mirrors ServerMessageTypeAdapter)
        string? targetStr = obj["target"]?.GetValue<string>();
        MessageTarget target = targetStr switch
        {
            "game"         => MessageTarget.Game,
            "connectivity" => MessageTarget.Connectivity,
            _              => MessageTarget.Client,
        };

        // GPGNet / ICE messages routed to game process — not handled here yet
        if (target != MessageTarget.Client)
        {
            _log.LogDebug("GPGNet message command={Command} target={Target} (not yet handled)", command, target);
            return;
        }

        FafServerMessageType msgType = FafServerMessageTypeExtensions.Parse(command);

        // Log raw game_info so we can see teams/num_players exactly as sent by server
        if (command == "game_info")
            Console.WriteLine($"[RAW game_info] {json}");

        FafServerMessage? message;
        try
        {
            message = DeserializeAs(msgType, json);
        }
        catch (JsonException ex)
        {
            // A single unexpected field type must never take down the whole
            // connection — this is exactly what happened with game_launch's
            // "args" array containing bare numbers (see StringOrNumberListConverter).
            // Log it loudly and move on instead of letting the read loop die.
            _log.LogError(ex, "Failed to deserialize '{Command}' message: {Json}", command, json);
            Console.WriteLine($"[CONN] Deserialize FAILED for command='{command}': {ex.Message}");
            Console.WriteLine($"[CONN] Raw json was: {json}");
            return;
        }

        if (message is null)
        {
            _log.LogDebug("No handler for server command '{Command}'", command);
            return;
        }

        // Walk the type hierarchy, mirroring Java's while (messageClass != Object.class) loop
        if (_listeners.TryGetValue(command, out var handlers))
            foreach (var h in handlers)
                try { h(message); }
                catch (Exception ex)
                { _log.LogWarning(ex, "Handler threw for command '{Command}'", command); }
    }

    private static FafServerMessage? DeserializeAs(FafServerMessageType type, string json)
    {
        return type switch
        {
            FafServerMessageType.Session              => JsonSerializer.Deserialize<SessionMessage>(json, JsonOpts),
            FafServerMessageType.Welcome              => JsonSerializer.Deserialize<LoginMessage>(json, JsonOpts),
            FafServerMessageType.PlayerInfo           => JsonSerializer.Deserialize<PlayersMessage>(json, JsonOpts),
            FafServerMessageType.GameInfo             => JsonSerializer.Deserialize<GameInfoMessage>(json, JsonOpts),
            FafServerMessageType.GameLaunch           => JsonSerializer.Deserialize<GameLaunchMessage>(json, JsonOpts),
            FafServerMessageType.Social               => JsonSerializer.Deserialize<SocialMessage>(json, JsonOpts),
            FafServerMessageType.Notice               => JsonSerializer.Deserialize<NoticeMessage>(json, JsonOpts),
            FafServerMessageType.AuthenticationFailed => JsonSerializer.Deserialize<AuthenticationFailedMessage>(json, JsonOpts),
            _                                         => JsonSerializer.Deserialize<FafServerMessage>(json, JsonOpts),
        };
    }
}
