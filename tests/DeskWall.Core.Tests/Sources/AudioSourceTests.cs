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

    public void Register()
    {
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

    private sealed class FakeClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset Now => now;
    }
}
