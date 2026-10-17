using System.Net;
using FeedConversations.Api.Data;

namespace FeedConversations.Api.PublicDocuments;

public static class PublicationHtml
{
    public static string E(string? value) => WebUtility.HtmlEncode(value ?? "");
    public static string Entry(Post p, string baseUrl)
    {
        var relation = p.TargetUrl is null ? "" : $"<p>{Label(p.Kind)} <a class=\"{Class(p.Kind)}\" href=\"{E(p.TargetUrl)}\">this page</a>.</p>";
        var authority = p.Provenance is null ? "" : " data-origin-authority=\"unverified-restore\"";
        return $"<article class=\"h-entry\"{authority}><a class=\"u-url\" href=\"{E(p.Permalink)}\">Permalink</a>"
            + $"<a class=\"p-author h-card\" href=\"{E(baseUrl)}/users/{E(p.Owner.UserName)}\">{E(p.Owner.DisplayName)}</a>"
            + $"<time class=\"dt-published\" datetime=\"{p.PublishedAt:O}\">{p.PublishedAt:O}</time>"
            + $"<time class=\"dt-updated\" datetime=\"{p.UpdatedAt:O}\">{p.UpdatedAt:O}</time>"
            + (p.Title is null ? "" : $"<h1 class=\"p-name\">{E(p.Title)}</h1>")
            + relation + (string.IsNullOrEmpty(p.Html) ? "" : $"<div class=\"e-content\">{p.Html}</div>") + "</article>";
    }
    public static string Label(string kind) => kind switch { "reply" => "Reply to", "like" => "Liked", "bookmark" => "Bookmarked", _ => "" };
    public static string Class(string kind) => kind switch { "reply" => "u-in-reply-to", "like" => "u-like-of", "bookmark" => "u-bookmark-of", _ => "" };
    public static string Page(string title, string body, string baseUrl, string? canonical = null, string? feed = null, string? authorFeed = null) =>
        "<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">"
        + $"<title>{E(title)}</title><link rel=\"webmention\" href=\"{E(baseUrl)}/webmention\">"
        + (canonical is null ? "" : $"<link rel=\"canonical\" href=\"{E(canonical)}\">")
        + (feed is null ? "" : $"<link rel=\"alternate\" type=\"application/rss+xml\" href=\"{E(feed)}\">")
        + (authorFeed is null ? "" : $"<link rel=\"alternate\" type=\"application/rss+xml\" title=\"Author feed\" href=\"{E(authorFeed)}\">")
        + "</head><body><main>" + body + "</main></body></html>";
}
