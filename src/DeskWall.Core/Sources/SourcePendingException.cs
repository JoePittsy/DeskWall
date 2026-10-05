namespace DeskWall.Core.Sources;

/// <summary>Thrown by <see cref="AsyncSource.RefreshAsync"/> after its first refresh when the fetch it
/// started has not finished: not a failure and not an answer. A caller records nothing (the previous
/// values, LastRefresh and failure count all stand) and refreshes again on the source's Changed (#20).</summary>
public sealed class SourcePendingException(string message) : Exception(message);
