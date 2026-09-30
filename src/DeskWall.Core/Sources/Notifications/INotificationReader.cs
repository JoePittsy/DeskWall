namespace DeskWall.Core.Sources.Notifications;

/// <summary>Whether this process may read the notification centre at all.</summary>
public enum NotificationAccess
{
    Allowed,
    /// <summary>Settings &gt; Privacy &gt; Notifications is off for desktop apps, or the user said no.</summary>
    Denied,
    /// <summary>The listener API is missing or threw: a Windows build without it, or a broken
    /// notification platform service.</summary>
    Unavailable,
}

/// <param name="App">The app's display name ("Outlook", "Windows Security").</param>
/// <param name="AppId">The app user model id, for a filter that must not depend on a display name.</param>
/// <param name="Title">The toast's first text line.</param>
/// <param name="Text">The rest of its text lines, joined with a space.</param>
public sealed record NotificationItem(string App, string AppId, string Title, string Text, DateTimeOffset Created);

public sealed record NotificationReading(NotificationAccess Access, IReadOnlyList<NotificationItem> Items)
{
    public static NotificationReading Denied { get; } = new(NotificationAccess.Denied, []);
    public static NotificationReading Unavailable { get; } = new(NotificationAccess.Unavailable, []);
}

/// <summary>The Windows side of <see cref="NotificationSource"/>, behind an interface so the
/// source's debounce, echo confirmation, filters and dedupe are tested without real toasts - the
/// same arrangement as <c>IAudioReader</c>.
/// <para>Implementations must not throw from <see cref="Start"/>, <see cref="Changed"/> handlers or
/// <see cref="IDisposable.Dispose"/>. <see cref="ReadAsync"/> may throw; the source counts it.</para></summary>
public interface INotificationReader : IDisposable
{
    /// <summary>Something in the notification store may have changed. Raised on whatever thread
    /// noticed (a watcher thread, a WinRT thread); handlers must be cheap and let nothing escape.</summary>
    event Action? Changed;

    /// <summary>True once <see cref="Start"/> found a way to be told about changes. False means
    /// the source has to read on a schedule.</summary>
    bool CanPush { get; }

    /// <summary>True when a read of our own can come back as <see cref="Changed"/>: the store
    /// watcher sees the platform's own bookkeeping writes, and a real toast landing inside the
    /// suppressed echo of a read is only caught by a second, confirming read.</summary>
    bool EchoesReads { get; }

    /// <summary>Begin listening. Idempotent; called after the first read, and only when that read
    /// was allowed.</summary>
    void Start();

    /// <summary>The notification centre's toasts right now, or a denied/unavailable reading.</summary>
    Task<NotificationReading> ReadAsync(CancellationToken ct);
}
