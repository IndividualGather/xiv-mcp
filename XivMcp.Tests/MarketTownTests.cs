using XivMcp.Maps;

namespace XivMcp.Tests;

public class MarketTownTests
{
    private static readonly MarketTown Limsa = new(129, "Limsa Lominsa Lower Decks", Region: 22, ExVersion: 0);
    private static readonly MarketTown Kugane = new(628, "Kugane", Region: 2400, ExVersion: 2);
    private static readonly MarketTown Tuliyollal = new(1185, "Tuliyollal", Region: 4500, ExVersion: 5);

    [Fact]
    public void Prefers_a_town_in_the_same_region()
    {
        var town = MarketTown.Pick([Limsa, Kugane, Tuliyollal], currentTerritory: 135, currentRegion: 22);
        Assert.Equal(Limsa, town);
    }

    [Fact]
    public void Otherwise_takes_the_newest_expansions_town()
    {
        // Solution Nine (Xak Tural) has no market board; Tuliyollal is in Yok Tural.
        var town = MarketTown.Pick([Limsa, Kugane, Tuliyollal], currentTerritory: 1186, currentRegion: 4501);
        Assert.Equal(Tuliyollal, town);
    }

    [Fact]
    public void Never_picks_the_zone_the_player_is_in()
    {
        var town = MarketTown.Pick([Limsa, Tuliyollal], currentTerritory: 1185, currentRegion: 4500);
        Assert.Equal(Limsa, town);
    }

    [Fact]
    public void No_towns_means_none()
    {
        Assert.Null(MarketTown.Pick([], currentTerritory: 1, currentRegion: 1));
    }
}
