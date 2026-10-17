using System.Text;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Syndication;
using FeedConversations.Api.Webmentions;

namespace FeedConversations.UnitTests;

public sealed class SemanticEvidenceTests
{
    private static RemoteEntry Read(string properties, string html = "", string scope = "https://controlled.example/feed") =>
        Assert.Single(new FeedReader().Parse(Encoding.UTF8.GetBytes(
            "<rss version='2.0' xmlns:a='http://www.w3.org/2005/Atom' xmlns:t='http://purl.org/syndication/thread/1.0'>"
            + "<channel><title>Synthetic</title><item><guid isPermaLink='false'>opaque:shared</guid>"
            + "<link>https://controlled.example/post</link>" + properties
            + "<description><![CDATA[" + html + "]]></description></item></channel></rss>"), new Uri(scope)));
    private const string Reply = "<a:category term='https://schema.org/Comment'/><a:link rel='https://schema.org/author' href='/author'/>"
        + "<a:link rel='https://schema.org/parentItem' href='/parent'/>";

    [Theory]
    [InlineData("http://source.scripting.com")]
    [InlineData("http://source.scripting.com/")]
    [InlineData("https://source.scripting.com")]
    [InlineData("https://source.scripting.com/")]
    public void ExactlyDocumentedSourceAliasesSupportThreadingAndMarkdown(string source)
    {
        var entry = Read($"<s:inReplyTo xmlns:s='{source}'>https://controlled.example/parent</s:inReplyTo>"
            + $"<s:markdown xmlns:s='{source}'>Authored Markdown.</s:markdown>");
        Assert.Equal("reply", entry.Kind);
        Assert.Equal("supported", entry.SemanticStatus);
        Assert.Equal("Authored Markdown.", entry.Markdown);
    }

    [Fact]
    public void OpaqueIdentitiesNeverMergeAcrossFeedScopes()
    {
        var a = Read("", scope: "https://controlled.example/a");
        var b = Read("", scope: "https://controlled.example/b");
        Assert.Equal(a.Identity.Guid, b.Identity.Guid);
        Assert.NotEqual(a.Identity.Key, b.Identity.Key);
        Assert.Equal("false", a.Identity.IsPermaLink);
    }

    [Theory]
    [InlineData("<a:link rel='https://schema.org/parentItem' href='/other'/>", "")]
    [InlineData("<t:in-reply-to href='/other'/>", "")]
    [InlineData("", "<article class='h-entry'><a class='u-in-reply-to' href='/other'>Other</a></article>")]
    [InlineData("", "<article class='h-entry'><a class='u-like-of' href='/parent'>Like</a></article>")]
    public void ExplicitXmlMf2OrThreadingConflictIsAmbiguous(string extra, string html) =>
        Assert.Equal("ambiguous", Read(Reply + extra, html).SemanticStatus);

    [Fact]
    public void LikeWithSameTargetThreadingStillCannotBecomeAReply()
    {
        var entry = Read("<a:category term='https://schema.org/LikeAction'/><a:link rel='https://schema.org/agent' href='/author'/>"
            + "<a:link rel='https://schema.org/object' href='/parent'/><t:in-reply-to href='/parent'/>");
        Assert.Equal("ambiguous", entry.SemanticStatus);
    }

    [Fact]
    public void IncompleteProfileDoesNotBorrowUnrelatedObjectLink()
    {
        var entry = Read("<a:category term='https://schema.org/Comment'/><a:link rel='https://schema.org/author' href='/author'/>"
            + "<a:link rel='https://schema.org/object' href='/parent'/>");
        Assert.Equal("incomplete", entry.SemanticStatus);
        Assert.Null(entry.TargetUrl);
        Assert.Equal("unknown", Read("<a:link rel='https://schema.org/object' href='/parent'/>").Kind);
    }

    [Fact]
    public void NestedQuotedRelationsAndMismatchedOuterUidCannotVerify()
    {
        Assert.Equal("unknown", Read("", "<article class='h-entry'><a class='u-url' href='/post'>Self</a>"
            + "<blockquote class='h-entry'><a class='u-like-of' href='/parent'>Quoted</a></blockquote></article>").SemanticStatus);
        var html = "<article class='h-entry'><a class='u-url' href='/forged'>Other source</a>"
            + "<a class='u-in-reply-to' href='/parent'>Claim</a></article>";
        var doc = new RemoteDocument(new Uri("https://controlled.example/post"), 200, "text/html", Encoding.UTF8.GetBytes(html), [],
            DateTime.UtcNow, DateTime.UtcNow, true, null);
        Assert.NotEqual("supported", new WebmentionProtocol().Extract(doc, doc.FinalUrl.AbsoluteUri, "https://controlled.example/parent").Status);
    }

    [Fact]
    public void PlainNativeAtomStaysReadableAndRelativeContentLinksAreResolved()
    {
        const string xml = "<feed xmlns='http://www.w3.org/2005/Atom' xml:base='https://controlled.example/'>"
            + "<title>Ordinary feed</title><entry><id>urn:opaque:entry</id><link href='post'/><title>Plain article</title>"
            + "<published>2026-01-01T12:00:00Z</published><content type='html'>&lt;p&gt;Readable &lt;a href='/more'&gt;link&lt;/a&gt;&lt;/p&gt;</content></entry></feed>";
        var entry = Assert.Single(new FeedReader().Parse(Encoding.UTF8.GetBytes(xml), new Uri("https://controlled.example/feed")));
        Assert.Equal("unknown", entry.Kind);
        Assert.Equal("https://controlled.example/post", entry.Permalink);
        Assert.Contains("https://controlled.example/more", entry.Html);
        Assert.Equal(DateTimeKind.Utc, entry.PublishedAt!.Value.Kind);
    }

    [Fact]
    public void MediaRequiresUserActivatedLinkAndCannotAutoload()
    {
        var safe = SemanticProfile.Sanitize("<video src='/clip'><source src='/other'></video><img src='javascript:x'>",
            new Uri("https://controlled.example/post"));
        Assert.Contains("href=\"https://controlled.example/clip\"", safe);
        Assert.Contains("Open external media", safe);
        Assert.DoesNotContain("<video", safe);
        Assert.DoesNotContain("<img", safe);
        Assert.DoesNotContain("<source", safe);
        Assert.DoesNotContain("javascript:", safe);
    }

    [Fact]
    public void RestoredHtmlDoesNotClaimOriginalRelationshipAuthority()
    {
        var html = "<article class='h-entry' data-origin-authority='unverified-restore'><a class='u-url' href='/post'>Self</a>"
            + "<a class='u-in-reply-to' href='/parent'>Reply</a></article>";
        var doc = new RemoteDocument(new Uri("https://controlled.example/post"), 200, "text/html", Encoding.UTF8.GetBytes(html), [],
            DateTime.UtcNow, DateTime.UtcNow, true, null);
        Assert.Equal("incomplete", new WebmentionProtocol().Extract(doc, doc.FinalUrl.AbsoluteUri, "https://controlled.example/parent").Status);
    }
}
