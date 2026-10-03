using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace XivMcp.Windows;

internal sealed class ConfigWindow : Window
{
    private readonly Plugin plugin;
    private int portInput;

    public ConfigWindow(Plugin plugin) : base("XIV MCP###XivMcpConfig")
    {
        this.plugin = plugin;
        portInput = plugin.Config.Port;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(520, 360), MaximumSize = new Vector2(1400, 1200) };
    }

    public override void Draw()
    {
        var config = plugin.Config;
        var server = plugin.Server;

        // Status
        if (server.IsRunning)
            ImGui.TextColored(new Vector4(0.4f, 0.9f, 0.4f, 1), $"Running on {server.Endpoint}");
        else
            ImGui.TextColored(new Vector4(0.9f, 0.4f, 0.4f, 1), "Stopped");
        if (server.LastError is { } error)
            ImGui.TextColored(new Vector4(0.9f, 0.6f, 0.3f, 1), $"Last error: {error}");
        ImGui.TextDisabled($"Requests: {server.RequestCount}" +
                           (server.LastRequestUtc is { } last ? $"   last: {last.ToLocalTime():T}" : "") +
                           (server.LastClient is { } client ? $"   client: {client}" : ""));

        ImGui.Separator();

        var enabled = config.ServerEnabled;
        if (ImGui.Checkbox("Enable MCP server", ref enabled))
        {
            config.ServerEnabled = enabled;
            config.Save();
            if (enabled) server.Start(); else server.Stop();
        }

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("Port", ref portInput);
        portInput = Math.Clamp(portInput, 1024, 65535);
        ImGui.SameLine();
        if (ImGui.Button(portInput == config.Port ? "Restart" : "Apply & restart"))
        {
            config.Port = portInput;
            config.Save();
            if (config.ServerEnabled) server.Start();
        }

        var requireToken = config.RequireToken;
        if (ImGui.Checkbox("Require bearer token (recommended)", ref requireToken))
        {
            config.RequireToken = requireToken;
            config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Without a token, any program on this PC can read your character data from the server.");

        if (config.RequireToken)
        {
            ImGui.TextUnformatted("Token:");
            ImGui.SameLine();
            ImGui.TextDisabled(config.Token[..6] + new string('•', 12));
            ImGui.SameLine();
            if (ImGui.SmallButton("Copy##token")) ImGui.SetClipboardText(config.Token);
            ImGui.SameLine();
            if (ImGui.SmallButton("Regenerate"))
            {
                config.Token = Configuration.NewToken();
                config.Save();
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Connect a client");

        var authHeader = config.RequireToken ? $" --header \"Authorization: Bearer {config.Token}\"" : "";
        var claudeCode = $"claude mcp add --transport http ffxiv {server.Endpoint}{authHeader}";
        if (ImGui.Button("Copy Claude Code command")) ImGui.SetClipboardText(claudeCode);
        ImGui.SameLine();
        if (ImGui.Button("Copy JSON config")) ImGui.SetClipboardText(JsonConfig(server.Endpoint, config));
        ImGui.TextDisabled("JSON works for Claude Desktop (via mcp-remote), Cursor, VS Code and other MCP clients.");

        ImGui.Separator();
        if (ImGui.CollapsingHeader($"Tools ({server.Tools.Count})"))
        {
            foreach (var tool in server.Tools.OrderBy(t => t.Name))
            {
                ImGui.BulletText(tool.Name);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip(tool.Description);
            }
        }
    }

    private static string JsonConfig(string endpoint, Configuration config)
    {
        var headers = config.RequireToken ? $",\n      \"headers\": {{ \"Authorization\": \"Bearer {config.Token}\" }}" : "";
        return $$"""
            {
              "mcpServers": {
                "ffxiv": {
                  "type": "http",
                  "url": "{{endpoint}}"{{headers}}
                }
              }
            }
            """;
    }
}
