using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DeskWall.Core.Sources.Notifications;

/// <summary>The real <see cref="INotificationReader"/>: <c>UserNotificationListener</c> for the
/// reading, and for being told.
/// <para>Being told is the part Windows does not give an unpackaged app. Access is granted
/// (<c>GetAccessStatus</c> answers Allowed when Settings &gt; Privacy &gt; Notifications allows
/// desktop apps) and <c>GetNotificationsAsync</c> works, but subscribing to
/// <c>NotificationChanged</c> throws 0x80070490 (element not found) without package identity -
/// measured on JOES-XPS-17. The subscription is still tried first, so a packaged build or a later
/// Windows gets the real event; otherwise the reader watches the notification platform's own store,
/// <c>%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db*</c>, which is silent at idle
/// and written the moment a toast lands or is dismissed. That store also takes a burst of writes
/// for every read (about ten per toast returned), so reads pass through a
/// <see cref="SharedReadGate"/> and the watcher drops what the gate calls an echo.</para></summary>
public sealed class WinRtNotificationReader : INotificationReader
{
    private readonly object _lock = new();
    private readonly Func<SharedReadGate> _makeGate;
    private SharedReadGate? _gate;
    private readonly string _storeDir;
    private UserNotificationListener? _listener;
    private FileSystemWatcher? _watcher;
    private bool _subscribed, _started, _disposed;
    private int _canPush, _echoes;

    public WinRtNotificationReader(SharedReadGate? gate = null, string? storeDir = null)
    {
        // Built on first use, not here: the designer constructs a source on every edit to the list.
        _makeGate = gate is null ? static () => new SharedReadGate() : () => gate;
        _storeDir = storeDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");
    }

    public event Action? Changed;

    public bool CanPush => Volatile.Read(ref _canPush) != 0;

    public bool EchoesReads => Volatile.Read(ref _echoes) != 0;

    private int _echoCount;

    /// <summary>Events the watcher dropped as a read's echo. Diagnostic only.</summary>
    public int Echoes => Volatile.Read(ref _echoCount);

    public void Start()
    {
        lock (_lock)
        {
            if (_started || _disposed) return;
            _started = true;
            try
            {
                var listener = _listener ??= UserNotificationListener.Current;
                listener.NotificationChanged += OnNotificationChanged;
                _subscribed = true;
                Volatile.Write(ref _canPush, 1);
                return;
            }
            catch (Exception)
            {
                // 0x80070490 without package identity. The store watcher below is the way in.
            }
            try
            {
                if (!Directory.Exists(_storeDir)) return;
                var w = new FileSystemWatcher(_storeDir, "wpndatabase.db*")
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    InternalBufferSize = 16384,
                };
                w.Changed += OnStoreEvent;
                w.Created += OnStoreEvent;
                w.Renamed += OnStoreEvent;
                w.Error += (_, _) => Raise();
                w.EnableRaisingEvents = true;
                _watcher = w;
                Volatile.Write(ref _echoes, 1);
                Volatile.Write(ref _canPush, 1);
            }
            catch (Exception)
            {
                _watcher = null;
            }
        }
    }

    private void OnNotificationChanged(UserNotificationListener sender, UserNotificationChangedEventArgs args) => Raise();

    private void OnStoreEvent(object sender, FileSystemEventArgs e)
    {
        try
        {
            if (_disposed) return;
            if (Gate().Suppressed) { Interlocked.Increment(ref _echoCount); return; }
        }
        catch (Exception) { return; }
        Raise();
    }

    /// <summary>Nothing escapes: this runs on a watcher or WinRT thread, and an exception out of
    /// either takes the process down.</summary>
    private void Raise()
    {
        if (_disposed) return;
        try { Changed?.Invoke(); } catch (Exception) { }
    }

    public async Task<NotificationReading> ReadAsync(CancellationToken ct)
    {
        UserNotificationListener listener;
        try
        {
            lock (_lock) listener = _listener ??= UserNotificationListener.Current;
        }
        catch (Exception)
        {
            return NotificationReading.Unavailable;
        }

        UserNotificationListenerAccessStatus access;
        try
        {
            access = listener.GetAccessStatus();
            // Asked once, and only while undecided. From an unpackaged process it prompts nothing:
            // it answers from the privacy setting.
            if (access == UserNotificationListenerAccessStatus.Unspecified) access = await listener.RequestAccessAsync();
        }
        catch (Exception)
        {
            return NotificationReading.Unavailable;
        }
        if (access != UserNotificationListenerAccessStatus.Allowed) return NotificationReading.Denied;
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<UserNotification> raw;
        var gate = Gate();
        gate.Enter();
        try { raw = await listener.GetNotificationsAsync(NotificationKinds.Toast); }
        finally { gate.Exit(); }

        var items = new List<NotificationItem>(raw.Count);
        foreach (var n in raw)
        {
            if (Map(n) is { } item) items.Add(item);
        }
        return new NotificationReading(NotificationAccess.Allowed, items);
    }

    /// <summary>One toast, or null for one whose app or payload cannot be read. Each property is a
    /// cross-process call that can fail on its own (an app uninstalled while its toast is still in
    /// the centre), so one bad toast costs itself, not the whole reading.</summary>
    private static NotificationItem? Map(UserNotification n)
    {
        try
        {
            string app = "", appId = "";
            try
            {
                var info = n.AppInfo;
                appId = info?.AppUserModelId ?? "";
                app = info?.DisplayInfo?.DisplayName ?? "";
            }
            catch (Exception) { }
            if (app.Length == 0) app = appId;

            var texts = new List<string>();
            try
            {
                var binding = n.Notification?.Visual?.GetBinding(KnownNotificationBindings.ToastGeneric)
                    ?? n.Notification?.Visual?.Bindings.FirstOrDefault();
                if (binding is not null)
                    foreach (var t in binding.GetTextElements())
                        if (!string.IsNullOrWhiteSpace(t.Text)) texts.Add(t.Text.Trim());
            }
            catch (Exception) { }

            return new NotificationItem(app, appId,
                texts.Count > 0 ? texts[0] : "",
                texts.Count > 1 ? string.Join(" ", texts.Skip(1)) : "",
                n.CreationTime);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        FileSystemWatcher? w;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            w = _watcher;
            _watcher = null;
            if (_subscribed && _listener is not null)
            {
                try { _listener.NotificationChanged -= OnNotificationChanged; } catch (Exception) { }
            }
        }
        if (w is not null)
        {
            try { w.EnableRaisingEvents = false; w.Dispose(); } catch (Exception) { }
        }
        SharedReadGate? built;
        lock (_lock) built = _gate;
        built?.Dispose();
    }

    private SharedReadGate Gate()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _gate ??= _makeGate();
        }
    }
}
