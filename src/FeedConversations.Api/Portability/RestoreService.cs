using System.Text.Json;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Posts;
using FeedConversations.Api.Reading;
using FeedConversations.Api.Streaming;
using FeedConversations.Api.Syndication;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Portability;

public sealed record RestorePreview(string Checksum, int New, int Unchanged, int Conflicts, bool AlreadyImported);
public sealed class RestoreService(AppDbContext db, InstanceOptions options, ChangeJournal journal)
{
    public async Task<RestorePreview> Preview(AccountArchive archive, string checksum, string owner, CancellationToken ct)
    {
        var imported = await db.ImportRuns.AnyAsync(r => r.OwnerId == owner && r.Checksum == checksum, ct);
        var source = $"{archive.Manifest.SourceInstance}:{archive.Manifest.SourceAccount}";
        var added = 0; var unchanged = 0; var conflicts = 0;
        foreach (var entry in archive.Posts)
        {
            var mapping = await db.RestoredMappings.SingleOrDefaultAsync(m => m.OwnerId == owner && m.SourceIdentity == source && m.OriginalGuid == entry.Guid, ct);
            if (mapping is null)
            {
                if (await db.Posts.AnyAsync(p => p.Guid == entry.Guid, ct)) conflicts++;
                else added++;
            }
            else
            {
                var post = await db.Posts.SingleAsync(p => p.Id == mapping.PostId, ct);
                var hash = Wire.Hash(JsonSerializer.Serialize(entry, Wire.Json));
                if (mapping.ImportedHash == hash) unchanged++;
                else if (post.Revision != mapping.DestinationRevision || post.DeletedAt is not null && entry.DeletedAt is null) conflicts++;
                else added++;
            }
        }
        return new RestorePreview(checksum, added, unchanged, conflicts, imported);
    }

    public async Task<RestorePreview> Commit(AccountArchive archive, string checksum, Account owner, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var preview = await Preview(archive, checksum, owner.Id, ct);
        if (preview.AlreadyImported) return preview;
        if (preview.Conflicts != 0) throw new ApiProblem(409, "restore-conflict", "The archive conflicts with destination publications.");
        var source = $"{archive.Manifest.SourceInstance}:{archive.Manifest.SourceAccount}";
        var mappings = await db.RestoredMappings.Where(m => m.OwnerId == owner.Id && m.SourceIdentity == source).ToListAsync(ct);
        var restored = new Dictionary<string, Post>();
        foreach (var entry in archive.Posts)
        {
            var mapping = mappings.SingleOrDefault(m => m.OriginalGuid == entry.Guid);
            var hash = Wire.Hash(JsonSerializer.Serialize(entry, Wire.Json));
            var post = mapping is null ? new Post { OwnerId = owner.Id, Owner = owner, Guid = entry.Guid }
                : await db.Posts.SingleAsync(p => p.Id == mapping.PostId, ct);
            restored[entry.Permalink] = post;
            if (mapping?.ImportedHash == hash) continue;
            if (mapping is null) { post.Permalink = $"{options.BaseUrl}/post/{post.Id}"; db.Posts.Add(post); }
            post.Kind = entry.Kind;
            post.Title = entry.Title;
            post.Markdown = entry.Markdown;
            post.Html = entry.DeletedAt is null ? SemanticProfile.RenderMarkdown(entry.Markdown) : null;
            post.PublishedAt = entry.PublishedAt;
            post.UpdatedAt = entry.UpdatedAt;
            post.DeletedAt = entry.DeletedAt;
            post.Revision = mapping is null ? 1 : post.Revision + 1;
            post.Provenance = JsonSerializer.Serialize(new { source, originalGuid = entry.Guid, originalPermalink = entry.Permalink }, Wire.Json);
            post.TargetUrl = entry.Relationship?.Target.Permalink;
            post.TargetIdentityJson = entry.Relationship is null ? null : JsonSerializer.Serialize(entry.Relationship.Target.Identity, Wire.Json);
            post.TargetKey = entry.Relationship?.Target.Identity.AsEntry().Key;
            if (mapping is null)
            {
                mapping = new RestoredPostMapping { OwnerId = owner.Id, SourceIdentity = source, OriginalGuid = entry.Guid, PostId = post.Id };
                db.RestoredMappings.Add(mapping);
            }
            mapping.ImportedHash = hash;
            mapping.DestinationRevision = post.Revision;
            ChangeJournal.Add(db, "post", post.Id.ToString(), post.Revision, post.DeletedAt is null ? "upsert" : "delete");
        }
        foreach (var entry in archive.Posts)
        {
            var post = restored[entry.Permalink];
            if (entry.Relationship is { } relation && restored.TryGetValue(relation.Target.Permalink, out var target))
            {
                post.TargetUrl = target.Permalink;
                post.TargetIdentityJson = JsonSerializer.Serialize(new TargetIdentity("permalink", target.Permalink), Wire.Json);
                post.TargetKey = new EntryIdentity("permalink", target.Permalink).Key;
                post.LocalTargetId = target.Id;
            }
        }
        var active = restored.Values.Where(p => p.DeletedAt is null && p.Kind is "like" or "bookmark").ToList();
        if (active.GroupBy(p => (p.Kind, p.TargetKey)).Any(g => g.Count() > 1)) throw new ApiProblem(409, "restore-association-conflict", "Duplicate active associations in archive.");
        foreach (var post in active)
            if (await db.Posts.AnyAsync(p => p.OwnerId == owner.Id && p.Id != post.Id && p.DeletedAt == null && p.Kind == post.Kind && p.TargetKey == post.TargetKey, ct))
                throw new ApiProblem(409, "restore-association-conflict", "Active association conflicts with destination.");
        var subscriptions = await db.Subscriptions.Where(s => s.OwnerId == owner.Id).ToListAsync(ct);
        var additions = archive.Subscriptions.Where(s => subscriptions.All(existing => existing.UrlKey != UrlPolicy.Key(s.Url))).ToList();
        if (subscriptions.Count + additions.Count > 100) throw new ApiProblem(409, "subscription-quota", "Restored subscriptions exceed quota.");
        foreach (var entry in additions) db.Subscriptions.Add(new Subscription { OwnerId = owner.Id, Url = entry.Url,
            UrlKey = UrlPolicy.Key(entry.Url), Label = entry.Label, Group = entry.Group });
        var views = await db.Views.Where(v => v.OwnerId == owner.Id).ToListAsync(ct);
        var viewAdditions = archive.Views.Where(v => views.All(existing => existing.Name != v.Name)).ToList();
        if (views.Count + viewAdditions.Count > 25) throw new ApiProblem(409, "view-quota", "Restored views exceed quota.");
        foreach (var view in viewAdditions) db.Views.Add(new SavedView { OwnerId = owner.Id, Name = view.Name, Definition = JsonSerializer.Serialize(view, Wire.Json) });
        foreach (var post in restored.Values.Where(p => p.LocalTargetId is not null && p.DeletedAt is null && !p.Hidden))
        {
            if (!await db.References.AnyAsync(r => r.TargetId == post.LocalTargetId && r.LocalSourceId == post.Id, ct))
            {
                var count = await db.References.CountAsync(r => r.TargetId == post.LocalTargetId, ct)
                    + db.References.Local.Count(r => r.TargetId == post.LocalTargetId && db.Entry(r).State == EntityState.Added);
                if (count >= 200 || await db.References.CountAsync(ct) + db.References.Local.Count(r => db.Entry(r).State == EntityState.Added) >= 20000)
                    throw new ApiProblem(409, "reference-quota", "Restored references exceed quota.");
                db.References.Add(new InteractionReference { TargetId = post.LocalTargetId!.Value, TargetUrl = post.TargetUrl, SourceUrl = post.Permalink,
                    SourceKey = UrlPolicy.Key(post.Permalink), LocalSourceId = post.Id, Kind = post.Kind, Status = "visible", ObservedAt = DateTime.UtcNow });
            }
        }
        owner.DisplayName = archive.Profile.DisplayName;
        owner.Bio = archive.Profile.Bio;
        owner.DirectoryVisible = archive.Profile.DirectoryVisible;
        owner.Revision++;
        db.ImportRuns.Add(new ImportRun { OwnerId = owner.Id, Checksum = checksum });
        ChangeJournal.Add(db, "workspace", owner.Id, owner.Revision, "invalidate", owner.Id);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        journal.Signal();
        return preview;
    }
}
