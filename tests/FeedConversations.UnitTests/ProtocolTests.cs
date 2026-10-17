using System.Net;
using System.Text;
using System.Xml.Linq;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Posts;
using FeedConversations.Api.Syndication;
using FeedConversations.Api.Webmentions;

namespace FeedConversations.UnitTests;

public sealed class ProtocolTests
{
    [Fact]
    public void IndependentFixtureWalkerAssertsExactNamespacesAndCardinality()
    {
        var doc = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "fixtures", "application-four-kinds.xml"));
        XNamespace atom = "http://www.w3.org/2005/Atom";
        XNamespace source = "https://source.scripting.com/";
        var expected = new[] { "SocialMediaPosting", "Comment", "LikeAction", "BookmarkAction" };
        var items = doc.Root!.Element("channel")!.Elements("item").ToArray();
        Assert.Equal(4, items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var item = items[i];
            Assert.Equal("https://schema.org/" + expected[i], (string?)Assert.Single(item.Elements(atom + "category")).Attribute("term"));
            var role = i >= 2 ? "agent" : "author";
            Assert.Single(item.Elements(atom + "link"), l => (string?)l.Attribute("rel") == "https://schema.org/" + role);
            Assert.NotNull(item.Element(source + "comments")?.Attribute("feedUrl"));
            Assert.True(Uri.IsWellFormedUriString(item.Element("link")!.Value, UriKind.Absolute));
        }
    }

    [Fact]
    public void ReaderRecognizesFiveKindsAndPreservesOpaqueGuid()
    {
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures", "publisher-five-kinds.xml"));
        var entries = new FeedReader().Parse(bytes, new Uri("https://publisher.example/rss.xml"));
        Assert.Equal(new[] { "bookmark", "like", "post", "post", "reply" }, entries.Select(e => e.Kind).Order());
        var article = Assert.Single(entries, e => e.Identity.Guid == "opaque:article");
        Assert.Equal("https://schema.org/BlogPosting", article.SemanticType);
        Assert.All(entries, e => Assert.Equal("supported", e.SemanticStatus));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.1.1")]
    [InlineData("192.168.0.1")]
    [InlineData("::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("fc00::1")]
    [InlineData("fe80::1")]
    [InlineData("224.1.2.3")]
    public void DestinationPolicyRejectsNonPublicAddresses(string ip) => Assert.False(UrlPolicy.PublicAddress(IPAddress.Parse(ip)));

    [Theory]
    [InlineData("http://user:secret@example.org/")]
    [InlineData("file:///etc/passwd")]
    [InlineData("https://example.org/a b")]
    [InlineData("https://example.org/%zz")]
    [InlineData("https://example.org\\private")]
    public void UrlValidationRejectsUnsafeSpellings(string url) => Assert.Throws<ApiProblem>(() => UrlPolicy.Parse(url));

    [Fact]
    public void UrlKeyDoesNotFoldPathOrDropFragment()
    {
        Assert.Equal(UrlPolicy.Key("HTTPS://EXAMPLE.ORG:443/A?X=1#f"), UrlPolicy.Key("https://example.org/A?X=1#f"));
        Assert.NotEqual(UrlPolicy.Key("https://example.org/A"), UrlPolicy.Key("https://example.org/a"));
        Assert.NotEqual(UrlPolicy.Key("https://example.org/A#x"), UrlPolicy.Key("https://example.org/A"));
    }

    [Fact]
    public void XmlRejectsDoctypeAndDeepStructures()
    {
        Assert.Throws<ApiProblem>(() => SemanticProfile.Xml(Encoding.UTF8.GetBytes("<!DOCTYPE rss [<!ENTITY x SYSTEM 'file:///secret'>]><rss>&x;</rss>")));
        Assert.Throws<ApiProblem>(() => SemanticProfile.Xml(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("<x>", 66)) + string.Concat(Enumerable.Repeat("</x>", 66)))));
    }

    [Fact]
    public void SanitizationDropsMediaScriptsAndProtocolClasses()
    {
        var safe = SemanticProfile.Sanitize("<script>alert(1)</script><img src='https://evil.example/x'><p class='u-like-of' onclick='x()'>Text</p>");
        Assert.DoesNotContain("<script", safe);
        Assert.DoesNotContain("<img", safe);
        Assert.DoesNotContain("onclick", safe);
        Assert.DoesNotContain("u-like-of", safe);
    }

    [Fact]
    public void NestedQuotedEntryCannotForgeReply()
    {
        const string html = "<article class='h-entry'><a class='u-url' href='https://s.example/post'>Self</a><a href='https://t.example/root'>Mention</a><blockquote class='h-entry'><a class='u-in-reply-to' href='https://t.example/root'>Quoted</a></blockquote></article>";
        var doc = new RemoteDocument(new Uri("https://s.example/post"), 200, "text/html", Encoding.UTF8.GetBytes(html), [],
            DateTime.UtcNow, DateTime.UtcNow, true, null);
        var result = new WebmentionProtocol().Extract(doc, "https://s.example/post", "https://t.example/root");
        Assert.True(result.Backlink);
        Assert.Equal("mention", result.Kind);
    }

    [Fact]
    public void AuthorLimitsRejectExactOverflow()
    {
        PostService.ValidateContent("post", null, new string('x', 256 * 1024));
        Assert.Throws<ApiProblem>(() => PostService.ValidateContent("post", null, new string('x', 256 * 1024 + 1)));
        PostService.ValidateContent("post", new string('x', 500), "Body");
        Assert.Throws<ApiProblem>(() => PostService.ValidateContent("post", new string('x', 501), "Body"));
        Assert.Throws<ApiProblem>(() => PostService.ValidateContent("like", "", null));
    }
}
