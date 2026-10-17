using System.Security.Cryptography;
using System.Text.Json;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Operations;
using FeedConversations.Api.Posts;
using FeedConversations.Api.Streaming;
using FeedConversations.Api.Syndication;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Webmentions;

public sealed class MentionWorker(IServiceScopeFactory scopes, IRemoteFetcher fetcher, WebmentionProtocol protocol,
    FeedReader reader, InstanceOptions options, ChangeJournal journal, TimeProvider clock, ILogger<MentionWorker> logger) : BackgroundService
{
    private static readonly int[] RetrySeconds = [5, 30, 120, 600, 1800];
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await ProcessOneAsync(stoppingToken))
                    await Task.Delay(TimeSpan.FromMilliseconds(500), clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                logger.LogError("Notification worker failed with {ExceptionType}; retrying local work.", e.GetType().Name);
                await Task.Delay(TimeSpan.FromSeconds(2), clock, stoppingToken);
            }
        }
    }
    public async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        MentionJob claimed;
        var now = clock.GetUtcNow().UtcDateTime;
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var job = await db.Jobs.Where(j => j.State != "accepted" && j.State != "failed" && j.State != "unsupported"
                && j.DueAt <= now && (j.LeaseUntil == null || j.LeaseUntil < now)).OrderBy(j => j.DueAt).FirstOrDefaultAsync(ct);
            if (job is null) return false;
            job.LeaseToken = Guid.NewGuid().ToString("N");
            job.LeaseUntil = now.AddSeconds(45);
            job.State = job.Direction == "out" ? "discovering" : "verifying";
            job.Attempt++;
            job.UpdatedAt = now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            claimed = job;
            OperationMetrics.JobAttempts.Add(1, new KeyValuePair<string, object?>("direction", claimed.Direction));
        }
        string state;
        string? error = null;
        TimeSpan? retryAfter = null;
        SourceEvidence? evidence = null;
        var withdraw = false;
        try
        {
            var document = await fetcher.GetFreshAsync(claimed.Direction == "out" ? claimed.TargetUrl : claimed.SourceUrl, ct);
            retryAfter = document.RetryAfter;
            if (claimed.Direction == "out")
            {
                if (document.Status is >= 400)
                    throw RemoteFailure(document);
                var endpoint = protocol.Discover(document);
                if (endpoint is null) state = "unsupported";
                else
                {
                    var response = await fetcher.PostAsync(endpoint, claimed.SourceUrl, claimed.TargetUrl, ct);
                    retryAfter = response.RetryAfter;
                    if (response.Status is >= 200 and <= 299) state = "accepted";
                    else throw RemoteFailure(response);
                }
            }
            else if (document.Status == 410) { state = "accepted"; withdraw = true; }
            else
            {
                if (document.Status != 200) throw RemoteFailure(document);
                evidence = protocol.Extract(document, claimed.SourceUrl, claimed.TargetUrl);
                if (!evidence.Backlink) { state = "accepted"; withdraw = true; }
                else
                {
                    if (evidence.SingletonUrl is { } singleton)
                    {
                        var feed = await fetcher.GetFreshAsync(singleton, ct);
                        evidence = protocol.CompareFeed(evidence, feed, claimed.SourceUrl, claimed.TargetUrl, reader);
                    }
                    state = "accepted";
                }
            }
        }
        catch (ApiProblem problem)
        {
            error = problem.Code;
            state = problem.Code == "remote-permanent" || claimed.Attempt >= 6 ? "failed" : "retrying";
        }
        using (var scope = scopes.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var current = await db.Jobs.SingleAsync(j => j.Id == claimed.Id, ct);
            // An edit/re-notification invalidates the old lease before it can clear newer intent.
            if (current.LeaseToken != claimed.LeaseToken || current.SourceRevision != claimed.SourceRevision) return true;
            if (claimed.Direction == "in" && state == "accepted")
            {
                var key = UrlPolicy.Key(claimed.SourceUrl);
                var reference = await db.References.SingleOrDefaultAsync(r => r.TargetId == claimed.TargetId && r.SourceKey == key, ct);
                if (reference is null && !withdraw && evidence is not null)
                {
                    var posts = scope.ServiceProvider.GetRequiredService<PostService>();
                    try
                    {
                        await posts.CheckReferenceCapacity(claimed.TargetId!.Value, ct);
                        var autoVisible = options.TrustedOrigins.Contains(new Uri(claimed.SourceUrl).GetLeftPart(UriPartial.Authority))
                            && evidence.Status is not ("ambiguous" or "incomplete");
                        if (!autoVisible && await db.References.CountAsync(r => r.Status == "pending", ct) >= ResourceLimits.PendingReferences)
                            throw new ApiProblem(409, "pending-reference-quota", "Pending reference capacity exhausted.");
                    }
                    catch (ApiProblem p) { state = "failed"; error = p.Code; }
                    if (state != "failed")
                    {
                        reference = new InteractionReference { TargetId = claimed.TargetId!.Value, TargetUrl = claimed.TargetUrl, SourceUrl = claimed.SourceUrl, SourceKey = key };
                        db.References.Add(reference);
                    }
                }
                if (reference is not null)
                {
                    reference.ObservedAt = clock.GetUtcNow().UtcDateTime;
                    if (withdraw) reference.Status = "withdrawn";
                    else if (evidence is not null)
                    {
                        var approved = reference.Status == "visible" && reference.Kind == evidence.Kind;
                        reference.Kind = evidence.Status == "supported" ? evidence.Kind : "mention";
                        reference.AuthorUrl = evidence.AuthorUrl;
                        reference.AuthorFeedUrl = evidence.AuthorFeedUrl;
                        reference.CommentsUrl = evidence.CommentsUrl;
                        reference.IdentityJson = evidence.Entry is null ? null : JsonSerializer.Serialize(evidence.Entry.Identity, Wire.Json);
                        reference.Error = evidence.Status is "ambiguous" or "incomplete" ? evidence.Status : null;
                        if (reference.Status != "hidden")
                        {
                            var visible = (approved || options.TrustedOrigins.Contains(new Uri(claimed.SourceUrl).GetLeftPart(UriPartial.Authority))) && reference.Error is null;
                            if (!visible && reference.Status != "pending" && await db.References.CountAsync(r => r.Status == "pending", ct) >= ResourceLimits.PendingReferences)
                            {
                                reference.Status = "hidden";
                                state = "failed";
                                error = "pending-reference-quota";
                            }
                            else reference.Status = visible ? "visible" : "pending";
                        }
                    }
                    ChangeJournal.Add(db, "interactions", reference.TargetId.ToString(), 0, "invalidate");
                }
            }
            current.State = state;
            current.Error = error;
            current.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            current.LeaseToken = null;
            current.LeaseUntil = null;
            if (state == "retrying")
            {
                var seconds = RetrySeconds[Math.Min(current.Attempt - 1, RetrySeconds.Length - 1)];
                var jitter = RandomNumberGenerator.GetInt32(0, Math.Max(2, seconds / 10));
                current.DueAt = current.UpdatedAt.AddSeconds(Math.Max(seconds + jitter, Math.Clamp(retryAfter?.TotalSeconds ?? 0, 0, 86400)));
            }
            if (current.SourceId is { } sourceId)
            {
                var owner = await db.Posts.Where(p => p.Id == sourceId).Select(p => p.OwnerId).SingleAsync(ct);
                ChangeJournal.Add(db, "delivery", sourceId.ToString(), current.SourceRevision, "invalidate", owner);
            }
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            journal.Signal();
            OperationMetrics.QueueDepth.Record(await db.Jobs.CountAsync(j => j.State != "accepted" && j.State != "unsupported" && j.State != "failed", ct));
        }
        return true;
    }
    private static ApiProblem RemoteFailure(RemoteDocument document) =>
        new(502, document.Status is >= 400 and < 500 and not (408 or 429) ? "remote-permanent" : "remote-status",
            "Remote endpoint did not accept the operation.");
}
