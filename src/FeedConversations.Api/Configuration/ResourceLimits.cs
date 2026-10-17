namespace FeedConversations.Api.Configuration;

public sealed class ResourceLimits
{
    public const int MarkdownBytes = 256 * 1024;
    public const int TitleScalars = 500;
    public const int Subscriptions = 100;
    public const int Views = 25;
    public const int RemoteBytes = 1024 * 1024;
    public const int CacheBytes = 32 * 1024 * 1024;
    public const int Jobs = 1000;
    public const int ReferencesPerTarget = 200;
    public const int References = 20000;
    public const int PendingReferences = 500;
    public const int ArchiveBytes = 25 * 1024 * 1024;
    public const int ArchivePosts = 20000;
}
