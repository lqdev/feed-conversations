using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;

namespace FeedConversations.Api.Reading;

public sealed record ViewDefinition(int Version, string Name, bool IncludeLocal, string[] SourceFeedUrls,
    string? TextContains, string? TextExcludes, string Sort, string[] EntryKinds)
{
    public void Validate(IReadOnlySet<string> subscribed)
    {
        if (Version != 1 || string.IsNullOrWhiteSpace(Name) || Name.Length > 100
            || SourceFeedUrls.Length > 10 || SourceFeedUrls.Select(UrlPolicy.Key).Distinct().Count() != SourceFeedUrls.Length
            || SourceFeedUrls.Any(u => !subscribed.Contains(UrlPolicy.Key(u)))
            || Sort != "published-desc" || EntryKinds.Length == 0 || EntryKinds.Length > 4
            || EntryKinds.Distinct().Count() != EntryKinds.Length || EntryKinds.Any(k => k is not ("post" or "reply" or "like" or "bookmark"))
            || TextContains?.Length > 200 || TextExcludes?.Length > 200)
            throw ApiProblem.Invalid("invalid-view-definition");
    }
}
