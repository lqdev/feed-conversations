using System.Text;
using System.Xml.Linq;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;

namespace FeedConversations.Api.Syndication;

public sealed record OpmlEntry(string Url, string Label, string? Group);
public sealed class OpmlCodec
{
    public IReadOnlyList<OpmlEntry> Parse(string text)
    {
        var doc = SemanticProfile.Xml(Encoding.UTF8.GetBytes(text));
        if (doc.Root?.Name != "opml" || doc.Root.Element("body") is null) throw ApiProblem.Invalid("invalid-opml");
        var entries = new List<OpmlEntry>();
        foreach (var element in doc.Root.Element("body")!.Descendants("outline"))
        {
            var url = (string?)element.Attribute("xmlUrl");
            if (url is null) continue;
            UrlPolicy.Parse(url);
            var label = (string?)element.Attribute("text") ?? (string?)element.Attribute("title") ?? url;
            var group = string.Join("/", element.Ancestors("outline").Reverse().Select(e => (string?)e.Attribute("text") ?? ""));
            if (label.Length > 200 || group.Length > 500) throw ApiProblem.Invalid("opml-label-too-long");
            entries.Add(new OpmlEntry(url, label, string.IsNullOrEmpty(group) ? null : group));
        }
        var distinct = entries.DistinctBy(e => UrlPolicy.Key(e.Url)).ToList();
        if (distinct.Count > 100) throw new ApiProblem(413, "opml-entry-limit", "At most 100 subscriptions can be imported.");
        return distinct;
    }
    public string Write(IEnumerable<OpmlEntry> entries) => new XDocument(new XElement("opml", new XAttribute("version", "2.0"),
        new XElement("head", new XElement("title", "Private subscriptions")),
        new XElement("body", entries.GroupBy(e => e.Group).Select(group =>
        {
            var outlines = group.Select(e => new XElement("outline", new XAttribute("type", "rss"),
                new XAttribute("text", e.Label), new XAttribute("xmlUrl", e.Url)));
            return group.Key is null ? outlines : [new XElement("outline", new XAttribute("text", group.Key), outlines)];
        })))).ToString();
}
