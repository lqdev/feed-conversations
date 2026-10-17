using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FeedConversations.Api.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Webmentions;
using FeedConversations.Api.Operations;

namespace FeedConversations.IntegrationTests;

public sealed class ApiFixture : WebApplicationFactory<Program>
{
    private readonly string suffix;
    private readonly IRemoteFetcher? remote;
    public ApiFixture(string suffix = "a", IRemoteFetcher? remote = null) { this.suffix = suffix; this.remote = remote; }
    private readonly string directory = Path.Combine(FindRoot(), ".runtime", "integration-" + Guid.NewGuid().ToString("N"));
    protected override IHost CreateHost(IHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureHostConfiguration(configuration => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Instance:Id"] = $"test-{suffix}", ["Instance:PublicBaseUrl"] = $"https://{suffix}.example",
            ["Instance:ProxyPath"] = $"/connections/{suffix}", ["Instance:FrontendOrigin"] = "https://ui.example",
            ["Instance:DataDirectory"] = directory
        }));
        return base.CreateHost(builder);
    }
    protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.ConfigureTestServices(services =>
    {
        var workers = services.Where(d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType is { } implementation && (implementation == typeof(MentionWorker) || implementation == typeof(MaintenanceWorker))).ToList();
        foreach (var worker in workers) services.Remove(worker);
        if (remote is not null) { services.RemoveAll<IRemoteFetcher>(); services.AddSingleton(remote); }
    });
    public async Task<(HttpClient Client, string Csrf, Account Account)> LoginAsync(string handle = "alice")
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{suffix}.example"), HandleCookies = false, AllowAutoRedirect = false });
        var password = "Test-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)) + "!a9";
        Account account;
        using (var scope = Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<Account>>();
            account = new Account { UserName = handle, DisplayName = handle, IsAdmin = handle == "alice" };
            var result = await users.CreateAsync(account, password);
            Assert.True(result.Succeeded);
        }
        var response = await client.GetAsync("/api/v1/auth/csrf");
        AddCookies(client, response);
        var csrf = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf);
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { handle, password });
        Assert.True(login.IsSuccessStatusCode, await login.Content.ReadAsStringAsync());
        AddCookies(client, login);
        var after = await client.GetAsync("/api/v1/auth/csrf");
        AddCookies(client, after);
        csrf = (await after.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf);
        return (client, csrf, account);
    }
    public static void AddCookies(HttpClient client, HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var values)) return;
        var cookies = client.DefaultRequestHeaders.TryGetValues("Cookie", out var previous) ?
            previous.SelectMany(v => v.Split("; ")).ToDictionary(v => v.Split('=')[0], v => v) : [];
        foreach (var value in values)
        {
            var pair = value.Split(';')[0];
            cookies[pair.Split('=')[0]] = pair;
        }
        client.DefaultRequestHeaders.Remove("Cookie");
        client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies.Values));
    }
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FeedConversations.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
}
