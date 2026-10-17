using System.Text;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
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
    string? Title, string Html, string? Markdown, string? TargetUrl, string? CommentsUrl);

public static class SemanticProfile
{
    public static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    public static readonly XNamespace Source = "https://source.scripting.com/";
    public static readonly XNamespace Thread = "http://purl.org/syndication/thread/1.0";
    public const string Schema = "https://schema.org/";
    public static readonly string[] SourceAliases =
        ["https://source.scripting.com/", "https://source.scripting.com", "http://source.scripting.com/", "http://source.scripting.com"];
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().DisableHtml().Build();

    public static string RenderMarkdown(string? markdown) => Sanitize(Markdig.Markdown.ToHtml(markdown ?? "", Pipeline));
    public static string Sanitize(string html)
    {
        var sanitizer = new HtmlSanitizer();
        sanitizer.AllowedTags.Remove("img");
        sanitizer.AllowedTags.Remove("video");
        sanitizer.AllowedTags.Remove("audio");
        sanitizer.AllowedTags.Remove("style");
        sanitizer.AllowedAttributes.Remove("style");
        sanitizer.AllowedAttributes.Remove("class");
        sanitizer.AllowedAttributes.Remove("id");
        return sanitizer.Sanitize(html);
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
