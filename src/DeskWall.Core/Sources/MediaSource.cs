using DeskWall.Core.Values;
using DeskWall.Core.Render;
using Windows.Media.Control;
using Windows.Storage.Streams;
using System.Security.Cryptography;

namespace DeskWall.Core.Sources;

/// <summary>Current Windows media session. Changes are signalled, never polled.</summary>
public sealed class MediaSource(string name) : ISource, ISignalSource, IDisposable
{
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
                var manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
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
            var properties = await session.TryGetMediaPropertiesAsync();
            if (_disposed) return Empty();
            var playing = session.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var art = "";
            if (playing && properties.Thumbnail is { } thumbnail)
            {
                using var stream = await thumbnail.OpenReadAsync();
                if (stream.Size > 0 && stream.Size <= 8 * 1024 * 1024)
                {
                    using var reader = new DataReader(stream);
                    var length = await reader.LoadAsync((uint)stream.Size);
                    var bytes = new byte[length]; reader.ReadBytes(bytes);
                    var hash = Convert.ToHexString(SHA256.HashData(bytes));
                    if (_artHash != hash || !File.Exists(_art))
                    {
                        var dir = Path.Combine(Paths.RuntimeDir, "media"); Directory.CreateDirectory(dir);
                        var raw = Path.Combine(dir, "thumbnail.tmp");
                        var output = Path.Combine(dir, "art.png");
                        try
                        {
                            await File.WriteAllBytesAsync(raw, bytes, ct).ConfigureAwait(false);
                            using (var image = Surface.Load(raw)) image.SavePng(output + ".tmp");
                            File.Move(output + ".tmp", output, true);
                            _artHash = hash; _art = output;
                        }
                        finally { File.Delete(raw); }
                    }
                    art = _art;
                }
            }
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
