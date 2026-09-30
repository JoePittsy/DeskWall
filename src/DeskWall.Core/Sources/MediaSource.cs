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
    private void Detach()
    {
        if (_session is null) return;
        _session.MediaPropertiesChanged -= MediaChanged;
        _session.PlaybackInfoChanged -= PlaybackChanged;
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
            return new RecordValue(new Dictionary<string, Value>
            {
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
    private static RecordValue Empty() => new(new Dictionary<string, Value>
    {
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
