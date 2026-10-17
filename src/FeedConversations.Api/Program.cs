using System.Security.Claims;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FeedConversations.Api.Accounts;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Syndication;
using FeedConversations.Api.Posts;
using FeedConversations.Api.Streaming;
using FeedConversations.Api.PublicDocuments;
using FeedConversations.Api.Webmentions;
using FeedConversations.Api.Reading;
using FeedConversations.Api.Conversations;
using FeedConversations.Api.Portability;
using FeedConversations.Api.Operations;
using FeedConversations.ServiceDefaults;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddServiceDefaults();
var instance = builder.Configuration.GetSection("Instance").Get<InstanceOptions>() ?? new();
instance.Validate(builder.Environment.IsDevelopment());
Directory.CreateDirectory(instance.DataDirectory);
builder.Services.AddSingleton(instance);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(
    $"Data Source={Path.Combine(instance.DataDirectory, "app.db")};Foreign Keys=True;Default Timeout=5"));
builder.Services.AddDataProtection().SetApplicationName($"FeedConversations:{instance.Id}")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(instance.DataDirectory, "keys")));
builder.Services.AddIdentity<Account, IdentityRole>(options =>
{
    options.Password.RequiredLength = 12;
    options.User.RequireUniqueEmail = false;
    options.Lockout.MaxFailedAccessAttempts = 5;
}).AddEntityFrameworkStores<AppDbContext>().AddDefaultTokenProviders();
builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.Name = $"fc-{instance.Id}";
    options.Cookie.Path = instance.ProxyPath;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
    options.Events.OnValidatePrincipal = async context =>
    {
        var users = context.HttpContext.RequestServices.GetRequiredService<UserManager<Account>>();
        var account = await users.GetUserAsync(context.Principal!);
        var stamp = context.Principal?.FindFirstValue("AspNet.Identity.SecurityStamp");
        if (account is null || account.Disabled || account.SecurityStamp != stamp) context.RejectPrincipal();
    };
});
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
    options.Cookie.Name = $"fc-csrf-{instance.Id}";
    options.Cookie.Path = instance.ProxyPath;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
});
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ProblemHandler>();
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.AddSingleton<IRemoteFetcher, SafeRemoteFetcher>();
builder.Services.AddSingleton<FeedReader>();
builder.Services.AddSingleton<ChangeJournal>();
builder.Services.AddScoped<PostService>();
builder.Services.AddSingleton<RssWriter>();
builder.Services.AddSingleton<WebmentionProtocol>();
builder.Services.AddHostedService<MentionWorker>();
builder.Services.AddSingleton<ReadingService>();
builder.Services.AddSingleton<OpmlCodec>();
builder.Services.AddSingleton<RemoteTargetRegistry>();
builder.Services.AddScoped<ThreadAssembler>();
builder.Services.AddSingleton<ArchiveCodec>();
builder.Services.AddScoped<RestoreService>();
builder.Services.AddHostedService<MaintenanceWorker>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealth>("database");
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = 429;
    foreach (var (name, limit) in new[] { ("login", 5), ("invite", 5), ("mention", 30), ("resolve", 30), ("thread", 30), ("feed", 120) })
        options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(
            $"{context.Connection.RemoteIpAddress}:{context.User.Identity?.Name}:{name}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.OnRejected = async (context, ct) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new { type = "urn:feed-conversations:problem:rate-limited",
            title = "Request rate exceeded.", status = 429, code = "rate-limited",
            traceId = context.HttpContext.TraceIdentifier }, ct);
    };
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = ResourceLimits.ArchiveBytes);
var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=FULL;");
    var saved = await db.Instance.SingleOrDefaultAsync();
    if (saved is null)
    {
        db.Instance.Add(new InstanceState { InstanceId = instance.Id, PublicBaseUrl = instance.BaseUrl });
        await db.SaveChangesAsync();
    }
    else if (saved.InstanceId != instance.Id || saved.PublicBaseUrl != instance.BaseUrl)
        throw new InvalidOperationException("Instance identity/public origin differs from its initialized database.");
}
if (BackupCommands.Run(instance, args) || await AdminCommands.RunAsync(app, args)) return;
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        if (context.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
        {
            if (context.Request.Headers.Origin is { Count: > 0 } origin
                && origin != instance.FrontendOrigin && origin != instance.BaseUrl)
                throw new ApiProblem(403, "origin-rejected", "This request origin is not configured.");
            try { await context.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(context); }
            catch (AntiforgeryValidationException) { throw new ApiProblem(403, "csrf-rejected", "Refresh the session token."); }
        }
    }
    await next(context);
});
app.MapDefaultEndpoints();
app.MapOpenApi();
app.MapGet("/api/v1/instance", () => new { id = instance.Id, publicBaseUrl = instance.BaseUrl,
    profile = "atom-schema-v1", capabilities = new[] { "notes", "replies", "likes", "bookmarks" },
    directoryUrl = $"{instance.BaseUrl}/data/subs.opml" });
app.MapAccounts();
app.MapPosts();
app.MapChanges();
app.MapPublicDocuments();
app.MapWebmentions();
app.MapReading();
app.MapThreads();
app.MapPortability();
app.Run();

public partial class Program;
