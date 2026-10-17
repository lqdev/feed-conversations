using System.Text.Json;
using FeedConversations.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Infrastructure;

public sealed record CreateIntent(string Key, string Hash, Guid? ResourceId);

public sealed class CreateReceipts(AppDbContext db, TimeProvider clock)
{
    public Task<CreateIntent> Prepare(string owner, string operation, HttpRequest request, object? input, CancellationToken ct) =>
        Prepare(owner, operation, request.Headers["Idempotency-Key"].ToString(), input, ct);

    public async Task<CreateIntent> Prepare(string owner, string operation, string key, object? input, CancellationToken ct)
    {
        if (!Guid.TryParseExact(key, "D", out _)) throw ApiProblem.Invalid("idempotency-key-required");
        var hash = Wire.Hash(JsonSerializer.Serialize(input, Wire.Json));
        var previous = await db.Receipts.SingleOrDefaultAsync(r => r.OwnerId == owner && r.Operation == operation && r.Key == key, ct);
        if (previous is not null)
        {
            if (previous.ExpiresAt <= clock.GetUtcNow().UtcDateTime)
                throw new ApiProblem(409, "idempotency-expired", "Reconcile the previous command before creating another.");
            if (previous.Hash != hash) throw new ApiProblem(409, "idempotency-mismatch", "The command key belongs to different input.");
            return new CreateIntent(key, hash, previous.ResourceId);
        }
        if (await db.Receipts.CountAsync(r => r.OwnerId == owner, ct) >= 1000)
            throw new ApiProblem(409, "receipt-quota", "The command receipt quota is exhausted.");
        return new CreateIntent(key, hash, null);
    }
    public void Record(string owner, string operation, CreateIntent intent, Guid resource)
    {
        db.Receipts.Add(new CommandReceipt { OwnerId = owner, Operation = operation, Key = intent.Key, Hash = intent.Hash,
            ResourceId = resource, ExpiresAt = clock.GetUtcNow().UtcDateTime.AddHours(24) });
    }
}
