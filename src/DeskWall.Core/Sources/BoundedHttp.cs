using System.Buffers;
using System.Text;

namespace DeskWall.Core.Sources;

/// <summary>Reading an HTTP response body with a ceiling on how many bytes are accepted.
/// <para>
/// Content-Length is not a bound: it is absent on every chunked response, and HttpContent's own
/// buffer ceiling is 2 GB, so checking the length of the string ReadAsStringAsync just produced is
/// a guard that fires after the damage. These read the stream and stop counting bytes the moment
/// the cap is passed, so a mistyped URL that serves something enormous costs one buffer, not
/// gigabytes of RAM or of the disk headroom this tool exists to report on.
/// </para>
/// <para>The <c>what</c> label reaches exception messages, so callers pass the URL *template*,
/// never a URL with its secrets substituted in.</para></summary>
internal static class BoundedHttp
{
    private const int ChunkBytes = 81920;

    /// <summary>The body as text, up to <paramref name="max"/> bytes. A byte-order mark is honoured
    /// and stripped; anything else is read as UTF-8, as HttpContent's own default is.</summary>
    internal static async Task<string> ReadStringAsync(HttpContent content, long max, string what, CancellationToken ct)
    {
        if (content.Headers.ContentLength > max) throw new HttpRequestException($"body over {max} bytes from {what}");
        using var buffer = new MemoryStream();
        await using (var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await CopyAsync(stream, buffer, max, what, ct).ConfigureAwait(false);
        buffer.Position = 0;
        using var reader = new StreamReader(buffer, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Copy at most <paramref name="max"/> bytes, throwing the moment one more arrives.
    /// The destination keeps whatever was written before the throw; callers delete their partial file.</summary>
    internal static async Task CopyAsync(Stream from, Stream to, long max, string what, CancellationToken ct)
    {
        var buf = ArrayPool<byte>.Shared.Rent(ChunkBytes);
        try
        {
            long total = 0;
            int n;
            while ((n = await from.ReadAsync(buf.AsMemory(0, buf.Length), ct).ConfigureAwait(false)) > 0)
            {
                total += n;
                if (total > max) throw new HttpRequestException($"body over {max} bytes from {what}");
                await to.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buf); }
    }
}
