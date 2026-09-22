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

        var r = await src.RefreshAsync(CancellationToken.None);

        output.WriteLine(string.Join(", ", r.Fields.Select(f => $"{f.Key}={f.Value.ToText(null)}")));
        Assert.Equal(0, src.ReaderFaults);
        if (r.Fields.Count == 0) return;            // no playback device: the empty record is correct
        Assert.Equal(["device", "muted", "volume", "volumePct"], r.Fields.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.InRange(((DeskWall.Core.Values.NumberValue)r.Fields["volume"]).Number, 0d, 1d);
    }
}
