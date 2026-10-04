using System.Collections.Generic;
using System.Linq;

namespace XivMcp.Permissions;

public enum RiskLevel { Low, Medium, High, Critical }

/// <summary>Per plugin and capability: run without asking, ask the player each time (or once per session), or refuse.</summary>
public enum PolicyMode { Allow, Ask, Deny }

/// <summary>Something a tool may do in the game. Third-party tools declare theirs when they register; the player sets a policy for each.</summary>
public sealed record Capability(string Id, RiskLevel Risk, string Title, string Description);

/// <summary>The fixed catalog of capabilities. Ids are part of the public plugin API: never rename one, only add.</summary>
public static class Capabilities
{
    public const string ReadGame = "read_game";
    public const string GameUi = "game_ui";
    public const string MoveCharacter = "move_character";
    public const string Combat = "combat";
    public const string MoveItems = "move_items";
    public const string SpendGil = "spend_gil";
    public const string SpendCurrency = "spend_currency";
    public const string TradeItems = "trade_items";
    public const string DiscardItems = "discard_items";
    public const string ChatSend = "chat_send";
    public const string Login = "login";
    public const string EditSettings = "edit_settings";
    public const string Network = "network";

    public static IReadOnlyList<Capability> All { get; } =
    [
        new(ReadGame, RiskLevel.Low, "Read game state", "Reads character, inventory, zone and other game data. Changes nothing."),
        new(GameUi, RiskLevel.Medium, "Use game windows", "Opens, closes and clicks game windows and menus, and interacts with NPCs and objects."),
        new(MoveCharacter, RiskLevel.Medium, "Move the character", "Walks, mounts, teleports (costs gil), changes zones, instances or worlds."),
        new(Combat, RiskLevel.High, "Fight", "Enters duties or fights; the character can die."),
        new(MoveItems, RiskLevel.Medium, "Move items", "Moves items between bags, armoury, saddlebag, retainers and chests."),
        new(SpendGil, RiskLevel.High, "Spend gil", "Pays gil: vendors, repairs, teleports, market board purchases."),
        new(SpendCurrency, RiskLevel.High, "Spend other currencies", "Pays tomestones, scrips, seals, MGP or items used as currency."),
        new(TradeItems, RiskLevel.High, "Sell or trade items", "Lists items on the market board, sells to vendors or trades with players."),
        new(DiscardItems, RiskLevel.Critical, "Destroy items", "Discards, desynthesises or otherwise destroys items. Can't be undone."),
        new(ChatSend, RiskLevel.High, "Send chat", "Sends chat messages or commands other players or the server can see."),
        new(Login, RiskLevel.High, "Log out or switch characters", "Logs out, switches character or service account, or closes the game."),
        new(EditSettings, RiskLevel.High, "Change settings", "Changes game, plugin or file settings."),
        new(Network, RiskLevel.Medium, "Go online", "Sends requests to web services outside the game."),
    ];

    private static readonly Dictionary<string, Capability> ById = All.ToDictionary(c => c.Id);

    public static Capability? Find(string id) => ById.GetValueOrDefault(id);

    /// <summary>What a capability is set to before the player chooses: only reading runs without asking.</summary>
    public static PolicyMode DefaultMode(RiskLevel risk) => risk == RiskLevel.Low ? PolicyMode.Allow : PolicyMode.Ask;

    /// <summary>Critical capabilities can be asked or denied, never always allowed.</summary>
    public static bool IsModeAllowed(RiskLevel risk, PolicyMode mode) => !(risk == RiskLevel.Critical && mode == PolicyMode.Allow);
}
