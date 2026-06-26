# downlords-taf-client — Java → C# Port

**Source:** `ta-forever/downlords-taf-client` (develop branch, 678 Java files)  
**Status:** Core networking + domain layer complete

---

## Critical protocol detail (from actual source)

The lobby server does **NOT** use newline-delimited JSON. It uses Qt `QDataStream` framing:

```
[int32 block_size, big-endian]       ← total bytes following (4 + str_len)
[int32 str_len, big-endian]          ← byte length of the JSON string (-1 = null)
[str_len bytes, UTF-16 Big-Endian]   ← JSON payload
```

This is implemented in `QDataInputStream.cs` / `QDataWriter.cs`, faithfully ported from
`com.faforever.client.remote.io.QDataInputStream` and `QDataWriter`.

The Java code in `AbstractServerAccessor.blockingReadServer()` does:
```java
dataInput.skipBlockSize();      // skip 4-byte block size
String message = dataInput.readQString();  // read 4-byte length + UTF-16BE bytes
```

And `QDataWriter.appendWithSize(byte[])` writes the inverse.

---

## Architecture mapping

| Java | C# | Notes |
|------|----|-------|
| `@SpringBootApplication` / `@Service` | `IHost` + `IServiceCollection` | App.xaml.cs |
| `@ConfigurationProperties` | `IOptions<T>` | `Config/ClientProperties.cs` |
| `SimpleStringProperty` / `SimpleIntegerProperty` | `[ObservableProperty]` (CommunityToolkit.Mvvm) | Generates property + INPC |
| `ObservableMap` | `Dictionary<K,V>` + [ObservableProperty] | Maps inside Game/Player |
| `ObservableList<Game>` | `ObservableCollection<Game>` | WPF data-binding |
| Guava `EventBus` / `@Subscribe` | `ServerMessageRouter.AddListener<T>()` | Per-command typed handlers |
| Gson `FieldNamingPolicy.LOWER_CASE_WITH_UNDERSCORES` | `JsonNamingPolicy.SnakeCaseLower` | Client→server serialization |
| Gson `JsonDeserializer<ServerMessage>` (type adapter) | `ServerMessageRouter.DeserializeAs()` | Type-driven deserialization |
| `JavaFxUtil.runLater()` | `SynchronizationContext.Current?.Post()` | UI thread marshalling |
| `CompletableFuture<T>` | `TaskCompletionSource<T>` + `Task<T>` | Async results |
| `ReconnectTimerService` | `ReconnectTimerService.cs` | Exponential backoff |

---

## What is ported (from actual source files)

### `Net/Io/`
- `QDataInputStream.cs` ← `remote/io/QDataInputStream.java`
- `QDataWriter.cs` ← `remote/io/QDataWriter.java`

### `Net/Domain/`
- `Enums.cs` ← `FafServerMessageType`, `ClientMessageType`, `GameStatus`, `PlayerStatus`, `GameType`, `GameVisibility`, `MatchmakingState`
- `ServerMessages.cs` ← all `FafServerMessage` subclasses: `SessionMessage`, `LoginMessage`, `PlayersMessage`, `PlayerDto`, `GameInfoMessage`, `GameLaunchMessage`, `SocialMessage`, `NoticeMessage`, `AuthenticationFailedMessage`
- `ClientMessages.cs` ← all `ClientMessage` subclasses: `InitSessionMessage`, `LoginClientMessage`, `HostGameMessage`, `JoinGameMessage`, `AddFriend/FoeMessage`, `PingMessage`, `GameMatchmakingMessage`, party messages, etc.

### `Net/`
- `ConnectionState.cs` ← `net/ConnectionState.java`
- `ServerMessageRouter.cs` ← `remote/gson/ServerMessageTypeAdapter.java` + listener dispatch from `FafServerAccessorImpl`
- `ServerWriter.cs` ← `remote/ServerWriter.java`
- `FafServerAccessorImpl.cs` ← `remote/FafServerAccessor.java` + `remote/FafServerAccessorImpl.java` + `remote/AbstractServerAccessor.java`

### `Domain/`
- `Models.cs` ← `player/Player.java`, `game/Game.java`, `game/NewGameInfo.java`, `leaderboard/LeaderboardRating.java`, `game/GameVisibility.java`, `player/SocialStatus.java`

### `Service/`
- `PlayerService.cs` ← `player/PlayerService.java`
- `UserService.cs` ← `user/UserService.java`
- `GameService.cs` ← `game/GameService.java` (lobby portion)

### `Config/`
- `ClientProperties.cs` ← `config/ClientProperties.java`

---

## What remains to be ported (by package)

### High priority
- `remote/FafService.java` → `Service/FafService.cs` (thin facade over accessor + API)
- `login/LoginController.java` → `UI/Login/LoginViewModel.cs` + `LoginView.xaml`
- `game/GameService.java` (game *launch* portion: `startGame`, `startBattleRoom`, `runWithReplay`)
- `fa/TotalAnnihilationService.java` → TA process management
- `fa/relay/ice/IceAdapter.java` → ICE WebRTC adapter integration

### Medium priority
- `chat/` package → `IrcDotNet` replacing `PircBotX`
- `map/MapService.java` → HPI archive reader + download
- `mod/ModService.java` → mod scanning + featured mod updater
- `replay/ReplayService.java` → `.tad` replay download/playback
- `patch/GameUpdater.java` → featured mod update via git-lfs
- `legacy/UidService.java` → call `faf-uid` native exe (TODO placeholder in FafServerAccessorImpl)
- `fa/relay/GpgClientMessageSerializer.java` → GPGNet binary framing for in-game messages
- `preferences/PreferencesService.java` → persist settings to JSON

### Low priority / stretch
- `achievements/` package
- `leaderboard/` package (API calls already scaffolded)
- `tournament/` package
- `galacticwar/` package
- `tada/` package (replay upload)
- `discord/DiscordRichPresenceService.java` → Discord RPC

---

## How to build

```bash
dotnet restore TafClient.sln
dotnet build TafClient.sln
dotnet test TafClient.sln
```

> The main project requires `net8.0-windows` (WPF). Tests target `net8.0` and run cross-platform.
