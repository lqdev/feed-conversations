using System.Threading.RateLimiting;
using FeedConversations.Api.Infrastructure;

namespace FeedConversations.Api.Accounts;

public sealed class LoginAttempts : IDisposable
{
    private readonly PartitionedRateLimiter<string> limiter = PartitionedRateLimiter.Create<string, string>(key =>
        RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        { PermitLimit = 5, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    public void Check(string handle, HttpContext context)
    {
        using var lease = limiter.AttemptAcquire($"{context.Connection.RemoteIpAddress}:{handle.ToUpperInvariant()}");
        if (!lease.IsAcquired)
        {
            context.Response.Headers.RetryAfter = "60";
            throw new ApiProblem(429, "rate-limited", "Login request rate exceeded.");
        }
    }
    public void Dispose() => limiter.Dispose();
}
