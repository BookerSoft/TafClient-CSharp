using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TafClient.Net;
using TafClient.Net.Domain;

namespace TafClient.Tests;

/// <summary>
/// Tests for <see cref="ServerMessageRouter"/>.
/// Verifies that the "command" field correctly routes to typed handlers,
/// matching the behavior of <c>ServerMessageTypeAdapter</c> + listener dispatch
/// in <c>FafServerAccessorImpl</c>.
/// </summary>
public class ServerMessageRouterTests
{
    private static ServerMessageRouter MakeRouter() =>
        new(NullLogger<ServerMessageRouter>.Instance);

    [Fact]
    public void Dispatch_SessionMessage_DeliveredTyped()
    {
        var router = MakeRouter();
        SessionMessage? received = null;
        router.AddListener<SessionMessage>("session", m => received = m);

        router.Dispatch("""{"command":"session","session":99999}""");

        received.Should().NotBeNull();
        received!.Session.Should().Be(99999);
    }

    [Fact]
    public void Dispatch_WelcomeMessage_DeliveredAsLoginMessage()
    {
        var router = MakeRouter();
        LoginMessage? received = null;
        router.AddListener<LoginMessage>("welcome", m => received = m);

        router.Dispatch("""{"command":"welcome","id":42,"login":"Axle1975"}""");

        received.Should().NotBeNull();
        received!.Id.Should().Be(42);
        received.Login.Should().Be("Axle1975");
    }

    [Fact]
    public void Dispatch_GameInfoMessage_ParsesAllCoreFields()
    {
        var router = MakeRouter();
        GameInfoMessage? received = null;
        router.AddListener<GameInfoMessage>("game_info", m => received = m);

        router.Dispatch("""
        {
          "command": "game_info",
          "uid": 77,
          "title": "Test Game",
          "state": "staging",
          "host": "SomePlayer",
          "featured_mod": "taesc",
          "map_name": "delta_siege_dry",
          "map_file_path": "TOTALA.HPI/delta_siege_dry/abcd1234",
          "num_players": 2,
          "max_players": 8,
          "password_protected": false,
          "teams": {"1": ["Alpha"], "2": ["Beta"]},
          "rating_min": 500,
          "rating_max": 1500
        }
        """);

        received.Should().NotBeNull();
        received!.Uid.Should().Be(77);
        received.Host.Should().Be("SomePlayer");
        received.State.Should().Be("staging");
        received.Teams!["1"].Should().Contain("Alpha");
        received.RatingMin.Should().Be(500);
        received.RatingMax.Should().Be(1500);
    }

    [Fact]
    public void Dispatch_PlayersMessage_PopulatesPlayerList()
    {
        var router = MakeRouter();
        PlayersMessage? received = null;
        router.AddListener<PlayersMessage>("player_info", m => received = m);

        router.Dispatch("""
        {
          "command": "player_info",
          "players": [
            {
              "id": 1,
              "login": "TestUser",
              "country": "DE",
              "clan": "TST",
              "ratings": {
                "global": {"rating": [1200.0, 80.0], "number_of_games": 50}
              },
              "state": "idle",
              "current_game_uid": -1,
              "afk_seconds": 0
            }
          ]
        }
        """);

        received.Should().NotBeNull();
        received!.Players.Should().HaveCount(1);
        var p = received.Players![0];
        p.Id.Should().Be(1);
        p.Login.Should().Be("TestUser");
        p.Country.Should().Be("DE");
        p.Ratings!["global"].Rating.Should().BeEquivalentTo(new float[] { 1200f, 80f });
    }

    [Fact]
    public void Dispatch_NoticeMessage_ParsesStyleAndText()
    {
        var router = MakeRouter();
        NoticeMessage? received = null;
        router.AddListener<NoticeMessage>("notice", m => received = m);

        router.Dispatch("""{"command":"notice","style":"kill","text":"game.kicked"}""");

        received.Should().NotBeNull();
        received!.Style.Should().Be("kill");
        received.Text.Should().Be("game.kicked");
        received.Severity.Should().Be(NoticeSeverity.Info); // "kill" maps to Info
    }

    [Fact]
    public void Dispatch_SocialMessage_ParsesFriendsAndFoes()
    {
        var router = MakeRouter();
        SocialMessage? received = null;
        router.AddListener<SocialMessage>("social", m => received = m);

        router.Dispatch("""{"command":"social","friends":[1,2,3],"foes":[4,5]}""");

        received!.Friends.Should().BeEquivalentTo(new[] { 1, 2, 3 });
        received.Foes.Should().BeEquivalentTo(new[] { 4, 5 });
    }

    [Fact]
    public void Dispatch_UnknownCommand_DoesNotThrow()
    {
        var router = MakeRouter();
        var act = () => router.Dispatch("""{"command":"future_unknown_cmd","data":42}""");
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispatch_GameTarget_IsIgnored()
    {
        // Messages with "target":"game" are GPGNet relay messages; router skips them
        var router = MakeRouter();
        bool called = false;
        router.AddListener<FafServerMessage>("ConnectToPeer", _ => called = true);

        router.Dispatch("""{"command":"ConnectToPeer","target":"game","args":["1.2.3.4:6112",1]}""");

        called.Should().BeFalse();
    }

    [Fact]
    public void Dispatch_MultipleListenersSameCommand_AllCalled()
    {
        var router = MakeRouter();
        int count = 0;
        router.AddListener<SessionMessage>("session", _ => count++);
        router.AddListener<SessionMessage>("session", _ => count++);

        router.Dispatch("""{"command":"session","session":1}""");

        count.Should().Be(2);
    }

    [Fact]
    public void Dispatch_GameInfoBatchedGames_NotDeliveredDirectly()
    {
        // When a game_info message has a "games" array, the outer message
        // itself is still delivered — callers must handle the nested array.
        var router = MakeRouter();
        GameInfoMessage? outer = null;
        router.AddListener<GameInfoMessage>("game_info", m => outer = m);

        router.Dispatch("""
        {
          "command": "game_info",
          "games": [
            {"uid": 1, "title": "Game 1", "state": "staging"},
            {"uid": 2, "title": "Game 2", "state": "staging"}
          ]
        }
        """);

        outer.Should().NotBeNull();
        outer!.Games.Should().HaveCount(2);
    }
}
