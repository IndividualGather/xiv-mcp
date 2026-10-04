using XivMcp.Mcp;

namespace XivMcp.Tests;

public class ToolTextTests
{
    [Fact]
    public void Takes_the_whole_first_sentence_however_long() =>
        Assert.Equal("Runs a duty with AutoDuty, in a loop: 'loops' times, or — with 'until' — until the inventory holds the wanted items or currency (e.g. a dungeon drop, or tomestones), at most 'loops' runs.",
            ToolText.FirstSentence("Runs a duty with AutoDuty, in a loop: 'loops' times, or — with 'until' — until the inventory holds the wanted items or currency (e.g. a dungeon drop, or tomestones), at most 'loops' runs. AutoDuty does the running."));

    [Theory]
    [InlineData("Uses a plugin (e.g. AutoDuty) to run. More.", "Uses a plugin (e.g. AutoDuty) to run.")]
    [InlineData("Lists items, i.e. everything you own. More.", "Lists items, i.e. everything you own.")]
    [InlineData("Version 1.2 of the tool. More.", "Version 1.2 of the tool.")]
    public void Abbreviations_and_numbers_dont_end_the_sentence(string text, string first) => Assert.Equal(first, ToolText.FirstSentence(text));

    [Fact]
    public void Text_without_a_sentence_end_is_returned_whole() => Assert.Equal("Reads the inventory", ToolText.FirstSentence("Reads the inventory"));

    [Fact]
    public void Truncate_cuts_at_a_word_and_marks_it() =>
        Assert.Equal("Moves items between…", ToolText.Truncate("Moves items between bags and retainers", 22));

    [Fact]
    public void Truncate_leaves_short_text_alone() => Assert.Equal("Short.", ToolText.Truncate("Short.", 22));
}
