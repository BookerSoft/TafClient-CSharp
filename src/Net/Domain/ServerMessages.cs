using System.Text.Json;
using System.Text.Json.Serialization;

namespace TafClient.Net.Domain;

// ─── Base ─────────────────────────────────────────────────────────────────────
// Port of com.faforever.client.remote.domain.FafServerMessage
// The "command" field drives polymorphic deserialization (see ServerMessageDeserializer).

public class FafServerMessage
{
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    [JsonPropertyName("target")]
    public string? Target { get; set; }
}

// ─── Session / Login ──────────────────────────────────────────────────────────

/// <summary>Port of SessionMessage. Server sends after InitSessionMessage ("ask_session").</summary>
public class SessionMessage : FafServerMessage
{
    [JsonPropertyName("session")]
    public long Session { get; set; }
}

/// <summary>Port of LoginMessage. Server sends "welcome" after successful authentication.</summary>
public class LoginMessage : FafServerMessage
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("token")]
    public string? Token { get; set; }   // JWT access token for API calls
}

/// <summary>Port of AuthenticationFailedMessage.</summary>
public class AuthenticationFailedMessage : FafServerMessage
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }
}

// ─── Players ──────────────────────────────────────────────────────────────────

/// <summary>Port of PlayersMessage ("player_info").</summary>
public class PlayersMessage : FafServerMessage
{
    [JsonPropertyName("players")]
    public List<PlayerDto>? Players { get; set; }
}

/// <summary>
/// Port of com.faforever.client.remote.domain.Player (the DTO, not the domain Player).
/// Field names must exactly match what taf-python-server sends (snake_case via Gson LOWER_CASE_WITH_UNDERSCORES).
/// </summary>
public class PlayerDto
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("login")]
    public string Login { get; set; } = string.Empty;

    [JsonPropertyName("alias")]
    public string? Alias { get; set; }

    [JsonPropertyName("clan")]
    public string? Clan { get; set; }

    [JsonPropertyName("country")]
    public string? Country { get; set; }

    [JsonPropertyName("avatar")]
    public AvatarDto? Avatar { get; set; }

    [JsonPropertyName("ratings")]
    public Dictionary<string, LeaderboardRatingDto>? Ratings { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("number_of_games")]
    public int? NumberOfGames { get; set; }

    [JsonPropertyName("current_game_uid")]
    public int? CurrentGameUid { get; set; }

    [JsonPropertyName("afk_seconds")]
    public long? AfkSeconds { get; set; }
}

public class AvatarDto
{
    [JsonPropertyName("url")]
    public string? Url { get; set; }

    [JsonPropertyName("tooltip")]
    public string? Tooltip { get; set; }
}

/// <summary>
/// Port of com.faforever.client.remote.domain.LeaderboardRating.
/// <c>rating</c> is a 2-element array [mean, deviation].
/// </summary>
public class LeaderboardRatingDto
{
    [JsonPropertyName("rating")]
    public float[]? Rating { get; set; }

    [JsonPropertyName("number_of_games")]
    public int? NumberOfGames { get; set; }
}

// ─── Social ───────────────────────────────────────────────────────────────────

/// <summary>Port of SocialMessage.</summary>
public class SocialMessage : FafServerMessage
{
    [JsonPropertyName("friends")]
    public List<int>? Friends { get; set; }

    [JsonPropertyName("foes")]
    public List<int>? Foes { get; set; }

    [JsonPropertyName("channels")]
    public List<string>? Channels { get; set; }
}

// ─── Games ────────────────────────────────────────────────────────────────────

/// <summary>
/// Port of GameInfoMessage.
/// The server may send either a single game or a list of games in the same message
/// (see <c>games</c> field) — that quirk is faithfully ported here.
/// </summary>
public class GameInfoMessage : FafServerMessage
{
    [JsonPropertyName("uid")]
    public int? Uid { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("state")]
    public string? State { get; set; }

    [JsonPropertyName("host")]
    public string? Host { get; set; }

    [JsonPropertyName("featured_mod")]
    public string? FeaturedMod { get; set; }

    [JsonPropertyName("featured_mod_version")]
    public string? FeaturedModVersion { get; set; }

    [JsonPropertyName("map_name")]
    public string? MapName { get; set; }

    /// <summary>
    /// Format: "{archive}/{name}/{crc}" — parsed in GameService to extract archive + CRC.
    /// </summary>
    [JsonPropertyName("map_file_path")]
    public string? MapFilePath { get; set; }

    [JsonPropertyName("num_players")]
    public int? NumPlayers { get; set; }

    [JsonPropertyName("max_players")]
    public int? MaxPlayers { get; set; }

    [JsonPropertyName("password_protected")]
    public bool? PasswordProtected { get; set; }

    [JsonPropertyName("visibility")]
    public string? Visibility { get; set; }

    [JsonPropertyName("teams")]
    public Dictionary<string, List<string>>? Teams { get; set; }

    [JsonPropertyName("pings")]
    public Dictionary<int, List<List<int>>>? Pings { get; set; }

    [JsonPropertyName("sim_mods")]
    public Dictionary<string, string>? SimMods { get; set; }

    [JsonPropertyName("launched_at")]
    public double? LaunchedAt { get; set; }

    [JsonPropertyName("rating_type")]
    public string? RatingType { get; set; }

    [JsonPropertyName("rating_min")]
    public float? RatingMin { get; set; }

    [JsonPropertyName("rating_max")]
    public float? RatingMax { get; set; }

    [JsonPropertyName("enforce_rating_range")]
    public bool? EnforceRatingRange { get; set; }

    [JsonPropertyName("replay_delay_seconds")]
    public int? ReplayDelaySeconds { get; set; }

    [JsonPropertyName("game_type")]
    public string? GameType { get; set; }

    [JsonPropertyName("galactic_war_planet_name")]
    public string? GalacticWarPlanetName { get; set; }

    /// <summary>
    /// Server quirk: may send a list of GameInfoMessages inside one envelope.
    /// Mirrors <c>GameInfoMessage.games</c> in Java.
    /// </summary>
    [JsonPropertyName("games")]
    public List<GameInfoMessage>? Games { get; set; }
}

// ─── Game launch ──────────────────────────────────────────────────────────────

/// <summary>
/// The real FAF/TAF server sends game_launch's "args" array with mixed element
/// types — some are quoted strings, but numeric-looking values (player counts,
/// rating colors, etc.) are sent as bare JSON numbers, not strings. The real
/// Java client uses Gson, which silently coerces numbers to strings; the
/// equivalent System.Text.Json List&lt;string&gt; deserialization is strict and
/// throws JsonException ("Cannot get the value of a token type 'Number' as a
/// string") the moment it hits one — which was killing the connection right
/// when game_launch arrived, making host/join look like a silent timeout.
/// This converter accepts string OR number tokens and converts both to string,
/// matching Gson's lenient behavior exactly.
/// </summary>
public sealed class StringOrNumberListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException("Expected start of array for args");

        var list = new List<string>();
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndArray) break;

            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    list.Add(reader.GetString() ?? "");
                    break;
                case JsonTokenType.Number:
                    // Utf8JsonReader has no GetRawText() (that's a JsonElement method).
                    // Decode the raw UTF-8 bytes of the number token directly — this
                    // preserves the exact original text (integer or decimal) without
                    // round-tripping through a parsed double that could reformat it.
                    list.Add(System.Text.Encoding.UTF8.GetString(reader.ValueSpan));
                    break;
                case JsonTokenType.True:
                case JsonTokenType.False:
                    list.Add(reader.GetBoolean().ToString());
                    break;
                case JsonTokenType.Null:
                    list.Add("");
                    break;
                default:
                    // Unexpected nested object/array — skip it rather than throwing,
                    // so one weird element can't take down the whole connection.
                    reader.Skip();
                    break;
            }
        }
        return list;
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        foreach (var s in value) writer.WriteStringValue(s);
        writer.WriteEndArray();
    }
}

/// <summary>Port of GameLaunchMessage.</summary>
public class GameLaunchMessage : FafServerMessage
{
    [JsonPropertyName("args")]
    [JsonConverter(typeof(StringOrNumberListConverter))]
    public List<string>? Args { get; set; }

    [JsonPropertyName("uid")]
    public int Uid { get; set; }

    [JsonPropertyName("mod")]
    public string? Mod { get; set; }

    [JsonPropertyName("mapname")]
    public string? Mapname { get; set; }

    [JsonPropertyName("map_crc")]
    public string? MapCrc { get; set; }

    [JsonPropertyName("map_archive")]
    public string? MapArchive { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("expected_players")]
    public int? ExpectedPlayers { get; set; }

    [JsonPropertyName("team")]
    public int? Team { get; set; }

    [JsonPropertyName("map_position")]
    public int? MapPosition { get; set; }

    [JsonPropertyName("rating_type")]
    public string? RatingType { get; set; }

    [JsonPropertyName("is_rated")]
    public bool? IsRated { get; set; }
}

// ─── Notice ───────────────────────────────────────────────────────────────────

/// <summary>Port of NoticeMessage.</summary>
public class NoticeMessage : FafServerMessage
{
    [JsonPropertyName("style")]
    public string? Style { get; set; }

    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("i18n_key")]
    public string? I18nKey { get; set; }

    /// <summary>Maps style string to a severity level; mirrors Java NoticeMessage.getSeverity().</summary>
    public NoticeSeverity Severity => Style switch
    {
        "error"   => NoticeSeverity.Error,
        "warning" => NoticeSeverity.Warn,
        _         => NoticeSeverity.Info,
    };
}

public enum NoticeSeverity { Info, Warn, Error }
