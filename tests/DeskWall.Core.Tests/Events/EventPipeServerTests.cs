using System.Diagnostics;
using System.IO.Pipes;
using DeskWall.Daemon.Host;
using Xunit;

namespace DeskWall.Core.Tests.Events;

/// <summary>The ingress. These talk to a real named pipe, so every test gets its own name: two
/// tests on one name would fight over the instances, and a leftover server from a failed test
/// would poison the next run.</summary>
[Trait("Category", "Pipe")]
public class EventPipeServerTests
{
    private static string UniqueName() => "DeskWall.Events.Test." + Guid.NewGuid().ToString("N");

    /// <summary>Connect, write the lines, disconnect. Mirrors what the documented one-liner does.</summary>
    private static void Send(string name, params string[] lines)
    {
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
        client.Connect(5000);
        using var w = new StreamWriter(client) { AutoFlush = true };
        foreach (var line in lines) w.WriteLine(line);
    }

    /// <summary>Poll rather than sleep a fixed time: the read happens on a pool thread and a fixed
    /// wait is either slow or flaky.</summary>
    private static bool WaitFor(Func<bool> done, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (done()) return true;
            Thread.Sleep(10);
        }
        return done();
    }

    [Fact]
    public void A_Line_Written_To_The_Pipe_Reaches_The_Callback()
    {
        var name = UniqueName();
        var got = new List<string>();
        using var server = new EventPipeServer(line => { lock (got) got.Add(line); return true; }, pipeName: name);
        server.Start();

        Send(name, """{"source":"build","data":{"status":"green"}}""");

        Assert.True(WaitFor(() => { lock (got) return got.Count == 1; }), "the line never reached the callback");
        lock (got) Assert.Equal("""{"source":"build","data":{"status":"green"}}""", got[0]);
    }

    [Fact]
    public void Two_Producers_Can_Connect_At_Once()
    {
        var name = UniqueName();
        var got = new List<string>();
        using var server = new EventPipeServer(line => { lock (got) got.Add(line); return true; }, pipeName: name);
        server.Start();

        using var a = new NamedPipeClientStream(".", name, PipeDirection.Out);
        a.Connect(5000);
        using var b = new NamedPipeClientStream(".", name, PipeDirection.Out);
        b.Connect(5000);
        var wa = new StreamWriter(a) { AutoFlush = true };
        var wb = new StreamWriter(b) { AutoFlush = true };
        wa.WriteLine("""{"source":"a","data":{}}""");
        wb.WriteLine("""{"source":"b","data":{}}""");

        Assert.True(WaitFor(() => { lock (got) return got.Count == 2; }), "both producers' lines did not arrive");
        lock (got)
        {
            Assert.Contains("""{"source":"a","data":{}}""", got);
            Assert.Contains("""{"source":"b","data":{}}""", got);
        }
    }

    [Fact]
    public void A_Producer_Can_Stream_Several_Lines_On_One_Connection()
    {
        var name = UniqueName();
        var got = new List<string>();
        using var server = new EventPipeServer(line => { lock (got) got.Add(line); return true; }, pipeName: name);
        server.Start();

        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
        client.Connect(5000);
        using var w = new StreamWriter(client) { AutoFlush = true };
        for (var i = 0; i < 5; i++) w.WriteLine("{\"source\":\"a\",\"data\":{\"n\":" + i + "}}");

        Assert.True(WaitFor(() => { lock (got) return got.Count == 5; }), "the stream stopped short");
        lock (got) Assert.Equal("""{"source":"a","data":{"n":4}}""", got[4]);
    }

    [Fact]
    public void A_Malformed_Line_Does_Not_Drop_The_Connection()
    {
        var name = UniqueName();
        var got = new List<string>();
        // false is what the bus returns for a rejected line, and a callback that throws is the
        // worse case: neither may take the reader down.
        using var server = new EventPipeServer(line =>
        {
            lock (got) got.Add(line);
            if (line.StartsWith("boom", StringComparison.Ordinal)) throw new InvalidOperationException("callback blew up");
            return !line.StartsWith("not json", StringComparison.Ordinal);
        }, pipeName: name);
        server.Start();

        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
        client.Connect(5000);
        using var w = new StreamWriter(client) { AutoFlush = true };
        w.WriteLine("not json");
        w.WriteLine("boom");
        w.WriteLine("""{"source":"a","data":{}}""");

        Assert.True(WaitFor(() => { lock (got) return got.Count == 3; }), "the connection died on the bad line");
        lock (got) Assert.Equal("""{"source":"a","data":{}}""", got[2]);
    }

    [Fact]
    public void Dispose_Stops_The_Listener_And_Is_Idempotent()
    {
        var name = UniqueName();
        var got = new List<string>();
        var server = new EventPipeServer(line => { lock (got) got.Add(line); return true; }, pipeName: name);
        server.Start();
        Send(name, """{"source":"a","data":{}}""");
        Assert.True(WaitFor(() => { lock (got) return got.Count == 1; }));

        server.Dispose();
        server.Dispose();   // a second Dispose is what `using` plus an explicit stop does

        // Nothing is listening any more, so a client cannot connect.
        using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
        Assert.Throws<TimeoutException>(() => client.Connect(500));
    }

    [Fact]
    public void An_Empty_Line_Is_Not_Offered_To_The_Callback()
    {
        // A producer that ends its stream with a newline, or writes a blank line between records,
        // would otherwise fill the diagnostics ring with rejected-empty entries and hide the real
        // reason its events are not landing.
        var name = UniqueName();
        var got = new List<string>();
        using var server = new EventPipeServer(line => { lock (got) got.Add(line); return true; }, pipeName: name);
        server.Start();

        Send(name, "", "   ", """{"source":"a","data":{}}""");

        Assert.True(WaitFor(() => { lock (got) return got.Count == 1; }), "the real line never arrived");
        lock (got) Assert.Single(got);
    }
}
