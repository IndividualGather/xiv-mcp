using XivMcp.Ui;

namespace XivMcp.Tests;

public class PromptTemplateTests
{
    // Addon row 4385 (English) as text pieces around its item name placeholders.
    private static readonly string[] ReplaceMissing = ["", " registered to this gear set could not be found in your Armoury Chest. Replace it with ", "?"];

    [Fact]
    public void A_prompt_matches_when_its_fixed_pieces_appear_in_order()
    {
        const string prompt = "The weathered saw registered to this gear set could not be found in your Armoury Chest. Replace it with a cosmic saw prototype v0.5?";
        Assert.True(PromptTemplate.Matches(prompt, ReplaceMissing));
    }

    [Fact]
    public void Line_breaks_and_case_in_the_window_do_not_matter()
    {
        const string prompt = "The weathered saw registered to this gear set could not be\nfound in your Armoury Chest. replace it with a cosmic saw?";
        Assert.True(PromptTemplate.Matches(prompt, ReplaceMissing));
    }

    [Fact]
    public void Other_prompts_do_not_match()
    {
        Assert.False(PromptTemplate.Matches("Discard the weathered saw?", ReplaceMissing));
        Assert.False(PromptTemplate.Matches("Replace it with a cosmic saw? It was registered to this gear set could not be found in your Armoury Chest.", ReplaceMissing));
    }

    [Fact]
    public void A_template_without_text_matches_nothing()
    {
        Assert.False(PromptTemplate.Matches("Anything?", ["", " "]));
        Assert.False(PromptTemplate.Matches("", ReplaceMissing));
    }
}
