using XivMcp.Integrations;
using XivMcp.Mcp;
using XivMcp.Permissions;

namespace XivMcp.Tests;

public class ToolSummaryTests
{
    private static IEnumerable<string> BuiltInTools() =>
        PermissionCatalog.CoreTools.Keys.Concat(IntegrationCatalog.All.SelectMany(i => i.Tools.Keys));

    [Fact]
    public void Every_built_in_tool_has_a_player_facing_summary() =>
        Assert.DoesNotContain(BuiltInTools(), t => ToolSummaries.For(t) is null);

    [Fact]
    public void Summaries_are_short_sentences() =>
        Assert.All(ToolSummaries.All, kv =>
        {
            Assert.True(kv.Value.Length <= 100, $"{kv.Key}: {kv.Value.Length} characters");
            Assert.EndsWith(".", kv.Value);
            Assert.True(char.IsUpper(kv.Value[0]), kv.Key);
        });

    [Fact]
    public void Summaries_speak_to_the_player_not_about_arguments() =>
        Assert.All(ToolSummaries.All, kv =>
        {
            Assert.DoesNotMatch(@"'[a-z_]+'", kv.Value);   // no quoted argument names like 'loops'
            Assert.DoesNotMatch(@"[a-z]+_[a-z_]+", kv.Value); // no snake_case tool or argument names
        });

    private static readonly string[] ReadingVerbs = ["Looks at ", "Checks ", "Lists ", "Finds "];

    [Fact]
    public void Summaries_use_no_contractions() =>
        Assert.All(ToolSummaries.All, kv => Assert.DoesNotMatch(@"(n't|'re|'ve|'ll|'d|(it|that|there|what)'s|I'm)", kv.Value));

    [Fact]
    public void Summaries_dont_name_the_plugin_doing_the_work() =>
        Assert.All(ToolSummaries.All, kv =>
        {
            foreach (var plugin in new[] { "AutoDuty", "Artisan", "GatherBuddy", "Lifestream", "Item Vendor Location", "FCCH" })
                Assert.DoesNotContain(plugin, kv.Value);
        });

    [Fact]
    public void Only_reading_tools_start_with_a_reading_verb()
    {
        foreach (var integration in IntegrationCatalog.All)
            foreach (var (tool, caps) in integration.Tools)
            {
                var text = ToolSummaries.For(tool)!;
                var reads = ReadingVerbs.Any(text.StartsWith);
                Assert.True(reads == (caps.Length == 0), $"{tool}: \"{text}\"");
            }
    }

    [Fact]
    public void Summaries_dont_list_tools_that_dont_exist() =>
        Assert.Empty(ToolSummaries.All.Keys.Except(BuiltInTools()));
}
