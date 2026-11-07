using System;

namespace XivMcp.Transfers;

/// <summary>
/// How many crystals a move between crystal containers takes. Each kind of crystal has one slot, which holds at most the stack size
/// (9,999); the game rejects a move that would go over it, so only what fits is moved.
/// </summary>
public static class CrystalMove
{
    public const int StackSize = 9_999;

    /// <summary>The amount to move, or a reason why nothing can move.</summary>
    public readonly record struct Plan(int Amount, string? Problem, string? Note)
    {
        /// <summary>Less than the whole source stack: the game needs the quantity entry of the item's context menu.</summary>
        public bool Partial(int have) => Amount > 0 && Amount < have;
    }

    /// <param name="requested">The quantity asked for, or null for the whole stack.</param>
    /// <param name="have">Crystals in the source slot.</param>
    /// <param name="inTarget">Crystals of the same kind already in the target slot.</param>
    public static Plan For(int? requested, int have, int inTarget, int stackSize = StackSize)
    {
        if (have <= 0) return new(0, "There are no crystals of this kind to move.", null);
        var wanted = Math.Min(requested ?? have, have);
        var room = Math.Max(0, stackSize - inTarget);
        if (room == 0) return new(0, $"The target already holds {inTarget:N0} of them, the most a slot holds.", null);
        if (wanted <= room)
            return new(wanted, null, requested > have ? $"only {have:N0} there" : null);
        return new(room, null, $"only {room:N0} fit (the target holds {inTarget:N0} of {stackSize:N0})");
    }
}
