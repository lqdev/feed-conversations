using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Xml.Linq;

namespace FeedConversations.IntegrationTests;

public sealed class PublicationTests
{
    private static async Task<(JsonElement Post, string ETag)> Publish(HttpClient client, object command, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/posts") { Content = JsonContent.Create(command) };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString());
        var response = await client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (json.GetProperty("post"), response.Headers.ETag!.Tag);
    }
    [Fact]
    public async Task RealCsrfLoginPublicationRssRevisionAndDeletion()
    {
        await using var fixture = new ApiFixture();
        var (client, _, _) = await fixture.LoginAsync();
        using (client)
        {
            var command = new { title = "Synthetic note", markdown = "Hello **feeds**.", relationship = (object?)null };
            var key = Guid.NewGuid().ToString();
            var (post, etag) = await Publish(client, command, key);
            var (replay, _) = await Publish(client, command, key);
            Assert.Equal(post.GetProperty("id").GetString(), replay.GetProperty("id").GetString());
            var id = post.GetProperty("id").GetString();
            var permalink = post.GetProperty("permalink").GetString();
            foreach (var path in new[] { $"/post/{id}/rss.xml", "/users/alice/rss.xml", "/users/rss.xml" })
            {
                var xml = XDocument.Parse(await client.GetStringAsync(path));
                var item = Assert.Single(xml.Root!.Element("channel")!.Elements("item"));
                Assert.Equal(permalink, item.Element("guid")!.Value);
                Assert.Contains("<strong>feeds</strong>", item.Element("description")!.Value);
            }
            Assert.Contains("h-entry", await client.GetStringAsync($"/post/{id}"));
            var missing = await client.PatchAsJsonAsync($"/api/v1/posts/{id}", new { markdown = "Edited." });
            Assert.Equal((HttpStatusCode)428, missing.StatusCode);
            using var patch = new HttpRequestMessage(HttpMethod.Patch, $"/api/v1/posts/{id}") { Content = JsonContent.Create(new { markdown = "Edited." }) };
            patch.Headers.TryAddWithoutValidation("If-Match", etag);
            var edited = await client.SendAsync(patch);
            Assert.True(edited.IsSuccessStatusCode, await edited.Content.ReadAsStringAsync());
            var current = await edited.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(post.GetProperty("guid").GetString(), current.GetProperty("guid").GetString());
            Assert.Equal(post.GetProperty("publishedAt").GetString(), current.GetProperty("publishedAt").GetString());
            using var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/posts/{id}");
            delete.Headers.TryAddWithoutValidation("If-Match", edited.Headers.ETag!.Tag);
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(delete)).StatusCode);
            Assert.Equal(HttpStatusCode.Gone, (await client.GetAsync($"/post/{id}")).StatusCode);
        }
    }
    [Fact]
    public async Task LocalReplyAndBodylessLikeHaveIndependentIdentityAndCounts()
    {
        await using var fixture = new ApiFixture();
        var (alice, _, _) = await fixture.LoginAsync();
        using (alice)
        {
            var (root, _) = await Publish(alice, new { markdown = "Root", relationship = (object?)null, title = (string?)null });
            var (bob, _, _) = await fixture.LoginAsync("bob");
            using (bob)
            {
                var url = root.GetProperty("permalink").GetString()!;
                object Relation(string kind) => new { kind, target = new { permalink = url, identity = new { kind = "permalink", value = url } } };
                var (reply, _) = await Publish(bob, new { markdown = "Response", relationship = Relation("reply"), title = (string?)null });
                var (like, _) = await Publish(bob, new { markdown = (string?)null, relationship = Relation("like"), title = (string?)null });
                var (duplicate, _) = await Publish(bob, new { markdown = (string?)null, relationship = Relation("like"), title = (string?)null });
                Assert.Equal(like.GetProperty("id").GetString(), duplicate.GetProperty("id").GetString());
                var comments = XDocument.Parse(await alice.GetStringAsync($"/post/{root.GetProperty("id").GetString()}/comments.xml"));
                Assert.Equal(reply.GetProperty("guid").GetString(), Assert.Single(comments.Descendants("item")).Element("guid")!.Value);
                var forbidden = await alice.PatchAsJsonAsync($"/api/v1/posts/{reply.GetProperty("id").GetString()}", new { markdown = "Steal" });
                Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
            }
        }
    }
    [Fact]
    public async Task AnonymousApiIs401AndCsrfIsRequiredBeforeLogin()
    {
        await using var fixture = new ApiFixture();
        using var client = fixture.CreateClient();
        client.BaseAddress = new Uri("https://a.example");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/v1/auth/login", new { handle = "alice", password = "not-a-password" })).StatusCode);
    }
}
