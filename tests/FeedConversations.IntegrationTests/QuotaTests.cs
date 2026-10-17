using System.Net;
using System.Net.Http.Json;
using FeedConversations.Api.Data;
using FeedConversations.Api.Networking;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FeedConversations.IntegrationTests;

public sealed class QuotaTests
{
    [Theory]
    [InlineData("subscription", 98, true)]
    [InlineData("subscription", 99, true)]
    [InlineData("subscription", 100, false)]
    [InlineData("view", 23, true)]
    [InlineData("view", 24, true)]
    [InlineData("view", 25, false)]
    public async Task WorkspaceCreateLimitsAreInclusiveAndAtomic(string resource, int existing, bool allowed)
    {
        await using var fixture = new ApiFixture(remote: new FakeRemote());
        var (client, _, owner) = await fixture.LoginAsync();
        using (client)
        {
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                for (var i = 0; i < existing; i++)
                    if (resource == "subscription") db.Subscriptions.Add(new Subscription { OwnerId = owner.Id,
                        Url = $"https://controlled.example/{i}", UrlKey = UrlPolicy.Key($"https://controlled.example/{i}"), Label = $"Source {i}" });
                    else db.Views.Add(new SavedView { OwnerId = owner.Id, Name = $"View {i}", Definition = "{}" });
                await db.SaveChangesAsync();
            }
            var response = resource == "subscription"
                ? await client.PostAsJsonAsync("/api/v1/subscriptions", new { url = "https://controlled.example/new", label = "New", group = (string?)null })
                : await client.PostAsJsonAsync("/api/v1/views", new { version = 1, name = "New", includeLocal = true,
                    sourceFeedUrls = Array.Empty<string>(), textContains = (string?)null, textExcludes = (string?)null,
                    sort = "published-desc", entryKinds = new[] { "post", "reply", "like", "bookmark" } });
            Assert.Equal(allowed ? HttpStatusCode.Created : HttpStatusCode.Conflict, response.StatusCode);
            using var verify = fixture.Services.CreateScope();
            var data = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(existing + (allowed ? 1 : 0), resource == "subscription" ? await data.Subscriptions.CountAsync() : await data.Views.CountAsync());
            Assert.Equal(allowed ? 1 : 0, await data.Receipts.CountAsync());
            Assert.Equal(allowed ? 1 : 0, await data.Changes.CountAsync());
        }
    }

    [Theory]
    [InlineData(198, true, false)]
    [InlineData(199, true, false)]
    [InlineData(200, false, false)]
    [InlineData(19998, true, true)]
    [InlineData(19999, true, true)]
    [InlineData(20000, false, true)]
    public async Task LocalReferenceAdmissionIncludesEveryKindAndRollsBackOwnedCommand(int existing, bool allowed, bool global)
    {
        await using var fixture = new ApiFixture(remote: new FakeRemote());
        var (client, _, owner) = await fixture.LoginAsync();
        using (client)
        {
            var root = new Post { OwnerId = owner.Id, PublishedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Markdown = "Owned root." };
            root.Guid = root.Permalink = "https://a.example/post/" + root.Id;
            var targets = global ? Enumerable.Range(0, 100).Select(_ => new Post { OwnerId = owner.Id,
                PublishedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow, Markdown = "Owned target." }).ToArray() : [root];
            foreach (var post in targets) post.Guid = post.Permalink = "https://a.example/post/" + post.Id;
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Posts.Add(root);
                if (global) db.Posts.AddRange(targets);
                db.References.AddRange(Enumerable.Range(0, existing).Select(i => new InteractionReference { TargetId = targets[i % targets.Length].Id,
                    SourceUrl = $"https://controlled.example/{i}", SourceKey = UrlPolicy.Key($"https://controlled.example/{i}"),
                    Kind = i % 2 == 0 ? "like" : "bookmark", Status = "visible", ObservedAt = DateTime.UtcNow }));
                await db.SaveChangesAsync();
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/posts") { Content = JsonContent.Create(new {
                title = (string?)null, markdown = "Owned local reply.", relationship = new { kind = "reply",
                    target = new { permalink = root.Permalink, identity = new { kind = "permalink", value = root.Permalink } } } }) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            Assert.Equal(allowed ? HttpStatusCode.Created : HttpStatusCode.Conflict, (await client.SendAsync(request)).StatusCode);
            using var verify = fixture.Services.CreateScope();
            var data = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(existing + (allowed ? 1 : 0), await data.References.CountAsync());
            Assert.Equal(1 + (global ? targets.Length : 0) + (allowed ? 1 : 0), await data.Posts.CountAsync());
            Assert.Equal(allowed ? 1 : 0, await data.Receipts.CountAsync());
            Assert.Equal(allowed ? 2 : 0, await data.Changes.CountAsync());
            Assert.Empty(await data.Jobs.ToListAsync());
        }
    }

    [Theory]
    [InlineData(998, true)]
    [InlineData(999, true)]
    [InlineData(1000, false)]
    public async Task NotificationAdmissionReservesCapacityInsidePublicationTransaction(int existing, bool allowed)
    {
        await using var fixture = new ApiFixture(remote: new FakeRemote());
        var (client, _, _) = await fixture.LoginAsync();
        using (client)
        {
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Jobs.AddRange(Enumerable.Range(0, existing).Select(i => new MentionJob { Direction = "out",
                    SourceUrl = $"https://a.example/source-{i}", TargetUrl = "https://controlled.example/target", DueAt = DateTime.UtcNow.AddDays(1) }));
                await db.SaveChangesAsync();
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/posts") { Content = JsonContent.Create(new {
                title = (string?)null, markdown = "Owned external reply.", relationship = new { kind = "reply",
                    target = new { permalink = "https://controlled.example/target", identity = new { kind = "permalink", value = "https://controlled.example/target" } } } }) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            Assert.Equal(allowed ? HttpStatusCode.Created : HttpStatusCode.ServiceUnavailable, (await client.SendAsync(request)).StatusCode);
            using var verify = fixture.Services.CreateScope();
            var data = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(existing + (allowed ? 1 : 0), await data.Jobs.CountAsync());
            Assert.Equal(allowed ? 1 : 0, await data.Posts.CountAsync());
            Assert.Equal(allowed ? 1 : 0, await data.Changes.CountAsync());
            Assert.Equal(allowed ? 1 : 0, await data.Receipts.CountAsync());
        }
    }
}
