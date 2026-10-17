using System.Text;
using System.Text.Json;
using FeedConversations.Api.Conversations;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Posts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FeedConversations.IntegrationTests;

public sealed class ThreadTests
{
    private static Post Publication(Account owner, string kind = "post", string? target = null) =>
        new() { OwnerId = owner.Id, Kind = kind, Guid = Guid.NewGuid().ToString(), Permalink = "https://a.example/post/" + Guid.NewGuid(),
            Markdown = "Synthetic local content.", PublishedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
            TargetUrl = target, TargetKey = target is null ? null : "url:" + UrlPolicy.Key(target),
            TargetIdentityJson = target is null ? null : JsonSerializer.Serialize(new TargetIdentity("permalink", target), Wire.Json) };

    [Fact]
    public async Task HiddenLocalAssociationIsNotReintroducedByScanningOwnedReplies()
    {
        await using var fixture = new ApiFixture(remote: new FakeRemote());
        var (client, _, owner) = await fixture.LoginAsync();
        using (client) using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var root = Publication(owner); root.Permalink = "https://a.example/post/" + root.Id;
            var child = Publication(owner, "reply", root.Permalink); child.Permalink = "https://a.example/post/" + child.Id;
            child.LocalTargetId = root.Id;
            db.Posts.AddRange(root, child);
            db.References.Add(new InteractionReference { TargetId = root.Id, SourceUrl = child.Permalink,
                SourceKey = UrlPolicy.Key(child.Permalink), LocalSourceId = child.Id, Kind = "reply", Status = "hidden" });
            await db.SaveChangesAsync();
            var result = await scope.ServiceProvider.GetRequiredService<ThreadAssembler>().Assemble(root.Permalink, true, default);
            Assert.Single(result.Nodes);
            Assert.Equal(root.Permalink, result.Root!.Permalink);
            Assert.True(result.Complete);
        }
    }

    [Fact]
    public async Task LikesAreAssociationsNotParentsButCanReceiveReplies()
    {
        await using var fixture = new ApiFixture(remote: new FakeRemote());
        var (client, _, owner) = await fixture.LoginAsync();
        using (client) using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var like = Publication(owner, "like", "https://controlled.example/not-a-parent");
            like.Permalink = "https://a.example/post/" + like.Id;
            var child = Publication(owner, "reply", like.Permalink); child.Permalink = "https://a.example/post/" + child.Id;
            db.Posts.AddRange(like, child);
            db.References.Add(new InteractionReference { TargetId = like.Id, SourceUrl = child.Permalink,
                SourceKey = UrlPolicy.Key(child.Permalink), LocalSourceId = child.Id, Kind = "reply", Status = "visible" });
            await db.SaveChangesAsync();
            var result = await scope.ServiceProvider.GetRequiredService<ThreadAssembler>().Assemble(like.Permalink, true, default);
            Assert.Equal(2, result.Nodes.Count);
            Assert.Null(result.Root!.ParentIdentity);
            Assert.DoesNotContain(result.Nodes, n => n.Permalink == like.TargetUrl);
        }
    }

    [Fact]
    public async Task LocalDepthBoundaryProducesExplicitPartialReason()
    {
        await using var fixture = new ApiFixture(remote: new FakeRemote());
        var (client, _, owner) = await fixture.LoginAsync();
        using (client) using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Post? previous = null;
            string? root = null;
            for (var depth = 0; depth <= 17; depth++)
            {
                var post = Publication(owner, depth == 0 ? "post" : "reply", previous?.Permalink);
                post.Permalink = "https://a.example/post/" + post.Id;
                db.Posts.Add(post);
                if (previous is not null) db.References.Add(new InteractionReference { TargetId = previous.Id,
                    SourceUrl = post.Permalink, SourceKey = UrlPolicy.Key(post.Permalink), LocalSourceId = post.Id, Kind = "reply", Status = "visible" });
                root ??= post.Permalink;
                previous = post;
            }
            await db.SaveChangesAsync();
            var result = await scope.ServiceProvider.GetRequiredService<ThreadAssembler>().Assemble(root!, true, default);
            Assert.Equal(17, result.Nodes.Count);
            Assert.False(result.Complete);
            Assert.Contains("depth-budget", result.PartialReasons);
        }
    }

    [Fact]
    public async Task RemoteParentCycleTerminatesWithoutPersistentForeignBodies()
    {
        var remote = new FakeRemote();
        foreach (var name in new[] { "first", "second" })
        {
            var parent = name == "first" ? "second" : "first";
            var url = $"https://controlled.example/{name}";
            Add(url, "text/html", $"<link rel='alternate' type='application/rss+xml' href='{url}.xml'>");
            Add(url + ".xml", "application/rss+xml", "<rss xmlns:a='http://www.w3.org/2005/Atom'><channel><item>"
                + $"<guid isPermaLink='false'>opaque:{name}</guid><link>{url}</link><a:category term='https://schema.org/Comment'/>"
                + $"<a:link rel='https://schema.org/author' href='/author'/><a:link rel='https://schema.org/parentItem' href='https://controlled.example/{parent}'/>"
                + "<description>FOREIGN_CYCLE_SENTINEL</description></item></channel></rss>");
        }
        await using var fixture = new ApiFixture(remote: remote);
        var (client, _, _) = await fixture.LoginAsync();
        using (client) using (var scope = fixture.Services.CreateScope())
        {
            var result = await scope.ServiceProvider.GetRequiredService<ThreadAssembler>().Assemble("https://controlled.example/first", true, default);
            Assert.Equal(2, result.Nodes.Count);
            Assert.Contains("cycle", result.PartialReasons);
            Assert.False(result.Complete);
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<AppDbContext>().References.ToListAsync());
        }
        void Add(string url, string type, string body) => remote.Documents[url] = new RemoteDocument(new Uri(url), 200, type,
            Encoding.UTF8.GetBytes(body), [], DateTime.UtcNow, DateTime.UtcNow, true, null);
    }
}
