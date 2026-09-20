using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using DeskWall.Core.Layout;
using DeskWall.Core.Values;

namespace DeskWall.Core.Sources;

/// <summary>Settings: url (required); every (default 900); timeout (default 10); max (items, default 20).
/// Publishes: title (TextValue), link (TextValue), items (ListValue keyed by "link") with fields
/// title, link, published (TimeValue, or omitted), summary (TextValue, HTML tags stripped, max 500 chars), author (TextValue, or omitted).
/// Accepts RSS 2.0 (channel/item, pubDate) and Atom 1.0 (feed/entry, published|updated, link[@rel=alternate|none]/@href).</summary>
public sealed partial class RssSource(string name, TimeSpan every, TimeSpan timeout, string urlTemplate, int max, Secrets secrets, HttpMessageHandler? handler = null)
    : AsyncSource(name, every, timeout)
{
    /// <summary>A feed is text; 4 MB is a very large one. BoundedHttp says why a body needs a cap.</summary>
    private const long MaxBody = 4 * 1024 * 1024;
    private static readonly HttpClient s_shared = new(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    private readonly HttpClient _client = handler is null ? s_shared : new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    /// <summary>The hard ceiling on one fetch, as a multiple of the source's own timeout: the token
    /// AsyncSource hands FetchAsync is never cancelled, so without this a stalled body keeps the
    /// source in flight - and so failing every tick - for the life of the daemon.</summary>
    private TimeSpan HardCeiling => Timeout * 6;

    public static RssSource FromDef(SourceDef def, Secrets secrets, HttpMessageHandler? handler = null)
    {
        var s = def.Settings;
        if (!s.TryGetValue("url", out var url) || string.IsNullOrWhiteSpace(url)) throw new ArgumentException($"rss source '{def.Name}' needs settings.url");
        var timeout = TimeSpan.FromSeconds(s.TryGetValue("timeout", out var t) && double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var ts) ? ts : 10);
        var max = s.TryGetValue("max", out var m) && int.TryParse(m, out var mi) && mi > 0 ? mi : 20;
        return new RssSource(def.Name, TimeSpan.FromSeconds(def.EverySeconds ?? 900), timeout, url, max, secrets, handler);
    }

    protected override async Task<RecordValue> FetchAsync(CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, secrets.Substitute(urlTemplate));
        req.Headers.UserAgent.ParseAdd("DeskWall/1.0");
        using var hard = CancellationTokenSource.CreateLinkedTokenSource(ct);
        hard.CancelAfter(HardCeiling);
        ct = hard.Token;
        using var res = await _client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!res.IsSuccessStatusCode) throw new HttpRequestException($"{(int)res.StatusCode} from {urlTemplate}");
        return ParseFeed(await BoundedHttp.ReadStringAsync(res.Content, MaxBody, urlTemplate, ct), max);
    }

    /// <summary>Pure parser, exposed for tests and for FileSource users who point it at a saved feed.</summary>
    public static RecordValue ParseFeed(string xml, int max)
    {
        var doc = new XmlDocument { XmlResolver = null };
        using (var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
            doc.Load(reader);
        var root = doc.DocumentElement ?? throw new FormatException("empty feed");
        return root.LocalName switch
        {
            "rss" => Rss2(root, max),
            "feed" => Atom(root, max),
            _ => throw new FormatException($"not an RSS or Atom feed (root <{root.LocalName}>)"),
        };
    }

    private static RecordValue Rss2(XmlElement rss, int max)
    {
        var channel = rss["channel"] ?? throw new FormatException("rss without channel");
        var items = new List<RecordValue>();
        foreach (XmlElement item in channel.GetElementsByTagName("item"))
        {
            if (items.Count >= max) break;
            var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
            Put(d, "title", Text(item["title"]));
            Put(d, "link", Text(item["link"]));
            Put(d, "summary", Strip(Text(item["description"])));
            Put(d, "author", Text(item["author"]) ?? Text(item["dc:creator"]));
            if (Date(Text(item["pubDate"])) is { } p) d["published"] = new TimeValue(p);
            items.Add(new RecordValue(d));
        }
        var feed = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase) { ["items"] = new ListValue(items, "link") };
        Put(feed, "title", Text(channel["title"]));
        Put(feed, "link", Text(channel["link"]));
        return new RecordValue(feed);
    }

    private static RecordValue Atom(XmlElement feedEl, int max)
    {
        var items = new List<RecordValue>();
        foreach (var entry in Children(feedEl, "entry"))
        {
            if (items.Count >= max) break;
            var d = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase);
            Put(d, "title", Text(Child(entry, "title")));
            Put(d, "link", AtomLink(entry));
            Put(d, "summary", Strip(Text(Child(entry, "summary")) ?? Text(Child(entry, "content"))));
            Put(d, "author", Text(Child(Child(entry, "author"), "name")));
            if (Date(Text(Child(entry, "published")) ?? Text(Child(entry, "updated"))) is { } p) d["published"] = new TimeValue(p);
            items.Add(new RecordValue(d));
        }
        var feed = new Dictionary<string, Value>(StringComparer.OrdinalIgnoreCase) { ["items"] = new ListValue(items, "link") };
        Put(feed, "title", Text(Child(feedEl, "title")));
        Put(feed, "link", AtomLink(feedEl));
        return new RecordValue(feed);
    }

    private static IEnumerable<XmlElement> Children(XmlElement? parent, string localName)
        => parent is null ? [] : parent.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == localName);
    private static XmlElement? Child(XmlElement? parent, string localName) => Children(parent, localName).FirstOrDefault();

    private static string? AtomLink(XmlElement el)
    {
        var links = Children(el, "link").ToList();
        var pick = links.FirstOrDefault(l => l.GetAttribute("rel") is "alternate" or "") ?? links.FirstOrDefault(l => l.GetAttribute("rel") != "self");
        var href = pick?.GetAttribute("href");
        return string.IsNullOrEmpty(href) ? null : href;
    }

    private static string? Text(XmlElement? e)
    {
        var t = e?.InnerText?.Trim();
        return string.IsNullOrEmpty(t) ? null : t;
    }

    private static void Put(Dictionary<string, Value> d, string k, string? v) { if (v is not null) d[k] = new TextValue(v); }

    private static DateTimeOffset? Date(string? s)
    {
        if (s is null) return null;
        if (DateTimeOffset.TryParseExact(s, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var r)) return r;
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var any)) return any;
        return null;
    }

    [GeneratedRegex("<[^>]+>")] private static partial Regex Tags();
    [GeneratedRegex(@"\s+")] private static partial Regex Spaces();

    private static string? Strip(string? html)
    {
        if (html is null) return null;
        var t = Spaces().Replace(WebUtility.HtmlDecode(Tags().Replace(html, " ")), " ").Trim();
        if (t.Length > 500) t = t[..500];
        return t.Length == 0 ? null : t;
    }
}
