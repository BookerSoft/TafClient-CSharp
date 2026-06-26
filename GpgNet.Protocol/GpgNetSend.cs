namespace GpgNet.Protocol;

/// <summary>
/// Port of GpgNetSend.cpp + all GpgNetClient::send* methods.
///
/// Each method mirrors its C++ counterpart exactly.
/// The stream is the TCP connection to the FAF GPGNet server.
/// </summary>
public sealed class GpgNetSender
{
    private readonly Stream _stream;

    public GpgNetSender(Stream stream) => _stream = stream;

    // ── sendGameState — NOTE: ICE adapter drops 2nd arg so we prefix with GameOption/SubState ──
    // Mirrors GpgNetClient::sendGameState exactly:
    //   sendCommand("GameOption", 2); sendArgument("SubState"); sendArgument(substate)
    //   sendCommand("GameState",  1); sendArgument(state)
    public Task SendGameStateAsync(string state, string substate, CancellationToken ct = default)
        => SendBatchAsync(ct,
            ("GameOption", new List<object> { "SubState", substate }),
            ("GameState",  new List<object> { state }));

    /// <summary>Mirrors sendHostGame</summary>
    public Task SendHostGameAsync(string mapName, CancellationToken ct = default)
        => Wire.WriteMessageAsync(_stream, "HostGame", [mapName], ct);

    /// <summary>Mirrors sendJoinGame</summary>
    public Task SendJoinGameAsync(string hostAndPort, string playerName, int playerId,
        CancellationToken ct = default)
        => Wire.WriteMessageAsync(_stream, "JoinGame", [hostAndPort, playerName, playerId], ct);

    /// <summary>Mirrors sendGameMods(QStringList uids)</summary>
    public Task SendGameModsAsync(IEnumerable<string> uids, CancellationToken ct = default)
        => Wire.WriteMessageAsync(_stream, "GameMods",
            new List<object> { "uids", string.Join(' ', uids) }, ct);

    /// <summary>Mirrors sendGameOption(key, value) — string value</summary>
    public Task SendGameOptionAsync(string key, string value, CancellationToken ct = default)
        => Wire.WriteMessageAsync(_stream, "GameOption", [key, value], ct);

    /// <summary>Mirrors sendGameOption(key, value) — int value</summary>
    public Task SendGameOptionAsync(string key, int value, CancellationToken ct = default)
        => Wire.WriteMessageAsync(_stream, "GameOption", [key, value], ct);

    /// <summary>Mirrors sendGameMetrics</summary>
    public Task SendGameMetricsAsync(string key, string value, CancellationToken ct = default)
        => Wire.WriteMessageAsync(_stream, "GameMetrics", [key, value], ct);

    /// <summary>Mirrors sendPlayerOption(playerId, key, value) — string</summary>
    public Task SendPlayerOptionAsync(string playerId, string key, string value,
        CancellationToken ct = default)
        => Wire.WriteMessageAsync(_stream, "PlayerOption", [playerId, key, value], ct);

    /// <summary>Mirrors sendPlayerOption(playerId, key, value) — int</summary>
    public Task SendPlayerOptionAsync(string playerId, string key, int value,
        CancellationToken ct = default)
        => Wire.WriteMessageAsync(_stream, "PlayerOption", [playerId, key, value], ct);

    /// <summary>Mirrors sendAiOption</summary>
    public Task SendAiOptionAsync(string name, string key, int value, CancellationToken ct = default)
        => Wire.WriteMessageAsync(_stream, "AIOption", [name, key, value], ct);

    /// <summary>Mirrors sendClearSlot</summary>
    public Task SendClearSlotAsync(int slot, CancellationToken ct = default)
        => Wire.WriteMessageAsync(_stream, "ClearSlot", [slot], ct);

    /// <summary>Mirrors sendGameEnded</summary>
    public Task SendGameEndedAsync(CancellationToken ct = default)
        => Wire.WriteMessageAsync(_stream, "GameEnded", [], ct);

    /// <summary>
    /// Mirrors sendGameResult(army, score):
    ///   score > 0 → "VICTORY 1"
    ///   score == 0 → "DRAW 0"
    ///   score &lt; 0 → "DEFEAT -1"
    /// </summary>
    public Task SendGameResultAsync(int army, int score, CancellationToken ct = default)
    {
        string result = score > 0 ? "VICTORY 1" : score == 0 ? "DRAW 0" : "DEFEAT -1";
        return Wire.WriteMessageAsync(_stream, "GameResult", [army, result], ct);
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task SendBatchAsync(CancellationToken ct,
        params (string cmd, List<object> args)[] messages)
    {
        foreach (var (cmd, args) in messages)
            await Wire.WriteMessageAsync(_stream, cmd, args, ct);
    }
}
