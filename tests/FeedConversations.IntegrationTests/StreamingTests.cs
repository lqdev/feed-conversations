using System.Text.Json;
using FeedConversations.Api.Data;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace FeedConversations.IntegrationTests;

public sealed class StreamingTests
{
    [Fact]
    public async Task SnapshotSubscribeRaceAndFanoutReadTheDurableJournalOncePerObserver()
    {
        await using var fixture = new ApiFixture();
        var (client, _, account) = await fixture.LoginAsync();
        using (client) using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var journal = scope.ServiceProvider.GetRequiredService<ChangeJournal>();
            var scopes = scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>();
            var cursor = journal.Cursor(await ChangeJournal.HighWater(db, default));
            var resource = Guid.NewGuid().ToString();
            ChangeJournal.Add(db, "post", resource, 2, "upsert");
            await db.SaveChangesAsync();
            journal.Signal(); // Deliberately before any subscription exists.
            var observers = Enumerable.Range(0, 3).Select(_ => journal.Observe(cursor, account.Id, account.SecurityStamp, scopes, default).GetAsyncEnumerator()).ToList();
            try
            {
                foreach (var observer in observers)
                {
                    Assert.True(await observer.MoveNextAsync());
                    Assert.Equal("change", observer.Current.EventType);
                    var envelope = Assert.IsType<ChangeEnvelope>(observer.Current.Data);
                    Assert.Equal(resource, envelope.ResourceId);
                    Assert.Equal(2, envelope.Revision);
                }
                Assert.Equal(3, journal.ActiveObservers);
                await using var rejected = journal.Observe(cursor, account.Id, account.SecurityStamp, scopes, default).GetAsyncEnumerator();
                Assert.Equal("stream-capacity", (await Assert.ThrowsAsync<ApiProblem>(() => rejected.MoveNextAsync().AsTask())).Code);
                foreach (var observer in observers)
                {
                    Assert.True(await observer.MoveNextAsync());
                    Assert.Equal("checkpoint", observer.Current.EventType);
                }
            }
            finally { foreach (var observer in observers) await observer.DisposeAsync(); }
            Assert.Equal(0, journal.ActiveObservers);
        }
    }

    [Fact]
    public async Task PrivateRowsOnlyAdvanceCheckpointAndHeartbeatNeverCarriesCursor()
    {
        var clock = new FakeTimeProvider();
        await using var fixture = new ApiFixture(clock: clock);
        var (client, _, account) = await fixture.LoginAsync();
        using (client) using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var journal = scope.ServiceProvider.GetRequiredService<ChangeJournal>();
            ChangeJournal.Add(db, "subscription", "PRIVATE_OTHER_USER_RESOURCE", 1, "upsert", "other-owner");
            await db.SaveChangesAsync();
            await using var observer = journal.Observe(journal.Cursor(0), account.Id, account.SecurityStamp,
                scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), default).GetAsyncEnumerator();
            Assert.True(await observer.MoveNextAsync());
            Assert.Equal("checkpoint", observer.Current.EventType);
            Assert.NotNull(observer.Current.EventId);
            Assert.DoesNotContain("PRIVATE_OTHER_USER_RESOURCE", JsonSerializer.Serialize(observer.Current.Data, Wire.Json));
            var heartbeat = observer.MoveNextAsync().AsTask();
            Assert.False(heartbeat.IsCompleted);
            clock.Advance(TimeSpan.FromSeconds(15));
            Assert.True(await heartbeat.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("heartbeat", observer.Current.EventType);
            Assert.Null(observer.Current.EventId);
        }
    }

    [Fact]
    public async Task MissedWakeupIsRecoveredByFiveSecondDurableRecheck()
    {
        var clock = new FakeTimeProvider();
        await using var fixture = new ApiFixture(clock: clock);
        var (client, _, account) = await fixture.LoginAsync();
        using (client) using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var journal = scope.ServiceProvider.GetRequiredService<ChangeJournal>();
            await using var observer = journal.Observe(journal.Cursor(0), account.Id, account.SecurityStamp,
                scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), default).GetAsyncEnumerator();
            var pending = observer.MoveNextAsync().AsTask();
            Assert.False(pending.IsCompleted);
            ChangeJournal.Add(db, "post", "committed-without-hint", 1, "upsert");
            await db.SaveChangesAsync();
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.True(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal("committed-without-hint", Assert.IsType<ChangeEnvelope>(observer.Current.Data).ResourceId);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExistingStreamClosesWithinRecheckAfterDisableOrStampRevocation(bool disable)
    {
        var clock = new FakeTimeProvider();
        await using var fixture = new ApiFixture(clock: clock);
        var (client, _, account) = await fixture.LoginAsync();
        using (client) using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var journal = scope.ServiceProvider.GetRequiredService<ChangeJournal>();
            await using var observer = journal.Observe(journal.Cursor(0), account.Id, account.SecurityStamp,
                scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), default).GetAsyncEnumerator();
            var pending = observer.MoveNextAsync().AsTask();
            Assert.False(pending.IsCompleted);
            var changed = await db.Users.SingleAsync(a => a.Id == account.Id);
            if (disable) changed.Disabled = true;
            else changed.SecurityStamp = Guid.NewGuid().ToString();
            await db.SaveChangesAsync();
            clock.Advance(TimeSpan.FromSeconds(5));
            Assert.False(await pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.Equal(0, journal.ActiveObservers);
        }
    }

    [Fact]
    public async Task RetentionCannotRewindHighWaterOrAcceptAnExpiredCursor()
    {
        await using var fixture = new ApiFixture();
        var (client, _, account) = await fixture.LoginAsync();
        using (client) using (var scope = fixture.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var journal = scope.ServiceProvider.GetRequiredService<ChangeJournal>();
            ChangeJournal.Add(db, "post", "retained-then-removed", 1, "upsert");
            await db.SaveChangesAsync();
            await db.Changes.ExecuteDeleteAsync();
            Assert.Equal(1, await ChangeJournal.HighWater(db, default));
            await using var observer = journal.Observe(journal.Cursor(0), account.Id, account.SecurityStamp,
                scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(), default).GetAsyncEnumerator();
            Assert.True(await observer.MoveNextAsync());
            Assert.Equal("reset-required", observer.Current.EventType);
            Assert.Equal("retention-expired", JsonSerializer.SerializeToElement(observer.Current.Data, Wire.Json).GetProperty("reason").GetString());
            Assert.False(await observer.MoveNextAsync());
        }
    }
}
