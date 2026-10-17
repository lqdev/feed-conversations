using System.Globalization;
using System.Xml.Linq;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;

namespace FeedConversations.Api.Syndication;

public sealed class FeedReader
{
    public IReadOnlyList<RemoteEntry> Parse(byte[] bytes, Uri documentUrl)
    {
        var doc = SemanticProfile.Xml(bytes);
        var atom = doc.Root?.Name == SemanticProfile.Atom + "feed";
        var elements = atom ? doc.Root!.Elements(SemanticProfile.Atom + "entry")
            : doc.Root?.Element("channel")?.Elements("item");
        if (elements is null) throw ApiProblem.Invalid("unsupported-feed");
        return elements.Select(e => ParseEntry(e, atom, documentUrl)).OrderByDescending(e => e.PublishedAt)
            .ThenBy(e => e.Identity.Key, StringComparer.Ordinal).Take(50).ToList();
    }

    private static RemoteEntry ParseEntry(XElement item, bool nativeAtom, Uri documentUrl)
    {
        XNamespace core = nativeAtom ? SemanticProfile.Atom : "";
        var baseUrl = documentUrl;
        foreach (var ancestor in item.AncestorsAndSelf().Reverse())
            if ((string?)ancestor.Attribute(XNamespace.Xml + "base") is { } xmlBase)
            {
                if (!Uri.TryCreate(baseUrl, xmlBase, out var resolvedBase)) throw ApiProblem.Invalid("invalid-xml-base");
                baseUrl = resolvedBase;
            }
        string? Resolve(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            try { return UrlPolicy.Parse(new Uri(baseUrl, value.Trim()).AbsoluteUri).AbsoluteUri; }
            catch (Exception e) when (e is ApiProblem or UriFormatException) { return null; }
        }
        var links = item.Elements(SemanticProfile.Atom + "link").ToList();
        var permalink = nativeAtom
            ? Resolve((string?)links.FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href"))
            : Resolve(item.Element("link")?.Value);
        if (permalink is null) throw ApiProblem.Invalid("feed-item-link");
        var guidNode = item.Element(core + (nativeAtom ? "id" : "guid"));
        var guid = guidNode?.Value ?? permalink;
        if (guid.Length is 0 or > 4096) throw ApiProblem.Invalid("feed-guid-limit");
        var identity = new EntryIdentity("feed-guid", guid, documentUrl.AbsoluteUri, guid, nativeAtom ? "false" : (string?)guidNode?.Attribute("isPermaLink"));
        var types = item.Elements(SemanticProfile.Atom + "category")
            .Select(c => (string?)c.Attribute("term"))
            .Where(t => t is SemanticProfile.Schema + "BlogPosting" or SemanticProfile.Schema + "SocialMediaPosting"
                or SemanticProfile.Schema + "Comment" or SemanticProfile.Schema + "LikeAction" or SemanticProfile.Schema + "BookmarkAction")
            .Distinct().ToList();
        var type = types.Count == 1 ? types[0]! : "unknown";
        var kind = type switch
        {
            SemanticProfile.Schema + "BlogPosting" or SemanticProfile.Schema + "SocialMediaPosting" => "post",
            SemanticProfile.Schema + "Comment" => "reply",
            SemanticProfile.Schema + "LikeAction" => "like",
            SemanticProfile.Schema + "BookmarkAction" => "bookmark",
            _ => "unknown"
        };
        var role = kind is "like" or "bookmark" ? "agent" : "author";
        var authors = links.Where(l => (string?)l.Attribute("rel") == SemanticProfile.Schema + role)
            .Select(l => Resolve((string?)l.Attribute("href"))).Where(s => s is not null).Distinct().ToList();
        var targets = links.Where(l => (string?)l.Attribute("rel") == SemanticProfile.Schema + (kind == "reply" ? "parentItem" : "object"))
            .Select(l => Resolve((string?)l.Attribute("href"))).Where(s => s is not null).Distinct().ToList();
        var target = targets.Count == 1 ? targets[0] : null;
        var status = types.Count > 1 || authors.Count > 1 || targets.Count > 1 ? "ambiguous"
            : types.Count == 0 ? "unknown"
            : authors.Count != 1 || kind is "reply" or "like" or "bookmark" && target is null ? "incomplete" : "supported";
        var hints = item.Elements().Where(e => SemanticProfile.SourceAliases.Contains(e.Name.NamespaceName)
            && e.Name.LocalName == "inReplyTo" && (string?)e.Attribute("isPermaLink") != "false")
            .Select(e => Resolve(e.Value)).Concat(item.Elements(SemanticProfile.Thread + "in-reply-to")
                .Select(e => Resolve((string?)e.Attribute("href") ?? (string?)e.Attribute("ref"))))
            .Where(s => s is not null).Distinct().ToList();
        if (hints.Count > 1 || hints.Count == 1 && target is not null && UrlPolicy.Key(hints[0]!) != UrlPolicy.Key(target))
            status = "ambiguous";
        if (hints.Count > 0 && types.Count > 0 && kind != "reply") status = "ambiguous";
        if (types.Count == 0 && hints.Count == 1) { kind = "reply"; target = hints[0]; status = "supported"; }
        var markdown = item.Elements().FirstOrDefault(e => SemanticProfile.SourceAliases.Contains(e.Name.NamespaceName)
            && e.Name.LocalName == "markdown")?.Value;
        var html = item.Element(core + (nativeAtom ? "content" : "description"))?.Value
            ?? item.Element(core + "summary")?.Value ?? "";
        var htmlRelation = SemanticProfile.HtmlRelation(html, permalink, baseUrl);
        if (htmlRelation.Ambiguous || htmlRelation.Kind is not null && (types.Count > 0 || hints.Count > 0)
            && (kind != htmlRelation.Kind || target is not null && UrlPolicy.Key(target) != UrlPolicy.Key(htmlRelation.Target!)))
            status = "ambiguous";
        if (types.Count == 0 && hints.Count == 0 && status != "ambiguous" && htmlRelation.Kind is not null)
        { kind = htmlRelation.Kind; target = htmlRelation.Target; status = "supported"; }
        var comments = item.Elements().FirstOrDefault(e => SemanticProfile.SourceAliases.Contains(e.Name.NamespaceName)
            && e.Name.LocalName == "comments");
        var source = item.Element("source");
        return new RemoteEntry(identity, permalink, kind, type, status, authors.SingleOrDefaultSafe(),
            Resolve((string?)source?.Attribute("url")), Date(item.Element(core + (nativeAtom ? "published" : "pubDate"))?.Value),
            Date(item.Element(SemanticProfile.Atom + "updated")?.Value), item.Element(core + "title")?.Value,
            SemanticProfile.Sanitize(html, baseUrl), markdown, target, Resolve((string?)comments?.Attribute("feedUrl")),
            (string?)item.Attribute(SemanticProfile.Provenance + "authority") != "unverified-restore");
    }

    private static DateTime? Date(string? text) => DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
        DateTimeStyles.AllowWhiteSpaces, out var result) ? result.UtcDateTime : null;
}

internal static class SemanticLists
{
    public static T? SingleOrDefaultSafe<T>(this IReadOnlyList<T> values) => values.Count == 1 ? values[0] : default;
}
