using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Streaming;
using FeedConversations.Api.Syndication;
using FeedConversations.Api.Webmentions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FeedConversations.IntegrationTests;

public sealed class WebmentionTests
{
    private static async Task<(string Url, Guid Id)> Create(HttpClient client, string? target = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/posts")
        {
            Content = JsonContent.Create(new
            {
                title = (string?)null,
                markdown = "Synthetic owned response.",
                relationship = target is null ? null :
                new { kind = "reply", target = new { permalink = target, identity = new { kind = "permalink", value = target } } }
            })
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var post = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("post");
        return (post.GetProperty("permalink").GetString()!, post.GetProperty("id").GetGuid());
    }
    private static MentionWorker Worker(ApiFixture fixture, IRemoteFetcher fetcher)
    {
        var services = fixture.Services;
        return new MentionWorker(services.GetRequiredService<IServiceScopeFactory>(), fetcher, services.GetRequiredService<WebmentionProtocol>(),
            services.GetRequiredService<FeedReader>(), services.GetRequiredService<InstanceOptions>(), services.GetRequiredService<ChangeJournal>(),
            services.GetRequiredService<TimeProvider>(), services.GetRequiredService<ILogger<MentionWorker>>());
    }
    private static RemoteDocument Html(string url, string html, int status = 200) =>
        new(new Uri(url), status, "text/html", Encoding.UTF8.GetBytes(html), [], DateTime.UtcNow, DateTime.UtcNow, true, null);

    [Fact]
    public async Task OrdinaryTargetWithoutEndpointIsUnsupportedRatherThanFalseAcceptance()
    {
        const string target = "https://controlled.example/ordinary";
        var remote = new FakeRemote();
        remote.Documents[target] = Html(target, "<p>An ordinary page without interaction support.</p>");
        await using var fixture = new ApiFixture(remote: remote);
        var (alice, _, _) = await fixture.LoginAsync();
        using (alice) using (var worker = Worker(fixture, remote))
        {
            await Create(alice, target);
            Assert.True(await worker.ProcessOneAsync(default));
            using var scope = fixture.Services.CreateScope();
            Assert.Equal("unsupported", (await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.SingleAsync()).State);
            Assert.Empty(remote.Writes);
        }
    }

    [Fact]
    public async Task VerifiedUntrustedReplyRequiresModerationAndDuplicateWithdrawalIsIdempotent()
    {
        const string source = "https://controlled.example/source";
        var remote = new FakeRemote();
        await using var fixture = new ApiFixture(remote: remote);
        var (alice, _, _) = await fixture.LoginAsync();
        using (alice)
        {
            var (target, id) = await Create(alice);
            remote.Documents[source] = Html(source, $"<article class='h-entry'><a class='u-url' href='{source}'>Self</a><a class='u-in-reply-to' href='{target}'>Parent</a><div class='e-content'>FOREIGN_BODY_TRANSIENT_ONLY</div></article>");
            using var worker = Worker(fixture, remote);
            async Task Notify()
            {
                var response = await alice.PostAsync("/webmention", new FormUrlEncodedContent(new Dictionary<string, string> { ["source"] = source, ["target"] = target }));
                Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
                Assert.True(await worker.ProcessOneAsync(default));
            }
            await Notify();
            await Notify();
            Guid referenceId;
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var reference = Assert.Single(await db.References.ToListAsync());
                Assert.Equal("reply", reference.Kind);
                Assert.Equal("pending", reference.Status);
                referenceId = reference.Id;
            }
            Assert.Equal(HttpStatusCode.NoContent, (await alice.PostAsync($"/api/v1/interactions/{referenceId}/approve", null)).StatusCode);
            await Notify();
            using (var scope = fixture.Services.CreateScope())
                Assert.Equal("visible", Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().References.ToListAsync()).Status);
            var visible = await alice.GetFromJsonAsync<JsonElement>($"/api/v1/posts/{id}/interactions?kind=reply");
            Assert.Equal(1, visible.GetProperty("count").GetInt32());
            remote.Documents[source] = Html(source, "", 503);
            await Notify();
            using (var scope = fixture.Services.CreateScope())
                Assert.Equal("visible", Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().References.ToListAsync()).Status);
            remote.Documents[source] = Html(source, "", 410);
            await Notify();
            using (var scope = fixture.Services.CreateScope())
                Assert.Equal("withdrawn", Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().References.ToListAsync()).Status);
        }
    }

    [Fact]
    public async Task ActiveCommentsReadDoesNotRelayChangedKindAsVerifiedReplyAndQueuesReverification()
    {
        const string source = "https://controlled.example/changing";
        var remote = new FakeRemote();
        await using var fixture = new ApiFixture(remote: remote);
        var (alice, _, _) = await fixture.LoginAsync();
        using (alice)
        {
            var (target, id) = await Create(alice);
            remote.Documents[source] = Html(source, $"<article class='h-entry'><a class='u-url' href='{source}'>Self</a><a class='u-in-reply-to' href='{target}'>Parent</a><div class='e-content'>Original reply.</div></article>");
            Assert.Equal(HttpStatusCode.Accepted, (await alice.PostAsync("/webmention",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["source"] = source, ["target"] = target }))).StatusCode);
            using var worker = Worker(fixture, remote);
            Assert.True(await worker.ProcessOneAsync(default));
            using (var scope = fixture.Services.CreateScope())
            {
                var reference = await scope.ServiceProvider.GetRequiredService<AppDbContext>().References.SingleAsync();
                Assert.Equal(HttpStatusCode.NoContent, (await alice.PostAsync($"/api/v1/interactions/{reference.Id}/approve", null)).StatusCode);
            }
            remote.Documents[source] = Html(source, $"<article class='h-entry'><a class='u-url' href='{source}'>Self</a><a class='u-like-of' href='{target}'>Liked</a><div class='e-content'>Changed source.</div></article>");
            var comments = await alice.GetAsync($"/post/{id}/comments.xml");
            Assert.Equal(HttpStatusCode.OK, comments.StatusCode);
            Assert.Contains("ambiguous-identity", comments.Headers.GetValues("X-Conversation-Partial"));
            Assert.DoesNotContain("https://schema.org/Comment", await comments.Content.ReadAsStringAsync());
            Assert.True(await worker.ProcessOneAsync(default));
            using var verify = fixture.Services.CreateScope();
            var changed = await verify.ServiceProvider.GetRequiredService<AppDbContext>().References.SingleAsync();
            Assert.Equal("like", changed.Kind);
            Assert.Equal("pending", changed.Status);
        }
    }

    [Fact]
    public async Task GenericBacklinkAndNestedQuoteCannotBecomeReply()
    {
        const string source = "https://controlled.example/quote";
        var remote = new FakeRemote();
        await using var fixture = new ApiFixture(remote: remote);
        var (alice, _, _) = await fixture.LoginAsync();
        using (alice)
        {
            var (target, _) = await Create(alice);
            remote.Documents[source] = Html(source, $"<article class='h-entry'><a class='u-url' href='{source}'>Self</a><a href='{target}'>Mention</a><article class='h-entry'><a class='u-in-reply-to' href='{target}'>Quoted</a></article></article>");
            await alice.PostAsync("/webmention", new FormUrlEncodedContent(new Dictionary<string, string> { ["source"] = source, ["target"] = target }));
            using var worker = Worker(fixture, remote);
            Assert.True(await worker.ProcessOneAsync(default));
            using var scope = fixture.Services.CreateScope();
            Assert.Equal("mention", Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().References.ToListAsync()).Kind);
        }
    }

    [Fact]
    public async Task LeasedOldDeliveryCannotClearNewerEditIntent()
    {
        var remote = new BlockingRemote();
        await using var fixture = new ApiFixture(remote: remote);
        var (alice, _, _) = await fixture.LoginAsync();
        using (alice)
        {
            var (_, id) = await Create(alice, "https://controlled.example/target");
            using var worker = Worker(fixture, remote);
            var old = worker.ProcessOneAsync(default);
            await remote.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var current = await alice.GetAsync($"/api/v1/posts/{id}");
            using var edit = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/posts/{id}") { Content = JsonContent.Create(new { markdown = "New revision." }) };
            edit.Headers.TryAddWithoutValidation("If-Match", current.Headers.ETag!.Tag);
            Assert.Equal(HttpStatusCode.OK, (await alice.SendAsync(edit)).StatusCode);
            remote.Release.SetResult();
            Assert.True(await old);
            using (var scope = fixture.Services.CreateScope())
            {
                var job = Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.ToListAsync());
                Assert.Equal("pending", job.State);
                Assert.Equal(2, job.SourceRevision);
                Assert.Null(job.LeaseToken);
            }
            Assert.True(await worker.ProcessOneAsync(default));
            using (var scope = fixture.Services.CreateScope())
                Assert.Equal("accepted", Assert.Single(await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.ToListAsync()).State);
        }
    }

    private sealed class BlockingRemote : IRemoteFetcher
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<RemoteDocument> GetAsync(string url, CancellationToken ct) => Task.FromResult(
            Html(url, "<html><head><link rel='webmention' href='/mention?mode=synthetic'></head></html>"));
        public async Task<RemoteDocument> PostAsync(string url, string source, string target, CancellationToken ct)
        {
            Assert.Equal("https://controlled.example/mention?mode=synthetic", url);
            Started.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return Html(url, "", 202);
        }
    }

    [Theory]
    [InlineData(498, true)]
    [InlineData(499, true)]
    [InlineData(500, false)]
    public async Task PendingAdmissionLimitNeverEvictsOtherReferences(int existing, bool allowed)
    {
        const string source = "https://controlled.example/pending-admission";
        var remote = new FakeRemote();
        await using var fixture = new ApiFixture(remote: remote);
        var (client, _, account) = await fixture.LoginAsync();
        using (client)
        {
            var (target, _) = await Create(client);
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var targets = Enumerable.Range(0, 3).Select(_ => new Post { OwnerId = account.Id, PublishedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow }).ToArray();
                foreach (var post in targets) post.Guid = post.Permalink = "https://a.example/post/" + post.Id;
                db.Posts.AddRange(targets);
                db.References.AddRange(Enumerable.Range(0, existing).Select(i => new InteractionReference
                {
                    TargetId = targets[i % targets.Length].Id,
                    SourceUrl = $"https://controlled.example/existing-{i}",
                    SourceKey = UrlPolicy.Key($"https://controlled.example/existing-{i}"),
                    Status = "pending",
                    ObservedAt = DateTime.UtcNow
                }));
                await db.SaveChangesAsync();
            }
            remote.Documents[source] = Html(source, $"<article class='h-entry'><a class='u-url' href='{source}'>Self</a><a class='u-in-reply-to' href='{target}'>Parent</a></article>");
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsync("/webmention",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["source"] = source, ["target"] = target }))).StatusCode);
            using var worker = Worker(fixture, remote);
            Assert.True(await worker.ProcessOneAsync(default));
            using var verify = fixture.Services.CreateScope();
            var data = verify.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(existing + (allowed ? 1 : 0), await data.References.CountAsync(r => r.Status == "pending"));
            Assert.Equal(existing, await data.References.CountAsync(r => r.SourceUrl != source));
            var job = await data.Jobs.SingleAsync();
            Assert.Equal(allowed ? "accepted" : "failed", job.State);
            Assert.Equal(allowed ? null : "pending-reference-quota", job.Error);
        }
    }

    [Fact]
    public async Task ExpiredLeaseIsRecoveredByNewWorkerAndOldCompletionCannotReplaceIt()
    {
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var blocked = new BlockingRemote();
        await using var fixture = new ApiFixture(remote: blocked, clock: clock);
        var (client, _, _) = await fixture.LoginAsync();
        using (client) using (var first = Worker(fixture, blocked))
        {
            await Create(client, "https://controlled.example/target");
            var abandoned = first.ProcessOneAsync(default);
            await blocked.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            try
            {
                clock.Advance(TimeSpan.FromSeconds(46));
                var recovered = new FakeRemote();
                recovered.Documents["https://controlled.example/target"] = Html("https://controlled.example/target",
                    "<link rel='webmention' href='/mention?mode=synthetic'>");
                using var second = Worker(fixture, recovered);
                Assert.True(await second.ProcessOneAsync(default));
            }
            finally { blocked.Release.TrySetResult(); }
            Assert.True(await abandoned);
            using var scope = fixture.Services.CreateScope();
            var job = await scope.ServiceProvider.GetRequiredService<AppDbContext>().Jobs.SingleAsync();
            Assert.Equal("accepted", job.State);
            Assert.Equal(2, job.Attempt);
            Assert.Null(job.LeaseToken);
        }
    }
}
