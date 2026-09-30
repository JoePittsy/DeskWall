using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace DeskWall.Core.Sources.Notifications;

/// <summary>Tells a store watcher whether a write it just saw is the echo of somebody's read.
/// <para>Measured on JOES-XPS-17: <c>UserNotificationListener.GetNotificationsAsync</c> makes the
/// notification service write <c>wpndatabase.db</c>/<c>-wal</c> about ten times per toast it
/// returns, every time, while the store is otherwise silent at idle. A watcher that reads on every
/// write therefore feeds itself. Suppressing its own echo is not enough either: the designer's
/// Data panel and the daemon both run the source, and each one's read is the other one's "change",
/// so the two would ping-pong a read a second between them for ever.</para>
/// <para>So the gate is shared by every DeskWall process in the session through a 24-byte named
/// mapping: <c>[0]</c> readers in flight, <c>[1]</c> a cap on how long "in flight" is believed
/// (a reader that crashed mid-read must not blind everybody for good), <c>[2]</c> when the last
/// read's echo is over. Times are <see cref="Environment.TickCount64"/>, which is the same clock
/// in every process. When the mapping cannot be created the gate is process-local, which still
/// stops a process feeding itself.</para></summary>
public sealed unsafe class SharedReadGate : IDisposable
{
    public const string DefaultName = @"Local\DeskWall.NotificationReads";

    /// <summary>After a read ends, writes arriving within this long are its echo. The echo lands
    /// during the read in the measurements; this covers watcher delivery lag.</summary>
    public static readonly TimeSpan DefaultEcho = TimeSpan.FromMilliseconds(750);

    /// <summary>Longest a read is believed to be in flight. A read measured 280-360 ms.</summary>
    public static readonly TimeSpan DefaultCap = TimeSpan.FromSeconds(10);

    private readonly MemoryMappedFile? _map;
    private readonly MemoryMappedViewAccessor? _view;
    private readonly long* _cells;
    private readonly bool _ownsNative;
    private readonly long _echoMs, _capMs;
    private readonly Func<long> _ticks;
    private int _disposed;

    public SharedReadGate(string? name = DefaultName, TimeSpan? echo = null, TimeSpan? cap = null, Func<long>? ticks = null)
    {
        _echoMs = (long)(echo ?? DefaultEcho).TotalMilliseconds;
        _capMs = (long)(cap ?? DefaultCap).TotalMilliseconds;
        _ticks = ticks ?? (static () => Environment.TickCount64);
        if (name is not null)
        {
            try
            {
                _map = MemoryMappedFile.CreateOrOpen(name, 24, MemoryMappedFileAccess.ReadWrite);
                _view = _map.CreateViewAccessor(0, 24, MemoryMappedFileAccess.ReadWrite);
                byte* p = null;
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                _cells = (long*)(p + _view.PointerOffset);
                IsShared = true;
                return;
            }
            catch (Exception)
            {
                _view?.Dispose(); _view = null;
                _map?.Dispose(); _map = null;
            }
        }
        _cells = (long*)NativeMemory.AllocZeroed(24);
        _ownsNative = true;
    }

    /// <summary>False when the named mapping could not be made and the gate is this process's only.</summary>
    public bool IsShared { get; }

    /// <summary>True while any process's read is in flight or its echo has not settled.</summary>
    public bool Suppressed
    {
        get
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            var now = _ticks();
            return (Volatile.Read(ref _cells[0]) > 0 && now < Volatile.Read(ref _cells[1])) || now < Volatile.Read(ref _cells[2]);
        }
    }

    /// <summary>Mark a read as starting. Pair with <see cref="Exit"/> in a finally.</summary>
    public void Enter()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        Interlocked.Increment(ref _cells[0]);
        RaiseTo(ref _cells[1], _ticks() + _capMs);
    }

    public void Exit()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        // Never below zero: a counter left high by a crashed reader is bounded by the cap, one
        // driven negative would hide every later reader.
        long seen;
        do
        {
            seen = Volatile.Read(ref _cells[0]);
            if (seen <= 0) break;
        } while (Interlocked.CompareExchange(ref _cells[0], seen - 1, seen) != seen);
        RaiseTo(ref _cells[2], _ticks() + _echoMs);
    }

    /// <summary>Only ever moves a deadline later: another process's longer window must survive ours.</summary>
    private static void RaiseTo(ref long cell, long value)
    {
        long seen;
        do
        {
            seen = Volatile.Read(ref cell);
            if (seen >= value) return;
        } while (Interlocked.CompareExchange(ref cell, value, seen) != seen);
    }

    /// <summary>The owner disposes whatever raises events (the watcher) first, so nothing is
    /// asking <see cref="Suppressed"/> while the cells go away.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsNative) NativeMemory.Free(_cells);
        if (_view is not null)
        {
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _view.Dispose();
        }
        _map?.Dispose();
    }
}
