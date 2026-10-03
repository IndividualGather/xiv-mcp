using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;
using XivMcp.Mcp;
using XivMcp.Util;

namespace XivMcp.Tools;

/// <summary>Read-only look into game windows (addons): which are open, and the values and texts a window currently shows.</summary>
internal static class WindowInspectTools
{
    public static IEnumerable<McpTool> Create()
    {
        yield return new McpTool
        {
            Name = "inspect_window",
            Description = "Read-only: without 'name', lists every game window (addon) that is currently visible. With 'name', returns that window's " +
                          "values (AtkValues: index, type, value) and the texts it displays (text nodes) — useful to read windows no other tool " +
                          "covers, such as a retainer's sale history.",
            InputSchema = """
                {
                  "type": "object",
                  "properties": {
                    "name": { "type": "string", "description": "Addon name, e.g. \"RetainerSellList\"." },
                    "max_values": { "type": "integer", "description": "Cap on AtkValues returned (default 300)." }
                  }
                }
                """,
            Handler = (args, _) => Game.Run<object?>(() =>
            {
                var name = args.String("name");
                return name is null ? VisibleWindows() : Inspect(name, args.Int("max_values", 300, 1, 5000));
            }),
        };
    }

    public static unsafe List<string> VisibleWindows()
    {
        var result = new List<string>();
        var manager = RaptureAtkUnitManager.Instance();
        if (manager == null) return result;
        var list = &manager->AtkUnitManager.AllLoadedUnitsList;
        for (var i = 0; i < list->Count; i++)
        {
            var unit = list->Entries[i].Value;
            if (unit != null && unit->IsVisible) result.Add(unit->NameString);
        }
        return result.Distinct().OrderBy(n => n).ToList();
    }

    private static unsafe object Inspect(string name, int maxValues)
    {
        var addon = Svc.GameGui.GetAddonByName<AtkUnitBase>(name, 1);
        if (addon == null) throw new ToolException($"Window '{name}' is not loaded. Open windows: {string.Join(", ", VisibleWindows())}");
        var values = new List<object>();
        for (var i = 0; i < addon->AtkValuesCount && values.Count < maxValues; i++)
        {
            var v = addon->AtkValues[i];
            object? value = v.Type switch
            {
                AtkValueType.Int => v.Int,
                AtkValueType.UInt => v.UInt,
                AtkValueType.Bool => v.Byte != 0,
                AtkValueType.Float => v.Float,
                AtkValueType.String or AtkValueType.String8 or AtkValueType.ManagedString =>
                    v.String.Value == null ? null : MemoryHelper.ReadSeStringNullTerminated((nint)v.String.Value).TextValue,
                _ => null,
            };
            if (v.Type == AtkValueType.Undefined) continue;
            values.Add(new { index = i, type = v.Type.ToString(), value });
        }
        var texts = new List<object>();
        for (var i = 0; i < addon->UldManager.NodeListCount; i++)
            CollectTexts(addon->UldManager.NodeList[i], texts, 0, $"{addon->UldManager.NodeList[i]->NodeId}");
        return new { name, visible = addon->IsVisible, ready = addon->IsReady, valueCount = addon->AtkValuesCount, values, texts = texts.Take(400).ToList() };
    }

    private static unsafe void CollectTexts(AtkResNode* node, List<object> texts, int depth, string path)
    {
        if (node == null || depth > 6 || texts.Count > 400) return;
        if (node->Type == NodeType.Text)
        {
            var text = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text)) texts.Add(new { path, text = Dalamud.Game.Text.SeStringHandling.SeString.Parse(((AtkTextNode*)node)->NodeText).TextValue, visible = node->IsVisible() });
        }
        else if ((int)node->Type >= 1000)
        {
            var component = ((AtkComponentNode*)node)->Component;
            if (component == null) return;
            for (var i = 0; i < component->UldManager.NodeListCount; i++)
                CollectTexts(component->UldManager.NodeList[i], texts, depth + 1, $"{path}/{component->UldManager.NodeList[i]->NodeId}");
        }
    }
}
