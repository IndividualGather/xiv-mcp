using XivMcp.Mcp;

namespace XivMcp.Tests;

public class ToolTextParagraphTests
{
    private static List<ToolText.Block> P(string text) => ToolText.Paragraphs(text);

    [Fact]
    public void The_first_sentence_is_the_summary_the_rest_details()
    {
        var p = P("Moves items between bags. Each move waits for the server. Nothing happens in combat.");
        Assert.Equal(2, p.Count);
        Assert.Equal(new ToolText.Block(ToolText.BlockKind.Summary, "Moves items between bags."), p[0]);
        Assert.Equal(new ToolText.Block(ToolText.BlockKind.Text, "Each move waits for the server. Nothing happens in combat."), p[1]);
    }

    [Fact]
    public void Requirement_sentences_go_last_in_their_own_paragraph()
    {
        var p = P("Runs a duty with AutoDuty. Only available while AutoDuty is loaded; needs 'Game & navigation' in /xivmcp. A run takes ~20 minutes.");
        Assert.Equal(ToolText.BlockKind.Summary, p[0].Kind);
        Assert.Equal(new ToolText.Block(ToolText.BlockKind.Text, "A run takes ~20 minutes."), p[1]);
        Assert.Equal(new ToolText.Block(ToolText.BlockKind.Requirement, "Only available while AutoDuty is loaded; needs 'Game & navigation' in /xivmcp."), p[2]);
    }

    [Theory]
    [InlineData("Requires Artisan.")]
    [InlineData("Needs Lifestream.")]
    [InlineData("Requires 'Market & purchases' in /xivmcp.")]
    public void Requirement_starts_are_recognised(string sentence) =>
        Assert.Equal(ToolText.BlockKind.Requirement, P($"Does a thing. {sentence}").Last().Kind);

    [Fact]
    public void Explicit_paragraphs_and_bullets_are_kept()
    {
        var p = P("Sells loot.\n\nIt walks to the vendor first.\n- Skips HQ items\n- Never sells gear\n\nRequires My Plugin's vendor list.");
        Assert.Equal(
        [
            new(ToolText.BlockKind.Summary, "Sells loot."),
            new(ToolText.BlockKind.Text, "It walks to the vendor first."),
            new(ToolText.BlockKind.Bullet, "Skips HQ items"),
            new(ToolText.BlockKind.Bullet, "Never sells gear"),
            new(ToolText.BlockKind.Requirement, "Requires My Plugin's vendor list."),
        ], p);
    }

    [Fact]
    public void A_single_sentence_is_just_a_summary() =>
        Assert.Equal([new ToolText.Block(ToolText.BlockKind.Summary, "Reads the inventory.")], P("Reads the inventory."));

    [Fact]
    public void Abbreviations_dont_split_sentences() =>
        Assert.Equal("Uses a plugin (e.g. AutoDuty) to run.", P("Uses a plugin (e.g. AutoDuty) to run. More here.")[0].Text);
}
