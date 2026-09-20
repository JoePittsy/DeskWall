namespace DeskWall.Core.Scheduling;

/// <summary>What a batch of wake reasons means for the run loop. Pure: no I/O, no state, no clock.
/// The daemon's message pump hands every reason it drained to <see cref="From"/> and does what the
/// decision says, so the policy is testable without a window, a timer or a display.</summary>
public static class TickPlan
{
    /// <param name="Tick">Run a tick at all.</param>
    /// <param name="Force">Ignore the content-key skip gate and redraw everything.</param>
    /// <param name="DelayForExplorer">Sleep before ticking so Explorer can finish re-laying the desktop (spec 3.1).</param>
    /// <param name="Reactivate">Rebuild the active layout/sources/scheduler set before ticking.</param>
    /// <param name="Shutdown">Leave the loop; nothing else in the decision applies.</param>
    public sealed record Decision(bool Tick, bool Force, bool DelayForExplorer, bool Reactivate, bool Shutdown);

    private static readonly Decision Stop = new(false, false, false, false, true);
    private static readonly Decision Nothing = new(false, false, false, false, false);

    public static Decision From(IReadOnlyList<WakeReason> reasons)
    {
        if (reasons.Any(r => r.Kind == WakeKind.Shutdown)) return Stop;
        // WaitAndPump returns an empty list whenever an unrelated window message woke the pump.
        // That is not a timer fire and must not redraw anything.
        if (reasons.Count == 0) return Nothing;
        var display = reasons.Any(r => r.Kind == WakeKind.DisplayChange);
        var layout = reasons.Any(r => r.Kind == WakeKind.LayoutChanged);
        var manual = reasons.Any(r => r.Kind == WakeKind.Manual);
        // SessionUnlock is forced (finding 9): the only reason to wake on an unlock or a resume is
        // that something else may have replaced the wallpaper while the session was away, and the
        // skip gate would otherwise return Skipped without ever reaching WallpaperSetter.Set. It is
        // rare and costs one redraw. It does not reactivate: the display has not changed.
        var unlock = reasons.Any(r => r.Kind == WakeKind.SessionUnlock);
        return new Decision(Tick: true, Force: display || layout || manual || unlock, DelayForExplorer: display,
            Reactivate: display || layout, Shutdown: false);
    }
}
