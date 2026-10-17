using System.Text;
using System.Text.Json;
using AngleSharp.Html.Parser;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Posts;
using FeedConversations.Api.Syndication;
using FeedConversations.Api.Webmentions;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Conversations;

public sealed record ThreadNode(EntryIdentity Identity, string Permalink, EntryIdentity? ParentIdentity,
    string Kind, object? Author, object? Publication, string Availability);
public sealed record ThreadResult(ThreadNode? Root, IReadOnlyList<ThreadNode> Nodes, bool Complete, IReadOnlyList<string> PartialReasons);

public sealed class ThreadAssembler(AppDbContext db, PostService posts, IRemoteFetcher fetcher, FeedReader reader)
{
    public async Task<ThreadResult> Assemble(string url, bool hydrate, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var nodes = new Dictionary<string, ThreadNode>();
        var expanded = new HashSet<string>();
        var path = new HashSet<string>();
        var reasons = new HashSet<string>();
        var remoteEntries = new Dictionary<string, RemoteEntry>();
        var fetches = 0;
        async Task<RemoteDocument?> Fetch(string location)
        {
            if (!hydrate) return null;
            if (fetches >= 20) { reasons.Add("fetch-budget"); return null; }
            if (deadline.IsCancellationRequested) { reasons.Add("deadline"); return null; }
            fetches++;
            try
            {
                var doc = await fetcher.GetAsync(location, deadline.Token);
                if (doc.Status != 200) { reasons.Add("origin-unavailable"); return null; }
                return doc;
            }
            catch (ApiProblem) { reasons.Add("origin-unavailable"); return null; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { reasons.Add("deadline"); return null; }
        }
        async Task<ThreadNode?> Load(string location)
        {
            var key = UrlPolicy.Key(location);
            if (nodes.TryGetValue(key, out var loaded)) return loaded;
            if (nodes.Count >= 200) { reasons.Add("node-budget"); return null; }
            ThreadNode node;
            var localId = posts.LocalId(location);
            if (localId is not null)
            {
                var post = await db.Posts.Include(p => p.Owner).SingleOrDefaultAsync(p => p.Id == localId, ct);
                if (post is null || post.Hidden)
                    node = new ThreadNode(new EntryIdentity("permalink", location), location, null, "unknown", null, null, "unavailable");
                else
                {
                    var parent = post.Kind == "reply" ? JsonSerializer.Deserialize<TargetIdentity>(post.TargetIdentityJson!, Wire.Json)!.AsEntry() : null;
                    node = new ThreadNode(new EntryIdentity("permalink", post.Permalink), post.Permalink, parent, post.Kind,
                        new { handle = post.Owner.UserName, post.Owner.DisplayName }, post.DeletedAt is null ? PostContracts.Dto(post) : null,
                        post.DeletedAt is null ? "local" : "tombstone");
                }
            }
            else
            {
                var html = await Fetch(location);
                RemoteEntry? entry = null;
                if (html is not null)
                {
                    var doc = new HtmlParser().ParseDocument(Encoding.UTF8.GetString(html.Bytes));
                    var alternate = doc.QuerySelectorAll("link[rel~=alternate]").FirstOrDefault(e => e.GetAttribute("type") is "application/rss+xml" or "application/atom+xml");
                    var feedUrl = WebmentionProtocol.Resolve(html.FinalUrl, alternate?.GetAttribute("href"));
                    if (feedUrl is not null)
                    {
                        var feed = await Fetch(feedUrl);
                        if (feed is not null)
                        {
                            try
                            {
                                var candidates = reader.Parse(feed.Bytes, feed.FinalUrl).Where(e => UrlPolicy.Key(e.Permalink) == key).ToList();
                                if (candidates.Count == 1) entry = candidates[0];
                                else reasons.Add("ambiguous-identity");
                            }
                            catch (ApiProblem) { reasons.Add("ambiguous-identity"); }
                        }
                    }
                }
                if (entry is not null)
                {
                    remoteEntries[key] = entry;
                    node = new ThreadNode(entry.Identity, location,
                        entry.Kind == "reply" && entry.SemanticStatus == "supported" && entry.TargetUrl is not null ? new EntryIdentity("permalink", entry.TargetUrl) : null,
                        entry.Kind, entry.AuthorUrl is null ? null : new { url = entry.AuthorUrl }, entry,
                        entry.SemanticStatus == "ambiguous" ? "ambiguous" : "fetched");
                    if (entry.SemanticStatus == "ambiguous") reasons.Add("ambiguous-identity");
                }
                else
                {
                    reasons.Add("origin-unavailable");
                    node = new ThreadNode(new EntryIdentity("permalink", location), location, null, "unknown", null, null, "unavailable");
                }
            }
            nodes[key] = node;
            return node;
        }
        async Task Expand(string location, int depth)
        {
            var key = UrlPolicy.Key(location);
            if (!expanded.Add(key)) return;
            if (depth > 16) { reasons.Add("depth-budget"); return; }
            path.Add(key);
            var node = await Load(location);
            if (node is null) { path.Remove(key); return; }
            string? parentUrl = null;
            if (node.Kind == "reply")
            {
                if (remoteEntries.TryGetValue(key, out var remote)) parentUrl = remote.TargetUrl;
                else if (posts.LocalId(location) is { } local)
                    parentUrl = await db.Posts.Where(p => p.Id == local).Select(p => p.TargetUrl).SingleOrDefaultAsync(ct);
            }
            if (parentUrl is not null && !expanded.Contains(UrlPolicy.Key(parentUrl))) await Expand(parentUrl, depth + 1);
            var ownReplies = await db.Posts.Where(p => p.Kind == "reply" && p.TargetUrl != null && !p.Hidden && p.DeletedAt == null)
                .Select(p => new { p.Permalink, p.TargetUrl }).Take(20001).ToListAsync(ct);
            var childUrls = ownReplies.Where(p => UrlPolicy.Key(p.TargetUrl!) == key).Select(p => p.Permalink).ToHashSet();
            if (posts.LocalId(location) is { } targetId)
            {
                var approved = await db.References.Where(r => r.TargetId == targetId && r.Kind == "reply" && r.Status == "visible")
                    .Select(r => r.SourceUrl).Take(200).ToListAsync(ct);
                childUrls.UnionWith(approved);
            }
            else if (remoteEntries.TryGetValue(key, out var origin) && origin.CommentsUrl is { } comments)
            {
                var feed = await Fetch(comments);
                if (feed is not null)
                {
                    try
                    {
                        foreach (var hint in reader.Parse(feed.Bytes, feed.FinalUrl))
                        {
                            var verified = await Load(hint.Permalink);
                            if (verified is not null && remoteEntries.TryGetValue(UrlPolicy.Key(hint.Permalink), out var actual)
                                && actual.Kind == "reply" && actual.SemanticStatus == "supported" && actual.TargetUrl is not null
                                && UrlPolicy.Key(actual.TargetUrl) == key) childUrls.Add(actual.Permalink);
                            else if (verified?.Availability != "local") reasons.Add("ambiguous-identity");
                            else childUrls.Add(verified.Permalink);
                        }
                    }
                    catch (ApiProblem) { reasons.Add("ambiguous-identity"); }
                }
            }
            foreach (var child in childUrls)
            {
                if (path.Contains(UrlPolicy.Key(child))) continue;
                await Expand(child, depth + 1);
            }
            path.Remove(key);
        }
        await Expand(url, 0);
        var root = nodes.GetValueOrDefault(UrlPolicy.Key(url));
        var ancestors = new HashSet<string>();
        while (root?.ParentIdentity is { Kind: "permalink" } parent
            && nodes.TryGetValue(UrlPolicy.Key(parent.Value), out var next))
        {
            if (!ancestors.Add(root.Identity.Key)) { reasons.Add("cycle"); break; }
            root = next;
        }
        return new ThreadResult(root, nodes.Values.ToList(), reasons.Count == 0, reasons.ToList());
    }
}
