using FluentAssertions;
using TafClient.Domain;
using TafClient.Net.Domain;

namespace TafClient.Tests;

public class DomainModelTests
{
    // ─── LeaderboardRating ────────────────────────────────────────────────────

    [Fact]
    public void LeaderboardRating_FromDto_ParsesMeanAndDeviation()
    {
        var dto = new LeaderboardRatingDto { Rating = [1500f, 120f], NumberOfGames = 30 };
        var rating = LeaderboardRating.FromDto(dto);

        rating.Mean.Should().Be(1500f);
        rating.Deviation.Should().Be(120f);
        rating.NumberOfGames.Should().Be(30);
    }

    [Fact]
    public void LeaderboardRating_FromDto_HandlesNullRating()
    {
        var dto = new LeaderboardRatingDto { Rating = null, NumberOfGames = null };
        var rating = LeaderboardRating.FromDto(dto);

        rating.Mean.Should().Be(0f);
        rating.Deviation.Should().Be(0f);
        rating.NumberOfGames.Should().Be(0);
    }

    // ─── Player ───────────────────────────────────────────────────────────────

    [Fact]
    public void Player_UpdateFromDto_SetsAllFields()
    {
        var dto = new PlayerDto
        {
            Id = 42, Login = "Axle1975", Alias = "AxAlias",
            Clan = "TST", Country = "AU",
            State = "idle", CurrentGameUid = -1, AfkSeconds = 0,
            NumberOfGames = 100,
            Ratings = new Dictionary<string, LeaderboardRatingDto>
            {
                ["global"] = new() { Rating = [1200f, 90f], NumberOfGames = 100 }
            },
            Avatar = new AvatarDto { Url = "https://example.com/av.png", Tooltip = "Cool" }
        };

        var player = new Player("Axle1975");
        player.UpdateFromDto(dto);

        player.Id.Should().Be(42);
        player.Clan.Should().Be("TST");
        player.Country.Should().Be("AU");
        player.Alias.Should().Be("AxAlias");
        player.AvatarUrl.Should().Be("https://example.com/av.png");
        player.LeaderboardRatings.Should().ContainKey("global");
        player.LeaderboardRatings["global"].Mean.Should().Be(1200f);
    }

    [Fact]
    public void Player_Equality_ById()
    {
        var p1 = new Player("Same") { Id = 5 };
        var p2 = new Player("Different") { Id = 5 };
        p1.Should().Be(p2);
    }

    [Fact]
    public void Player_Equality_ByUsernameWhenIdIsZero()
    {
        var p1 = new Player("alice");
        var p2 = new Player("ALICE");
        p1.Should().Be(p2);
    }

    // ─── Game ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Game_Status_IsOpen_StagingAndBattleroom()
    {
        new Game { Status = GameStatus.Staging }.IsOpen().Should().BeTrue();
        new Game { Status = GameStatus.Battleroom }.IsOpen().Should().BeTrue();
        new Game { Status = GameStatus.Live }.IsOpen().Should().BeFalse();
        new Game { Status = GameStatus.Ended }.IsOpen().Should().BeFalse();
    }

    [Fact]
    public void Game_Status_IsInProgress_LaunchingAndLive()
    {
        new Game { Status = GameStatus.Launching }.IsInProgress().Should().BeTrue();
        new Game { Status = GameStatus.Live }.IsInProgress().Should().BeTrue();
        new Game { Status = GameStatus.Staging }.IsInProgress().Should().BeFalse();
    }

    [Fact]
    public void Game_Equality_ById()
    {
        var g1 = new Game { Id = 10 };
        var g2 = new Game { Id = 10 };
        g1.Should().Be(g2);
    }

    // ─── GameStatus parsing ───────────────────────────────────────────────────

    [Theory]
    [InlineData("staging",    GameStatus.Staging)]
    [InlineData("battleroom", GameStatus.Battleroom)]
    [InlineData("launching",  GameStatus.Launching)]
    [InlineData("live",       GameStatus.Live)]
    [InlineData("ended",      GameStatus.Ended)]
    [InlineData("STAGING",    GameStatus.Staging)]   // case-insensitive
    [InlineData("unknown_xyz",GameStatus.Unknown)]   // unknown → Unknown
    [InlineData(null,         GameStatus.Unknown)]
    public void GameStatus_Parse_Correct(string? input, GameStatus expected) =>
        GameStatusExtensions.Parse(input).Should().Be(expected);

    // ─── PlayerStatus parsing ─────────────────────────────────────────────────

    [Theory]
    [InlineData("idle",    PlayerStatus.Idle)]
    [InlineData("hosting", PlayerStatus.Hosting)]
    [InlineData("playing", PlayerStatus.Playing)]
    [InlineData(null,      PlayerStatus.Idle)]
    public void PlayerStatus_Parse_Correct(string? input, PlayerStatus expected) =>
        PlayerStatusExtensions.Parse(input).Should().Be(expected);
}
