using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using XivMcp.Connect;

namespace XivMcp.Tests;

public class ClientSetupTests
{
    private const string Endpoint = "http://localhost:37521/mcp";
    private const string Token = "abc123";

    private static JsonObject Json(string s) => JsonNode.Parse(s)!.AsObject();

    // ---- Claude Desktop: an .mcpb bundle with a stdio bridge

    [Fact]
    public void Mcpb_manifest_runs_the_bridge_with_node_and_passes_the_address_and_token()
    {
        var m = Json(ClientSetup.McpbManifest("0.2.0", Endpoint, Token));
        Assert.Equal("0.3", m["manifest_version"]!.GetValue<string>());
        Assert.Equal("0.2.0", m["version"]!.GetValue<string>());
        Assert.Equal("node", m["server"]!["type"]!.GetValue<string>());
        Assert.Equal("server/index.js", m["server"]!["entry_point"]!.GetValue<string>());
        var config = m["server"]!["mcp_config"]!;
        Assert.Equal("node", config["command"]!.GetValue<string>());
        Assert.Equal("${__dirname}/server/index.js", config["args"]![0]!.GetValue<string>());
        Assert.Equal(Endpoint, config["env"]!["XIVMCP_URL"]!.GetValue<string>());
        Assert.Equal(Token, config["env"]!["XIVMCP_TOKEN"]!.GetValue<string>());
        Assert.Equal("icon.png", m["icon"]!.GetValue<string>());
        Assert.True(m["tools_generated"]!.GetValue<bool>());
        Assert.NotNull(m["author"]!["name"]);
    }

    [Fact]
    public void Mcpb_manifest_without_a_token_sends_none()
    {
        var env = Json(ClientSetup.McpbManifest("0.2.0", Endpoint, null))["server"]!["mcp_config"]!["env"]!.AsObject();
        Assert.False(env.ContainsKey("XIVMCP_TOKEN"));
    }

    [Fact]
    public void Mcpb_bundle_holds_the_manifest_the_bridge_and_the_icon()
    {
        using var stream = new MemoryStream();
        ClientSetup.WriteMcpb(stream, "0.2.0", Endpoint, Token, [1, 2, 3]);
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Equal(["icon.png", "manifest.json", "server/index.js"], zip.Entries.Select(e => e.FullName).Order());
        using var bridge = new StreamReader(zip.GetEntry("server/index.js")!.Open());
        var js = bridge.ReadToEnd();
        Assert.Contains("XIVMCP_URL", js);
        Assert.Contains("XIVMCP_TOKEN", js);
    }

    [Fact]
    public void Mcpb_bundle_skips_the_icon_when_there_is_none()
    {
        using var stream = new MemoryStream();
        ClientSetup.WriteMcpb(stream, "0.2.0", Endpoint, Token, null);
        stream.Position = 0;
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        Assert.Null(zip.GetEntry("icon.png"));
        using var manifest = new StreamReader(zip.GetEntry("manifest.json")!.Open());
        Assert.False(Json(manifest.ReadToEnd()).ContainsKey("icon"));
    }

    // ---- install links

    [Fact]
    public void VsCode_link_carries_the_server_as_url_encoded_json()
    {
        var link = ClientSetup.VsCodeLink(Endpoint, Token);
        Assert.StartsWith("vscode:mcp/install?", link);
        var config = Json(Uri.UnescapeDataString(link["vscode:mcp/install?".Length..]));
        Assert.Equal("ffxiv", config["name"]!.GetValue<string>());
        Assert.Equal("http", config["type"]!.GetValue<string>());
        Assert.Equal(Endpoint, config["url"]!.GetValue<string>());
        Assert.Equal($"Bearer {Token}", config["headers"]!["Authorization"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("cursor://anysphere.cursor-deeplink/mcp/install?")]
    [InlineData("lmstudio://add_mcp?")]
    public void Cursor_and_LM_Studio_links_carry_a_base64_config(string prefix)
    {
        var link = prefix.StartsWith("cursor") ? ClientSetup.CursorLink(Endpoint, Token) : ClientSetup.LmStudioLink(Endpoint, Token);
        Assert.StartsWith(prefix, link);
        var query = System.Web.HttpUtility.ParseQueryString(link[prefix.Length..]);
        Assert.Equal("ffxiv", query["name"]);
        var config = Json(Encoding.UTF8.GetString(Convert.FromBase64String(query["config"]!)));
        Assert.Equal(Endpoint, config["url"]!.GetValue<string>());
        Assert.Equal($"Bearer {Token}", config["headers"]!["Authorization"]!.GetValue<string>());
    }

    [Fact]
    public void Links_without_a_token_send_no_headers()
    {
        var config = Json(Uri.UnescapeDataString(ClientSetup.VsCodeLink(Endpoint, null)["vscode:mcp/install?".Length..]));
        Assert.False(config.ContainsKey("headers"));
    }

    // ---- Claude Code: the claude CLI

    [Fact]
    public void Claude_Code_adds_the_server_for_all_projects_with_the_token_header()
    {
        Assert.Equal(["mcp", "add", "--scope", "user", "--transport", "http", "ffxiv", Endpoint, "--header", $"Authorization: Bearer {Token}"],
                     ClientSetup.ClaudeCodeAddArgs(Endpoint, Token));
        Assert.Equal(["mcp", "add", "--scope", "user", "--transport", "http", "ffxiv", Endpoint], ClientSetup.ClaudeCodeAddArgs(Endpoint, null));
    }

    [Fact]
    public void Claude_Code_removes_only_the_user_entry_before_adding_again() =>
        Assert.Equal(["mcp", "remove", "--scope", "user", "ffxiv"], ClientSetup.ClaudeCodeRemoveArgs());

    // ---- Codex: ~/.codex/config.toml

    [Fact]
    public void Codex_entry_is_added_to_an_empty_config()
    {
        var toml = ClientSetup.MergeCodexConfig("", Endpoint, Token);
        Assert.Equal($"[mcp_servers.ffxiv]\nurl = \"{Endpoint}\"\nhttp_headers = {{ \"Authorization\" = \"Bearer {Token}\" }}\n", toml);
    }

    [Fact]
    public void Codex_entry_keeps_everything_else_in_the_config()
    {
        const string existing = "model = \"gpt-5\"\n\n[mcp_servers.other]\ncommand = \"npx\"\n";
        var toml = ClientSetup.MergeCodexConfig(existing, Endpoint, Token);
        Assert.StartsWith(existing, toml);
        Assert.Contains("[mcp_servers.ffxiv]", toml);
    }

    [Fact]
    public void Codex_entry_replaces_an_older_one_with_its_sub_tables_in_place()
    {
        const string existing = "model = \"gpt-5\"\n\n[mcp_servers.ffxiv]\nurl = \"http://localhost:1/mcp\"\nbearer_token_env_var = \"XIVMCP_TOKEN\"\n\n" +
                                "[mcp_servers.ffxiv.env]\nA = \"b\"\n\n[profiles.work]\nmodel = \"o3\"\n";
        var toml = ClientSetup.MergeCodexConfig(existing, Endpoint, Token);
        Assert.Equal(1, toml.Split("[mcp_servers.ffxiv]").Length - 1);
        Assert.DoesNotContain("localhost:1", toml);
        Assert.DoesNotContain("mcp_servers.ffxiv.env", toml);
        Assert.DoesNotContain("bearer_token_env_var", toml);
        Assert.Contains("[profiles.work]\nmodel = \"o3\"", toml);
        Assert.True(toml.IndexOf("[mcp_servers.ffxiv]", StringComparison.Ordinal) < toml.IndexOf("[profiles.work]", StringComparison.Ordinal));
    }

    [Fact]
    public void Codex_merge_is_idempotent_and_keeps_windows_line_endings()
    {
        const string existing = "model = \"gpt-5\"\r\n";
        var once = ClientSetup.MergeCodexConfig(existing, Endpoint, Token);
        Assert.Equal(once, ClientSetup.MergeCodexConfig(once, Endpoint, Token));
        Assert.DoesNotContain("\n", once.Replace("\r\n", ""));
    }

    [Fact]
    public void Codex_entry_without_a_token_has_no_headers()
    {
        Assert.DoesNotContain("http_headers", ClientSetup.MergeCodexConfig("", Endpoint, null));
    }

    [Fact]
    public void Removing_the_Codex_entry_takes_its_sub_tables_and_keeps_the_rest()
    {
        const string existing = "model = \"gpt-5\"\n\n[mcp_servers.ffxiv]\nurl = \"x\"\n\n[mcp_servers.ffxiv.env]\nA = \"b\"\n\n[profiles.work]\nmodel = \"o3\"\n";
        var toml = ClientSetup.RemoveCodexEntry(existing);
        Assert.False(ClientSetup.HasCodexEntry(toml));
        Assert.Equal("model = \"gpt-5\"\n\n[profiles.work]\nmodel = \"o3\"\n", toml);
    }

    [Fact]
    public void Removing_an_added_entry_gives_back_the_original_config()
    {
        const string existing = "model = \"gpt-5\"\r\n";
        Assert.Equal(existing, ClientSetup.RemoveCodexEntry(ClientSetup.MergeCodexConfig(existing, Endpoint, Token)));
        Assert.Equal("", ClientSetup.RemoveCodexEntry(ClientSetup.MergeCodexConfig("", Endpoint, Token)));
    }

    [Fact]
    public void Removing_from_a_config_without_the_entry_changes_nothing() =>
        Assert.Equal("model = \"gpt-5\"\n", ClientSetup.RemoveCodexEntry("model = \"gpt-5\"\n"));

    [Fact]
    public void Claude_Code_removes_project_entries_with_the_local_scope() =>
        Assert.Equal(["mcp", "remove", "--scope", "local", "ffxiv"], ClientSetup.ClaudeCodeRemoveArgs("local"));

    [Fact]
    public void Codex_config_reports_whether_the_entry_is_there() =>
        Assert.True(ClientSetup.HasCodexEntry(ClientSetup.MergeCodexConfig("", Endpoint, Token)));
}
