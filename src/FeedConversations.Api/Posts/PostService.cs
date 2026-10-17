using System.Net;
using System.Text.Json;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Streaming;
using FeedConversations.Api.Syndication;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Posts;

public sealed class PostService(AppDbContext db, InstanceOptions options, IRemoteFetcher fetcher,
    FeedReader reader, ChangeJournal journal, TimeProvider clock)
{
    public Guid? LocalId(string url)
    {
        var uri = UrlPolicy.Parse(url);
        var own = new Uri(options.BaseUrl);
        if (uri.GetLeftPart(UriPartial.Authority) != own.GetLeftPart(UriPartial.Authority) || uri.Query.Length != 0) return null;
        var segments = uri.AbsolutePath.Split('/');
        return segments.Length == 3 && segments[1] == "post" && Guid.TryParseExact(segments[2], "D", out var id) ? id : null;
    }

    public static void ValidateContent(string kind, string? title, string? markdown)
    {
        SemanticProfile.ValidateText(markdown, ResourceLimits.MarkdownBytes, "markdown-too-large");
        SemanticProfile.ValidateText(title, ResourceLimits.MarkdownBytes, "title-too-large");
        if (title?.EnumerateRunes().Count() > ResourceLimits.TitleScalars) throw ApiProblem.Invalid("title-too-long");
        if (kind is "post" or "reply" && string.IsNullOrWhiteSpace(markdown)) throw ApiProblem.Invalid("markdown-required");
        if (kind == "like" && (title is not null || markdown is not null)) throw ApiProblem.Invalid("like-has-authored-content");
    }

    public async Task<(Post Post, bool Created)> CreateAsync(Account owner, CreatePost command, string key, CancellationToken ct)
    {
        if (!Guid.TryParseExact(key, "D", out _)) throw ApiProblem.Invalid("idempotency-key-required");
        var kind = command.Relationship?.Kind ?? "post";
        if (kind is not ("post" or "reply" or "like" or "bookmark") || command.Relationship?.Kind == "post") throw ApiProblem.Invalid("invalid-kind");
        ValidateContent(kind, command.Title, command.Markdown);
        var identity = command.Relationship?.Target.Identity.AsEntry();
        var target = command.Relationship?.Target.Permalink.Trim();
        if (target is not null)
        {
            var uri = UrlPolicy.Parse(target);
            if (IPAddress.TryParse(uri.Host, out var ip) && !UrlPolicy.PublicAddress(ip)
                && !options.DevelopmentOrigins.Contains(uri.GetLeftPart(UriPartial.Authority))
                && uri.GetLeftPart(UriPartial.Authority) != options.BaseUrl)
                throw ApiProblem.Invalid("unsafe-target");
            if (identity!.Kind == "permalink" && UrlPolicy.Key(identity.Value) != UrlPolicy.Key(target))
                throw ApiProblem.Invalid("target-identity-mismatch");
        }
        var hash = Wire.Hash(JsonSerializer.Serialize(command, Wire.Json));
        var existingReceipt = await db.Receipts.AsNoTracking().SingleOrDefaultAsync(r =>
            r.OwnerId == owner.Id && r.Operation == "post" && r.Key == key, ct);
        if (existingReceipt is not null) return (await ReplayAsync(existingReceipt, hash, ct), false);
        if (identity?.Kind == "feed-guid")
        {
            var document = await fetcher.GetAsync(identity.FeedUrl!, ct);
            if (document.Status != 200) throw new ApiProblem(502, "target-feed-unavailable", "Target feed is unavailable.");
            var matches = reader.Parse(document.Bytes, document.FinalUrl).Where(e => e.Identity.Guid == identity.Guid).ToList();
            if (matches.Count != 1 || UrlPolicy.Key(matches[0].Permalink) != UrlPolicy.Key(target!))
                throw new ApiProblem(422, "structured-target-unresolved", "The structured target is ambiguous or does not match its feed.");
        }
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var receipt = await db.Receipts.SingleOrDefaultAsync(r => r.OwnerId == owner.Id && r.Operation == "post" && r.Key == key, ct);
        if (receipt is not null) return (await ReplayAsync(receipt, hash, ct), false);
        var targetKey = identity?.Key;
        if (kind is "like" or "bookmark")
        {
            var duplicate = await db.Posts.Include(p => p.Owner).SingleOrDefaultAsync(p => p.OwnerId == owner.Id
                && p.Kind == kind && p.TargetKey == targetKey && p.DeletedAt == null, ct);
            if (duplicate is not null)
            {
                if (duplicate.Hidden) throw new ApiProblem(403, "moderation-restriction", "This association is restricted by moderation.");
                await ReceiptAsync(owner.Id, key, hash, duplicate.Id, ct);
                await db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
                return (duplicate, false);
            }
        }
        var now = clock.GetUtcNow().UtcDateTime;
        var post = new Post { OwnerId = owner.Id, Owner = owner, Kind = kind, Title = command.Title,
            Markdown = command.Markdown, Html = SemanticProfile.RenderMarkdown(command.Markdown),
            PublishedAt = now, UpdatedAt = now, TargetUrl = target, TargetKey = targetKey,
            TargetIdentityJson = identity is null ? null : JsonSerializer.Serialize(command.Relationship!.Target.Identity, Wire.Json),
            LocalTargetId = target is null ? null : LocalId(target) };
        post.Guid = post.Permalink = $"{options.BaseUrl}/post/{post.Id:D}";
        db.Posts.Add(post);
        await IndexAndQueueAsync(post, ct);
        await ReceiptAsync(owner.Id, key, hash, post.Id, ct);
        ChangeJournal.Add(db, "post", post.Id.ToString(), post.Revision, "upsert");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        journal.Signal();
        return (post, true);
    }

    private async Task<Post> ReplayAsync(CommandReceipt receipt, string hash, CancellationToken ct)
    {
        if (receipt.ExpiresAt <= clock.GetUtcNow().UtcDateTime)
            throw new ApiProblem(409, "idempotency-expired", "Reconcile the previous command before creating another.");
        if (receipt.Hash != hash) throw new ApiProblem(409, "idempotency-mismatch", "The command key belongs to different input.");
        var post = await db.Posts.Include(p => p.Owner).SingleAsync(p => p.Id == receipt.ResourceId, ct);
        CheckVisible(post);
        return post;
    }
    private async Task ReceiptAsync(string owner, string key, string hash, Guid id, CancellationToken ct)
    {
        if (await db.Receipts.CountAsync(r => r.OwnerId == owner, ct) >= 1000)
            throw new ApiProblem(409, "receipt-quota", "The command receipt quota is exhausted.");
        db.Receipts.Add(new CommandReceipt { OwnerId = owner, Operation = "post", Key = key, Hash = hash,
            ResourceId = id, ExpiresAt = clock.GetUtcNow().UtcDateTime.AddHours(24) });
    }

    public static void CheckVisible(Post post)
    {
        if (post.DeletedAt is not null) throw new ApiProblem(410, "post-deleted", "This publication was withdrawn.");
        if (post.Hidden) throw new ApiProblem(404, "post-unavailable", "This publication is unavailable.");
    }

    public async Task IndexAndQueueAsync(Post post, CancellationToken ct)
    {
        if (post.TargetUrl is null) return;
        var now = clock.GetUtcNow().UtcDateTime;
        if (post.LocalTargetId is { } localId)
        {
            var target = await db.Posts.SingleOrDefaultAsync(p => p.Id == localId, ct)
                ?? throw ApiProblem.Invalid("local-target-missing");
            var reference = await db.References.SingleOrDefaultAsync(r => r.TargetId == localId && r.LocalSourceId == post.Id, ct);
            if (reference is null)
            {
                CheckVisible(target);
                await CheckReferenceCapacity(localId, ct);
                reference = new InteractionReference { TargetId = localId, TargetUrl = post.TargetUrl, SourceUrl = post.Permalink,
                    SourceKey = UrlPolicy.Key(post.Permalink), LocalSourceId = post.Id, Kind = post.Kind, Status = "visible" };
                db.References.Add(reference);
            }
            if (post.DeletedAt is not null || post.Hidden) reference.Status = "withdrawn";
            else if (reference.Status == "withdrawn") reference.Status = "visible";
            reference.ObservedAt = now;
            ChangeJournal.Add(db, "interactions", localId.ToString(), post.Revision, "invalidate");
        }
        else
        {
            var job = await db.Jobs.SingleOrDefaultAsync(j => j.Direction == "out" && j.SourceUrl == post.Permalink
                && j.TargetUrl == post.TargetUrl, ct);
            if (job is null)
            {
                await CheckJobCapacity(ct);
                job = new MentionJob { Direction = "out", SourceId = post.Id, SourceUrl = post.Permalink, TargetUrl = post.TargetUrl };
                db.Jobs.Add(job);
            }
            else if (job.State is "accepted" or "unsupported" or "failed") await CheckJobCapacity(ct);
            job.SourceRevision = post.Revision;
            job.State = "pending";
            job.Attempt = 0;
            job.DueAt = now;
            job.UpdatedAt = now;
            job.LeaseToken = null;
            job.LeaseUntil = null;
            job.Error = null;
        }
    }

    public async Task CheckJobCapacity(CancellationToken ct)
    {
        if (await db.Jobs.CountAsync(j => j.State != "accepted" && j.State != "unsupported" && j.State != "failed", ct) >= ResourceLimits.Jobs)
            throw new ApiProblem(503, "job-capacity", "Notification capacity is exhausted.");
    }
    public async Task CheckReferenceCapacity(Guid target, CancellationToken ct)
    {
        if (await db.References.CountAsync(ct) >= ResourceLimits.References
            || await db.References.CountAsync(r => r.TargetId == target, ct) >= ResourceLimits.ReferencesPerTarget)
            throw new ApiProblem(409, "reference-quota", "The reference quota is exhausted.");
    }
}
