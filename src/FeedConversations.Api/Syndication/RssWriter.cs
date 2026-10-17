using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using FeedConversations.Api.Data;
using FeedConversations.Api.Posts;
using FeedConversations.Api.PublicDocuments;

namespace FeedConversations.Api.Syndication;

public sealed class RssWriter
{
    public XElement Owned(Post p, string baseUrl, int count)
    {
        var item = new XElement("item",
            new XElement("guid", p.Guid), new XElement("link", p.Permalink),
            new XElement("pubDate", p.PublishedAt.ToString("R", CultureInfo.InvariantCulture)),
            new XElement(SemanticProfile.Atom + "updated", p.UpdatedAt.ToString("O")),
            new XElement(SemanticProfile.Atom + "category", new XAttribute("term", SemanticProfile.Schema + Type(p.Kind))),
            new XElement(SemanticProfile.Atom + "link",
                new XAttribute("rel", SemanticProfile.Schema + (p.Kind is "like" or "bookmark" ? "agent" : "author")),
                new XAttribute("href", $"{baseUrl}/users/{p.Owner.UserName}")),
            new XElement(SemanticProfile.Source + "comments", new XAttribute("count", count),
                new XAttribute("feedUrl", $"{p.Permalink}/comments.xml")),
            new XElement("description", PublicationHtml.Entry(p, baseUrl)));
        if (p.Guid != p.Permalink) item.Element("guid")!.SetAttributeValue("isPermaLink", "false");
        if (p.Provenance is not null) item.SetAttributeValue(SemanticProfile.Provenance + "authority", "unverified-restore");
        if (p.Title is not null) item.Add(new XElement("title", p.Title));
        if (p.TargetUrl is not null)
        {
            item.Add(new XElement(SemanticProfile.Atom + "link",
                new XAttribute("rel", SemanticProfile.Schema + (p.Kind == "reply" ? "parentItem" : "object")),
                new XAttribute("href", p.TargetUrl)));
            if (p.Kind == "reply")
            {
                var identity = JsonSerializer.Deserialize<TargetIdentity>(p.TargetIdentityJson!, Infrastructure.Wire.Json)!;
                var opaque = identity.Kind == "feed-guid";
                item.Add(new XElement(SemanticProfile.Source + "inReplyTo",
                    opaque ? new XAttribute("isPermaLink", "false") : null, opaque ? identity.Guid : p.TargetUrl));
                item.Add(new XElement(SemanticProfile.Thread + "in-reply-to",
                    !opaque ? new XAttribute("ref", p.TargetUrl) : null, new XAttribute("href", p.TargetUrl)));
            }
        }
        if (p.Kind != "like")
        {
            var context = p.TargetUrl is null ? "" : $"{PublicationHtml.Label(p.Kind)} <{p.TargetUrl}>\n\n";
            item.Add(new XElement(SemanticProfile.Source + "markdown", context + (p.Markdown ?? "")));
        }
        return item;
    }
    public XElement Remote(RemoteEntry entry)
    {
        var item = new XElement("item", new XElement("guid",
            entry.Identity.IsPermaLink is null ? null : new XAttribute("isPermaLink", entry.Identity.IsPermaLink), entry.Identity.Guid ?? entry.Identity.Value),
            new XElement("link", entry.Permalink), new XElement("description",
                (entry.TargetUrl is null || entry.Kind is not ("reply" or "like" or "bookmark") ? ""
                    : $"<p>{PublicationHtml.Label(entry.Kind)} <a href=\"{PublicationHtml.E(entry.TargetUrl)}\">this page</a>.</p>") + entry.Html));
        if (entry.Title is not null) item.Add(new XElement("title", entry.Title));
        if (entry.PublishedAt is { } published) item.Add(new XElement("pubDate", published.ToString("R", CultureInfo.InvariantCulture)));
        if (entry.UpdatedAt is { } updated) item.Add(new XElement(SemanticProfile.Atom + "updated", updated.ToString("O")));
        if (entry.SemanticStatus == "supported")
        {
            item.Add(new XElement(SemanticProfile.Atom + "category", new XAttribute("term",
                entry.SemanticType == SemanticProfile.Schema + "BlogPosting" ? entry.SemanticType : SemanticProfile.Schema + Type(entry.Kind))));
            if (entry.AuthorUrl is not null) item.Add(new XElement(SemanticProfile.Atom + "link",
                new XAttribute("rel", SemanticProfile.Schema + (entry.Kind is "like" or "bookmark" ? "agent" : "author")), new XAttribute("href", entry.AuthorUrl)));
            if (entry.TargetUrl is not null) item.Add(new XElement(SemanticProfile.Atom + "link",
                new XAttribute("rel", SemanticProfile.Schema + (entry.Kind == "reply" ? "parentItem" : "object")), new XAttribute("href", entry.TargetUrl)));
        }
        if (entry.CommentsUrl is not null) item.Add(new XElement(SemanticProfile.Source + "comments", new XAttribute("feedUrl", entry.CommentsUrl)));
        if (entry.AuthorFeedUrl is not null) item.Add(new XElement("source", new XAttribute("url", entry.AuthorFeedUrl), "Origin author"));
        if (!entry.OriginAuthority) item.SetAttributeValue(SemanticProfile.Provenance + "authority", "unverified-restore");
        return item;
    }
    public string Write(string title, string page, string self, DateTime changedAt, IEnumerable<XElement> items) =>
        new XDocument(new XDeclaration("1.0", "utf-8", null),
            new XElement("rss", new XAttribute("version", "2.0"),
                new XAttribute(XNamespace.Xmlns + "atom", SemanticProfile.Atom),
                new XAttribute(XNamespace.Xmlns + "source", SemanticProfile.Source),
                new XAttribute(XNamespace.Xmlns + "thr", SemanticProfile.Thread),
                new XElement("channel", new XElement("title", title), new XElement("link", page),
                    new XElement("description", "Public publications and known approved direct replies"),
                    new XElement("lastBuildDate", changedAt.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture)),
                    new XElement(SemanticProfile.Atom + "link", new XAttribute("rel", "self"),
                        new XAttribute("type", "application/rss+xml"), new XAttribute("href", self)), items)))
            .ToString(SaveOptions.DisableFormatting);
    public static string Type(string kind) => kind switch
    { "reply" => "Comment", "like" => "LikeAction", "bookmark" => "BookmarkAction", _ => "SocialMediaPosting" };
}
