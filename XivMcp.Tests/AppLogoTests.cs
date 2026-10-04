using XivMcp.Connect;

namespace XivMcp.Tests;

public class AppLogoTests
{
    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10" xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10">
          <Properties><DisplayName>ChatGPT</DisplayName><Logo>assets\StoreLogo.png</Logo></Properties>
          <Applications>
            <Application Id="App" Executable="app\ChatGPT.exe">
              <uap:VisualElements DisplayName="ChatGPT" Square150x150Logo="assets\Square150x150Logo.png" Square44x44Logo="assets\Square44x44Logo.png" />
            </Application>
          </Applications>
        </Package>
        """;

    [Fact]
    public void The_app_logo_comes_from_the_first_application_of_the_manifest() =>
        Assert.Equal(@"assets\Square44x44Logo.png", AppLogos.LogoFromManifest(Manifest));

    [Fact]
    public void A_manifest_without_visual_elements_has_no_logo() =>
        Assert.Null(AppLogos.LogoFromManifest("<Package><Applications><Application Id=\"A\" /></Applications></Package>"));

    private static readonly string[] Files =
    [
        "Square44x44Logo.png", "Square44x44Logo.scale-200.png",
        "Square44x44Logo.targetsize-32_altform-unplated.png", "Square44x44Logo.targetsize-32_altform-lightunplated.png",
        "Square44x44Logo.targetsize-64_altform-unplated.png", "Square44x44Logo.targetsize-64_altform-lightunplated.png",
        "Square44x44Logo.targetsize-256_altform-unplated.png", "Square44x44Logo.targetsize-64.png", "Square150x150Logo.png",
    ];

    [Fact]
    public void The_smallest_unplated_size_that_is_large_enough_is_picked() =>
        Assert.Equal("Square44x44Logo.targetsize-64_altform-unplated.png", AppLogos.BestLogo(@"assets\Square44x44Logo.png", Files, 48));

    [Fact]
    public void Larger_than_every_size_takes_the_largest() =>
        Assert.Equal("Square44x44Logo.targetsize-256_altform-unplated.png", AppLogos.BestLogo(@"assets\Square44x44Logo.png", Files, 512));

    [Fact]
    public void Light_theme_variants_are_never_picked() =>
        Assert.DoesNotContain("lightunplated", AppLogos.BestLogo(@"assets\Square44x44Logo.png", Files, 30));

    [Fact]
    public void Without_target_sizes_the_largest_scale_is_used_then_the_plain_file()
    {
        Assert.Equal("Square44x44Logo.scale-200.png", AppLogos.BestLogo("Square44x44Logo.png", ["Square44x44Logo.png", "Square44x44Logo.scale-100.png", "Square44x44Logo.scale-200.png"], 48));
        Assert.Equal("Square44x44Logo.png", AppLogos.BestLogo("Square44x44Logo.png", ["Square44x44Logo.png", "Other.png"], 48));
        Assert.Null(AppLogos.BestLogo("Square44x44Logo.png", ["Other.png"], 48));
    }

    [Theory]
    [InlineData("\"C:\\Users\\me\\AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe\" --open-url -- \"%1\"", "C:\\Users\\me\\AppData\\Local\\Programs\\Microsoft VS Code\\Code.exe")]
    [InlineData("C:\\Apps\\Cursor.exe \"%1\"", "C:\\Apps\\Cursor.exe")]
    [InlineData("", null)]
    public void The_program_is_read_from_an_open_command(string command, string? exe) => Assert.Equal(exe, AppLogos.ExeFromCommand(command));
}
