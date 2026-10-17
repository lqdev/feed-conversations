using FeedConversations.Api.Accounts;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Posts;
using FeedConversations.Api.Streaming;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Webmentions;

public static class WebmentionEndpoints
{
    public static void MapWebmentions(this WebApplication app)
    {
        app.MapPost("/webmention", async (HttpContext ctx, AppDbContext db, PostService posts,
            TimeProvider clock, CancellationToken ct) =>
        {
            if (ctx.Request.ContentLength > 16384 || !ctx.Request.HasFormContentType) throw ApiProblem.Invalid("invalid-webmention-form");
            var form = await ctx.Request.ReadFormAsync(ct);
            if (form["source"].Count != 1 || form["target"].Count != 1) throw ApiProblem.Invalid("invalid-webmention-form");
            var source = UrlPolicy.Parse(form["source"].ToString()).AbsoluteUri;
            var target = UrlPolicy.Parse(form["target"].ToString()).AbsoluteUri;
            if (UrlPolicy.Key(source) == UrlPolicy.Key(target)) throw ApiProblem.Invalid("self-webmention");
            var targetId = posts.LocalId(target) ?? throw ApiProblem.Invalid("target-not-owned");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            PostService.CheckVisible(await PostEndpoints.Get(db, targetId, ct));
            var job = await db.Jobs.SingleOrDefaultAsync(j => j.Direction == "in" && j.SourceUrl == source && j.TargetUrl == target, ct);
            if (job is null)
            {
                await posts.CheckJobCapacity(ct);
                job = new MentionJob { Direction = "in", SourceUrl = source, TargetUrl = target, TargetId = targetId };
                db.Jobs.Add(job);
            }
            job.SourceRevision++;
            job.State = "pending";
            job.Attempt = 0;
            job.LeaseToken = null;
            job.LeaseUntil = null;
            job.DueAt = job.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Accepted();
        }).RequireRateLimiting("mention");
        var api = app.MapGroup("/api/v1");
        api.MapGet("/posts/{id:guid}/interactions", async (Guid id, string? kind, AppDbContext db, CancellationToken ct) =>
        {
            PostService.CheckVisible(await PostEndpoints.Get(db, id, ct));
            var query = db.References.AsNoTracking().Where(r => r.TargetId == id && r.Status == "visible");
            if (kind is not null) query = query.Where(r => r.Kind == kind);
            var items = await query.OrderByDescending(r => r.ObservedAt).Take(200)
                .Select(r => new { r.Id, r.SourceUrl, r.Kind, r.Status, r.ObservedAt, r.AuthorUrl }).ToListAsync(ct);
            return Results.Ok(new { items, count = items.Count, nextCursor = (string?)null });
        });
        api.MapGet("/interactions/pending", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var account = await AccountEndpoints.Current(ctx, db, ct);
            return Results.Ok(await db.References.Where(r => r.Status == "pending" &&
                (account.IsAdmin || db.Posts.Any(p => p.Id == r.TargetId && p.OwnerId == account.Id)))
                .OrderBy(r => r.ObservedAt).Take(200).ToListAsync(ct));
        }).RequireAuthorization();
        foreach (var approve in new[] { true, false })
            api.MapPost($"/interactions/{{id:guid}}/{(approve ? "approve" : "hide")}",
                async (Guid id, HttpContext ctx, AppDbContext db, ChangeJournal journal, CancellationToken ct) =>
                {
                    var account = await AccountEndpoints.Current(ctx, db, ct);
                    var reference = await db.References.SingleOrDefaultAsync(r => r.Id == id, ct)
                        ?? throw new ApiProblem(404, "reference-missing", "Reference not found.");
                    var target = await db.Posts.SingleAsync(p => p.Id == reference.TargetId, ct);
                    if (target.OwnerId != account.Id && !account.IsAdmin) throw new ApiProblem(403, "target-owner-required", "Target owner access is required.");
                    if (approve && reference.Error is "ambiguous" or "incomplete") throw new ApiProblem(409, "unverified-reference", "Ambiguous evidence cannot be approved.");
                    reference.Status = approve ? "visible" : "hidden";
                    ChangeJournal.Add(db, "interactions", target.Id.ToString(), target.Revision, "invalidate");
                    await db.SaveChangesAsync(ct);
                    journal.Signal();
                    return Results.NoContent();
                }).RequireAuthorization();
        api.MapGet("/admin/jobs", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            await AccountEndpoints.Admin(ctx, db, ct);
            return Results.Ok(await db.Jobs.OrderByDescending(j => j.UpdatedAt).Take(100)
                .Select(j => new { j.Id, j.Direction, j.State, j.Attempt, j.DueAt, j.Error }).ToListAsync(ct));
        }).RequireAuthorization();
        foreach (var hide in new[] { true, false })
            api.MapPost($"/admin/posts/{{id:guid}}/{(hide ? "hide" : "unhide")}",
                async (Guid id, HttpContext ctx, AppDbContext db, PostService posts, ChangeJournal journal, CancellationToken ct) =>
                {
                    var admin = await AccountEndpoints.Admin(ctx, db, ct);
                    await using var tx = await db.Database.BeginTransactionAsync(ct);
                    var post = await PostEndpoints.Get(db, id, ct);
                    post.Hidden = hide;
                    post.ModeratedBy = admin.Id;
                    post.ModeratedAt = post.UpdatedAt = DateTime.UtcNow;
                    post.Revision++;
                    await posts.IndexAndQueueAsync(post, ct);
                    ChangeJournal.Add(db, "post", id.ToString(), post.Revision, hide ? "delete" : "upsert");
                    await db.SaveChangesAsync(ct);
                    await tx.CommitAsync(ct);
                    journal.Signal();
                    return Results.NoContent();
                }).RequireAuthorization();
    }
}
