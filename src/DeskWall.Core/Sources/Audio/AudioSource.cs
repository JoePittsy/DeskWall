using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources.Audio;

/// <summary>The default playback endpoint's master volume, and the first source that is pure push:
/// CoreAudio tells it when the volume or the mute flag moved, so there is nothing to poll and
/// nothing to wait for. Publishes: volume, volumePct, muted, device.
/// <para>The schedule it keeps is the whole minute, the same boundary <see cref="TimeSource"/> and
/// <c>HardwareSource</c> use, so it shares the clock's existing wake and costs nothing of its own.
/// A refresh has only two jobs the callback cannot do: take the very first reading, and notice
/// that the *default device* changed - a headset being plugged in - which arrives as no
/// notification at all because the notification came from the endpoint that is no longer default.
/// </para>
/// <para>Signalling: raises <c>Changed</c> when the published form actually differs. The host turns
/// that into <c>EventBus.Signal(Name)</c>, which coalesces. Shape matches the shared
/// <c>ISignalSource</c> interface.</para></summary>
public sealed class AudioSource : ISource, IDisposable
{
    private readonly IAudioReader _reader;
    private readonly object _lock = new();
    private bool _disposed;
    private int _readerFaults;

    /// <summary>1 while a notification has been signalled but not yet published. Read from the
    /// scheduler thread and written from the audio thread, so it is an int under Volatile rather
    /// than a bool behind _lock: NextDue is asked on every wake and must never block on a refresh.</summary>
    private int _pending;

    /// <summary>What was last published or last signalled: the values as a *consumer* sees them,
    /// so a notification that does not move any of them is not a repaint. Null before the first
    /// refresh and whenever there is no playback device.</summary>
    private (string Device, double Volume, bool Muted)? _lastForm;

    public AudioSource(string name, IAudioReader reader)
    {
        Name = name;
        _reader = reader;
        _reader.Changed += OnReaderChanged;
    }

    public static AudioSource FromDef(SourceDef def) => new(def.Name, new CoreAudioReader());

    public string Name { get; }

    /// <summary>Same shape as the shared <c>ISignalSource.Changed</c>. Raised from the audio
    /// service's thread, so a subscriber must be cheap and must not block.</summary>
    public event Action<ISource>? Changed;

    /// <summary>Notifications and refreshes lost to a reader or a subscriber that threw. The reader
    /// is written not to throw and the subscriber is the bus, which does not either; this counts
    /// the times one did anyway, because Core sources have no logger to report it to.</summary>
    public int ReaderFaults => Volatile.Read(ref _readerFaults);

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
    /// <para>Off the notification path the schedule exists only to catch a default-device change,
    /// and it does that riding the clock's wake rather than asking for one of its own.</para></summary>
    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
    {
        if (lastRefresh is null || Volatile.Read(ref _pending) != 0) return now;
        var l = lastRefresh.Value;
        return new DateTimeOffset(l.Year, l.Month, l.Day, l.Hour, l.Minute, 0, l.Offset).AddMinutes(1);
    }

    public TimeSpan Interval(DateTimeOffset now) => TimeSpan.FromMinutes(1);

    public ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            // Cleared before the reading is taken, not after: a notification that lands while this
            // refresh is running sets it again and earns the next tick, rather than being lost
            // between the read and the clear.
            Volatile.Write(ref _pending, 0);
            AudioReading? reading;
            try
            {
                // The callback only ever fires for the endpoint it was registered on, so a headset
                // becoming default is silent by construction. This is where it is caught.
                var current = _reader.DefaultDeviceId;
                if (!string.Equals(current, _reader.RegisteredDeviceId, StringComparison.Ordinal))
                    _reader.Register();
                reading = _reader.Current;
            }
            catch (Exception)
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
        Action<ISource>? handler = null;
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
                // Due and awake are set together, on purpose: see NextDue.
                Volatile.Write(ref _pending, 1);
                handler = Changed;
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _readerFaults);
            return;
        }

        // Outside the lock: the handler is the host's bus.Signal, and holding a source's lock
        // across it would invite a deadlock with the tick that is refreshing this source.
        try
        {
            handler?.Invoke(this);
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _readerFaults);
        }
    }

    private static (string Device, double Volume, bool Muted) Form(AudioReading r)
        => (r.DeviceId, Math.Round(Math.Clamp(r.Volume, 0, 1), 3), r.Muted);

    /// <summary>Unsubscribes and lets go of the reader, which unregisters the COM callback.
    /// Idempotent: nothing guarantees the host disposes a source exactly once.</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _reader.Changed -= OnReaderChanged;
        try { _reader.Dispose(); }
        catch (Exception) { }
    }
}
