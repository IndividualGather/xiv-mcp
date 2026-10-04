using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using XivMcp.Permissions;
using XivMcp.Util;

namespace XivMcp.Windows;

/// <summary>The in-game approval popup for <see cref="Consent"/> requests. Opens itself when a request arrives.</summary>
internal sealed class ConsentWindow : Window
{
    private static readonly Vector4 Gold = new(0.89f, 0.75f, 0.48f, 1);
    private static readonly Vector4 Muted = new(0.62f, 0.64f, 0.70f, 1);
    private static readonly Vector4 Amber = new(0.91f, 0.70f, 0.29f, 1);
    private static readonly Vector4 Red = new(0.88f, 0.42f, 0.42f, 1);

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
            var color = request.Risk >= RiskLevel.High ? Red : Gold;
            using (Ui.IconFont())
                ImGui.TextColored(color, (request.Risk >= RiskLevel.High ? FontAwesomeIcon.ExclamationTriangle : FontAwesomeIcon.QuestionCircle).ToIconString());
            ImGui.SameLine();
            ImGui.TextColored(color, request.Title);
            ImGui.PushTextWrapPos(460 * Ui.Scale);
            foreach (var line in request.Details) ImGui.BulletText(line);
            if (request.Warning is not null) ImGui.TextColored(request.Risk >= RiskLevel.High ? Red : Amber, request.Warning);
            ImGui.PopTextWrapPos();
            var left = request.Deadline - DateTime.UtcNow;
            ImGui.TextColored(Muted, $"Requested by {request.Source}. Declines automatically in {Math.Max(0, left.TotalSeconds):0} s.");
            ImGui.Spacing();
            var w = new Vector2(150 * Ui.Scale, 0);
            if (ImGui.Button("Approve once", w)) Consent.Answer(request, ApprovalDecision.ApprovedOnce);
            if (request.OfferSession)
            {
                ImGui.SameLine();
                if (ImGui.Button("Approve for this session", w)) Consent.Answer(request, ApprovalDecision.ApprovedForSession);
            }
            if (request.OfferAlways)
            {
                ImGui.SameLine();
                if (ImGui.Button("Always allow", w)) Consent.Answer(request, ApprovalDecision.AlwaysAllow);
                if (ImGui.IsItemHovered()) ImGui.SetTooltip("Sets these capabilities to Allow for this plugin (change it in /xivmcp → Third-party plugins). Destroying items is never always allowed.");
            }
            ImGui.SameLine();
            if (ImGui.Button("Decline", w)) Consent.Answer(request, ApprovalDecision.Denied);
            ImGui.Separator();
        }
    }
}
