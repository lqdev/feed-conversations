using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeedConversations.Api.Data;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Streaming;
using FeedConversations.Api.Syndication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FeedConversations.IntegrationTests;

public sealed class FakeRemote : IRemoteFetcher
{
    public Dictionary<string, RemoteDocument> Documents { get; } = [];
    public System.Collections.Concurrent.ConcurrentQueue<string> Reads { get; } = new();
    public List<(string Url, string Source, string Target)> Writes { get; } = [];
    public Task<RemoteDocument> GetAsync(string url, CancellationToken token)
    {
        Reads.Enqueue(url);
        return Task.FromResult(Documents.TryGetValue(url, out var document) ? document
            : new RemoteDocument(new Uri(url), 503, "text/html", [], [], DateTime.UtcNow, DateTime.UtcNow, true, null));
    }
    public Task<RemoteDocument> PostAsync(string url, string source, string target, CancellationToken token)
    {
        Writes.Add((url, source, target));
        return Task.FromResult(new RemoteDocument(new Uri(url), 202, "text/plain", [], [], DateTime.UtcNow, DateTime.UtcNow, true, null));
    }
}

public sealed class WorkspaceTests
{
    [Fact]
    public async Task SubscriptionOpmlAndViewRemainPrivateAndDoNotFetchOnImport()
    {
        var remote = new FakeRemote();
        await using var fixture = new ApiFixture(remote: remote);
        var (alice, _, _) = await fixture.LoginAsync();
        using (alice)
        {
            const string opml = "<opml version='2.0'><body><outline text='Group'><outline text='Feed' xmlUrl='https://publisher.example/rss.xml'/></outline></body></opml>";
            var preview = await alice.PostAsJsonAsync("/api/v1/opml/preview", new { text = opml });
            Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
            foreach (var _ in Enumerable.Range(0, 2))
            {
                var import = await alice.PostAsJsonAsync("/api/v1/opml/import", new { text = opml, selectedUrls = new[] { "https://publisher.example/rss.xml" } });
                Assert.True(import.IsSuccessStatusCode, await import.Content.ReadAsStringAsync());
            }
            var subs = await alice.GetFromJsonAsync<JsonElement>("/api/v1/subscriptions");
            Assert.Single(subs.GetProperty("items").EnumerateArray());
            Assert.Equal("Group", subs.GetProperty("items")[0].GetProperty("group").GetString());
            Assert.Empty(remote.Writes);
            Assert.Empty(remote.Reads);
            var view = await alice.PostAsJsonAsync("/api/v1/views", new { version = 1, name = "Following", includeLocal = true,
                sourceFeedUrls = new[] { "https://publisher.example/rss.xml" }, textContains = "hello", textExcludes = (string?)null,
                sort = "published-desc", entryKinds = new[] { "post", "reply" } });
            Assert.Equal(HttpStatusCode.Created, view.StatusCode);
            var (bob, _, _) = await fixture.LoginAsync("bob");
            using (bob)
            {
                var privateSubs = await bob.GetFromJsonAsync<JsonElement>("/api/v1/subscriptions");
                Assert.Empty(privateSubs.GetProperty("items").EnumerateArray());
                var privateViews = await bob.GetFromJsonAsync<JsonElement>("/api/v1/views");
                Assert.Empty(privateViews.GetProperty("items").EnumerateArray());
            }
            Assert.DoesNotContain("publisher.example", await alice.GetStringAsync("/data/subs.opml"));
            Assert.DoesNotContain("/users/alice/rss.xml", await alice.GetStringAsync("/data/subs.opml"));
            var profile = await alice.GetAsync("/api/v1/me");
            using var optIn = new HttpRequestMessage(HttpMethod.Patch, "/api/v1/me")
                { Content = JsonContent.Create(new { directoryVisible = true }) };
            optIn.Headers.TryAddWithoutValidation("If-Match", profile.Headers.ETag!.Tag);
            Assert.True((await alice.SendAsync(optIn)).IsSuccessStatusCode);
            var directory = await alice.GetStringAsync("/data/subs.opml");
            Assert.Contains("/users/alice/rss.xml", directory);
            Assert.DoesNotContain("/users/bob/rss.xml", directory);
            Assert.DoesNotContain("publisher.example", directory);
        }
    }

    [Fact]
    public async Task ForeignReadingIsTransientAndExplicitFailuresAreNotEmptySuccess()
    {
        const string sentinel = "FOREIGN_BODY_SENTINEL_d730c5c383824b869d40e27c8659bc6d";
        var remote = new FakeRemote();
        var bytes = Encoding.UTF8.GetBytes($"<rss version='2.0'><channel><title>Fixture</title><item><guid isPermaLink='false'>opaque:x</guid><link>https://publisher.example/post</link><description>{sentinel}</description></item></channel></rss>");
        remote.Documents["https://publisher.example/rss.xml"] = new RemoteDocument(new Uri("https://publisher.example/rss.xml"), 200, "application/rss+xml",
            bytes, [], DateTime.UtcNow, DateTime.UtcNow.AddSeconds(30), true, null);
        await using var fixture = new ApiFixture(remote: remote);
        var (alice, _, _) = await fixture.LoginAsync();
        using (alice)
        {
            foreach (var url in new[] { "https://publisher.example/rss.xml", "https://outage.example/rss.xml" })
                Assert.True((await alice.PostAsJsonAsync("/api/v1/subscriptions", new { url, label = "Synthetic", group = (string?)null })).IsSuccessStatusCode);
            var resolve = await alice.PostAsJsonAsync("/api/v1/reading/resolve", new { feedUrls = new[] { "https://publisher.example/rss.xml", "https://outage.example/rss.xml" } });
            Assert.Equal(HttpStatusCode.OK, resolve.StatusCode);
            var result = await resolve.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Contains(sentinel, result.GetProperty("sources")[0].GetProperty("items")[0].GetProperty("html").GetString());
            Assert.Equal("failed", result.GetProperty("sources")[1].GetProperty("status").GetString());
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var path = db.Database.GetDbConnection().DataSource;
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(FULL);");
            foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*", SearchOption.AllDirectories))
            {
                await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                Assert.False(Encoding.UTF8.GetString(buffer.ToArray()).Contains(sentinel, StringComparison.Ordinal),
                    "Foreign body sentinel was found in scoped durable storage.");
            }
            Assert.Empty(await db.Posts.ToListAsync());
        }
    }

    [Fact]
    public async Task CursorIncarnationFutureAndPrivateCheckpointAreExplicit()
    {
        await using var fixture = new ApiFixture();
        var (alice, _, account) = await fixture.LoginAsync();
        using (alice)
        {
            var snapshot = await alice.GetFromJsonAsync<JsonElement>("/api/v1/state");
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var journal = scope.ServiceProvider.GetRequiredService<ChangeJournal>();
            Assert.Null(journal.Validate(snapshot.GetProperty("cursor").GetString()!, 1, 0, out _));
            Assert.Equal("future-cursor", journal.Validate(journal.Cursor(10), 1, 0, out _));
            ChangeJournal.Add(db, "subscription", Guid.NewGuid().ToString(), 1, "upsert", "different-account");
            await db.SaveChangesAsync();
            Assert.Equal(1, await ChangeJournal.HighWater(db, default));
            var differentStartup = new ChangeJournal(scope.ServiceProvider.GetRequiredService<FeedConversations.Api.Configuration.InstanceOptions>(),
                scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(), TimeProvider.System);
            Assert.Equal("incarnation-changed", differentStartup.Validate(snapshot.GetProperty("cursor").GetString()!, 1, 1, out _));
            Assert.DoesNotContain(account.Id, snapshot.GetProperty("cursor").GetString());
        }
    }

    [Fact]
    public async Task QueueFullAndIdempotencyMismatchLeaveNoPartialPublication()
    {
        await using var fixture = new ApiFixture(remote: new FakeRemote());
        var (alice, _, _) = await fixture.LoginAsync();
        using (alice)
        {
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                db.Jobs.AddRange(Enumerable.Range(0, 1000).Select(i => new MentionJob { Direction = "out", SourceUrl = $"https://a.example/{i}",
                    TargetUrl = "https://controlled.example/target", DueAt = DateTime.UtcNow.AddDays(1), UpdatedAt = DateTime.UtcNow }));
                await db.SaveChangesAsync();
            }
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/posts") { Content = JsonContent.Create(new {
                title = (string?)null, markdown = "Reply", relationship = new { kind = "reply", target = new {
                    permalink = "https://controlled.example/target", identity = new { kind = "permalink", value = "https://controlled.example/target" } } } }) };
            request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
            var full = await alice.SendAsync(request);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, full.StatusCode);
            using var verify = fixture.Services.CreateScope();
            var data = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(await data.Posts.ToListAsync());
            Assert.Empty(await data.Changes.ToListAsync());
            Assert.Empty(await data.Receipts.ToListAsync());
        }
    }
}
