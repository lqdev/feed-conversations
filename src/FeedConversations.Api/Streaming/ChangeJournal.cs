using System.Collections.Concurrent;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using FeedConversations.Api.Accounts;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Operations;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Streaming;

public sealed record StreamCursor(string Instance, string Incarnation, long Sequence);
public sealed record ChangeEnvelope(string ResourceType, string ResourceId, int Revision, string Operation);
public sealed class ChangeJournal(InstanceOptions options, IDataProtectionProvider protection, TimeProvider clock)
{
    private readonly string incarnation = Guid.NewGuid().ToString("N");
    private readonly IDataProtector protector = protection.CreateProtector("journal-cursor-v1");
    private readonly ConcurrentDictionary<Guid, (string Owner, Channel<bool> Signal)> observers = [];
    private readonly object gate = new();
    public int ActiveObservers => observers.Count;
    public string Cursor(long sequence) => protector.Protect(JsonSerializer.Serialize(new StreamCursor(options.Id, incarnation, sequence), Wire.Json));
    public void Signal()
    {
        foreach (var observer in observers.Values) observer.Signal.Writer.TryWrite(true);
    }
    public static void Add(AppDbContext db, string type, string id, int revision, string operation, string? owner = null)
        => db.Changes.Add(new LocalChange { ResourceType = type, ResourceId = id, Revision = revision,
            Operation = operation, OwnerId = owner, At = DateTime.UtcNow });
    public static Task<long> HighWater(AppDbContext db, CancellationToken ct) =>
        db.Database.SqlQueryRaw<long>("SELECT COALESCE((SELECT seq FROM sqlite_sequence WHERE name='Changes'),0) AS Value").SingleAsync(ct);

    public string? Validate(string cursor, long low, long high, out long sequence)
    {
        sequence = 0;
        StreamCursor parsed;
        try { parsed = JsonSerializer.Deserialize<StreamCursor>(protector.Unprotect(cursor), Wire.Json)!; }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or JsonException or ArgumentException)
        { throw ApiProblem.Invalid("invalid-cursor"); }
        if (parsed is null) throw ApiProblem.Invalid("invalid-cursor");
        sequence = parsed.Sequence;
        if (parsed.Instance != options.Id) return "instance-changed";
        if (parsed.Incarnation != incarnation) return "incarnation-changed";
        if (sequence > high) return "future-cursor";
        if (sequence < Math.Max(0, low - 1)) return "retention-expired";
        return null;
    }

    public async IAsyncEnumerable<SseItem<object>> Observe(string after, string owner, string? securityStamp,
        IServiceScopeFactory scopes, [EnumeratorCancellation] CancellationToken token)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        lock (gate)
        {
            if (observers.Count >= 100 || observers.Values.Count(o => o.Owner == owner) >= 3)
                throw new ApiProblem(503, "stream-capacity", "The stream capacity is exhausted.");
            observers[id] = (owner, channel);
            OperationMetrics.Observers.Add(1);
        }
        try
        {
            long sequence;
            using (var initialScope = scopes.CreateScope())
            {
                var db = initialScope.ServiceProvider.GetRequiredService<AppDbContext>();
                var high = await HighWater(db, token);
                var low = await db.Changes.MinAsync(c => (long?)c.Sequence, token) ?? high + 1;
                var reason = Validate(after, low, high, out sequence);
                if (reason is not null)
                {
                    OperationMetrics.Resets.Add(1, new KeyValuePair<string, object?>("reason", reason));
                    yield return new SseItem<object>(new { reason }, "reset-required");
                    yield break;
                }
            }
            var heartbeat = clock.GetUtcNow().AddSeconds(15);
            while (!token.IsCancellationRequested)
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var account = await db.Users.AsNoTracking().SingleOrDefaultAsync(a => a.Id == owner, token);
                if (account is null || account.Disabled || account.SecurityStamp != securityStamp) yield break;
                var low = await db.Changes.MinAsync(c => (long?)c.Sequence, token) ?? await HighWater(db, token) + 1;
                if (sequence < Math.Max(0, low - 1))
                {
                    OperationMetrics.Resets.Add(1, new KeyValuePair<string, object?>("reason", "slow-consumer"));
                    yield return new SseItem<object>(new { reason = "slow-consumer" }, "reset-required");
                    yield break;
                }
                var batch = await db.Changes.AsNoTracking().Where(c => c.Sequence > sequence)
                    .OrderBy(c => c.Sequence).Take(100).ToListAsync(token);
                OperationMetrics.JournalLag.Record(Math.Max(0, await HighWater(db, token) - sequence));
                foreach (var change in batch)
                {
                    sequence = change.Sequence;
                    if (change.OwnerId is null || change.OwnerId == owner)
                        yield return new SseItem<object>(new ChangeEnvelope(change.ResourceType, change.ResourceId,
                            change.Revision, change.Operation), "change") { EventId = Cursor(sequence) };
                }
                if (batch.Count > 0)
                    yield return new SseItem<object>(new { }, "checkpoint") { EventId = Cursor(sequence) };
                if (batch.Count == 100) continue;
                if (clock.GetUtcNow() >= heartbeat)
                {
                    yield return new SseItem<object>(new { }, "heartbeat");
                    heartbeat = clock.GetUtcNow().AddSeconds(15);
                }
                using var recheck = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token, recheck.Token);
                try { await channel.Reader.ReadAsync(wait.Token); }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
        }
        finally
        {
            if (observers.TryRemove(id, out _)) OperationMetrics.Observers.Add(-1);
            channel.Writer.TryComplete();
        }
    }
}
