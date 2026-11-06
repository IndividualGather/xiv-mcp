namespace XivMcp.Quests;

/// <summary>
/// What a journal quest's step number says. The game moves a quest to step 255 when only the final talk to the quest NPC is left;
/// for quests that ask for items (crafter and gatherer class quests, deliveries) that talk is the hand-in, and the step says
/// nothing about whether the items are in the bags.
/// </summary>
public static class QuestStep
{
    public const byte Final = 255;

    public static bool IsFinal(byte sequence) => sequence == Final;

    /// <summary>Shown with the quest list so an assistant doesn't take the final step for "done" or "items in hand".</summary>
    public const string FinalStepMeaning =
        "onFinalStep: the quest's last step (talk to the quest NPC to finish). It does not mean the requirements are met: for quests " +
        "that ask for items (crafter and gatherer class quests, deliveries) the items are handed in at that talk and are not checked " +
        "here; check the bags (search_inventory) first.";
}
