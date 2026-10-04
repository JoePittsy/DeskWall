namespace DeskWall.Core.Sources.Audio;

/// <summary>Tells <see cref="AudioSource"/> that the set of playback endpoints moved: a headset
/// became the default, the last speaker was unplugged. The volume callback cannot say so, because
/// it only ever fires for the endpoint it was registered on, and that is the one no longer in use.
/// <para>Plumbing only: it reports what CoreAudio said, and <see cref="AudioSource"/> decides
/// whether that is a change, so the deciding is tested with no sound card involved.</para>
/// <para>Both events are raised on an audio service thread, inside CoreAudio's own call frame:
/// a handler must not block, must not call into CoreAudio (re-registering from inside the
/// callback is exactly what the documentation forbids), and must let nothing escape.</para>
/// <para>Implementations must not throw from any member.</para></summary>
public interface IAudioDeviceNotifier : IDisposable
{
    /// <summary>The default playback endpoint for the role <see cref="AudioSource"/> reads
    /// changed. The argument is the new default's id, or null when no playback endpoint is left.
    /// Other roles and capture endpoints are filtered out before this is raised.</summary>
    event Action<string?>? DefaultDeviceChanged;

    /// <summary>An endpoint's state changed (active, disabled, unplugged, not present), with its
    /// id. Raised for every endpoint, render or capture; the source keeps the ones that matter.</summary>
    event Action<string>? DeviceStateChanged;

    /// <summary>Start listening. Idempotent; tick thread only, so COM is first touched on the
    /// first refresh rather than when the layout is loaded. A failure leaves the notifier silent,
    /// and the source's whole-minute schedule still catches a device change.</summary>
    void Start();
}
