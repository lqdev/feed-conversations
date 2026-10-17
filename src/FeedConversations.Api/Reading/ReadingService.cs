using System.Collections.Concurrent;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;
using FeedConversations.Api.Syndication;

namespace FeedConversations.Api.Reading;

public sealed record SourceResult(string FeedUrl, string Status, DateTime ObservedAt, DateTime? FreshUntil,
    IReadOnlyList<RemoteEntry> Items, string? Error);
public sealed class ReadingService(IRemoteFetcher fetcher, FeedReader reader, TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<string, DateTime> next = [];
    public async Task<SourceResult> ResolveAsync(string url, CancellationToken ct)
    {
        var key = UrlPolicy.Key(url);
        var now = clock.GetUtcNow().UtcDateTime;
        lock (gate)
        {
            foreach (var stale in next.Where(p => p.Value <= now).Select(p => p.Key).ToList()) next.Remove(stale);
            if (next.TryGetValue(key, out var until) && until > now)
            {
                if (fetcher.TryGetCached(url, out var retained) && retained is not null)
                    return new SourceResult(url, retained.FreshUntil > now ? "fresh" : "stale", retained.ObservedAt,
                        retained.FreshUntil, reader.Parse(retained.Bytes, retained.FinalUrl), null);
                return new SourceResult(url, "rate-limited", now, until, [], "source-refresh-interval");
            }
            if (next.Count >= 1000) throw new ApiProblem(503, "reading-capacity", "Reading refresh capacity is exhausted.");
            next[key] = now.AddSeconds(30);
        }
        try
        {
            var document = await fetcher.GetAsync(url, ct);
            if (document.Status != 200)
            {
                var until = now + (document.RetryAfter ?? TimeSpan.FromSeconds(30));
                lock (gate) next[key] = until;
                return new SourceResult(url, document.Status == 429 ? "rate-limited" : "failed", now, until, [], $"remote-status-{document.Status}");
            }
            return new SourceResult(url, "fresh", document.ObservedAt, document.FreshUntil,
                reader.Parse(document.Bytes, document.FinalUrl), null);
        }
        catch (ApiProblem e) { return new SourceResult(url, "failed", now, null, [], e.Code); }
    }
}
