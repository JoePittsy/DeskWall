using DeskWall.Core.Sources;
using DeskWall.Core.Values;
using Xunit;

public class RssSourceTests
{
    private const string Rss = """
    <?xml version="1.0"?><rss version="2.0"><channel><title>Blog</title><link>https://blog/</link>
      <item><title>First &amp; foremost</title><link>https://blog/1</link><pubDate>Sun, 20 Sep 2026 10:00:00 GMT</pubDate><description>&lt;p&gt;Hello &lt;b&gt;world&lt;/b&gt;&lt;/p&gt;</description><author>joe@x</author></item>
      <item><title>Second</title><link>https://blog/2</link><pubDate>not a date</pubDate></item>
      <item><title>Third</title><link>https://blog/3</link></item>
    </channel></rss>
    """;

    private const string Atom = """
    <?xml version="1.0"?><feed xmlns="http://www.w3.org/2005/Atom"><title>Releases</title><link rel="self" href="https://gh/feed"/><link rel="alternate" href="https://gh/"/>
      <entry><title>v1.2</title><link rel="alternate" href="https://gh/v1.2"/><updated>2026-09-19T08:30:00Z</updated><summary>Bug fixes</summary><author><name>Joe</name></author></entry>
      <entry><title>v1.1</title><link href="https://gh/v1.1"/><published>2026-09-01T00:00:00Z</published><content type="html">&lt;ul&gt;&lt;li&gt;Thing&lt;/li&gt;&lt;/ul&gt;</content></entry>
    </feed>
    """;

    [Fact]
    public void Parses_Rss2_With_Entities_Html_Stripping_And_Bad_Dates()
    {
        var v = RssSource.ParseFeed(Rss, 20);
        Assert.Equal("Blog", ((TextValue)v.Get("title")!).Text);
        Assert.Equal("https://blog/", ((TextValue)v.Get("link")!).Text);
        var items = (ListValue)v.Get("items")!;
        Assert.Equal("link", items.KeyField);
        Assert.Equal(3, items.Items.Count);
        var first = items.Items[0];
        Assert.Equal("First & foremost", ((TextValue)first.Get("title")!).Text);
        Assert.Equal("Hello world", ((TextValue)first.Get("summary")!).Text);
        Assert.Equal(new DateTimeOffset(2026, 9, 20, 10, 0, 0, TimeSpan.Zero), ((TimeValue)first.Get("published")!).Time);
        Assert.Equal("joe@x", ((TextValue)first.Get("author")!).Text);
        Assert.Null(items.Items[1].Get("published"));
        Assert.Null(items.Items[2].Get("summary"));
        Assert.Same(items.Items[1], items.ByKey("https://blog/2"));
    }

    [Fact]
    public void Parses_Atom_With_Alternate_Links_And_Content_Fallback()
    {
        var v = RssSource.ParseFeed(Atom, 20);
        Assert.Equal("Releases", ((TextValue)v.Get("title")!).Text);
        Assert.Equal("https://gh/", ((TextValue)v.Get("link")!).Text);
        var items = (ListValue)v.Get("items")!;
        Assert.Equal("https://gh/v1.2", ((TextValue)items.Items[0].Get("link")!).Text);
        Assert.Equal("Joe", ((TextValue)items.Items[0].Get("author")!).Text);
        Assert.Equal(new DateTimeOffset(2026, 9, 19, 8, 30, 0, TimeSpan.Zero), ((TimeValue)items.Items[0].Get("published")!).Time);
        Assert.Equal("Thing", ((TextValue)items.Items[1].Get("summary")!).Text);
        Assert.Equal("https://gh/v1.1", ((TextValue)items.Items[1].Get("link")!).Text);
    }

    [Fact]
    public void Max_Caps_Items_And_Garbage_Throws()
    {
        Assert.Single(((ListValue)RssSource.ParseFeed(Rss, 1).Get("items")!).Items);
        Assert.ThrowsAny<Exception>(() => RssSource.ParseFeed("<html>not a feed</html>", 5));
        Assert.ThrowsAny<Exception>(() => RssSource.ParseFeed("<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///c:/x'>]><rss><channel><title>&e;</title></channel></rss>", 5));
    }
}
