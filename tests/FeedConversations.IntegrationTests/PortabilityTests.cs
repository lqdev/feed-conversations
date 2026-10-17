using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using FeedConversations.Api.Data;
using FeedConversations.Api.Portability;
using FeedConversations.Api.Syndication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FeedConversations.IntegrationTests;

public sealed class PortabilityTests
{
    [Fact]
    public async Task TwentyThousandOwnedPostsCommitAndReimportWithoutTruncation()
    {
        await using var destination = new ApiFixture("b", new FakeRemote());
        var (carol, _, _) = await destination.LoginAsync("carol");
        using (carol)
        {
            var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var posts = Enumerable.Range(0, 20000).Select(i =>
            {
                var url = $"https://a.example/post/{new Guid(i, 0, 0, new byte[8])}";
                return new ArchivePost(url, url, date, date, 1, "post", null, "Owned synthetic text.", null, null, null);
            }).ToList();
            var archive = new AccountArchive(new(1, "synthetic-export", "synthetic-instance", "alice",
                "https://a.example", "local-sequence:0", posts.Count, []), new("Alice", "", false), posts, [], []);
            var bytes = new ArchiveCodec(new OpmlCodec()).Write(archive);
            await destination.ConfirmAsync(carol, "carol");
            var commit = await Upload(carol, bytes, "commit", ArchiveCodec.Hash(bytes));
            Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
            var repeat = await Upload(carol, bytes, "commit", ArchiveCodec.Hash(bytes));
            Assert.True(repeat.IsSuccessStatusCode, await repeat.Content.ReadAsStringAsync());
            Assert.True((await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("alreadyImported").GetBoolean());
            using var scope = destination.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(20000, await db.Posts.CountAsync());
            Assert.Single(await db.ImportRuns.ToListAsync());
            Assert.Empty(await db.Jobs.ToListAsync());
        }
    }

    [Fact]
    public async Task LaterArchiveUpdatesAndThenWithdrawsMappedPublicationWithoutChangingIdentity()
    {
        await using var source = new ApiFixture(remote: new FakeRemote());
        await using var destination = new ApiFixture("b", new FakeRemote());
        var (alice, _, _) = await source.LoginAsync();
        var (carol, _, _) = await destination.LoginAsync("carol");
        using (alice) using (carol)
        {
            var original = await Create(alice, "post", null, "First owned state.");
            var sourceId = original.GetProperty("id").GetString()!;
            await source.ConfirmAsync(alice, "alice");
            await destination.ConfirmAsync(carol, "carol");
            async Task Import()
            {
                var bytes = await alice.GetByteArrayAsync("/api/v1/export");
                var response = await Upload(carol, bytes, "commit", ArchiveCodec.Hash(bytes));
                Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            }
            await Import();
            var before = await alice.GetAsync($"/api/v1/posts/{sourceId}");
            using var edit = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/posts/{sourceId}")
                { Content = JsonContent.Create(new { markdown = "Updated owned state." }) };
            edit.Headers.TryAddWithoutValidation("If-Match", before.Headers.ETag!.Tag);
            Assert.True((await alice.SendAsync(edit)).IsSuccessStatusCode);
            await Import();
            Guid destinationId;
            using (var scope = destination.Services.CreateScope())
            {
                var restored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Posts.SingleAsync();
                destinationId = restored.Id;
                Assert.Equal("Updated owned state.", restored.Markdown);
                Assert.Contains("Updated owned state.", restored.Html);
                Assert.Equal(original.GetProperty("guid").GetString(), restored.Guid);
            }
            var current = await alice.GetAsync($"/api/v1/posts/{sourceId}");
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/posts/{sourceId}");
            delete.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
            Assert.Equal(HttpStatusCode.NoContent, (await alice.SendAsync(delete)).StatusCode);
            await Import();
            using var verify = destination.Services.CreateScope();
            var post = await verify.ServiceProvider.GetRequiredService<AppDbContext>().Posts.SingleAsync();
            Assert.Equal(destinationId, post.Id);
            Assert.NotNull(post.DeletedAt);
            Assert.Null(post.Markdown);
            Assert.Null(post.Html);
        }
    }

    private static async Task<JsonElement> Create(HttpClient client, string kind, string? target, string? body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/posts") { Content = JsonContent.Create(new
        {
            title = (string?)null, markdown = body, relationship = target is null ? null : new
            { kind, target = new { permalink = target, identity = new { kind = "permalink", value = target } } }
        }) };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("post");
    }
    private static async Task<HttpResponseMessage> Upload(HttpClient client, byte[] bytes, string operation, string? hash = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/import/{operation}") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("application/zip");
        if (hash is not null) request.Headers.Add("X-Archive-SHA256", hash);
        return await client.SendAsync(request);
    }
    [Fact]
    public async Task FourKindsTombstonesAndSettingsRestoreAtomicallyWithoutNotifications()
    {
        await using var source = new ApiFixture(remote: new FakeRemote());
        await using var destination = new ApiFixture("b", new FakeRemote());
        var (alice, _, _) = await source.LoginAsync();
        var (carol, _, _) = await destination.LoginAsync("carol");
        using (alice) using (carol)
        {
            var root = await Create(alice, "post", null, "Owned source root.");
            var rootUrl = root.GetProperty("permalink").GetString()!;
            await Create(alice, "reply", rootUrl, "Owned reply.");
            await Create(alice, "like", "https://controlled.example/target", null);
            await Create(alice, "bookmark", "https://controlled.example/reference", "Owned bookmark.");
            var deleted = await Create(alice, "post", null, "Withdraw me.");
            var deletedId = deleted.GetProperty("id").GetString();
            var beforeDelete = await alice.GetAsync($"/api/v1/posts/{deletedId}");
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/posts/{deletedId}");
            delete.Headers.TryAddWithoutValidation("If-Match", beforeDelete.Headers.ETag!.Tag);
            Assert.Equal(HttpStatusCode.NoContent, (await alice.SendAsync(delete)).StatusCode);
            await alice.PostAsJsonAsync("/api/v1/subscriptions", new { url = "https://controlled.example/rss", label = "Synthetic", group = "Group" });
            Assert.Equal(HttpStatusCode.Created, (await alice.PostAsJsonAsync("/api/v1/views", new {
                version = 1, name = "All kinds", includeLocal = true, sourceFeedUrls = new[] { "https://controlled.example/rss" },
                textContains = (string?)null, textExcludes = (string?)null, sort = "published-desc",
                entryKinds = new[] { "post", "reply", "like", "bookmark" } })).StatusCode);
            await source.ConfirmAsync(alice, "alice");
            var bytes = await alice.GetByteArrayAsync("/api/v1/export");
            using (var memory = new MemoryStream(bytes))
            using (var zip = new ZipArchive(memory))
            {
                Assert.Equal(new[] { "manifest.json", "posts.json", "profile.json", "subscriptions.opml", "views.json" },
                    zip.Entries.Select(e => e.FullName).Order());
                using var reader = new StreamReader(zip.GetEntry("posts.json")!.Open());
                var posts = JsonDocument.Parse(await reader.ReadToEndAsync()).RootElement;
                Assert.Equal(5, posts.GetArrayLength());
                Assert.Single(posts.EnumerateArray(), p => p.GetProperty("kind").GetString() == "like" && p.GetProperty("markdown").ValueKind == JsonValueKind.Null);
            }
            await destination.ConfirmAsync(carol, "carol");
            var preview = await Upload(carol, bytes, "preview");
            Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
            var report = await preview.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(5, report.GetProperty("new").GetInt32());
            var hash = report.GetProperty("checksum").GetString();
            var commit = await Upload(carol, bytes, "commit", hash);
            Assert.True(commit.IsSuccessStatusCode, await commit.Content.ReadAsStringAsync());
            var repeated = await Upload(carol, bytes, "commit", hash);
            Assert.True(repeated.IsSuccessStatusCode, await repeated.Content.ReadAsStringAsync());
            Assert.True((await repeated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("alreadyImported").GetBoolean());
            using var scope = destination.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var restored = await db.Posts.OrderBy(p => p.Kind).ToListAsync();
            Assert.Equal(5, restored.Count);
            Assert.Single(restored, p => p.DeletedAt is not null);
            Assert.Empty(await db.Jobs.ToListAsync());
            var restoredRoot = Assert.Single(restored, p => p.Guid == rootUrl);
            Assert.NotEqual(restoredRoot.Guid, restoredRoot.Permalink);
            var rss = XDocument.Parse(await carol.GetStringAsync($"/post/{restoredRoot.Id}/rss.xml"));
            Assert.Equal("false", (string?)Assert.Single(rss.Descendants("guid")).Attribute("isPermaLink"));
            Assert.Equal(rootUrl, Assert.Single(rss.Descendants("guid")).Value);
            Assert.Single(await db.Subscriptions.ToListAsync());
            Assert.Single(await db.Views.ToListAsync());
            Assert.Single(await db.References.ToListAsync());
        }
    }

    [Fact]
    public async Task LaterArchiveConflictsWithIndependentDestinationEditAndDoesNotPartiallyWrite()
    {
        await using var source = new ApiFixture(remote: new FakeRemote());
        await using var destination = new ApiFixture("b", new FakeRemote());
        var (alice, _, _) = await source.LoginAsync();
        var (carol, _, _) = await destination.LoginAsync("carol");
        using (alice) using (carol)
        {
            var original = await Create(alice, "post", null, "Original authored state.");
            var id = original.GetProperty("id").GetString()!;
            await source.ConfirmAsync(alice, "alice");
            await destination.ConfirmAsync(carol, "carol");
            var first = await alice.GetByteArrayAsync("/api/v1/export");
            Assert.True((await Upload(carol, first, "commit", FeedConversations.Api.Portability.ArchiveCodec.Hash(first))).IsSuccessStatusCode);
            string restoredId;
            using (var scope = destination.Services.CreateScope())
                restoredId = (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Posts.SingleAsync()).Id.ToString();
            await Edit(carol, restoredId, "Independent destination edit.");
            await Edit(alice, id, "Later source edit.");
            await Create(alice, "post", null, "Must not be partially added.");
            var later = await alice.GetByteArrayAsync("/api/v1/export");
            var preview = await Upload(carol, later, "preview");
            Assert.Equal(1, (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("conflicts").GetInt32());
            var conflict = await Upload(carol, later, "commit", FeedConversations.Api.Portability.ArchiveCodec.Hash(later));
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            using var verify = destination.Services.CreateScope();
            var db = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal("Independent destination edit.", (await db.Posts.SingleAsync()).Markdown);
            Assert.Single(await db.ImportRuns.ToListAsync());
            Assert.Empty(await db.Jobs.ToListAsync());
        }
        static async Task Edit(HttpClient client, string id, string markdown)
        {
            var current = await client.GetAsync($"/api/v1/posts/{id}");
            using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/posts/{id}") { Content = JsonContent.Create(new { markdown }) };
            patch.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
            Assert.True((await client.SendAsync(patch)).IsSuccessStatusCode);
        }
    }

    [Fact]
    public async Task MaliciousArchiveIsRejectedWithNoWrites()
    {
        await using var fixture = new ApiFixture(remote: new FakeRemote());
        var (alice, _, _) = await fixture.LoginAsync();
        using (alice)
        {
            await fixture.ConfirmAsync(alice, "alice");
            using var buffer = new MemoryStream();
            using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                var entry = zip.CreateEntry("../escape.json");
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes("{}"));
            }
            var response = await Upload(alice, buffer.ToArray(), "preview");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(await db.Posts.ToListAsync());
            Assert.Empty(await db.ImportRuns.ToListAsync());
        }
    }
}
