using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace DeskWall.Daemon.Host;

/// <summary>The event ingress: newline delimited JSON on <c>\\.\pipe\DeskWall.Events</c>, one
/// object per line, restricted to the current user. Spec section 6. It lives in the daemon rather
/// than Core because two processes cannot own one pipe name, and the designer runs its own bus.
/// <para>A producer may connect, write one line and disconnect, or hold the connection open and
/// stream; both are ordinary. Nothing a producer sends may take the listener down, so a malformed
/// line, a callback that returns false and a callback that throws all leave the connection
/// standing and the loop running.</para>
/// <para>Threading: one background thread blocked in <c>ConnectNamedPipe</c>, which costs no CPU
/// at rest, plus one more for as long as a producer is actually connected. Synchronous, not
/// overlapped, for the reason in <see cref="Accept"/>; the callback therefore runs on a reader
/// thread and must be thread safe, which
/// <see cref="DeskWall.Core.Events.EventBus.Publish(string)"/> is.</para></summary>
public sealed class EventPipeServer : IDisposable
{
    public const string PipeName = "DeskWall.Events";

    /// <summary>Four at once: enough for a script, a shell and a background producer to overlap,
    /// and low enough that a runaway producer cannot make the daemon hold instances open forever.
    /// A fifth producer's connect waits in WaitNamedPipe until one frees, which is what a client
    /// that cannot be served yet should do.</summary>
    private const int MaxInstances = 4;

    private readonly Func<string, bool> _onLine;
    private readonly Action<string>? _onError;
    private readonly string _pipeName;
    private readonly object _lock = new();
    private readonly List<NamedPipeServerStream> _live = [];
    private readonly ManualResetEventSlim _slotFreed = new(false);
    private Thread? _accept;
    private int _instances;
    private string? _lastError;   // so a pipe name that cannot be created does not fill the log
    private bool _started;
    private volatile bool _disposed;

    /// <param name="onLine">Called for every non-blank line. Its bool is the bus's accepted flag;
    /// the server does not act on it, and it is there so a caller can pass <c>bus.Publish</c>
    /// straight in.</param>
    /// <param name="onError">Diagnostics for what never reached the callback at all: a pipe that
    /// could not be created, a connection that broke mid-line.</param>
    /// <param name="pipeName">Overridden only by tests, which each need their own name; two tests
    /// on one name would fight over the instances.</param>
    public EventPipeServer(Func<string, bool> onLine, Action<string>? onError = null, string pipeName = PipeName)
    {
        ArgumentNullException.ThrowIfNull(onLine);
        ArgumentException.ThrowIfNullOrEmpty(pipeName);
        _onLine = onLine;
        _onError = onError;
        _pipeName = pipeName;
    }

    /// <summary>Begin accepting. Returns at once; nothing here blocks the caller's thread, which
    /// is the daemon's message pump.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _started) return;
            _started = true;
            _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "DeskWall event pipe" };
        }
        _accept!.Start();
    }

    /// <summary>Its own thread, not the thread pool: the pool is what the tick and every async
    /// source use, and an event that waits for a free pool thread is an event that arrives late
    /// for no reason. One thread, blocked in the kernel, costing nothing until a producer
    /// arrives.</summary>
    private void AcceptLoop()
    {
        while (!_disposed)
        {
            if (!TryReserve())
            {
                _slotFreed.Wait(1000);
                continue;
            }
            NamedPipeServerStream? server = null;
            try
            {
                server = Create();
                lock (_lock)
                {
                    // Dispose may have run since the check above; the stream it could not see
                    // must not be left listening.
                    if (_disposed) { server.Dispose(); Release(); return; }
                    _live.Add(server);
                }
                Accept(server);
            }
            catch (Exception ex)
            {
                Drop(server);
                Release();
                if (_disposed) return;
                // A name that cannot be created at all is the case that matters here. Reported
                // once per distinct message: otherwise it would be a log line a second, forever.
                ReportOnce($"pipe {_pipeName}: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    /// <summary>Take one producer, hand it to a reader thread and come straight back for the
    /// next.
    /// <para>The IOException path is the whole reason this class is synchronous rather than
    /// overlapped. An instance is connectable the moment CreateNamedPipe returns, but its
    /// ConnectNamedPipe is only pending once this call is made; a producer that connects, writes
    /// its line and disconnects inside that window makes ConnectNamedPipe fail with ERROR_NO_DATA
    /// ("the pipe is being closed"). Connect-write-disconnect is the documented way to send one
    /// event, so this is not a corner case: measured at 20 sends in a row it lost one or two of
    /// them. The line is not gone - it is sitting in the instance's buffer - but
    /// NamedPipeServerStream will not read from a stream it does not believe is connected. So the
    /// handle is wrapped in a second stream that is told it is, and drained. That wrap is only
    /// safe on a non-overlapped handle: an asynchronous one is already bound to the I/O
    /// completion port and binding it twice throws.</para></summary>
    private void Accept(NamedPipeServerStream server)
    {
        var connected = true;
        try { server.WaitForConnection(); }
        catch (IOException) { connected = false; }
        if (_disposed) { Drop(server); Release(); return; }

        var reader = new Thread(() => Serve(server, connected)) { IsBackground = true, Name = "DeskWall event reader" };
        reader.Start();
    }

    /// <summary>Read lines until the producer goes away. One thread per connected producer, alive
    /// only while that producer is.</summary>
    private void Serve(NamedPipeServerStream server, bool connected)
    {
        NamedPipeServerStream? wrapper = null;
        try
        {
            var stream = server;
            if (!connected)
            {
                // The client came and went before the connect landed; the handle still holds what
                // it wrote. See Accept.
                wrapper = new NamedPipeServerStream(PipeDirection.In, isAsync: false, isConnected: true, server.SafePipeHandle);
                stream = wrapper;
            }
            // No BOM detection: a producer that opens with one would otherwise have its first
            // character eaten or kept depending on the writer that made it.
            using var lines = new StreamReader(stream, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            while (!_disposed && lines.ReadLine() is { } line)
            {
                // A blank line is what a producer's trailing newline leaves behind. Offering it to
                // the bus would put a rejected "empty" entry in the diagnostics ring for every
                // event sent, and hide the reason a real event is not landing.
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { _onLine(line); }
                catch (Exception ex)
                {
                    // The callback is the bus, which does not throw; if a future one does, it
                    // costs this line and not the producer's connection.
                    ReportOnce($"pipe {_pipeName}: handler threw: {ex.Message}");
                }
            }
        }
        catch (IOException)
        {
            // A producer killed mid-line. Ordinary, and its own problem.
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            // Both streams share one SafePipeHandle, and SafeHandle.Dispose is idempotent, so
            // closing both closes the handle once.
            try { wrapper?.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
            Drop(server);
            Release();
        }
    }

    /// <summary>Only the current user, so another account's process on the same machine cannot
    /// push values into this desktop (spec section 9: the threat left is a process already
    /// running as this user, which the docs state rather than this defending against).
    /// <para>The DACL has exactly one ACE, so nothing else - not SYSTEM, not an administrator
    /// account - is granted access by omission.</para></summary>
    private NamedPipeServerStream Create()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("the current identity has no user SID");
        var security = new PipeSecurity();
        // CreateNewInstance as well as ReadWrite: the second and later instances are access
        // checked against this same DACL, so without it the server could serve one producer and
        // then fail to listen again.
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.In,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.None,
            inBufferSize: 4096,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private bool TryReserve()
    {
        lock (_lock)
        {
            if (_disposed || _instances >= MaxInstances) return false;
            _instances++;
            _slotFreed.Reset();
            return true;
        }
    }

    private void Release()
    {
        lock (_lock) _instances--;
        try { _slotFreed.Set(); } catch (ObjectDisposedException) { }
    }

    private void Drop(NamedPipeServerStream? server)
    {
        if (server is null) return;
        lock (_lock) _live.Remove(server);
        try { server.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
    }

    private void ReportOnce(string message)
    {
        if (string.Equals(_lastError, message, StringComparison.Ordinal)) return;
        _lastError = message;
        _onError?.Invoke(message);
    }

    public void Dispose()
    {
        Thread? accept;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            accept = _accept;
            _accept = null;
        }
        // Poke before closing anything: the accept thread is blocked in the kernel on
        // ConnectNamedPipe, and a connection it can complete is the one way to get it back to the
        // top of its loop, where it sees _disposed and leaves. Closing the handle under it is not
        // reliably enough to end a synchronous wait.
        Poke();
        List<NamedPipeServerStream> live;
        lock (_lock)
        {
            live = [.. _live];
            _live.Clear();
        }
        foreach (var s in live)
        {
            try { s.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
        try { _slotFreed.Set(); } catch (ObjectDisposedException) { }
        accept?.Join(2000);
        _slotFreed.Dispose();
    }

    /// <summary>Connect to our own pipe for as long as it takes the accept to notice. Best effort
    /// in every sense: nothing is written, and a failure means there was nothing waiting.</summary>
    private void Poke()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(500);
        }
        catch (TimeoutException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
