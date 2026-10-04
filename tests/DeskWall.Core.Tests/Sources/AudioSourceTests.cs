using DeskWall.Core.Events;
using DeskWall.Core.Sources;
using DeskWall.Core.Sources.Audio;
using DeskWall.Core.Values;
using Xunit;

/// <summary>A reader that raises notifications on demand, so the arithmetic, the debounce, the
/// device change and the empty case are all tested with no sound card involved.</summary>
internal sealed class FakeAudioReader : IAudioReader
{
    public event Action? Changed;

    /// <summary>Per-device state the fake hands back once that device is registered.</summary>
    public Dictionary<string, AudioReading> Devices { get; } = new(StringComparer.Ordinal);
    public string? DefaultDeviceId { get; set; }
    public string? RegisteredDeviceId { get; private set; }
    public int Registrations { get; private set; }
    public int Disposals { get; private set; }
    public bool ThrowOnDefaultDeviceId { get; set; }

    public AudioReading? Current
        => RegisteredDeviceId is { } id && Devices.TryGetValue(id, out var r) ? r : null;

    /// <summary>Runs inside <see cref="Register"/>, where the real reader unregisters the previous
    /// callback and may wait for a notification already in flight.</summary>
    public Action? DuringRegister { get; set; }

    public void Register()
    {
        DuringRegister?.Invoke();
        RegisteredDeviceId = DefaultDeviceId;
        Registrations++;
    }

    /// <summary>Change the registered device's state and push it, the way the COM callback does.</summary>
    public void Push(double volume, bool muted)
    {
        var id = RegisteredDeviceId!;
        var was = Devices[id];
        Devices[id] = was with { Volume = volume, Muted = muted };
        Changed?.Invoke();
    }

    public void Raise() => Changed?.Invoke();

    public void Dispose() => Disposals++;

    string? IAudioReader.DefaultDeviceId
        => ThrowOnDefaultDeviceId ? throw new InvalidOperationException("reader fault") : DefaultDeviceId;
}

public class AudioSourceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 22, 9, 30, 12, 500, TimeSpan.Zero);

    private static FakeAudioReader OneSpeaker(double volume = 0.375, bool muted = false)
    {
        var r = new FakeAudioReader { DefaultDeviceId = "spk" };
        r.Devices["spk"] = new AudioReading("spk", "Speakers (Realtek)", volume, muted);
        return r;
    }

    private static RecordValue Refresh(AudioSource s) => s.RefreshAsync(CancellationToken.None).AsTask().Result;

    private static double Num(RecordValue r, string field) => Assert.IsType<NumberValue>(r.Fields[field]).Number;

    [Fact]
    public void Publishes_Volume_Pct_Muted_And_Device()
    {
        using var reader = OneSpeaker(0.375);
        using var src = new AudioSource("audio", reader);

        var r = Refresh(src);

        Assert.Equal(0.375, Num(r, "volume"));
        Assert.Equal(38, Num(r, "volumePct"));
        Assert.False(Assert.IsType<BoolValue>(r.Fields["muted"]).Flag);
        Assert.Equal("Speakers (Realtek)", Assert.IsType<TextValue>(r.Fields["device"]).Text);
    }

    [Fact]
    public void Volume_Is_Quantised_So_A_Sub_Pixel_Wobble_Does_Not_Change_The_Content_Key()
    {
        using var reader = OneSpeaker(0.3333333);
        using var src = new AudioSource("audio", reader);

        Assert.Equal(0.333, Num(Refresh(src), "volume"));
    }

    [Fact]
    public void Half_A_Percent_Rounds_Up_Not_To_Even()
    {
        // Math.Round's default is banker's rounding, which would make 12.5 percent read as "12"
        // and 37.5 percent read as "38". A volume readout that rounds one way at one step and the
        // other way at the next is a bug report waiting to happen.
        using var reader = OneSpeaker(0.125);
        using var src = new AudioSource("audio", reader);

        Assert.Equal(13, Num(Refresh(src), "volumePct"));
    }

    [Fact]
    public void Mute_Is_Published_As_A_Bool_So_The_Map_Format_Can_Drive_A_Colour()
    {
        using var reader = OneSpeaker(0.5, muted: true);
        using var src = new AudioSource("audio", reader);

        var muted = Assert.IsType<BoolValue>(Refresh(src).Fields["muted"]);
        Assert.True(muted.Flag);
        Assert.Equal("#D13438", muted.ToText("?true=#D13438,false=#EBFFFFFF"));
    }

    [Fact]
    public void No_Playback_Device_Publishes_An_Empty_Record_Not_Zeros()
    {
        var reader = new FakeAudioReader { DefaultDeviceId = null };
        using var src = new AudioSource("audio", reader);

        var r = Refresh(src);

        Assert.Empty(r.Fields);
        Assert.Equal(0, src.ReaderFaults);
    }

    [Fact]
    public void First_Refresh_Registers_The_Callback()
    {
        using var reader = OneSpeaker();
        using var src = new AudioSource("audio", reader);
        Assert.Equal(0, reader.Registrations);

        Refresh(src);

        Assert.Equal(1, reader.Registrations);
        Assert.Equal("spk", reader.RegisteredDeviceId);
    }

    [Fact]
    public void A_Steady_Default_Device_Is_Not_Re_Registered_Every_Minute()
    {
        using var reader = OneSpeaker();
        using var src = new AudioSource("audio", reader);

        Refresh(src);
        Refresh(src);
        Refresh(src);

        Assert.Equal(1, reader.Registrations);
    }

    [Fact]
    public void A_Headset_Becoming_Default_Re_Registers_And_Publishes_The_New_Endpoint()
    {
        using var reader = OneSpeaker();
        using var src = new AudioSource("audio", reader);
        Refresh(src);

        reader.Devices["hs"] = new AudioReading("hs", "Headset Earphone", 0.2, false);
        reader.DefaultDeviceId = "hs";
        var r = Refresh(src);

        Assert.Equal(2, reader.Registrations);
        Assert.Equal("hs", reader.RegisteredDeviceId);
        Assert.Equal("Headset Earphone", Assert.IsType<TextValue>(r.Fields["device"]).Text);
        Assert.Equal(20, Num(r, "volumePct"));
    }

    [Fact]
    public void The_Last_Playback_Device_Going_Away_Falls_Back_To_The_Empty_Record()
    {
        using var reader = OneSpeaker();
        using var src = new AudioSource("audio", reader);
        Refresh(src);

        reader.DefaultDeviceId = null;
        var r = Refresh(src);

        Assert.Empty(r.Fields);
        Assert.Null(reader.RegisteredDeviceId);
    }

    [Fact]
    public void A_Notification_Raises_Changed_With_This_Source()
    {
        using var reader = OneSpeaker(0.5);
        using var src = new AudioSource("audio", reader);
        Refresh(src);
        ISource? signalled = null;
        src.Changed += s => signalled = s;

        reader.Push(0.6, muted: false);

        Assert.Same(src, signalled);
    }

    [Fact]
    public void A_Notification_That_Changes_Nothing_Visible_Does_Not_Wake_The_Daemon()
    {
        // CoreAudio fires per slider step, and several steps land inside one rounded percent.
        // A repaint is two megabytes re-encoded and written, so a notification only counts when
        // the published form actually differs.
        using var reader = OneSpeaker(0.500);
        using var src = new AudioSource("audio", reader);
        Refresh(src);
        var signals = 0;
        src.Changed += _ => signals++;

        reader.Push(0.5001, muted: false);
        reader.Push(0.5004, muted: false);
        reader.Raise();

        Assert.Equal(0, signals);

        reader.Push(0.51, muted: false);
        Assert.Equal(1, signals);
    }

    [Fact]
    public void Muting_At_The_Same_Volume_Is_A_Change()
    {
        using var reader = OneSpeaker(0.5);
        using var src = new AudioSource("audio", reader);
        Refresh(src);
        var signals = 0;
        src.Changed += _ => signals++;

        reader.Push(0.5, muted: true);

        Assert.Equal(1, signals);
    }

    [Fact]
    public void A_Refresh_Reseeds_The_Debounce_So_A_Device_Change_Still_Signals_Its_Own_Level()
    {
        using var reader = OneSpeaker(0.5);
        using var src = new AudioSource("audio", reader);
        Refresh(src);
        var signals = 0;
        src.Changed += _ => signals++;

        // Headset arrives at the same 50 percent. The refresh that swapped the endpoint already
        // published that, so a notification repeating it is still nothing new...
        reader.Devices["hs"] = new AudioReading("hs", "Headset Earphone", 0.5, false);
        reader.DefaultDeviceId = "hs";
        Refresh(src);
        reader.Raise();
        Assert.Equal(0, signals);

        // ... and the next real change on the new endpoint still gets through.
        reader.Push(0.5, muted: true);
        Assert.Equal(1, signals);
    }

    [Fact]
    public void Nothing_Escapes_The_Callback_Path_And_The_Fault_Is_Counted()
    {
        // This handler runs on the audio service's thread, inside native code's call frame. An
        // exception crossing back into it takes the whole process down, exactly as it would out of
        // HardwareSource's timer callback.
        using var reader = OneSpeaker(0.5);
        using var src = new AudioSource("audio", reader);
        Refresh(src);
        src.Changed += _ => throw new InvalidOperationException("a subscriber misbehaved");

        reader.Push(0.9, muted: false);

        Assert.Equal(1, src.ReaderFaults);
    }

    [Fact]
    public void A_Reader_That_Throws_On_The_Tick_Path_Costs_An_Empty_Record_Not_A_Failed_Source()
    {
        using var reader = OneSpeaker();
        using var src = new AudioSource("audio", reader);
        reader.ThrowOnDefaultDeviceId = true;

        var r = Refresh(src);

        Assert.Empty(r.Fields);
        Assert.Equal(1, src.ReaderFaults);
    }

    [Fact]
    public void Due_On_The_Whole_Minute_So_It_Shares_The_Clock_Wake()
    {
        using var reader = OneSpeaker();
        using var src = new AudioSource("audio", reader);

        Assert.Equal(T0, src.NextDue(null, T0));
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 9, 31, 0, TimeSpan.Zero), src.NextDue(T0, T0));
        Assert.Equal(TimeSpan.FromMinutes(1), src.Interval(T0));
    }

    [Fact]
    public void A_Pending_Notification_Makes_The_Source_Due_Now()
    {
        // Signalling the bus only buys a wake; the tick that follows refreshes the sources the
        // scheduler says are due, and on the whole-minute schedule this one would not be. Without
        // this the volume moves, the daemon wakes, every source reports the same values it had,
        // the content key is unchanged and nothing is painted until the minute turns. Measured on
        // JOES-PC before the fix: a volume change produced no repaint at all inside 5 seconds.
        using var reader = OneSpeaker(0.5);
        using var src = new AudioSource("audio", reader);
        Refresh(src);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 9, 31, 0, TimeSpan.Zero), src.NextDue(T0, T0));

        reader.Push(0.6, muted: false);
        Assert.Equal(T0, src.NextDue(T0, T0));

        Refresh(src);
        Assert.Equal(new DateTimeOffset(2026, 9, 22, 9, 31, 0, TimeSpan.Zero), src.NextDue(T0, T0));
    }

    [Fact]
    public void A_Debounced_Notification_Does_Not_Make_It_Due()
    {
        // The two must agree: a notification that is not worth a wake is not worth a refresh
        // either, or the daemon's next scheduled wake finds a source permanently due and the
        // pair of them pin the tick at Scheduler.MinDelay.
        using var reader = OneSpeaker(0.5);
        using var src = new AudioSource("audio", reader);
        Refresh(src);

        reader.Push(0.5001, muted: false);

        Assert.Equal(new DateTimeOffset(2026, 9, 22, 9, 31, 0, TimeSpan.Zero), src.NextDue(T0, T0));
    }

    [Fact]
    public void Dispose_Lets_Go_Of_The_Reader_And_Is_Safe_Twice()
    {
        var reader = OneSpeaker();
        var src = new AudioSource("audio", reader);
        Refresh(src);
        var signals = 0;
        src.Changed += _ => signals++;

        src.Dispose();
        src.Dispose();
        reader.Push(0.9, muted: false);

        Assert.Equal(1, reader.Disposals);
        Assert.Equal(0, signals);
    }

    [Fact]
    public void A_Notification_Signals_The_Bus_Under_Its_Own_Name()
    {
        // The wiring the daemon does, proved here so the lane does not depend on the daemon's.
        var clock = new FakeClock(T0);
        using var bus = new EventBus(clock, TimeSpan.FromMilliseconds(400), autoWake: false);
        var wakes = 0;
        bus.WakeRequested += () => wakes++;
        using var reader = OneSpeaker(0.5);
        using var src = new AudioSource("audio", reader);
        Refresh(src);
        src.Changed += s => bus.Signal(s.Name);

        reader.Push(0.6, muted: false);
        reader.Push(0.7, muted: false);

        Assert.True(bus.PumpWake(T0.AddMilliseconds(400)));
        Assert.Equal(1, wakes);
    }

    // ---- device changes (IAudioDeviceNotifier) -------------------------------------------

    private static readonly DateTimeOffset NextMinute = new(2026, 9, 22, 9, 31, 0, TimeSpan.Zero);

    /// <summary>Speakers registered, a headset known to the fake but not yet default.</summary>
    private static (FakeAudioReader Reader, FakeAudioDeviceNotifier Notifier, AudioSource Source) SpeakersWithHeadsetAvailable()
    {
        var reader = OneSpeaker(0.5);
        reader.Devices["hs"] = new AudioReading("hs", "Headset Earphone", 0.2, false);
        var notifier = new FakeAudioDeviceNotifier();
        var src = new AudioSource("audio", reader, notifier);
        Refresh(src);
        return (reader, notifier, src);
    }

    [Fact]
    public void The_Notifier_Starts_On_The_First_Refresh_Not_At_Construction()
    {
        // COM is first touched on the first refresh, never when the layout is loaded.
        using var reader = OneSpeaker();
        var notifier = new FakeAudioDeviceNotifier();
        using var src = new AudioSource("audio", reader, notifier);
        Assert.Equal(0, notifier.Starts);

        Refresh(src);
        Refresh(src);

        Assert.True(notifier.Starts >= 1);
    }

    [Fact]
    public void A_Headset_Becoming_Default_Signals_At_Once_And_The_Refresh_Publishes_It()
    {
        // The bug in #26: before the notifier, this waited for the next whole minute.
        var (reader, notifier, src) = SpeakersWithHeadsetAvailable();
        using var _ = src;
        var signals = 0;
        src.Changed += _ => signals++;
        Assert.Equal(NextMinute, src.NextDue(T0, T0));

        reader.DefaultDeviceId = "hs";
        notifier.RaiseDefault("hs");

        Assert.Equal(1, signals);
        Assert.True(src.HasPending);
        Assert.Equal(T0, src.NextDue(T0, T0));

        var r = Refresh(src);
        Assert.Equal("hs", reader.RegisteredDeviceId);
        Assert.Equal("Headset Earphone", Assert.IsType<TextValue>(r.Fields["device"]).Text);
        Assert.Equal(20, Num(r, "volumePct"));

        // Cleared in the refresh, or the daemon is pinned at Scheduler.MinDelay.
        Assert.False(src.HasPending);
        Assert.Equal(NextMinute, src.NextDue(T0, T0));
    }

    [Fact]
    public void A_Default_Change_To_The_Endpoint_Already_Read_Is_Not_A_Change()
    {
        // CoreAudio repeats the notification per role, and the refresh acting on the first one
        // has registered the new endpoint before the repeats land. They must not each buy a tick.
        var (reader, notifier, src) = SpeakersWithHeadsetAvailable();
        using var _ = src;
        var signals = 0;
        src.Changed += _ => signals++;

        notifier.RaiseDefault("spk");

        Assert.Equal(0, signals);
        Assert.False(src.HasPending);

        reader.DefaultDeviceId = "hs";
        notifier.RaiseDefault("hs");
        Refresh(src);
        notifier.RaiseDefault("hs");
        notifier.RaiseDefault("hs");

        Assert.Equal(1, signals);
        Assert.False(src.HasPending);
    }

    [Fact]
    public void The_Last_Playback_Device_Going_Away_Signals_And_Publishes_The_Empty_Record()
    {
        var (reader, notifier, src) = SpeakersWithHeadsetAvailable();
        using var _ = src;
        var signals = 0;
        src.Changed += _ => signals++;

        reader.DefaultDeviceId = null;
        notifier.RaiseDefault(null);

        Assert.Equal(1, signals);
        Assert.Empty(Refresh(src).Fields);
        Assert.Null(reader.RegisteredDeviceId);
        Assert.Equal(0, src.ReaderFaults);
    }

    [Fact]
    public void The_Endpoint_Being_Read_Changing_State_Is_A_Change()
    {
        var (_, notifier, src) = SpeakersWithHeadsetAvailable();
        using var __ = src;
        var signals = 0;
        src.Changed += _ => signals++;

        notifier.RaiseState("spk");

        Assert.Equal(1, signals);
        Assert.True(src.HasPending);
    }

    [Fact]
    public void Another_Endpoint_Changing_State_Is_Not_A_Change_While_One_Is_Being_Read()
    {
        // OnDeviceStateChanged fires for every endpoint, capture included: a microphone plugged
        // into a machine that has speakers must not cost a tick.
        var (_, notifier, src) = SpeakersWithHeadsetAvailable();
        using var __ = src;
        var signals = 0;
        src.Changed += _ => signals++;

        notifier.RaiseState("mic");

        Assert.Equal(0, signals);
        Assert.False(src.HasPending);
    }

    [Fact]
    public void Any_Endpoint_Changing_State_Is_A_Change_While_None_Is_Being_Read()
    {
        // No playback device: whatever just arrived may be the first one.
        var reader = new FakeAudioReader { DefaultDeviceId = null };
        var notifier = new FakeAudioDeviceNotifier();
        using var src = new AudioSource("audio", reader, notifier);
        Assert.Empty(Refresh(src).Fields);
        var signals = 0;
        src.Changed += _ => signals++;

        reader.Devices["spk"] = new AudioReading("spk", "Speakers (Realtek)", 0.4, false);
        reader.DefaultDeviceId = "spk";
        notifier.RaiseState("spk");

        Assert.Equal(1, signals);
        Assert.Equal(40, Num(Refresh(src), "volumePct"));
    }

    [Fact]
    public void A_Device_Change_Signals_The_Bus_Under_Its_Own_Name_Once()
    {
        // A headset arriving is up to six default-change calls (three roles, two flows) plus state
        // changes; the notifier filters the roles and the bus coalesces the rest into one wake.
        var clock = new FakeClock(T0);
        using var bus = new EventBus(clock, TimeSpan.FromMilliseconds(400), autoWake: false);
        var wakes = 0;
        bus.WakeRequested += () => wakes++;
        var (reader, notifier, src) = SpeakersWithHeadsetAvailable();
        using var _ = src;
        src.Changed += s => bus.Signal(s.Name);

        reader.DefaultDeviceId = "hs";
        notifier.RaiseState("hs");
        notifier.RaiseDefault("hs");
        notifier.RaiseState("spk");

        Assert.True(bus.PumpWake(T0.AddMilliseconds(400)));
        Assert.Equal(1, wakes);
    }

    [Fact]
    public void Nothing_Escapes_The_Device_Change_Path_And_The_Fault_Is_Counted()
    {
        var (reader, notifier, src) = SpeakersWithHeadsetAvailable();
        using var _ = src;
        src.Changed += _ => throw new InvalidOperationException("a subscriber misbehaved");

        reader.DefaultDeviceId = "hs";
        notifier.RaiseDefault("hs");
        notifier.RaiseState("spk");

        Assert.Equal(2, src.ReaderFaults);
        Assert.True(src.HasPending);
    }

    [Fact]
    public void Dispose_Lets_Go_Of_The_Notifier_And_Stops_Listening()
    {
        var (reader, notifier, src) = SpeakersWithHeadsetAvailable();
        var signals = 0;
        src.Changed += _ => signals++;

        src.Dispose();
        src.Dispose();
        reader.DefaultDeviceId = "hs";
        notifier.RaiseDefault("hs");
        notifier.RaiseState("spk");

        Assert.Equal(1, notifier.Disposals);
        Assert.Equal(0, signals);
    }

    [Fact]
    public void Re_Registering_Does_Not_Hold_The_Source_Lock_A_Volume_Callback_Needs()
    {
        // The real reader's Register unregisters the old volume callback, and
        // UnregisterControlChangeNotify can wait for a notification already in flight. That
        // notification's handler takes the source's lock, so a refresh that held the lock across
        // Register would deadlock the tick against the audio service - and a device change is
        // exactly when the old endpoint may still be notifying. Here the in-flight notification
        // runs on another thread and Register waits for it, as the real one may.
        var (reader, _, src) = SpeakersWithHeadsetAvailable();
        using var __ = src;
        var completed = false;
        reader.DuringRegister = () =>
        {
            var t = new Thread(() => reader.Raise());
            t.Start();
            completed = t.Join(TimeSpan.FromSeconds(5));
        };

        reader.DefaultDeviceId = "hs";
        Refresh(src);

        Assert.True(completed, "the in-flight volume notification could not take the source lock");
        Assert.Equal("hs", reader.RegisteredDeviceId);
    }

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now => now;
    }
}

/// <summary>Raises device notifications on demand, as the audio service would from its thread.</summary>
internal sealed class FakeAudioDeviceNotifier : IAudioDeviceNotifier
{
    public event Action<string?>? DefaultDeviceChanged;
    public event Action<string>? DeviceStateChanged;
    public int Starts { get; private set; }
    public int Disposals { get; private set; }

    public void Start() => Starts++;
    public void RaiseDefault(string? id) => DefaultDeviceChanged?.Invoke(id);
    public void RaiseState(string id) => DeviceStateChanged?.Invoke(id);
    public void Dispose() => Disposals++;
}
