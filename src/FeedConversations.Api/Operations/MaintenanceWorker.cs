using FeedConversations.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace FeedConversations.Api.Operations;

public sealed class MaintenanceWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), clock);
        while (await timer.WaitForNextTickAsync(token))
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var now = clock.GetUtcNow().UtcDateTime;
                await using var tx = await db.Database.BeginTransactionAsync(token);
                await db.Changes.Where(c => c.At < now.AddHours(-24)).ExecuteDeleteAsync(token);
                var cutoff = await db.Changes.OrderByDescending(c => c.Sequence).Skip(9999).Select(c => (long?)c.Sequence).FirstOrDefaultAsync(token);
                if (cutoff is not null) await db.Changes.Where(c => c.Sequence < cutoff).ExecuteDeleteAsync(token);
                await db.Receipts.Where(r => r.ExpiresAt < now.AddHours(-1)).ExecuteDeleteAsync(token);
                await db.Invites.Where(i => i.ExpiresAt < now.AddDays(-1)).ExecuteDeleteAsync(token);
                await db.Jobs.Where(j => (j.State == "accepted" || j.State == "unsupported") && j.UpdatedAt < now.AddDays(-7)
                    || j.State == "failed" && j.UpdatedAt < now.AddDays(-30)).ExecuteDeleteAsync(token);
                var old = await db.Jobs.Where(j => j.State == "accepted" || j.State == "unsupported" || j.State == "failed")
                    .OrderByDescending(j => j.UpdatedAt).Skip(10000).Select(j => j.Id).Take(1000).ToListAsync(token);
                if (old.Count > 0) await db.Jobs.Where(j => old.Contains(j.Id)).ExecuteDeleteAsync(token);
                await tx.CommitAsync(token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogError("Retention maintenance failed with {ExceptionType}.", e.GetType().Name); }
        }
    }
}
