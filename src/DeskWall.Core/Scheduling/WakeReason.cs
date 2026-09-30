namespace DeskWall.Core.Scheduling;

/// <summary>Appended only: the value crosses <c>WM_APP_WAKE</c> as an int.</summary>
public enum WakeKind { Timer, DisplayChange, SessionUnlock, LayoutChanged, Manual, SourceCompleted, Shutdown, ExplorerRestarted }

/// <summary>Why the daemon woke. Detail is free text for the log (e.g. the changed file).</summary>
public readonly record struct WakeReason(WakeKind Kind, string? Detail = null)
{
    public override string ToString() => Detail is null ? Kind.ToString() : $"{Kind} ({Detail})";
}
