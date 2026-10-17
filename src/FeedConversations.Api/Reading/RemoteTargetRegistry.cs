using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;

namespace FeedConversations.Api.Reading;

public sealed class RemoteTargetRegistry(TimeProvider clock)
{
    private readonly object gate = new();
    private readonly Dictionary<(string Owner, string Url), DateTime> allowed = [];
    public void Add(string owner, IEnumerable<string> urls)
    {
        lock (gate)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var expired in allowed.Where(p => p.Value <= now).Select(p => p.Key).ToList()) allowed.Remove(expired);
            var additions = urls.Select(UrlPolicy.Key).Distinct().Select(url => (owner, url)).Where(k => !allowed.ContainsKey(k)).ToList();
            if (allowed.Count + additions.Count > 5000) throw new ApiProblem(503, "target-validation-capacity", "Remote target validation capacity is exhausted.");
            foreach (var key in additions) allowed[key] = now.AddSeconds(60);
        }
    }
    public bool Contains(string owner, string url)
    {
        lock (gate) return allowed.TryGetValue((owner, UrlPolicy.Key(url)), out var expiry) && expiry > clock.GetUtcNow().UtcDateTime;
    }
}
