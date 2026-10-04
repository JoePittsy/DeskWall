using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources.Audio;

/// <summary>The default playback endpoint's master volume, and the first source that is pure push:
/// CoreAudio tells it when the volume or the mute flag moved, so there is nothing to poll and
/// nothing to wait for. Publishes: volume, volumePct, muted, device.
/// <para>A *default device* change - a headset plugged in, the last speaker unplugged - arrives
/// as no volume notification at all, because that notification comes from the endpoint that is
/// no longer default. <see cref="IAudioDeviceNotifier"/> reports it instead, and it is handled
/// the same way as a volume change: signal, due now, and the refresh re-registers.</para>
/// <para>The schedule it keeps is the whole minute, the same boundary <see cref="TimeSource"/> and
/// <c>HardwareSource</c> use, so it shares the clock's existing wake and costs nothing of its own.
/// It takes the very first reading, and is the backstop for a device notification that never
/// came (a notifier that failed to register).</para>
/// <para>Signalling: raises <c>Changed</c> when the published form actually differs. The host turns
/// that into <c>EventBus.Signal(Name)</c>, which coalesces. Shape matches the shared
/// <c>ISignalSource</c> interface.</para></summary>
public sealed class AudioSource : ISource, ISignalSource, IWarningSource, IDisposable
{
    private readonly IAudioReader _reader;
    private readonly IAudioDeviceNotifier? _notifier;
    private readonly object _lock = new();
    private volatile bool _disposed;
    private int _readerFaults;

    /// <summary>True while the notifier is known not to be listening; tick thread only. Makes a
    /// failed registration one fault and one warning per outage, not one per refresh.</summary>
    private bool _notifierDown;
    private string? _warning;

    /// <summary>1 while a notification has been signalled but not yet published. Read from the
    /// scheduler thread and written from the audio thread, so it is an int under Volatile rather
    /// than a bool behind _lock: NextDue is asked on every wake and must never block on a refresh.</summary>
    private int _pending;

    /// <inheritdoc />
    public bool HasPending => Volatile.Read(ref _pending) != 0;

    /// <summary>What was last published or last signalled: the values as a *consumer* sees them,
    /// so a notification that does not move any of them is not a repaint. Null before the first
    /// refresh and whenever there is no playback device.</summary>
    private (string Device, double Volume, bool Muted)? _lastForm;

    /// <param name="notifier">Device-change notifications. Null leaves a device change to the
    /// whole-minute refresh, which is how the source behaved before it had one.</param>
    public AudioSource(string name, IAudioReader reader, IAudioDeviceNotifier? notifier = null)
    {
        Name = name;
        _reader = reader;
        _notifier = notifier;
        _reader.Changed += OnReaderChanged;
        if (_notifier is not null)
        {
            _notifier.DefaultDeviceChanged += OnDefaultDeviceChanged;
            _notifier.DeviceStateChanged += OnDeviceStateChanged;
        }
    }

    public static AudioSource FromDef(SourceDef def) => new(def.Name, new CoreAudioReader(), new CoreAudioDeviceNotifier());

    public string Name { get; }

    /// <summary>The shared <see cref="ISignalSource.Changed"/>. Raised from the audio
    /// service's thread, so a subscriber must be cheap and must not block.</summary>
    public event Action<ISource>? Changed;

    /// <summary>Notifications and refreshes lost to a reader or a subscriber that threw. The reader
    /// is written not to throw and the subscriber is the bus, which does not either; this counts
    /// the times one did anyway, because Core sources have no logger to report it to. Also counts
    /// each outage of the device notifier (once, not once per refresh); that one also reaches
    /// <c>deskwall.log</c> through <see cref="TakeWarning"/>.</summary>
    public int ReaderFaults => Volatile.Read(ref _readerFaults);

    /// <inheritdoc />
    public string? TakeWarning() => Interlocked.Exchange(ref _warning, null);

    /// <summary>Due now while a notification is waiting to be published, otherwise on the next
    /// whole minute, like <see cref="TimeSource"/>.
    /// <para>Both halves are load-bearing. Signalling the bus only buys a *wake*; the tick that
    /// follows refreshes the sources the scheduler says are due (<c>Scheduler.IsDue</c>, which asks
    /// this method), so without the pending flag the volume moves, the daemon wakes, every source
    /// reports what it already had, the content key is unchanged and nothing is painted until the
    /// minute turns. Measured on JOES-PC before this existed: no repaint at all inside 5 seconds.
    /// The flag is set only when <c>Changed</c> was raised, so the debounce governs both - a
    /// notification not worth a wake is not worth a refresh either, or the source is permanently
    /// due and pins the daemon's wake at <c>Scheduler.MinDelay</c>.</para>
    /// <para>Off the notification path the schedule is a backstop for a device notification that
    /// never came, and it rides the clock's wake rather than asking for one of its own.</para></summary>
    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
    {
        if (lastRefresh is null || Volatile.Read(ref _pending) != 0) return now;
        var l = lastRefresh.Value;
        return new DateTimeOffset(l.Year, l.Month, l.Day, l.Hour, l.Minute, 0, l.Offset).AddMinutes(1);
    }

    public TimeSpan Interval(DateTimeOffset now) => TimeSpan.FromMinutes(1);

    public ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        // Cleared before anything is read, not after: a notification that lands while this
        // refresh is running sets it again and earns the next tick, rather than being lost
        // between the read and the clear.
        Volatile.Write(ref _pending, 0);

        // COM first, and outside _lock. Re-registering unregisters the volume callback, and
        // UnregisterControlChangeNotify can wait for a notification already in flight - whose
        // handler (OnReaderChanged) takes _lock. Holding _lock here would be that deadlock, and a
        // device change is exactly when the old endpoint may still be notifying.
        var faulted = false;
        try
        {
            // Listening starts before the first reading, so a change between the two is not lost.
            // A registration that failed is retried here every refresh, so it heals on its own.
            if (_notifier is not null)
            {
                if (_notifier.TryStart(out var why)) _notifierDown = false;
                else if (!_notifierDown)
                {
                    _notifierDown = true;
                    Interlocked.Increment(ref _readerFaults);
                    Volatile.Write(ref _warning, $"device-change notifications unavailable ({why}); a new default playback device shows on the next whole minute");
                }
            }
            // The volume callback only ever fires for the endpoint it was registered on, so a
            // headset becoming default is silent on that path. This is where it is acted on.
            var current = _reader.DefaultDeviceId;
            if (!string.Equals(current, _reader.RegisteredDeviceId, StringComparison.Ordinal))
                _reader.Register();
        }
        catch (Exception)
        {
            faulted = true;
        }

        lock (_lock)
        {
            AudioReading? reading = null;
            if (!faulted)
            {
                try { reading = _reader.Current; }
                catch (Exception) { faulted = true; }
            }
            if (faulted)
            {
                // A COM hiccup on the endpoint must cost this refresh and nothing more. Publishing
                // nothing lets every bound property fall back to its own default, the same as a
                // machine with no playback device; a failed refresh would instead put the source
                // into the scheduler's back-off for something that is fixed by the next minute.
                Interlocked.Increment(ref _readerFaults);
                reading = null;
            }

            if (reading is not { } r)
            {
                _lastForm = null;
                return new(new RecordValue(new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)));
            }

            var form = Form(r);
            _lastForm = form;
            var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase)
            {
                // Quantised to 3 decimals so a content key does not change for a difference below
                // a pixel, the same rule as ResolvedBar and the hardware source.
                ["volume"] = new NumberValue(form.Volume),
                // AwayFromZero, not Math.Round's banker's default: 12.5 percent reading as "12"
                // while 37.5 percent reads as "38" is a readout that rounds differently at
                // neighbouring steps of the same slider.
                ["volumePct"] = new NumberValue(Math.Round(r.Volume * 100, MidpointRounding.AwayFromZero)),
                ["muted"] = new BoolValue(r.Muted),
                ["device"] = new TextValue(r.DeviceName),
            };
            return new(new RecordValue(d));
        }
    }

    /// <summary>The push path. Runs on an audio service thread, called from the reader's own COM
    /// callback frame, so it touches only <see cref="IAudioReader.Current"/> (no COM) and lets
    /// nothing escape: an exception crossing back into native code takes the whole process down,
    /// the same reason HardwareSource.SampleOnce swallows its reader's.</summary>
    private void OnReaderChanged()
    {
        try
        {
            lock (_lock)
            {
                if (_disposed) return;
                var form = _reader.Current is { } r ? Form(r) : ((string, double, bool)?)null;
                // CoreAudio fires per slider step and several steps land inside one rounded
                // percent. A repaint re-encodes and writes about two megabytes, so a notification
                // that moves nothing a consumer can see is dropped here rather than at the bus.
                if (form == _lastForm) return;
                _lastForm = form;
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _readerFaults);
            return;
        }

        // Outside the lock: the handler is the host's bus.Signal, and holding a source's lock
        // across it would invite a deadlock with the tick that is refreshing this source.
        Wake();
    }

    /// <summary>The default endpoint moved. A change only when it moved away from the endpoint
    /// the volume callback is on: CoreAudio repeats this per role, and the refresh acting on the
    /// first one may already have registered the new endpoint by the time a repeat arrives.
    /// Runs on an audio service thread; takes no lock (see <see cref="RefreshAsync"/>) and reads
    /// only <see cref="IAudioReader.RegisteredDeviceId"/>, which touches no COM.</summary>
    private void OnDefaultDeviceChanged(string? id)
    {
        try
        {
            if (_disposed) return;
            if (string.Equals(id, _reader.RegisteredDeviceId, StringComparison.Ordinal)) return;
            Wake();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _readerFaults);
        }
    }

    /// <summary>An endpoint was enabled, disabled, plugged or unplugged. A change when it is the
    /// endpoint being read (it may have gone away), or when nothing is being read (it may be the
    /// first device to arrive); a microphone plugged into a machine that has speakers is not.</summary>
    private void OnDeviceStateChanged(string id)
    {
        try
        {
            if (_disposed) return;
            var registered = _reader.RegisteredDeviceId;
            if (registered is not null && !string.Equals(id, registered, StringComparison.Ordinal)) return;
            Wake();
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _readerFaults);
        }
    }

    /// <summary>Due and awake are set together, on purpose: see <see cref="NextDue"/>. Nothing
    /// escapes, because every caller is on an audio service thread.</summary>
    private void Wake()
    {
        Volatile.Write(ref _pending, 1);
        try
        {
            Changed?.Invoke(this);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _readerFaults);
        }
    }

    private static (string Device, double Volume, bool Muted) Form(AudioReading r)
        => (r.DeviceId, Math.Round(Math.Clamp(r.Volume, 0, 1), 3), r.Muted);

    /// <summary>Unsubscribes and lets go of the notifier and the reader, which unregister their
    /// COM callbacks. Idempotent: nothing guarantees the host disposes a source exactly once.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _reader.Changed -= OnReaderChanged;
        if (_notifier is not null)
        {
            _notifier.DefaultDeviceChanged -= OnDefaultDeviceChanged;
            _notifier.DeviceStateChanged -= OnDeviceStateChanged;
            try { _notifier.Dispose(); }
            catch (Exception) { }
        }
        try { _reader.Dispose(); }
        catch (Exception) { }
    }
}
