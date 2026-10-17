using FeedConversations.Api.Accounts;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Posts;
using FeedConversations.Api.Reading;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Conversations;

public static class ThreadEndpoints
{
    public static void MapThreads(this WebApplication app)
    {
        app.MapGet("/api/v1/thread", async (string url, HttpContext ctx, AppDbContext db, PostService posts,
            RemoteTargetRegistry validated, ThreadAssembler assembler, CancellationToken ct) =>
        {
            UrlPolicy.Parse(url);
            var local = posts.LocalId(url);
            var authenticated = ctx.User.Identity?.IsAuthenticated == true;
            if (local is null)
            {
                if (!authenticated) throw new ApiProblem(401, "authentication-required", "Foreign-root hydration requires authentication.");
                var owner = AccountEndpoints.Owner(ctx);
                var targets = await db.Posts.Where(p => p.OwnerId == owner && p.TargetUrl != null).Select(p => p.TargetUrl!).Take(20001).ToListAsync(ct);
                if (!validated.Contains(owner, url) && !targets.Any(t => UrlPolicy.Key(t) == UrlPolicy.Key(url)))
                    throw new ApiProblem(403, "target-not-validated", "Read this target from a saved source before opening its thread.");
            }
            return Results.Ok(await assembler.Assemble(url, authenticated, ct));
        }).RequireRateLimiting("thread");
    }
}
