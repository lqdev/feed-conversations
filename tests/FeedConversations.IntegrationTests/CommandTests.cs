using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FeedConversations.IntegrationTests;

public sealed class CommandTests
{
    [Theory]
    [InlineData("/api/v1/auth/login", "{}")]
    [InlineData("/api/v1/auth/redeem-invite", "{}")]
    [InlineData("/api/v1/subscriptions", "{}")]
    [InlineData("/api/v1/reading/resolve", "{}")]
    [InlineData("/api/v1/reading/resolve", "{\"feedUrls\":[null]}")]
    [InlineData("/api/v1/opml/import", "{}")]
    [InlineData("/api/v1/views", "{}")]
    public async Task MissingRequiredFieldsAndNullArrayElementsAre400Not500(string route, string json)
    {
        await using var fixture = new ApiFixture(remote: new FakeRemote());
        var (client, _, _) = await fixture.LoginAsync();
        using (client)
        {
            var response = await client.PostAsync(route, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
    }

    private static async Task<HttpResponseMessage> Create(HttpClient client, string route, object? input, string key)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/" + route) { Content = input is null ? null : JsonContent.Create(input) };
        request.Headers.TryAddWithoutValidation("Idempotency-Key", key);
        return await client.SendAsync(request);
    }

    [Fact]
    public async Task WorkspaceCreateKeysRejectMismatchAndNeverResurrectDeletedResources()
    {
        await using var fixture = new ApiFixture();
        var (client, _, _) = await fixture.LoginAsync();
        using (client)
        {
            var input = new { url = "https://controlled.example/feed", label = "Synthetic", group = (string?)null };
            var key = Guid.NewGuid().ToString();
            Assert.Equal(HttpStatusCode.BadRequest, (await Create(client, "subscriptions", input, "invalid")).StatusCode);
            var created = await Create(client, "subscriptions", input, key);
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
            var replay = await Create(client, "subscriptions", input, key);
            Assert.Equal(id, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString());
            Assert.Equal(HttpStatusCode.Conflict, (await Create(client, "subscriptions", input with { label = "Different" }, key)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync("/api/v1/subscriptions/" + id)).StatusCode);
            Assert.Equal(HttpStatusCode.Gone, (await Create(client, "subscriptions", input, key)).StatusCode);
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Empty(await db.Subscriptions.ToListAsync());
            Assert.Single(await db.Receipts.ToListAsync());
        }
    }

    [Fact]
    public async Task ExpiredReceiptCannotCreateANewSubscription()
    {
        await using var fixture = new ApiFixture();
        var (client, _, _) = await fixture.LoginAsync();
        using (client)
        {
            var input = new { url = "https://controlled.example/feed", label = "Synthetic", group = (string?)null };
            var key = Guid.NewGuid().ToString();
            Assert.Equal(HttpStatusCode.Created, (await Create(client, "subscriptions", input, key)).StatusCode);
            using (var scope = fixture.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                (await db.Receipts.SingleAsync()).ExpiresAt = DateTime.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();
            }
            var expired = await Create(client, "subscriptions", input, key);
            Assert.Equal(HttpStatusCode.Conflict, expired.StatusCode);
            Assert.Equal("idempotency-expired", (await expired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task InviteHasOneConcurrentWinnerAndSameIntentCanReconcileWithoutPasswordReceiptHash()
    {
        await using var fixture = new ApiFixture();
        var (admin, _, _) = await fixture.LoginAsync();
        using (admin)
        {
            var inviteKey = Guid.NewGuid().ToString();
            var issued = await Create(admin, "admin/invites", null, inviteKey);
            var invitation = await issued.Content.ReadFromJsonAsync<JsonElement>();
            var code = invitation.GetProperty("code").GetString()!;
            var repeat = await Create(admin, "admin/invites", null, inviteKey);
            Assert.Equal(JsonValueKind.Null, (await repeat.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").ValueKind);
            var password = "Synthetic-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "!9a";
            var input = new { code, handle = "synthetic-reader", password, displayName = "Synthetic reader" };
            using var first = await Anonymous();
            using var second = await Anonymous();
            var keys = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };
            var outcomes = await Task.WhenAll(Create(first, "auth/redeem-invite", input, keys[0]), Create(second, "auth/redeem-invite", input, keys[1]));
            Assert.Single(outcomes, response => response.IsSuccessStatusCode);
            Assert.Single(outcomes, response => response.StatusCode == HttpStatusCode.BadRequest);
            var winner = outcomes[0].IsSuccessStatusCode ? 0 : 1;
            var replay = await Create(winner == 0 ? first : second, "auth/redeem-invite", input, keys[winner]);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            using var scope = fixture.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            Assert.Equal(2, await db.Users.CountAsync());
            var consumed = await db.Invites.SingleAsync();
            Assert.NotNull(consumed.ConsumedBy);
            var receipt = await db.Receipts.SingleAsync(r => r.Operation == "redeem");
            Assert.Equal(Wire.Hash(JsonSerializer.Serialize(new { consumed.Id, input.handle, input.displayName }, Wire.Json)), receipt.Hash);
            Assert.NotEqual(Wire.Hash(JsonSerializer.Serialize(input, Wire.Json)), receipt.Hash);
        }
        async Task<HttpClient> Anonymous()
        {
            var client = new HttpClient(fixture.Server.CreateHandler()) { BaseAddress = new Uri("https://a.example") };
            var response = await client.GetAsync("/api/v1/auth/csrf");
            ApiFixture.AddCookies(client, response);
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!);
            return client;
        }
    }

    [Fact]
    public async Task LoginLimitIsPerAccountAndIpRatherThanAllAnonymousUsersSharingAnIp()
    {
        await using var fixture = new ApiFixture();
        var (alice, _, _) = await fixture.LoginAsync();
        using (alice)
        {
            for (var count = 0; count < 4; count++)
                Assert.Equal(HttpStatusCode.Unauthorized, (await alice.PostAsJsonAsync("/api/v1/auth/login",
                    new { handle = "alice", password = "Synthetic-wrong-password" })).StatusCode);
            Assert.Equal(HttpStatusCode.TooManyRequests, (await alice.PostAsJsonAsync("/api/v1/auth/login",
                new { handle = "alice", password = "Synthetic-wrong-password" })).StatusCode);
            var (bob, _, _) = await fixture.LoginAsync("bob");
            using (bob) Assert.Equal(HttpStatusCode.OK, (await bob.GetAsync("/api/v1/me")).StatusCode);
        }
    }
}
