namespace DeskWall.Core.Scheduling;

public enum WakeKind { Timer, DisplayChange, SessionUnlock, LayoutChanged, Manual, SourceCompleted, Shutdown }

/// <summary>Why the daemon woke. Detail is free text for the log (e.g. the changed file).</summary>
public readonly record struct WakeReason(WakeKind Kind, string? Detail = null)
{
    public override string ToString() => Detail is null ? Kind.ToString() : $"{Kind} ({Detail})";
}
