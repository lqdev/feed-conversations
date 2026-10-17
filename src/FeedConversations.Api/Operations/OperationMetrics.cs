using System.Diagnostics.Metrics;

namespace FeedConversations.Api.Operations;

public static class OperationMetrics
{
    private static readonly Meter Meter = new("FeedConversations");
    public static readonly Histogram<double> RemoteDuration = Meter.CreateHistogram<double>("remote.duration", "ms");
    public static readonly Histogram<long> RemoteBytes = Meter.CreateHistogram<long>("remote.response.bytes", "By");
    public static readonly UpDownCounter<long> CacheBytes = Meter.CreateUpDownCounter<long>("remote.cache.bytes", "By");
    public static readonly UpDownCounter<long> Inflight = Meter.CreateUpDownCounter<long>("remote.inflight");
    public static readonly Counter<long> RemoteFailures = Meter.CreateCounter<long>("remote.failures");
    public static readonly Counter<long> JobAttempts = Meter.CreateCounter<long>("notification.attempts");
    public static readonly Histogram<long> QueueDepth = Meter.CreateHistogram<long>("notification.queue.depth");
    public static readonly UpDownCounter<long> Observers = Meter.CreateUpDownCounter<long>("journal.observers");
    public static readonly Histogram<long> JournalLag = Meter.CreateHistogram<long>("journal.lag");
    public static readonly Counter<long> Resets = Meter.CreateCounter<long>("journal.resets");
    public static readonly Counter<long> ThreadPartials = Meter.CreateCounter<long>("thread.partials");
}
