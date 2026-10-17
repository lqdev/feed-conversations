using System.Security.Claims;
using System.Text.Json;
using FeedConversations.Api.Accounts;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Posts;
using FeedConversations.Api.Reading;
using FeedConversations.Api.Streaming;
using FeedConversations.Api.Syndication;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Portability;

public static class PortabilityEndpoints
{
    public static void MapPortability(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapGet("/export", async (HttpContext ctx, AppDbContext db, InstanceOptions o,
            ArchiveCodec codec, CancellationToken ct) =>
        {
            Confirmed(ctx);
            var owner = await AccountEndpoints.Current(ctx, db, ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var minimumBytes = await db.Database.SqlQuery<long>($"""
                SELECT COALESCE(SUM(
                    length(CAST(COALESCE("Guid",'') AS BLOB)) +
                    length(CAST(COALESCE("Permalink",'') AS BLOB)) +
                    length(CAST(COALESCE("Title",'') AS BLOB)) +
                    length(CAST(COALESCE("Markdown",'') AS BLOB)) +
                    length(CAST(COALESCE("TargetUrl",'') AS BLOB)) +
                    length(CAST(COALESCE("TargetIdentityJson",'') AS BLOB)) +
                    length(CAST(COALESCE("Provenance",'') AS BLOB))),0) AS Value
                FROM Posts WHERE OwnerId = {owner.Id}
                """).SingleAsync(ct);
            if (minimumBytes > ResourceLimits.ArchiveBytes) throw new ApiProblem(413, "archive-too-large", "Owned state exceeds the archive byte limit.");
            var posts = await db.Posts.Where(p => p.OwnerId == owner.Id).Select(p => new
            { p.Guid, p.Permalink, p.PublishedAt, p.UpdatedAt, p.Revision, p.Kind, p.Title, p.Markdown,
                p.TargetUrl, p.TargetIdentityJson, p.DeletedAt, p.Provenance }).Take(ResourceLimits.ArchivePosts + 1).ToListAsync(ct);
            if (posts.Count > ResourceLimits.ArchivePosts) throw new ApiProblem(413, "archive-post-limit", "Owned publications exceed archive limit.");
            var subscriptions = await db.Subscriptions.Where(s => s.OwnerId == owner.Id).Select(s => new OpmlEntry(s.Url, s.Label, s.Group)).ToListAsync(ct);
            var views = await db.Views.Where(v => v.OwnerId == owner.Id).ToListAsync(ct);
            var sequence = await ChangeJournal.HighWater(db, ct);
            var archive = new AccountArchive(new ArchiveManifest(1, Guid.NewGuid().ToString(), o.Id, owner.Id, o.BaseUrl,
                $"local-sequence:{sequence}", posts.Count, []),
                new ArchiveProfile(owner.DisplayName, owner.Bio, owner.DirectoryVisible),
                posts.Select(p => new ArchivePost(p.Guid, p.Permalink, p.PublishedAt, p.UpdatedAt, p.Revision, p.Kind, p.Title, p.Markdown,
                    p.TargetUrl is null ? null : new Relationship(p.Kind, new TargetReference(p.TargetUrl,
                        JsonSerializer.Deserialize<TargetIdentity>(p.TargetIdentityJson!, Wire.Json)!)), p.DeletedAt, p.Provenance)).ToList(),
                subscriptions, views.Select(v => JsonSerializer.Deserialize<ViewDefinition>(v.Definition, Wire.Json)!).ToList());
            await tx.CommitAsync(ct);
            return Results.File(codec.Write(archive), "application/zip", "account-v1.zip");
        });
        api.MapPost("/import/preview", async (HttpContext ctx, AppDbContext db, ArchiveCodec codec, RestoreService restore, CancellationToken ct) =>
        {
            Confirmed(ctx);
            var owner = await AccountEndpoints.Current(ctx, db, ct);
            var bytes = await ReadArchive(ctx.Request, ct);
            return Results.Ok(await restore.Preview(codec.Read(bytes), ArchiveCodec.Hash(bytes), owner.Id, ct));
        });
        api.MapPost("/import/commit", async (HttpContext ctx, AppDbContext db, ArchiveCodec codec, RestoreService restore, CancellationToken ct) =>
        {
            Confirmed(ctx);
            var owner = await AccountEndpoints.Current(ctx, db, ct);
            var bytes = await ReadArchive(ctx.Request, ct);
            var checksum = ArchiveCodec.Hash(bytes);
            if (ctx.Request.Headers["X-Archive-SHA256"] != checksum) throw ApiProblem.Invalid("archive-preview-hash-required");
            return Results.Ok(await restore.Commit(codec.Read(bytes), checksum, owner, ctx.Request.Headers["Idempotency-Key"].ToString(), ct));
        });
    }
    private static void Confirmed(HttpContext ctx)
    {
        if (!long.TryParse(ctx.User.FindFirstValue("confirmed-at"), out var timestamp)
            || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp is < 0 or > 300)
            throw new ApiProblem(403, "recent-auth-required", "Confirm your password before archive operations.");
    }
    private static async Task<byte[]> ReadArchive(HttpRequest request, CancellationToken ct)
    {
        if (request.ContentType != "application/zip") throw ApiProblem.Invalid("archive-content-type");
        using var buffer = new MemoryStream();
        var block = new byte[16384];
        int read;
        while ((read = await request.Body.ReadAsync(block, ct)) > 0)
        {
            if (buffer.Length + read > ResourceLimits.ArchiveBytes) throw new ApiProblem(413, "archive-too-large", "Archive exceeds byte limit.");
            buffer.Write(block, 0, read);
        }
        return buffer.ToArray();
    }
}
