using XivMcp.Maps;

namespace XivMcp.Tests;

public class TeleportPickTests
{
    // Limsa Upper Decks: the Airship Landing shard is right above the vendor (flat distance), but on a platform reached by lift.
    private static readonly TeleportOption Landing = new(1, false, ToTarget: 20, ToPlayer: null);
    private static readonly TeleportOption Aftcastle = new(2, false, ToTarget: 90, ToPlayer: null);
    private static readonly TeleportOption City = new(3, true, ToTarget: 300, ToPlayer: null);
    private static readonly TeleportOption[] Options = [Landing, Aftcastle, City];

    [Fact]
    public void From_another_zone_the_nearest_shard_is_taken() =>
        Assert.Equal(Landing, TeleportPick.Choose(Options, playerToTarget: null, retry: false, tried: []));

    [Fact]
    public void A_close_player_walks() =>
        Assert.Null(TeleportPick.Choose(Options, playerToTarget: 40, retry: false, tried: []));

    [Fact]
    public void After_a_blocked_walk_the_tried_shard_is_skipped() =>
        Assert.Equal(Aftcastle, TeleportPick.Choose(Options, playerToTarget: 20, retry: true, tried: [1]));

    [Fact]
    public void After_a_blocked_walk_the_shard_the_player_stands_at_is_skipped()
    {
        // Walked from the platform (nothing tried yet), the player stands at the landing shard.
        var here = new[] { Landing with { ToPlayer = 5 }, Aftcastle with { ToPlayer = 100 }, City with { ToPlayer = 280 } };
        Assert.Equal(here[1], TeleportPick.Choose(here, playerToTarget: 20, retry: true, tried: []));
    }

    [Fact]
    public void A_retry_never_walks_even_when_close() =>
        Assert.Equal(Aftcastle, TeleportPick.Choose(Options, playerToTarget: 10, retry: true, tried: [1]));

    [Fact]
    public void Nothing_left_to_try_is_null() =>
        Assert.Null(TeleportPick.Choose(Options, playerToTarget: 20, retry: true, tried: [1, 2, 3]));

    [Fact]
    public void An_aetheryte_wins_a_tie() =>
        Assert.Equal(3u, TeleportPick.Choose([new(4, false, 50, null), new(3, true, 50, null)], null, false, [])?.Id);
}
