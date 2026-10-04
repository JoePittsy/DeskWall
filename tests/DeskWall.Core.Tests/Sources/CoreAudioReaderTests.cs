using DeskWall.Core.Sources.Audio;
using Xunit;
using Xunit.Abstractions;

/// <summary>Plausibility facts against the real machine, in the style of
/// <c>Win32HardwareReaderTests</c>: they assert only what must hold on any Windows box with a
/// sound card, and skip rather than fail on one without. The output lines record what this
/// machine actually reported.
/// <para>Nothing here changes the volume. B3's measurements do that from a script, and put it
/// back.</para></summary>
public class CoreAudioReaderTests(ITestOutputHelper output)
{
    [Fact]
    public void Default_Endpoint_Is_Found_And_Reads_Plausibly()
    {
        using var r = new CoreAudioReader();
        var id = r.DefaultDeviceId;
        output.WriteLine($"DefaultDeviceId = {id ?? "(none)"}");
        if (id is null) return;                     // a machine with no playback device is allowed

        Assert.Null(r.RegisteredDeviceId);
        Assert.Null(r.Current);

        r.Register();

        Assert.Equal(id, r.RegisteredDeviceId);
        var reading = r.Current;
        Assert.NotNull(reading);
        output.WriteLine($"device = \"{reading!.Value.DeviceName}\" volume = {reading.Value.Volume} muted = {reading.Value.Muted}");
        Assert.InRange(reading.Value.Volume, 0d, 1d);
        Assert.Equal(id, reading.Value.DeviceId);
        Assert.NotEqual("", reading.Value.DeviceName);
    }

    [Fact]
    public void Registering_Twice_Is_Registering_Once_And_Dispose_Is_Safe_Twice()
    {
        // The re-registration path is the one a headset takes, and it runs while the previous
        // callback is still live: it must release the old endpoint without freeing the shim the
        // new one is about to use.
        var r = new CoreAudioReader();
        if (r.DefaultDeviceId is null) { r.Dispose(); return; }

        r.Register();
        var first = r.Current;
        r.Register();
        var second = r.Current;

        Assert.Equal(first!.Value.DeviceId, second!.Value.DeviceId);
        Assert.Equal(first.Value.Volume, second.Value.Volume);

        r.Dispose();
        r.Dispose();
        Assert.Null(r.DefaultDeviceId);             // disposed: reports "no endpoint", does not throw
        Assert.Null(r.Current);
    }

    [Fact]
    public async Task A_Whole_AudioSource_Publishes_This_Machine()
    {
        using var src = new AudioSource("audio", new CoreAudioReader());

        // Measured, not asserted (CLAUDE.md): the first refresh pays for the enumerator, the
        // endpoint, the property store and the registration; every later one only compares two
        // device ids. Printed rather than bounded, because a number that fails a build on a busy
        // machine teaches nothing.
        var cold = System.Diagnostics.Stopwatch.StartNew();
        var r = await src.RefreshAsync(CancellationToken.None);
        cold.Stop();
        var warm = System.Diagnostics.Stopwatch.StartNew();
        await src.RefreshAsync(CancellationToken.None);
        warm.Stop();
        output.WriteLine($"refresh: first {cold.Elapsed.TotalMilliseconds:F2} ms, second {warm.Elapsed.TotalMilliseconds:F2} ms");
        output.WriteLine(string.Join(", ", r.Fields.Select(f => $"{f.Key}={f.Value.ToText(null)}")));
        Assert.Equal(0, src.ReaderFaults);
        if (r.Fields.Count == 0) return;            // no playback device: the empty record is correct
        Assert.Equal(["device", "muted", "volume", "volumePct"], r.Fields.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.InRange(((DeskWall.Core.Values.NumberValue)r.Fields["volume"]).Number, 0d, 1d);
    }

    [Fact]
    public void The_Device_Notifier_Registers_With_CoreAudio_And_Lets_Go_Twice()
    {
        // Registering an IMMNotificationClient needs no playback device, only the audio service,
        // so this skips on a machine with Audiosrv stopped, as the facts above skip on one with no
        // sound card. What it proves is the IUnknown half of the hand-built vtable: CoreAudio
        // QueryInterfaces and AddRefs the client on registration and Releases it on
        // unregistration, through slots 0-2.
        var n = new CoreAudioDeviceNotifier();
        Assert.False(n.IsListening);

        var started = n.TryStart(out var error);
        output.WriteLine($"listening = {started} {error}");
        if (!started && !AudioServiceRunning())
        {
            output.WriteLine("Audiosrv is not running: skipped");
            n.Dispose();
            return;
        }
        Assert.True(started, error);
        Assert.True(n.TryStart(out _));            // idempotent
        Assert.True(n.IsListening);

        n.Dispose();
        n.Dispose();
        Assert.False(n.IsListening);
        Assert.False(n.TryStart(out var afterDispose));   // disposed: reports why, does not throw
        Assert.Equal("disposed", afterDispose);
    }

    private static bool AudioServiceRunning()
    {
        var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", "query Audiosrv")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return text.Contains("RUNNING", StringComparison.Ordinal);
    }

    /// <summary>The hand-built vtables put each callback at a slot number written by hand. A
    /// default-device change cannot be provoked from a test without changing the machine's audio
    /// device, so a wrong slot would otherwise surface only as the wrong method running on the
    /// audio service's thread. This reads the table each class actually builds and checks every
    /// slot holds the method named for that slot in the interface - the slot order coming from the
    /// Windows SDK metadata, through CsWin32's own (internal) Vtbl struct. Slots 0-2 are
    /// <c>ComCallback</c>'s IUnknown; slot n &gt;= 3 named <c>X</c> must be the class's <c>CbX</c>,
    /// except the two device notifications deliberately routed to one ignoring callback.</summary>
    [Theory]
    [InlineData(typeof(CoreAudioDeviceNotifier), "Windows.Win32.Media.Audio.IMMNotificationClient",
        "QueryInterface,AddRef,Release,OnDeviceStateChanged,OnDeviceAdded,OnDeviceRemoved,OnDefaultDeviceChanged,OnPropertyValueChanged")]
    [InlineData(typeof(CoreAudioReader), "Windows.Win32.Media.Audio.Endpoints.IAudioEndpointVolumeCallback",
        "QueryInterface,AddRef,Release,OnNotify")]
    public unsafe void Hand_Built_Vtable_Slots_Hold_The_Matching_Callbacks(Type owner, string interfaceName, string expectedSlots)
    {
        const System.Reflection.BindingFlags Any = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance
            | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var core = typeof(CoreAudioReader).Assembly;

        // The interface's slot order, from the SDK metadata.
        var vtblType = core.GetType(interfaceName + "+Vtbl", throwOnError: true)!;
        var slots = vtblType.GetFields(Any & ~System.Reflection.BindingFlags.Static)
            .OrderBy(f => (long)System.Runtime.InteropServices.Marshal.OffsetOf(vtblType, f.Name))
            .Select(f => f.Name.Split('_')[0])
            .ToArray();
        output.WriteLine(string.Join(", ", slots));
        Assert.Equal(expectedSlots.Split(','), slots);

        // The table the class hands to CoreAudio.
        var built = (void**)System.Reflection.Pointer.Unbox(owner.GetMethod("Vtable", Any)!.Invoke(null, null)!);
        var comCallback = core.GetType("DeskWall.Core.Sources.Audio.ComCallback", throwOnError: true)!;
        for (var i = 0; i < slots.Length; i++)
        {
            var expected = i < 3
                ? comCallback.GetMethod(slots[i], Any)
                : owner.GetMethod(slots[i] is "OnDeviceAdded" or "OnDeviceRemoved" ? "CbIgnoreDevice" : "Cb" + slots[i], Any);
            Assert.True(expected is not null, $"no callback for slot {i} ({slots[i]})");
            Assert.True(expected!.MethodHandle.GetFunctionPointer() == (nint)built[i],
                $"slot {i} ({slots[i]}) does not hold {expected.DeclaringType!.Name}.{expected.Name}");
        }
    }
}
