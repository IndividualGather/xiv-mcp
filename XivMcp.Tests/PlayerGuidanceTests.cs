using XivMcp.Maps;

namespace XivMcp.Tests;

public class PlayerGuidanceTests
{
    [Fact]
    public void Walking_names_the_spot_the_zone_and_the_coordinates()
    {
        var text = PlayerGuidance.WalkTo("Summoning Bell", "Limsa Lominsa Lower Decks", 9.04, 11.46);
        Assert.Contains("Summoning Bell", text);
        Assert.Contains("Limsa Lominsa Lower Decks", text);
        Assert.Contains("(9.0, 11.5)", text);
        Assert.Contains("flag", text);
    }

    [Fact]
    public void Teleporting_names_the_aetheryte_and_the_aethernet_shard_if_any()
    {
        Assert.Equal("Teleport to Limsa Lominsa Lower Decks (in Limsa Lominsa Lower Decks).",
            PlayerGuidance.TeleportTo("Limsa Lominsa Lower Decks", "Limsa Lominsa Lower Decks", null));
        var withShard = PlayerGuidance.TeleportTo("Limsa Lominsa Lower Decks", "Limsa Lominsa Upper Decks", "The Aftcastle");
        Assert.Contains("aethernet", withShard);
        Assert.Contains("The Aftcastle", withShard);
    }

    [Theory]
    [InlineData("inn", "inn room")]
    [InlineData("lifestream", "inn room")]
    [InlineData("fc", "free company house")]
    [InlineData("workshop", "workshop")]
    [InlineData("home", "house")]
    [InlineData("apartment", "apartment")]
    public void Places_are_named(string destination, string expected) => Assert.Contains(expected, PlayerGuidance.GoTo(destination));

    [Fact]
    public void Unknown_places_are_refused() => Assert.Throws<ArgumentException>(() => PlayerGuidance.GoTo("moon"));

    [Theory]
    [InlineData("inn", PlayerGuidance.InnUse, true)]
    [InlineData("inn", PlayerGuidance.HousingInteriorUse, false)]
    [InlineData("lifestream", PlayerGuidance.InnUse, true)]
    [InlineData("fc", PlayerGuidance.HousingInteriorUse, true)]
    [InlineData("home", PlayerGuidance.HousingInteriorUse, true)]
    [InlineData("apartment", PlayerGuidance.HousingInteriorUse, true)]
    [InlineData("workshop", PlayerGuidance.HousingInteriorUse, true)]
    [InlineData("fc", 0u, false)]
    public void A_place_is_reached_by_the_kind_of_area(string destination, uint intendedUse, bool reached) =>
        Assert.Equal(reached, PlayerGuidance.Reached(destination, intendedUse));

    [Fact]
    public void Messages_use_no_contractions()
    {
        var texts = new[]
        {
            PlayerGuidance.WalkTo("Summoning Bell", "Mist", 10, 10),
            PlayerGuidance.TeleportTo("Mist", "Mist", "Mist Shard"),
            PlayerGuidance.OpenShop("Grade 8 Tincture of Strength"),
            PlayerGuidance.FindAppraiser,
            PlayerGuidance.VisitWorld("Shiva", "Light", true),
            PlayerGuidance.VisitWorld("Gilgamesh", "Aether", false),
        }.Concat(new[] { "inn", "fc", "workshop", "home", "apartment" }.Select(PlayerGuidance.GoTo));
        Assert.All(texts, t => Assert.DoesNotMatch(@"(n't|'re|'ve|'ll|'d|(it|that|there|what)'s|I'm)", t));
    }

    [Fact]
    public void World_travel_names_the_world_and_how_to_get_there()
    {
        var visit = PlayerGuidance.VisitWorld("Shiva", "Light", sameDataCenter: true);
        Assert.Contains("Shiva", visit);
        Assert.Contains("World Visit", visit);
        var travel = PlayerGuidance.VisitWorld("Gilgamesh", "Aether", sameDataCenter: false);
        Assert.Contains("Gilgamesh", travel);
        Assert.Contains("Aether", travel);
        Assert.Contains("Data Center Travel", travel);
    }

    [Fact]
    public void The_shop_request_names_the_item() => Assert.Contains("Hi-Potion", PlayerGuidance.OpenShop("Hi-Potion"));
}
