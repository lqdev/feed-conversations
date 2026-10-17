using System.Text.Json;
using FeedConversations.Api.Accounts;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Streaming;
using FeedConversations.Api.Syndication;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Posts;

public static class PostEndpoints
{
    public static void MapPosts(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");
        api.MapGet("/posts", async (string? author, string? cursor, AppDbContext db, CancellationToken ct) =>
        {
            var query = db.Posts.Include(p => p.Owner).Where(p => !p.Hidden && p.DeletedAt == null);
            if (author is not null) query = query.Where(p => p.Owner.UserName == author);
            if (cursor is not null)
            {
                if (!Guid.TryParse(cursor, out var id)) throw ApiProblem.Invalid("invalid-page-cursor");
                var previous = await db.Posts.FindAsync([id], ct) ?? throw ApiProblem.Invalid("invalid-page-cursor");
                query = query.Where(p => p.PublishedAt < previous.PublishedAt || p.PublishedAt == previous.PublishedAt && p.Id.CompareTo(id) < 0);
            }
            var page = await query.OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.Id).Take(51).ToListAsync(ct);
            return Results.Ok(new { items = page.Take(50).Select(PostContracts.Dto), nextCursor = page.Count > 50 ? page[49].Id.ToString() : null });
        });
        api.MapGet("/posts/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var post = await Get(db, id, ct);
            PostService.CheckVisible(post);
            ctx.Response.Headers.ETag = Wire.ETag("post", id.ToString(), post.Revision);
            return Results.Ok(PostContracts.Dto(post));
        });
        api.MapPost("/posts", async (CreatePost command, HttpContext ctx, AppDbContext db, PostService service, CancellationToken ct) =>
        {
            var owner = await AccountEndpoints.Current(ctx, db, ct);
            var (post, created) = await service.CreateAsync(owner, command, ctx.Request.Headers["Idempotency-Key"].ToString(), ct);
            ctx.Response.Headers.ETag = Wire.ETag("post", post.Id.ToString(), post.Revision);
            var delivery = post.TargetUrl is null ? "not-required" : post.LocalTargetId is not null ? "local"
                : await db.Jobs.Where(j => j.SourceId == post.Id).Select(j => j.State).SingleOrDefaultAsync(ct) ?? "not-queued";
            var result = new { post = PostContracts.Dto(post), delivery = new { state = delivery }, created };
            return created ? Results.Created($"/api/v1/posts/{post.Id}", result) : Results.Ok(result);
        }).RequireAuthorization();
        api.MapPatch("/posts/{id:guid}", async (Guid id, JsonElement patch, HttpContext ctx, AppDbContext db,
            PostService service, ChangeJournal journal, CancellationToken ct) =>
        {
            var owner = await AccountEndpoints.Current(ctx, db, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var post = await Get(db, id, ct);
            Own(post, owner);
            PostService.CheckVisible(post);
            Wire.CheckMatch(ctx.Request, "post", id.ToString(), post.Revision);
            if (patch.ValueKind != JsonValueKind.Object || patch.EnumerateObject().Any(p => p.Name is not ("title" or "markdown")))
                throw ApiProblem.Invalid("immutable-post-fields");
            if (patch.TryGetProperty("title", out var title)) post.Title = Text(title);
            if (patch.TryGetProperty("markdown", out var markdown)) post.Markdown = Text(markdown);
            PostService.ValidateContent(post.Kind, post.Title, post.Markdown);
            post.Html = SemanticProfile.RenderMarkdown(post.Markdown);
            post.UpdatedAt = DateTime.UtcNow;
            post.Revision++;
            await service.IndexAndQueueAsync(post, ct);
            ChangeJournal.Add(db, "post", id.ToString(), post.Revision, "upsert");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            journal.Signal();
            ctx.Response.Headers.ETag = Wire.ETag("post", id.ToString(), post.Revision);
            return Results.Ok(PostContracts.Dto(post));
        }).RequireAuthorization();
        api.MapDelete("/posts/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db,
            PostService service, ChangeJournal journal, CancellationToken ct) =>
        {
            var owner = await AccountEndpoints.Current(ctx, db, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var post = await Get(db, id, ct);
            Own(post, owner);
            Wire.CheckMatch(ctx.Request, "post", id.ToString(), post.Revision);
            if (post.DeletedAt is null)
            {
                post.DeletedAt = post.UpdatedAt = DateTime.UtcNow;
                post.Title = post.Markdown = post.Html = null;
                post.Revision++;
                await service.IndexAndQueueAsync(post, ct);
                ChangeJournal.Add(db, "post", id.ToString(), post.Revision, "delete");
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
            journal.Signal();
            return Results.NoContent();
        }).RequireAuthorization();
        api.MapGet("/posts/{id:guid}/delivery", async (Guid id, HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var owner = await AccountEndpoints.Current(ctx, db, ct);
            Own(await Get(db, id, ct), owner);
            return Results.Ok(await db.Jobs.Where(j => j.SourceId == id).Select(j => new { j.Id, j.State, j.Attempt, j.DueAt, j.Error }).ToListAsync(ct));
        }).RequireAuthorization();
        api.MapPost("/posts/{id:guid}/delivery/retry", async (Guid id, HttpContext ctx, AppDbContext db, PostService service, CancellationToken ct) =>
        {
            var owner = await AccountEndpoints.Current(ctx, db, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var post = await Get(db, id, ct);
            Own(post, owner);
            var job = await db.Jobs.SingleOrDefaultAsync(j => j.SourceId == id, ct);
            if (job is null || job.State is not ("failed" or "unsupported")) throw new ApiProblem(409, "retry-unavailable", "This delivery is not eligible for retry.");
            await service.CheckJobCapacity(ct);
            await service.IndexAndQueueAsync(post, ct);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return Results.Accepted();
        }).RequireAuthorization();
    }
    public static Task<Post> Get(AppDbContext db, Guid id, CancellationToken ct) =>
        GetCore(db, id, ct);
    private static async Task<Post> GetCore(AppDbContext db, Guid id, CancellationToken ct) =>
        await db.Posts.Include(p => p.Owner).SingleOrDefaultAsync(p => p.Id == id, ct)
        ?? throw new ApiProblem(404, "post-missing", "Publication not found.");
    private static string? Text(JsonElement value) => value.ValueKind == JsonValueKind.Null ? null
        : value.ValueKind == JsonValueKind.String ? value.GetString() : throw ApiProblem.Invalid();
    private static void Own(Post p, Account a)
    {
        if (p.OwnerId != a.Id) throw new ApiProblem(403, "owner-required", "Only the owner can change this publication.");
    }
}
