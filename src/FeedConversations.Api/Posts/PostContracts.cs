using System.Text.Json;
using FeedConversations.Api.Data;
using FeedConversations.Api.Syndication;

namespace FeedConversations.Api.Posts;

public sealed record TargetIdentity(string Kind, string? Value = null, string? FeedUrl = null, string? Guid = null)
{
    public EntryIdentity AsEntry() => Kind switch
    {
        "permalink" when Value is not null && FeedUrl is null && Guid is null => new EntryIdentity(Kind, Value),
        "feed-guid" when FeedUrl is not null && Guid is not null && Value is null => new EntryIdentity(Kind, Guid, FeedUrl, Guid),
        _ => throw Infrastructure.ApiProblem.Invalid("invalid-target-identity")
    };
}
public sealed record TargetReference(string Permalink, TargetIdentity Identity);
public sealed record Relationship(string Kind, TargetReference Target);
public sealed record CreatePost(string? Title = null, string? Markdown = null, Relationship? Relationship = null);
public static class PostContracts
{
    public static object Dto(Post p) => new
    {
        p.Id, p.Guid, p.Permalink,
        author = new { id = p.OwnerId, handle = p.Owner.UserName, displayName = p.Owner.DisplayName },
        p.PublishedAt, p.UpdatedAt, p.Revision, p.Kind, p.Title, p.Markdown, p.Html,
        relationship = p.TargetUrl is null ? null : new
        {
            kind = p.Kind,
            target = new { permalink = p.TargetUrl, identity = JsonSerializer.Deserialize<TargetIdentity>(p.TargetIdentityJson!, Infrastructure.Wire.Json) }
        }
    };
}
