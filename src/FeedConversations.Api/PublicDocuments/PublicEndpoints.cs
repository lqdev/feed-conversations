using System.Xml.Linq;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Posts;
using FeedConversations.Api.Syndication;
using FeedConversations.Api.Webmentions;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.PublicDocuments;

public static class PublicEndpoints
{
    public static void MapPublicDocuments(this WebApplication app)
    {
        app.MapGet("/", (InstanceOptions o) => Results.Content(PublicationHtml.Page("Feed conversations",
            "<h1>Feed conversations</h1><a href=\"/users/rss.xml\">Public feed</a> <a href=\"/data/subs.opml\">Member directory</a>", o.BaseUrl), "text/html"));
        app.MapGet("/post/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, InstanceOptions o, CancellationToken ct) =>
        {
            var post = await PostEndpoints.Get(db, id, ct);
            PostService.CheckVisible(post);
            Discovery(ctx, o);
            var body = PublicationHtml.Entry(post, o.BaseUrl)
                + $"<p><a href=\"{post.Permalink}/comments.xml\">Known direct replies</a></p>";
            return Conditional(ctx, PublicationHtml.Page(post.Title ?? RssWriter.Type(post.Kind), body, o.BaseUrl,
                post.Permalink, post.Permalink + "/rss.xml"), "text/html");
        });
        app.MapGet("/users/{handle}", async (string handle, HttpContext ctx, AppDbContext db, InstanceOptions o, CancellationToken ct) =>
        {
            var user = await db.Users.SingleOrDefaultAsync(a => a.UserName == handle && !a.Disabled, ct)
                ?? throw new ApiProblem(404, "profile-missing", "Profile not found.");
            var posts = await db.Posts.Include(p => p.Owner).Where(p => p.OwnerId == user.Id && p.DeletedAt == null && !p.Hidden)
                .OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.Id).Take(50).ToListAsync(ct);
            Discovery(ctx, o);
            return Conditional(ctx, PublicationHtml.Page(user.DisplayName,
                $"<h1>{PublicationHtml.E(user.DisplayName)}</h1><p>{PublicationHtml.E(user.Bio)}</p>"
                + string.Join("", posts.Select(p => PublicationHtml.Entry(p, o.BaseUrl))), o.BaseUrl,
                $"{o.BaseUrl}/users/{handle}", $"{o.BaseUrl}/users/{handle}/rss.xml"), "text/html");
        });
        app.MapGet("/users/rss.xml", (HttpContext ctx, AppDbContext db, InstanceOptions o, RssWriter writer, CancellationToken ct) =>
            Feed(ctx, db, o, writer, null, null, ct)).RequireRateLimiting("feed");
        app.MapGet("/users/{handle}/rss.xml", (string handle, HttpContext ctx, AppDbContext db, InstanceOptions o, RssWriter writer, CancellationToken ct) =>
            Feed(ctx, db, o, writer, handle, null, ct)).RequireRateLimiting("feed");
        app.MapGet("/post/{id:guid}/rss.xml", (Guid id, HttpContext ctx, AppDbContext db, InstanceOptions o, RssWriter writer, CancellationToken ct) =>
            Feed(ctx, db, o, writer, null, id, ct)).RequireRateLimiting("feed");
        app.MapGet("/post/{id:guid}/comments.xml", async (Guid id, HttpContext ctx, AppDbContext db, InstanceOptions o,
            RssWriter writer, IRemoteFetcher fetcher, WebmentionProtocol protocol, FeedReader reader, CancellationToken ct) =>
        {
            PostService.CheckVisible(await PostEndpoints.Get(db, id, ct));
            var refs = await db.References.AsNoTracking().Where(r => r.TargetId == id && r.Kind == "reply" && r.Status == "visible").Take(200).ToListAsync(ct);
            var items = new List<XElement>();
            var partial = new List<string>();
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(TimeSpan.FromSeconds(10));
            var fetches = 0;
            foreach (var reference in refs)
            {
                if (reference.LocalSourceId is { } local)
                {
                    var p = await db.Posts.Include(p => p.Owner).SingleOrDefaultAsync(p => p.Id == local && p.DeletedAt == null && !p.Hidden, ct);
                    if (p is not null) items.Add(writer.Owned(p, o.BaseUrl, await Count(db, p.Id, ct)));
                    continue;
                }
                try
                {
                    if (++fetches > 20 || budget.IsCancellationRequested) throw new ApiProblem(502, "fetch-budget", "Conversation fetch budget exhausted.");
                    var doc = await fetcher.GetAsync(reference.SourceUrl, budget.Token);
                    if (doc.Status != 200) throw new ApiProblem(502, "origin-unavailable", "Origin unavailable.");
                    var evidence = protocol.Extract(doc, reference.SourceUrl, reference.TargetUrl ?? $"{o.BaseUrl}/post/{id}");
                    if (!evidence.Backlink || evidence.Entry is null) throw new ApiProblem(502, "origin-unavailable", "Origin unavailable.");
                    var entry = evidence.Entry with
                    {
                        Identity = reference.IdentityJson is null ? evidence.Entry.Identity
                            : System.Text.Json.JsonSerializer.Deserialize<EntryIdentity>(reference.IdentityJson, Wire.Json)!,
                        CommentsUrl = reference.CommentsUrl, AuthorFeedUrl = reference.AuthorFeedUrl,
                        Kind = reference.Kind, SemanticStatus = "supported"
                    };
                    items.Add(writer.Remote(entry));
                }
                catch (Exception e) when (e is ApiProblem or OperationCanceledException && !ct.IsCancellationRequested)
                {
                    partial.Add(e is ApiProblem problem ? problem.Code : "deadline");
                    var identity = reference.IdentityJson is null ? null
                        : System.Text.Json.JsonSerializer.Deserialize<EntryIdentity>(reference.IdentityJson, Wire.Json);
                    items.Add(new XElement("item", new XElement("guid", new XAttribute("isPermaLink", "false"), identity?.Guid ?? identity?.Value ?? reference.SourceUrl),
                        new XElement("link", reference.SourceUrl), new XElement("description", "Source currently unavailable; approved reference retained.")));
                }
            }
            ctx.Response.Headers["X-Conversation-Partial"] = partial.Count == 0 ? "false" : string.Join(",", partial.Distinct());
            return Conditional(ctx, writer.Write("Known approved direct replies", $"{o.BaseUrl}/post/{id}",
                $"{o.BaseUrl}/post/{id}/comments.xml", await Changed(db, ct), items), "application/rss+xml");
        }).RequireRateLimiting("feed");
        app.MapGet("/data/subs.opml", async (AppDbContext db, InstanceOptions o, HttpContext ctx, CancellationToken ct) =>
        {
            var members = await db.Users.Where(a => a.DirectoryVisible && !a.Disabled).OrderBy(a => a.UserName).Take(1000).ToListAsync(ct);
            var xml = new XDocument(new XElement("opml", new XAttribute("version", "2.0"),
                new XElement("head", new XElement("title", "Member feeds")),
                new XElement("body", members.Select(a => new XElement("outline", new XAttribute("type", "rss"),
                    new XAttribute("text", a.DisplayName), new XAttribute("xmlUrl", $"{o.BaseUrl}/users/{a.UserName}/rss.xml"))))));
            return Conditional(ctx, xml.ToString(), "text/x-opml");
        });
    }
    public static IResult Conditional(HttpContext ctx, string text, string type)
    {
        var etag = $"\"{Wire.Hash(text)}\"";
        ctx.Response.Headers.ETag = etag;
        ctx.Response.Headers.CacheControl = "public, max-age=0, must-revalidate";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        ctx.Response.Headers.ContentSecurityPolicy = "default-src 'none'; style-src 'self'; base-uri 'none'; frame-ancestors 'none'";
        return ctx.Request.Headers.IfNoneMatch == etag ? Results.StatusCode(304) : Results.Content(text, type + "; charset=utf-8");
    }
    private static void Discovery(HttpContext ctx, InstanceOptions o) =>
        ctx.Response.Headers.Link = $"<{o.BaseUrl}/webmention>; rel=\"webmention\"";
    private static async Task<IResult> Feed(HttpContext ctx, AppDbContext db, InstanceOptions o, RssWriter writer,
        string? handle, Guid? id, CancellationToken ct)
    {
        if (id is not null) PostService.CheckVisible(await PostEndpoints.Get(db, id.Value, ct));
        var query = db.Posts.Include(p => p.Owner).Where(p => !p.Hidden && p.DeletedAt == null && !p.Owner.Disabled);
        if (handle is not null) query = query.Where(p => p.Owner.UserName == handle);
        if (id is not null) query = query.Where(p => p.Id == id);
        var posts = await query.OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.Id).Take(50).ToListAsync(ct);
        var items = new List<XElement>();
        foreach (var p in posts) items.Add(writer.Owned(p, o.BaseUrl, await Count(db, p.Id, ct)));
        return Conditional(ctx, writer.Write(handle ?? "Local publications", o.BaseUrl,
            o.BaseUrl + ctx.Request.Path, await Changed(db, ct), items), "application/rss+xml");
    }
    private static Task<int> Count(AppDbContext db, Guid id, CancellationToken ct) =>
        db.References.CountAsync(r => r.TargetId == id && r.Kind == "reply" && r.Status == "visible", ct);
    private static async Task<DateTime> Changed(AppDbContext db, CancellationToken ct) =>
        await db.Changes.MaxAsync(c => (DateTime?)c.At, ct) ?? DateTime.UnixEpoch;
}
