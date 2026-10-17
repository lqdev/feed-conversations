using System.Security.Claims;
using FeedConversations.Api.Accounts;
using FeedConversations.Api.Data;
using FeedConversations.Api.Posts;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Streaming;

public static class ChangeEndpoints
{
    public static void MapChanges(this WebApplication app)
    {
        app.MapGet("/api/v1/state", async (HttpContext ctx, AppDbContext db, ChangeJournal journal, CancellationToken ct) =>
        {
            var account = await AccountEndpoints.Current(ctx, db, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var posts = await db.Posts.Include(p => p.Owner).Where(p => !p.Hidden && p.DeletedAt == null)
                .OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.Id).Take(50).ToListAsync(ct);
            var subscriptions = await db.Subscriptions.Where(s => s.OwnerId == account.Id).ToListAsync(ct);
            var views = await db.Views.Where(v => v.OwnerId == account.Id).ToListAsync(ct);
            var sequence = await ChangeJournal.HighWater(db, ct);
            await tx.CommitAsync(ct);
            return Results.Ok(new { posts = posts.Select(PostContracts.Dto), subscriptions, views = views.Select(Reading.ReadingEndpoints.ViewDto), cursor = journal.Cursor(sequence) });
        }).RequireAuthorization();
        app.MapGet("/api/v1/changes", async (string? after, HttpContext ctx, AppDbContext db,
            ChangeJournal journal, IServiceScopeFactory scopes, CancellationToken ct) =>
        {
            var account = await AccountEndpoints.Current(ctx, db, ct);
            var cursor = ctx.Request.Headers["Last-Event-ID"].FirstOrDefault() ?? after;
            if (cursor is null) throw Infrastructure.ApiProblem.Invalid("cursor-required");
            // Validate syntax before committing SSE response headers.
            journal.Validate(cursor, 0, long.MaxValue, out _);
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.Headers["X-Accel-Buffering"] = "no";
            return TypedResults.ServerSentEvents(journal.Observe(cursor, account.Id,
                ctx.User.FindFirstValue("AspNet.Identity.SecurityStamp"), scopes, ct));
        }).RequireAuthorization();
    }
}
