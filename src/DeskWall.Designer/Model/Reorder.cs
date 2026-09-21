namespace DeskWall.Designer.Model;

/// <summary>Where a dragged widget lands. Split out of the preview because the answer is arithmetic
/// on a list and a pointer position, and getting it wrong is the difference between "the widget
/// went where I dropped it" and "the column reshuffled itself".</summary>
public static class Reorder
{
    /// <summary>One widget in the stack as the drag sees it: its id and the top and bottom of its
    /// bounds on the canvas.</summary>
    public readonly record struct Slot(string Id, int Top, int Bottom)
    {
        public int Centre => Top + (Bottom - Top) / 2;
    }

    /// <summary>The index, into <paramref name="stack"/> with <paramref name="dragged"/> taken out,
    /// where a widget dropped with its centre at <paramref name="pointerY"/> belongs: after every
    /// remaining widget whose own centre is above the pointer.
    /// <para>Centres, not edges: a tall widget dragged over a short one would otherwise have to
    /// clear the short one entirely before the swap, which reads as the drag being ignored.</para></summary>
    public static int IndexFor(IReadOnlyList<Slot> stack, string dragged, int pointerY)
    {
        ArgumentNullException.ThrowIfNull(stack);
        var index = 0;
        foreach (var slot in stack)
        {
            if (string.Equals(slot.Id, dragged, StringComparison.Ordinal)) continue;
            if (slot.Centre < pointerY) index++;
        }
        return index;
    }

    /// <summary>The order with <paramref name="id"/> taken out and put back at
    /// <paramref name="index"/>. An id that is not in the list, or an index that cannot be reached,
    /// leaves the order alone rather than throwing at a mouse-up.</summary>
    public static IReadOnlyList<string> Move(IReadOnlyList<string> order, string id, int index)
    {
        ArgumentNullException.ThrowIfNull(order);
        var list = order.ToList();
        if (!list.Remove(id)) return order;
        list.Insert(Math.Clamp(index, 0, list.Count), id);
        return list;
    }

    /// <summary>Whether the move would change anything, so a click that happened to wobble does not
    /// become an undo entry.</summary>
    public static bool Changes(IReadOnlyList<string> order, string id, int index)
        => !order.SequenceEqual(Move(order, id, index), StringComparer.Ordinal);
}
