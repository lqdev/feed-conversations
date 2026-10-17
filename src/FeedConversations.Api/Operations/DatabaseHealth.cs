using FeedConversations.Api.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FeedConversations.Api.Operations;

public sealed class DatabaseHealth(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await db.Database.CanConnectAsync(cancellationToken) ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy("Database unavailable.");
}
