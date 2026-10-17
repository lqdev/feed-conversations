using System.Text;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using AngleSharp.Dom;
using Ganss.Xss;
using Markdig;
using FeedConversations.Api.Infrastructure;

namespace FeedConversations.Api.Syndication;

public sealed record EntryIdentity(string Kind, string Value, string? FeedUrl = null, string? Guid = null, string? IsPermaLink = null)
{
    public string Key => Kind == "feed-guid"
        ? $"feed:{Networking.UrlPolicy.Key(FeedUrl!)}:{Guid}"
        : $"url:{Networking.UrlPolicy.Key(Value)}";
}
public sealed record RemoteEntry(EntryIdentity Identity, string Permalink, string Kind, string SemanticType,
    string SemanticStatus, string? AuthorUrl, string? AuthorFeedUrl, DateTime? PublishedAt, DateTime? UpdatedAt,
    string? Title, string Html, string? Markdown, string? TargetUrl, string? CommentsUrl, bool OriginAuthority = true);

public static class SemanticProfile
{
    public static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    public static readonly XNamespace Source = "https://source.scripting.com/";
    public static readonly XNamespace Thread = "http://purl.org/syndication/thread/1.0";
    public static readonly XNamespace Provenance = "urn:feed-conversations:provenance:1";
    public const string Schema = "https://schema.org/";
    public static readonly string[] SourceAliases =
        ["https://source.scripting.com/", "https://source.scripting.com", "http://source.scripting.com/", "http://source.scripting.com"];
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    public static string RenderMarkdown(string? markdown) => Sanitize(Markdig.Markdown.ToHtml(markdown ?? "", Pipeline));
    public static string Sanitize(string html, Uri? baseUrl = null)
    {
        var document = new HtmlParser().ParseDocument(html);
        foreach (var media in document.QuerySelectorAll("img,video,audio"))
        {
            var source = media.GetAttribute("src") ?? media.QuerySelector("source[src]")?.GetAttribute("src");
            var url = Resolve(baseUrl, source);
            if (url is null) { media.Remove(); continue; }
            var link = document.CreateElement("a");
            link.SetAttribute("href", url);
            link.TextContent = "Open external media";
            media.Replace(link);
        }
        foreach (var anchor in document.QuerySelectorAll("[href]"))
        {
            var url = Resolve(baseUrl, anchor.GetAttribute("href"));
            if (url is not null) anchor.SetAttribute("href", url);
            else anchor.RemoveAttribute("href");
        }
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Remove("img");
        sanitizer.AllowedTags.Remove("video");
        sanitizer.AllowedTags.Remove("audio");
        sanitizer.AllowedTags.Remove("style");
        sanitizer.AllowedAttributes.Remove("style");
        sanitizer.AllowedAttributes.Remove("class");
        sanitizer.AllowedAttributes.Remove("id");
        return sanitizer.Sanitize(document.Body?.InnerHtml ?? "");
    }

    private static string? Resolve(Uri? baseUrl, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Networking.UrlPolicy.Parse(baseUrl is null ? value : new Uri(baseUrl, value).AbsoluteUri).AbsoluteUri; }
        catch (Exception e) when (e is ApiProblem or UriFormatException) { return null; }
    }

    public static (bool Ambiguous, string? Kind, string? Target) HtmlRelation(string html, string permalink, Uri baseUrl)
    {
        var document = new HtmlParser().ParseDocument(html);
        var entries = document.QuerySelectorAll(".h-entry").Where(e => !e.Ancestors<IElement>().Any(a => a.ClassList.Contains("h-entry"))).ToList();
        IEnumerable<IElement> Scoped(IElement entry, string selector) => entry.QuerySelectorAll(selector)
            .Where(e => e.Ancestors<IElement>().FirstOrDefault(a => a.ClassList.Contains("h-entry")) == entry);
        var matching = entries.Where(entry => Scoped(entry, ".u-url[href]").Any(e =>
            Resolve(baseUrl, e.GetAttribute("href")) is { } actual && Networking.UrlPolicy.Key(actual) == Networking.UrlPolicy.Key(permalink))).ToList();
        var selected = matching.Count == 1 ? matching[0]
            : entries.Count == 1 && !Scoped(entries[0], ".u-url[href]").Any() ? entries[0] : null;
        if (matching.Count > 1) return (true, null, null);
        if (selected is null) return (false, null, null);
        var relations = new List<(string Kind, string Target)>();
        foreach (var (selector, kind) in new[] { (".u-in-reply-to[href]", "reply"), (".u-like-of[href]", "like"), (".u-bookmark-of[href]", "bookmark") })
            foreach (var element in Scoped(selected, selector))
                if (Resolve(baseUrl, element.GetAttribute("href")) is { } target) relations.Add((kind, target));
        var distinct = relations.Distinct().ToList();
        return distinct.Count == 1 ? (false, distinct[0].Kind, distinct[0].Target) : (distinct.Count > 1, null, null);
    }

    public static string PlainText(string html) => new HtmlParser().ParseDocument(html).Body?.TextContent ?? "";

    public static XDocument Xml(byte[] bytes)
    {
        if (bytes.Length > Configuration.ResourceLimits.RemoteBytes) throw ApiProblem.Invalid("xml-too-large");
        using var stream = new MemoryStream(bytes);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = Configuration.ResourceLimits.RemoteBytes, IgnoreComments = true };
        try
        {
            using var reader = XmlReader.Create(stream, settings);
            var count = 0;
            while (reader.Read())
                if (reader.Depth > 64 || reader.NodeType == XmlNodeType.Element && ++count > 100000)
                    throw ApiProblem.Invalid("xml-structure-limit");
            stream.Position = 0;
            using var second = XmlReader.Create(stream, settings);
            return XDocument.Load(second);
        }
        catch (XmlException) { throw ApiProblem.Invalid("invalid-xml"); }
    }

    public static void ValidateText(string? text, int byteLimit, string code)
    {
        if (text is null) return;
        if (Encoding.UTF8.GetByteCount(text) > byteLimit) throw new ApiProblem(413, code, "Text exceeds its byte limit.");
        try { XmlConvert.VerifyXmlChars(text); }
        catch (XmlException) { throw ApiProblem.Invalid("illegal-xml-character"); }
    }
}
