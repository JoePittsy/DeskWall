using DeskWall.Core.Values;
using DeskWall.Core.Render;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;
using System.Security.Cryptography;

namespace DeskWall.Core.Sources;

/// <summary>Current Windows media session. Changes are signalled, never polled.</summary>
public sealed class MediaSource(string name) : ISource, ISignalSource, IDisposable
{
    internal MediaSource(string name, Func<IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager>> request, TimeSpan timeout) : this(name)
        => (_request, _timeout) = (request, timeout);

    /// <summary>How long any one call into the media service may take before the refresh fails.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);
    private readonly Func<IAsyncOperation<GlobalSystemMediaTransportControlsSessionManager>> _request = GlobalSystemMediaTransportControlsSessionManager.RequestAsync;
    private readonly TimeSpan _timeout = CallTimeout;
    public string Name => name;
    public event Action<ISource>? Changed;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private readonly SemaphoreSlim _gate = new(1);
    private volatile bool _disposed;
    private int _pending;
    private string? _artHash;
    private string _art = "";
    public bool HasPending => Volatile.Read(ref _pending) != 0;
    public DateTimeOffset NextDue(DateTimeOffset? lastRefresh, DateTimeOffset now)
        => lastRefresh is null || HasPending ? now : DateTimeOffset.MaxValue;
    private void Signal()
    {
        if (_disposed) return;
        Interlocked.Exchange(ref _pending, 1);
        try { Changed?.Invoke(this); } catch (Exception) { }
    }
    private void CurrentChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) => Signal();
    private void MediaChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => Signal();
    private void PlaybackChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => Signal();

    // (end time, position, when the position was stamped) as of the last refresh.
    private (TimeSpan End, TimeSpan Position, DateTimeOffset At) _timeline;
    private bool _timelinePlaying;

    /// <summary>Some apps raise this every second while playing. Only a change the last refresh could
    /// not have predicted -- a seek, a new duration -- is worth a tick; ordinary forward progress
    /// is extrapolated at the next refresh any other change causes.</summary>
    private void TimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        try
        {
            var t = sender.GetTimelineProperties();
            if (t is null) return;
            var (end, position, at) = _timeline;
            var expected = position + (_timelinePlaying ? t.LastUpdatedTime - at : TimeSpan.Zero);
            if (t.EndTime != end || Math.Abs((t.Position - expected).TotalSeconds) > 3) Signal();
        }
        catch (Exception) { }
    }
    private void Detach()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= MediaChanged;
        _session.PlaybackInfoChanged -= PlaybackChanged;
        _session.TimelinePropertiesChanged -= TimelineChanged;
        _session = null;
    }
    public async ValueTask<RecordValue> RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            Interlocked.Exchange(ref _pending, 0);
            if (_disposed) return Empty();
            if (_manager is null)
            {
                var manager = await Bounded(_request(), ct).ConfigureAwait(false);
                if (_disposed) return Empty();
                _manager = manager;
                _manager.CurrentSessionChanged += CurrentChanged;
            }
            var current = _manager.GetCurrentSession();
            if (!Equals(current, _session))
            {
                Detach();
                _session = current;
                if (_session is not null)
                {
                    _session.MediaPropertiesChanged += MediaChanged;
                    _session.PlaybackInfoChanged += PlaybackChanged;
                    _session.TimelinePropertiesChanged += TimelineChanged;
                }
            }
            if (_session is null) return Empty();
            var session = _session;
            var properties = await Bounded(session.TryGetMediaPropertiesAsync(), ct).ConfigureAwait(false);
            if (_disposed) return Empty();
            var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var art = playing && properties.Thumbnail is { } thumbnail ? await Art(thumbnail, ct).ConfigureAwait(false) : "";
            var (position, duration) = Timeline(session, playing);
            return new RecordValue(new Dictionary<string, Value>
            {
                ["position"] = new NumberValue(position),
                ["duration"] = new NumberValue(duration),
                ["progress"] = new NumberValue(duration > 0 ? Math.Clamp(position / duration, 0, 1) : 0),
                ["title"] = new TextValue(playing ? properties.Title : ""),
                ["artist"] = new TextValue(playing ? properties.Artist : ""),
                ["album"] = new TextValue(playing ? properties.AlbumTitle : ""),
                ["playing"] = new BoolValue(playing),
                ["app"] = new TextValue(session.SourceAppUserModelId),
                ["art"] = new ImageValue(art),
            });
        }
        finally
        {
            if (_disposed) Release();
            _gate.Release();
        }
    }
    /// <summary>The cover as a PNG path, or "" when there is none this refresh. Never fails the
    /// record: the cover is decoration, and a thumbnail that will not open, read or decode must not
    /// take the title and artist with it. Only the tick's own cancellation gets out.</summary>
    private async Task<string> Art(IRandomAccessStreamReference thumbnail, CancellationToken ct)
    {
        try
        {
            using var stream = await Bounded(thumbnail.OpenReadAsync(), ct).ConfigureAwait(false);
            if (stream.Size == 0 || stream.Size > 8 * 1024 * 1024) return "";
            using var reader = new DataReader(stream);
            var length = await Bounded(reader.LoadAsync((uint)stream.Size), ct).ConfigureAwait(false);
            var bytes = new byte[length]; reader.ReadBytes(bytes);
            return SaveArt(bytes);
        }
        catch (Exception) when (!ct.IsCancellationRequested) { return ""; }
    }

    /// <summary>Decode the thumbnail bytes to runtime/media/art.png, unless they are the ones already
    /// there. Temp files are unique to the call: the designer runs its own media source on the same
    /// runtime dir, and with one fixed temp name each could delete or lock the other's. A failure is
    /// "" and is not remembered, so the next refresh tries again.</summary>
    internal string SaveArt(byte[] bytes)
    {
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (_artHash == hash && File.Exists(_art)) return _art;
        var dir = Path.Combine(Paths.RuntimeDir, "media");
        var output = Path.Combine(dir, "art.png");
        var unique = Guid.NewGuid().ToString("N");
        var raw = Path.Combine(dir, $"thumbnail.{unique}.tmp");
        var png = Path.Combine(dir, $"art.{unique}.tmp");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(raw, bytes);
            using (var image = Surface.Load(raw)) image.SavePng(png);
            File.Move(png, output, true);
            _artHash = hash; _art = output;
            return output;
        }
        catch (Exception) { return ""; }
        finally { TryDelete(raw); TryDelete(png); }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception) { }
    }

    /// <summary>One call into the media service, bounded by <see cref="CallTimeout"/> and the token.
    /// The service is out of process and the daemon's tick thread blocks on this refresh, so a call
    /// that never answers must fail the source rather than freeze the wallpaper. WaitAsync, not
    /// only AsTask(ct): an operation that ignores Cancel would otherwise still never complete.</summary>
    private async Task<T> Bounded<T>(IAsyncOperation<T> operation, CancellationToken ct)
    {
        try { return await operation.AsTask(ct).WaitAsync(_timeout, ct).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            try { operation.Cancel(); } catch (Exception) { }
            throw new TimeoutException($"the media service did not answer within {_timeout.TotalSeconds:0.#} s");
        }
    }

    /// <summary>Seconds into the track and its length, the position carried forward from when the
    /// app last stamped it if it is playing. Zero and zero when the app does not report a timeline.</summary>
    private (double Position, double Duration) Timeline(GlobalSystemMediaTransportControlsSession session, bool playing)
    {
        try
        {
            var t = session.GetTimelineProperties();
            if (t is null) return (0, 0);
            _timeline = (t.EndTime, t.Position, t.LastUpdatedTime);
            _timelinePlaying = playing;
            if (!playing) return (0, 0);
            var duration = (t.EndTime - t.StartTime).TotalSeconds;
            if (duration <= 0) return (0, 0);
            var position = (t.Position - t.StartTime).TotalSeconds;
            if (t.LastUpdatedTime.Year > 2000) position += Math.Max(0, (DateTimeOffset.Now - t.LastUpdatedTime).TotalSeconds);
            return (Math.Clamp(position, 0, duration), duration);
        }
        catch (Exception) { return (0, 0); }
    }
    private static RecordValue Empty() => new(new Dictionary<string, Value>
    {
        ["position"] = new NumberValue(0), ["duration"] = new NumberValue(0), ["progress"] = new NumberValue(0),
        ["title"] = new TextValue(""), ["artist"] = new TextValue(""), ["album"] = new TextValue(""),
        ["playing"] = new BoolValue(false), ["app"] = new TextValue(""), ["art"] = new ImageValue(""),
    });
    private void Release()
    {
        Detach();
        if (_manager is not null) _manager.CurrentSessionChanged -= CurrentChanged;
        _manager = null;
    }
    public void Dispose()
    {
        _disposed = true;
        if (!_gate.Wait(0)) return;
        try { Release(); } finally { _gate.Release(); }
    }
}
