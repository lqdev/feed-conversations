using Microsoft.AspNetCore.Identity;

namespace FeedConversations.Api.Data;

public sealed class Account : IdentityUser
{
    public string DisplayName { get; set; } = "";
    public string Bio { get; set; } = "";
    public bool DirectoryVisible { get; set; }
    public bool Disabled { get; set; }
    public bool IsAdmin { get; set; }
    public int Revision { get; set; } = 1;
}

public sealed class Invite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public string CreatorId { get; set; } = "";
    public string? ConsumedBy { get; set; }
}

public sealed class Post
{
    public Guid Id { get; set; } = System.Guid.NewGuid();
    public string OwnerId { get; set; } = "";
    public Account Owner { get; set; } = null!;
    public string Guid { get; set; } = "";
    public string Permalink { get; set; } = "";
    public DateTime PublishedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int Revision { get; set; } = 1;
    public string Kind { get; set; } = "post";
    public string? Title { get; set; }
    public string? Markdown { get; set; }
    public string? Html { get; set; }
    public string? TargetUrl { get; set; }
    public string? TargetIdentityJson { get; set; }
    public string? TargetKey { get; set; }
    public Guid? LocalTargetId { get; set; }
    public DateTime? DeletedAt { get; set; }
    public bool Hidden { get; set; }
    public string? ModerationReason { get; set; }
    public string? ModeratedBy { get; set; }
    public DateTime? ModeratedAt { get; set; }
    public string? Provenance { get; set; }
}

public sealed class Subscription
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = "";
    public string Url { get; set; } = "";
    public string UrlKey { get; set; } = "";
    public string Label { get; set; } = "";
    public string? Group { get; set; }
    public int Revision { get; set; } = 1;
}

public sealed class SavedView
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string OwnerId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Definition { get; set; } = "";
    public int Revision { get; set; } = 1;
}

public sealed class InteractionReference
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TargetId { get; set; }
    public string? TargetUrl { get; set; }
    public string SourceUrl { get; set; } = "";
    public string SourceKey { get; set; } = "";
    public Guid? LocalSourceId { get; set; }
    public string Kind { get; set; } = "mention";
    public string Status { get; set; } = "pending";
    public string? IdentityJson { get; set; }
    public string? AuthorUrl { get; set; }
    public string? AuthorFeedUrl { get; set; }
    public string? CommentsUrl { get; set; }
    public DateTime ObservedAt { get; set; }
    public string? Error { get; set; }
}

public sealed class MentionJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Direction { get; set; } = "";
    public string SourceUrl { get; set; } = "";
    public string TargetUrl { get; set; } = "";
    public Guid? SourceId { get; set; }
    public Guid? TargetId { get; set; }
    public int SourceRevision { get; set; }
    public string State { get; set; } = "pending";
    public int Attempt { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public string? LeaseToken { get; set; }
    public string? Error { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed class LocalChange
{
    public long Sequence { get; set; }
    public string ResourceType { get; set; } = "";
    public string ResourceId { get; set; } = "";
    public int Revision { get; set; }
    public string Operation { get; set; } = "";
    public string? OwnerId { get; set; }
    public DateTime At { get; set; }
}

public sealed class InstanceState
{
    public int Id { get; set; } = 1;
    public string InstanceId { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
}

public sealed class CommandReceipt
{
    public long Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string Operation { get; set; } = "";
    public string Key { get; set; } = "";
    public string Hash { get; set; } = "";
    public Guid ResourceId { get; set; }
    public DateTime ExpiresAt { get; set; }
}

public sealed class ImportRun
{
    public long Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string Checksum { get; set; } = "";
}

public sealed class RestoredPostMapping
{
    public long Id { get; set; }
    public string OwnerId { get; set; } = "";
    public string SourceIdentity { get; set; } = "";
    public string OriginalGuid { get; set; } = "";
    public Guid PostId { get; set; }
    public string ImportedHash { get; set; } = "";
    public int DestinationRevision { get; set; }
}
