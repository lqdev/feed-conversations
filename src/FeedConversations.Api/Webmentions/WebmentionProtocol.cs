using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Syndication;

namespace FeedConversations.Api.Webmentions;

public sealed record SourceEvidence(bool Backlink, string Status, string Kind, string? AuthorUrl,
    string? SingletonUrl, string? AuthorFeedUrl, string? CommentsUrl, RemoteEntry? Entry);

public sealed partial class WebmentionProtocol
{
    public string? Discover(RemoteDocument document)
    {
        foreach (var header in document.LinkHeaders)
            foreach (Match match in LinkHeader().Matches(header))
                if (match.Groups["rel"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("webmention"))
                    return Resolve(document.FinalUrl, match.Groups["url"].Value);
        var html = new HtmlParser().ParseDocument(Encoding.UTF8.GetString(document.Bytes));
        var baseUrl = DocumentBase(html, document.FinalUrl);
        var link = html.QuerySelectorAll("link[href],a[href]").FirstOrDefault(e =>
            (e.GetAttribute("rel") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("webmention"));
        return Resolve(baseUrl, link?.GetAttribute("href"));
    }

    public SourceEvidence Extract(RemoteDocument document, string source, string target)
    {
        var html = new HtmlParser().ParseDocument(Encoding.UTF8.GetString(document.Bytes));
        var baseUrl = DocumentBase(html, document.FinalUrl);
        bool Matches(IElement e, string url) => Resolve(baseUrl, e.GetAttribute("href")) is { } actual && UrlPolicy.Key(actual) == UrlPolicy.Key(url);
        var entries = html.QuerySelectorAll(".h-entry").Where(e => !e.Ancestors<IElement>().Any(a => a.ClassList.Contains("h-entry"))).ToList();
        var matching = entries.Where(e => Scoped(e, ".u-url[href]").Any(a => Matches(a, source))).ToList();
        var entry = matching.Count == 1 ? matching[0] : entries.Count == 1 && UrlPolicy.Key(source) == UrlPolicy.Key(document.FinalUrl.AbsoluteUri) ? entries[0] : null;
        var backlink = entry is not null ? Scoped(entry, "a[href],link[href]").Any(a => Matches(a, target))
            : html.QuerySelectorAll("a[href],link[href]").Any(a => Matches(a, target));
        if (entry is null || !backlink)
            return new SourceEvidence(backlink, matching.Count > 1 ? "ambiguous" : "unknown", "mention", null, null, null, null, null);
        var relations = new List<(string Kind, string Url)>();
        foreach (var (selector, kind) in new[] { (".u-in-reply-to[href]", "reply"), (".u-like-of[href]", "like"), (".u-bookmark-of[href]", "bookmark") })
            foreach (var element in Scoped(entry, selector))
                if (Resolve(baseUrl, element.GetAttribute("href")) is { } url) relations.Add((kind, url));
        var distinct = relations.Distinct().ToList();
        var status = distinct.Count > 1 ? "ambiguous" : distinct.Count == 1 && UrlPolicy.Key(distinct[0].Url) == UrlPolicy.Key(target) ? "supported" : "unknown";
        var author = Resolve(baseUrl, Scoped(entry, ".p-author[href]").FirstOrDefault()?.GetAttribute("href"));
        var singleton = Resolve(baseUrl, html.QuerySelectorAll("link[rel~=alternate]").FirstOrDefault(l =>
            l.GetAttribute("type") == "application/rss+xml")?.GetAttribute("href"));
        DateTime? Time(string selector) => DateTime.TryParse(Scoped(entry, selector).FirstOrDefault()?.GetAttribute("datetime"),
            null, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var result) ? result : null;
        var remote = new RemoteEntry(new EntryIdentity("permalink", source), source, status == "supported" ? distinct[0].Kind : "unknown",
            "unknown", status, author, null, Time(".dt-published"), Time(".dt-updated"),
            Scoped(entry, ".p-name").FirstOrDefault()?.TextContent,
            SemanticProfile.Sanitize(Scoped(entry, ".e-content").FirstOrDefault()?.InnerHtml ?? ""),
            null, status == "supported" ? target : null, null);
        return new SourceEvidence(true, status, status == "supported" ? distinct[0].Kind : "mention", author, singleton, null, null, remote);
    }
    private static IEnumerable<IElement> Scoped(IElement entry, string selector) =>
        entry.QuerySelectorAll(selector).Where(e => e.Ancestors<IElement>().FirstOrDefault(a => a.ClassList.Contains("h-entry")) == entry);
    private static Uri DocumentBase(IDocument document, Uri fallback) =>
        Resolve(fallback, document.QuerySelector("base[href]")?.GetAttribute("href")) is { } url ? new Uri(url) : fallback;
    public static string? Resolve(Uri origin, string? value)
    {
        if (value is null) return null;
        try { return UrlPolicy.Parse(new Uri(origin, value).AbsoluteUri).AbsoluteUri; }
        catch (Exception e) when (e is Infrastructure.ApiProblem or UriFormatException) { return null; }
    }
    [GeneratedRegex("<(?<url>[^>]+)>\\s*;[^,]*?\\brel\\s*=\\s*[\"']?(?<rel>[^\"',;]+)", RegexOptions.IgnoreCase)]
    private static partial Regex LinkHeader();
}
