using System.Text.Json;
using FeedConversations.Api.Accounts;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Streaming;
using FeedConversations.Api.Syndication;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Reading;

public sealed record SubscriptionCommand(string Url, string Label, string? Group = null);
public sealed record ResolveCommand(string[] FeedUrls);
public sealed record OpmlPreview(string Text);
public sealed record OpmlImport(string Text, string[] SelectedUrls);
public static class ReadingEndpoints
{
    public static void MapReading(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1").RequireAuthorization();
        api.MapGet("/subscriptions", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var owner = AccountEndpoints.Owner(ctx);
            return Results.Ok(new { items = await db.Subscriptions.Where(s => s.OwnerId == owner).OrderBy(s => s.Label).Take(100).ToListAsync(ct), nextCursor = (string?)null });
        });
        api.MapPost("/subscriptions", async (SubscriptionCommand command, HttpContext ctx, AppDbContext db, ChangeJournal journal, CreateReceipts receipts, CancellationToken ct) =>
        {
            var owner = AccountEndpoints.Owner(ctx);
            var urlKey = UrlPolicy.Key(command.Url);
            if (command.Label.Length > 200 || command.Group?.Length > 500) throw ApiProblem.Invalid();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var intent = await receipts.Prepare(owner, "subscription", ctx.Request, command, ct);
            if (intent.ResourceId is { } replay)
                return Results.Ok(await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == replay && s.OwnerId == owner, ct)
                    ?? throw new ApiProblem(410, "subscription-deleted", "The original subscription was removed."));
            var existing = await db.Subscriptions.SingleOrDefaultAsync(s => s.OwnerId == owner && s.UrlKey == urlKey, ct);
            if (existing is not null)
            {
                receipts.Record(owner, "subscription", intent, existing.Id);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return Results.Ok(existing);
            }
            if (await db.Subscriptions.CountAsync(s => s.OwnerId == owner, ct) >= ResourceLimits.Subscriptions)
                throw new ApiProblem(409, "subscription-quota", "Subscription quota exhausted.");
            var subscription = new Subscription { OwnerId = owner, Url = command.Url.Trim(), UrlKey = urlKey, Label = command.Label, Group = command.Group };
            db.Subscriptions.Add(subscription);
            receipts.Record(owner, "subscription", intent, subscription.Id);
            ChangeJournal.Add(db, "subscription", subscription.Id.ToString(), 1, "upsert", owner);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            journal.Signal();
            return Results.Created($"/api/v1/subscriptions/{subscription.Id}", subscription);
        });
        api.MapDelete("/subscriptions/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, ChangeJournal journal, CancellationToken ct) =>
        {
            var owner = AccountEndpoints.Owner(ctx);
            var subscription = await db.Subscriptions.SingleOrDefaultAsync(s => s.Id == id && s.OwnerId == owner, ct)
                ?? throw new ApiProblem(404, "subscription-missing", "Subscription not found.");
            var views = await db.Views.Where(v => v.OwnerId == owner).Select(v => v.Definition).ToListAsync(ct);
            if (views.Any(v => JsonSerializer.Deserialize<ViewDefinition>(v, Wire.Json)!.SourceFeedUrls.Any(url => UrlPolicy.Key(url) == subscription.UrlKey)))
                throw new ApiProblem(409, "view-source-in-use", "Remove this source from saved views before unsubscribing.");
            db.Subscriptions.Remove(subscription);
            ChangeJournal.Add(db, "subscription", id.ToString(), subscription.Revision + 1, "delete", owner);
            await db.SaveChangesAsync(ct);
            journal.Signal();
            return Results.NoContent();
        });
        api.MapGet("/views", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var owner = AccountEndpoints.Owner(ctx);
            var views = await db.Views.Where(v => v.OwnerId == owner).OrderBy(v => v.Name).Take(25).ToListAsync(ct);
            return Results.Ok(new { items = views.Select(ViewDto), nextCursor = (string?)null });
        });
        api.MapPost("/views", async (ViewDefinition definition, HttpContext ctx, AppDbContext db, ChangeJournal journal, CreateReceipts receipts, CancellationToken ct) =>
        {
            var owner = AccountEndpoints.Owner(ctx);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var intent = await receipts.Prepare(owner, "view", ctx.Request, definition, ct);
            if (intent.ResourceId is { } replay)
            {
                var previous = await db.Views.SingleOrDefaultAsync(v => v.Id == replay && v.OwnerId == owner, ct)
                    ?? throw new ApiProblem(410, "view-deleted", "The original saved view was removed.");
                ctx.Response.Headers.ETag = Wire.ETag("view", previous.Id.ToString(), previous.Revision);
                return Results.Ok(ViewDto(previous));
            }
            await ValidateView(definition, owner, db, ct);
            if (await db.Views.CountAsync(v => v.OwnerId == owner, ct) >= ResourceLimits.Views) throw new ApiProblem(409, "view-quota", "View quota exhausted.");
            if (await db.Views.AnyAsync(v => v.OwnerId == owner && v.Name == definition.Name, ct)) throw new ApiProblem(409, "view-name-conflict", "A view already uses this name.");
            var view = new SavedView { OwnerId = owner, Name = definition.Name, Definition = JsonSerializer.Serialize(definition, Wire.Json) };
            db.Views.Add(view);
            receipts.Record(owner, "view", intent, view.Id);
            ChangeJournal.Add(db, "view", view.Id.ToString(), 1, "upsert", owner);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            journal.Signal();
            ctx.Response.Headers.ETag = Wire.ETag("view", view.Id.ToString(), view.Revision);
            return Results.Created($"/api/v1/views/{view.Id}", ViewDto(view));
        });
        api.MapPatch("/views/{id:guid}", async (Guid id, ViewDefinition definition, HttpContext ctx, AppDbContext db, ChangeJournal journal, CancellationToken ct) =>
        {
            var owner = AccountEndpoints.Owner(ctx);
            var view = await db.Views.SingleOrDefaultAsync(v => v.Id == id && v.OwnerId == owner, ct)
                ?? throw new ApiProblem(404, "view-missing", "View not found.");
            Wire.CheckMatch(ctx.Request, "view", id.ToString(), view.Revision);
            await ValidateView(definition, owner, db, ct);
            if (await db.Views.AnyAsync(v => v.Id != id && v.OwnerId == owner && v.Name == definition.Name, ct))
                throw new ApiProblem(409, "view-name-conflict", "A view already uses this name.");
            view.Name = definition.Name;
            view.Definition = JsonSerializer.Serialize(definition, Wire.Json);
            view.Revision++;
            ChangeJournal.Add(db, "view", id.ToString(), view.Revision, "upsert", owner);
            await db.SaveChangesAsync(ct);
            journal.Signal();
            ctx.Response.Headers.ETag = Wire.ETag("view", id.ToString(), view.Revision);
            return Results.Ok(ViewDto(view));
        });
        api.MapDelete("/views/{id:guid}", async (Guid id, HttpContext ctx, AppDbContext db, ChangeJournal journal, CancellationToken ct) =>
        {
            var owner = AccountEndpoints.Owner(ctx);
            var view = await db.Views.SingleOrDefaultAsync(v => v.Id == id && v.OwnerId == owner, ct)
                ?? throw new ApiProblem(404, "view-missing", "View not found.");
            Wire.CheckMatch(ctx.Request, "view", id.ToString(), view.Revision);
            db.Views.Remove(view);
            ChangeJournal.Add(db, "view", id.ToString(), view.Revision + 1, "delete", owner);
            await db.SaveChangesAsync(ct);
            journal.Signal();
            return Results.NoContent();
        });
        api.MapPost("/reading/resolve", async (ResolveCommand command, HttpContext ctx, AppDbContext db, ReadingService reading, RemoteTargetRegistry targets, CancellationToken ct) =>
        {
            var owner = AccountEndpoints.Owner(ctx);
            if (command.FeedUrls.Length > 10 || command.FeedUrls.Select(UrlPolicy.Key).Distinct().Count() != command.FeedUrls.Length)
                throw ApiProblem.Invalid("source-selection-limit");
            var allowed = (await db.Subscriptions.Where(s => s.OwnerId == owner).Select(s => s.UrlKey).ToListAsync(ct)).ToHashSet();
            if (command.FeedUrls.Any(url => !allowed.Contains(UrlPolicy.Key(url)))) throw ApiProblem.Invalid("source-not-subscribed");
            var sources = await Task.WhenAll(command.FeedUrls.Select(url => reading.ResolveAsync(url, ct)));
            targets.Add(owner, sources.SelectMany(s => s.Items).Select(e => e.Permalink));
            return Results.Ok(new { sources, coverage = new { selectedSources = sources.Length, maxSources = 10, maxItems = 500, perSourceWindow = 50 } });
        }).RequireRateLimiting("resolve");
        api.MapPost("/opml/preview", (OpmlPreview command, OpmlCodec codec) => Results.Ok(new { entries = codec.Parse(command.Text) }));
        api.MapPost("/opml/import", async (OpmlImport command, HttpContext ctx, AppDbContext db, OpmlCodec codec, ChangeJournal journal, CreateReceipts receipts, CancellationToken ct) =>
        {
            var owner = AccountEndpoints.Owner(ctx);
            var entries = codec.Parse(command.Text);
            var selected = command.SelectedUrls.Select(UrlPolicy.Key).ToHashSet();
            if (selected.Count != command.SelectedUrls.Length || selected.Any(k => entries.All(e => UrlPolicy.Key(e.Url) != k))) throw ApiProblem.Invalid("invalid-opml-selection");
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var intent = await receipts.Prepare(owner, "opml", ctx.Request, command, ct);
            if (intent.ResourceId is not null) return Results.Ok(new { imported = 0, replayed = true });
            var existing = (await db.Subscriptions.Where(s => s.OwnerId == owner).Select(s => s.UrlKey).ToListAsync(ct)).ToHashSet();
            var additions = entries.Where(e => selected.Contains(UrlPolicy.Key(e.Url)) && !existing.Contains(UrlPolicy.Key(e.Url))).ToList();
            if (existing.Count + additions.Count > 100) throw new ApiProblem(409, "subscription-quota", "Subscription quota exhausted.");
            foreach (var entry in additions)
            {
                var subscription = new Subscription { OwnerId = owner, Url = entry.Url, UrlKey = UrlPolicy.Key(entry.Url), Label = entry.Label, Group = entry.Group };
                db.Subscriptions.Add(subscription);
                ChangeJournal.Add(db, "subscription", subscription.Id.ToString(), 1, "upsert", owner);
            }
            receipts.Record(owner, "opml", intent, Guid.Empty);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            journal.Signal();
            return Results.Ok(new { imported = additions.Count });
        });
        api.MapGet("/opml/export", async (HttpContext ctx, AppDbContext db, OpmlCodec codec, CancellationToken ct) =>
        {
            var owner = AccountEndpoints.Owner(ctx);
            var entries = await db.Subscriptions.Where(s => s.OwnerId == owner).Select(s => new OpmlEntry(s.Url, s.Label, s.Group)).ToListAsync(ct);
            return Results.File(System.Text.Encoding.UTF8.GetBytes(codec.Write(entries)), "text/x-opml", "subscriptions.opml");
        });
    }
    public static object ViewDto(SavedView view) => new { view.Id, view.Name, view.Revision,
        etag = Wire.ETag("view", view.Id.ToString(), view.Revision), definition = JsonSerializer.Deserialize<ViewDefinition>(view.Definition, Wire.Json) };
    private static async Task ValidateView(ViewDefinition definition, string owner, AppDbContext db, CancellationToken ct)
    {
        var urls = (await db.Subscriptions.Where(s => s.OwnerId == owner).Select(s => s.UrlKey).ToListAsync(ct)).ToHashSet();
        definition.Validate(urls);
    }
}
