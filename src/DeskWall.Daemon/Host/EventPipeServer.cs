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
/// <para>Threading: the accept loop and the readers are asynchronous, so at rest this costs no
/// thread of its own - a pending accept is an I/O completion, not a blocked thread. The callback
/// therefore runs on a pool thread and must be thread safe; <see cref="DeskWall.Core.Events.EventBus.Publish(string)"/>
/// is.</para></summary>
public sealed class EventPipeServer : IDisposable
{
    public const string PipeName = "DeskWall.Events";

    /// <summary>Four at once: enough for a script, a shell and a background producer to overlap,
    /// and low enough that a runaway producer cannot make the daemon hold instances open forever.
    /// One of the four is always the instance waiting for the next connection.</summary>
    private const int MaxInstances = 4;

    private readonly Func<string, bool> _onLine;
    private readonly Action<string>? _onError;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _slots = new(MaxInstances, MaxInstances);
    private readonly object _lock = new();
    private readonly List<NamedPipeServerStream> _live = [];
    private string? _lastError;   // so a pipe name that cannot be created does not fill the log
    private bool _started;
    private bool _disposed;

    /// <param name="onLine">Called for every non-blank line. Its bool is the bus's accepted flag;
    /// the server only uses it to decide nothing, and it is there so a caller can pass
    /// <c>bus.Publish</c> directly.</param>
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

    /// <summary>Begin accepting. Returns at once; nothing here blocks the caller's thread, which is
    /// the daemon's message pump.</summary>
    public void Start()
    {
        lock (_lock)
        {
            if (_disposed || _started) return;
            _started = true;
        }
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await _slots.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }

            NamedPipeServerStream? server = null;
            try
            {
                server = Create();
                lock (_lock)
                {
                    // Dispose may have run between the check above and here; the stream it could
                    // not see must not be left listening.
                    if (_disposed) { server.Dispose(); _slots.Release(); return; }
                    _live.Add(server);
                }
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Drop(server);
                ReleaseSlot();
                if (ct.IsCancellationRequested || _disposed) return;
                // ObjectDisposedException and IOException both arrive here when a client vanishes
                // mid-handshake, which is not worth a log line every time it happens; a name that
                // cannot be created is, once.
                ReportOnce($"pipe {_pipeName}: {ex.Message}");
                try { await Task.Delay(1000, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                continue;
            }

            var connected = server;
            _ = Task.Run(() => ServeAsync(connected), CancellationToken.None);
        }
    }

    /// <summary>Read lines until the producer goes away. Runs on a pool thread, one per connected
    /// producer.</summary>
    private async Task ServeAsync(NamedPipeServerStream server)
    {
        try
        {
            // No BOM detection: a producer that opens with one would otherwise have its first
            // character eaten or kept depending on the writer that made it.
            using var reader = new StreamReader(server, new System.Text.UTF8Encoding(false), detectEncodingFromByteOrderMarks: false);
            while (await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false) is { } line)
            {
                // A blank line is what a producer's trailing newline leaves behind. Offering it to
                // the bus would put a rejected "empty" entry in the diagnostics ring for every
                // event sent, and hide the reason a real event is not landing.
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { _onLine(line); }
                catch (Exception ex)
                {
                    // The callback is the bus, which does not throw; if a future one does, it costs
                    // this line and not the producer's connection.
                    ReportOnce($"pipe {_pipeName}: handler threw: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
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
            Drop(server);
            ReleaseSlot();
        }
    }

    /// <summary>Dispose can win the race against a reader that is finishing; a semaphore that has
    /// already gone is nothing left to give a slot back to.</summary>
    private void ReleaseSlot()
    {
        try { _slots.Release(); }
        catch (ObjectDisposedException) { }
    }

    /// <summary>Only the current user, so another account's process on the same machine cannot
    /// push values into this desktop (spec section 9: the threat left is a process already running
    /// as this user, which is stated in the docs rather than defended against here).
    /// <para>The DACL has exactly one ACE, so nothing else - not SYSTEM, not an administrator
    /// account - is granted access by omission.</para></summary>
    private NamedPipeServerStream Create()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("the current identity has no user SID");
        var security = new PipeSecurity();
        // CreateNewInstance as well as ReadWrite: the second and later instances of the pipe are
        // access-checked against this same DACL, so without it the server could serve exactly one
        // producer and then fail to listen again.
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(
            _pipeName,
            PipeDirection.In,
            MaxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 0,
            pipeSecurity: security);
    }

    private void Drop(NamedPipeServerStream? server)
    {
        if (server is null) return;
        lock (_lock) _live.Remove(server);
        try { server.Dispose(); } catch (IOException) { }
    }

    private void ReportOnce(string message)
    {
        if (string.Equals(_lastError, message, StringComparison.Ordinal)) return;
        _lastError = message;
        _onError?.Invoke(message);
    }

    public void Dispose()
    {
        List<NamedPipeServerStream> live;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            live = [.. _live];
            _live.Clear();
        }
        try { _cts.Cancel(); } catch (AggregateException) { }
        // Cancelling the pending WaitForConnectionAsync is not enough on every Windows build:
        // disposing the instance is what takes the name out of the namespace, which is what makes
        // "the listener has stopped" observable to a client.
        foreach (var s in live)
        {
            try { s.Dispose(); } catch (IOException) { } catch (ObjectDisposedException) { }
        }
        _cts.Dispose();
        _slots.Dispose();
    }
}
