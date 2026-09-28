namespace DeskWall.Core.Sources.Audio;

/// <summary>One playback endpoint's identity and its master volume state.</summary>
/// <param name="DeviceId">The endpoint id, stable across renames; what a device change is detected on.</param>
/// <param name="DeviceName">The friendly name, for display.</param>
/// <param name="Volume">Master scalar, 0..1.</param>
/// <param name="Muted">Master mute flag.</param>
public readonly record struct AudioReading(string DeviceId, string DeviceName, double Volume, bool Muted);

/// <summary>The audio side of <see cref="AudioSource"/>, behind an interface so the source's
/// arithmetic, debounce, device change and empty case are tested without real hardware - the same
/// arrangement as <c>IHardwareReader</c>.
/// <para>The split between <see cref="Current"/> and the rest is not cosmetic. <see cref="Changed"/>
/// is raised from the audio service's own thread, and a handler there must not call into COM;
/// <see cref="Current"/> is the only member it may touch. Everything else is tick-thread only.</para>
/// <para>Implementations must not throw from any member.</para></summary>
public interface IAudioReader : IDisposable
{
    /// <summary>The registered endpoint's volume or mute changed. Raised on an audio service
    /// thread: handlers must not block, must not call into COM, and must let nothing escape.</summary>
    event Action? Changed;

    /// <summary>Id of the default playback endpoint right now, or null when the machine has none.
    /// Touches COM; tick thread only.</summary>
    string? DefaultDeviceId { get; }

    /// <summary>Id of the endpoint the change callback is currently registered on, or null when
    /// nothing is registered yet.</summary>
    string? RegisteredDeviceId { get; }

    /// <summary>Point the change callback at the default endpoint, releasing any previous
    /// registration first. Tick thread only. A machine with no playback device leaves
    /// <see cref="RegisteredDeviceId"/> null and is not an error.</summary>
    void Register();

    /// <summary>The registered endpoint's state: what the callback last stored, or the reading
    /// taken at registration. Null when nothing is registered. Must not touch COM, because the
    /// push path reads it from the audio thread.</summary>
    AudioReading? Current { get; }
}
