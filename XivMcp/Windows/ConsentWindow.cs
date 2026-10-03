using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using XivMcp.Util;

namespace XivMcp.Windows;

/// <summary>The in-game approval popup for <see cref="Consent"/> requests. Opens itself when a request arrives.</summary>
internal sealed class ConsentWindow : Window
{
    private static readonly Vector4 Gold = new(0.89f, 0.75f, 0.48f, 1);
    private static readonly Vector4 Muted = new(0.62f, 0.64f, 0.70f, 1);

    public ConsentWindow() : base("XIV MCP — approval needed###XivMcpConsent", ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoCollapse)
    {
        RespectCloseHotkey = false;
        Consent.Asked += () => IsOpen = true;
    }

    public override void OnClose() => Consent.DeclineAll();

    public override void Draw()
    {
        var open = Consent.Open;
        if (open.Count == 0)
        {
            IsOpen = false;
            return;
        }

        foreach (var request in open)
        {
            using var id = ImRaii.PushId(request.Id.ToString());
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(Gold, FontAwesomeIcon.QuestionCircle.ToIconString());
            ImGui.SameLine();
            ImGui.TextColored(Gold, request.Title);
            ImGui.PushTextWrapPos(460 * ImGuiHelpers.GlobalScale);
            foreach (var line in request.Details) ImGui.BulletText(line);
            ImGui.PopTextWrapPos();
            var left = request.Deadline - DateTime.UtcNow;
            ImGui.TextColored(Muted, $"Requested by your MCP assistant. Declines automatically in {Math.Max(0, left.TotalSeconds):0} s.");
            ImGui.Spacing();
            if (ImGui.Button("Approve", new Vector2(120 * ImGuiHelpers.GlobalScale, 0))) Consent.Answer(request, true);
            ImGui.SameLine();
            if (ImGui.Button("Decline", new Vector2(120 * ImGuiHelpers.GlobalScale, 0))) Consent.Answer(request, false);
            ImGui.Separator();
        }
    }
}
