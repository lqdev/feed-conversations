using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Networking;

namespace FeedConversations.UnitTests;

public sealed class TransportTests
{
    private sealed class Resolver(params IPAddress[][] answers) : IAddressResolver
    {
        public int Calls;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken token) =>
            Task.FromResult(answers[Math.Min(Interlocked.Increment(ref Calls) - 1, answers.Length - 1)]);
    }
    private sealed class Connector(params byte[][] responses) : IRemoteConnector
    {
        public ConcurrentQueue<IPEndPoint> Endpoints { get; } = new();
        public Channel<IPEndPoint> Started { get; } = Channel.CreateUnbounded<IPEndPoint>();
        public TaskCompletionSource? Hold { get; set; }
        private int calls;
        private int active;
        public int Maximum;
        public async ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken token)
        {
            Endpoints.Enqueue(endpoint);
            var index = Math.Min(Interlocked.Increment(ref calls) - 1, responses.Length - 1);
            var current = Interlocked.Increment(ref active);
            int previous;
            do { previous = Maximum; } while (current > previous && Interlocked.CompareExchange(ref Maximum, current, previous) != previous);
            Started.Writer.TryWrite(endpoint);
            try
            {
                if (Hold is not null) await Hold.Task.WaitAsync(token);
                return new ScriptedStream(responses[index]);
            }
            finally { Interlocked.Decrement(ref active); }
        }
    }
    private sealed class ScriptedStream(byte[] response) : Stream
    {
        private readonly MemoryStream input = new(response);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => input.Length;
        public override long Position { get => input.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => input.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) { }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { input.Dispose(); base.Dispose(disposing); }
    }
    private static byte[] Response(int status = 200, string headers = "", byte[]? bytes = null)
    {
        bytes ??= Encoding.UTF8.GetBytes("<p>Synthetic transient response.</p>");
        return Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Synthetic\r\nContent-Type: text/html\r\nContent-Length: {bytes.Length}\r\n{headers}Connection: close\r\n\r\n")
            .Concat(bytes).ToArray();
    }
    private static readonly IPAddress Public = IPAddress.Parse("8.8.8.8");
    private static SafeRemoteFetcher Fetcher(Resolver resolver, Connector connector) => new(new InstanceOptions(), TimeProvider.System, resolver, connector);

    [Fact]
    public async Task ConnectPinsTheValidatedAddressWithoutSecondDnsLookup()
    {
        var resolver = new Resolver([Public], [IPAddress.Loopback]);
        var connector = new Connector(Response());
        using var fetcher = Fetcher(resolver, connector);
        Assert.Equal(200, (await fetcher.GetAsync("http://controlled.example/source", default)).Status);
        Assert.Equal(1, resolver.Calls);
        Assert.Equal(Public, Assert.Single(connector.Endpoints).Address);
    }

    [Fact]
    public async Task EveryRedirectRevalidatesDestinationBeforeConnection()
    {
        var resolver = new Resolver([Public], [IPAddress.Loopback]);
        var connector = new Connector(Response(302, "Location: http://second-controlled.example/private\r\n"));
        using var fetcher = Fetcher(resolver, connector);
        await Assert.ThrowsAsync<ApiProblem>(() => fetcher.GetAsync("http://controlled.example/start", default));
        Assert.Equal(2, resolver.Calls);
        Assert.Single(connector.Endpoints);
    }

    [Fact]
    public async Task PostUsesIdenticalDnsPolicyAndNeverSendsToPrivateEndpoint()
    {
        var resolver = new Resolver([IPAddress.Parse("::ffff:127.0.0.1")]);
        var connector = new Connector(Response(202));
        using var fetcher = Fetcher(resolver, connector);
        await Assert.ThrowsAsync<ApiProblem>(() => fetcher.PostAsync("http://controlled.example/mention", "https://a.example/post/source", "https://b.example/post/target", default));
        Assert.Empty(connector.Endpoints);
    }

    [Fact]
    public async Task DecompressedByteLimitIsMeasuredNotContentLength()
    {
        using var memory = new MemoryStream();
        using (var gzip = new GZipStream(memory, CompressionMode.Compress, true))
            gzip.Write(Encoding.ASCII.GetBytes(new string('x', ResourceLimits.RemoteBytes + 1)));
        var connector = new Connector(Response(headers: "Content-Encoding: gzip\r\n", bytes: memory.ToArray()));
        using var fetcher = Fetcher(new Resolver([Public]), connector);
        var error = await Assert.ThrowsAsync<ApiProblem>(() => fetcher.GetAsync("http://controlled.example/compressed", default));
        Assert.Equal("remote-too-large", error.Code);
        Assert.Equal(0, fetcher.CachedBytes);
    }

    [Fact]
    public async Task CoalescingIsBoundedAndOneCanceledObserverCannotCancelAnother()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Connector(Response()) { Hold = release };
        using var fetcher = Fetcher(new Resolver([Public]), connector);
        using var cancel = new CancellationTokenSource();
        var first = fetcher.GetAsync("http://controlled.example/shared", cancel.Token);
        var second = fetcher.GetAsync("http://controlled.example/shared", default);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        release.SetResult();
        Assert.Equal(200, (await second).Status);
        Assert.Single(connector.Endpoints);
        Assert.Equal(0, fetcher.InflightCount);
    }

    [Fact]
    public async Task NoStoreDoesNotRetainBodiesAnd304RetriesUnconditionallyOnce()
    {
        var connector = new Connector(Response(headers: "Cache-Control: no-store\r\n"), Response(304), Response());
        using var fetcher = Fetcher(new Resolver([Public]), connector);
        await fetcher.GetAsync("http://controlled.example/no-store", default);
        Assert.Equal(0, fetcher.CachedBytes);
        Assert.False(fetcher.TryGetCached("http://controlled.example/no-store", out _));
        await fetcher.GetAsync("http://controlled.example/unconditional", default);
        Assert.Equal(3, connector.Endpoints.Count);
    }

    [Fact]
    public async Task CacheBytesNeverExceedMeasuredCapacityAtBoundary()
    {
        var connector = new Connector(Response(headers: "Cache-Control: max-age=60\r\n", bytes: new byte[ResourceLimits.RemoteBytes]));
        using var fetcher = Fetcher(new Resolver([Public]), connector);
        for (var i = 0; i < 33; i++)
        {
            await fetcher.GetAsync($"http://controlled.example/item-{i}", default);
            Assert.InRange(fetcher.CachedBytes, 0, ResourceLimits.CacheBytes);
        }
        Assert.Equal(ResourceLimits.CacheBytes, fetcher.CachedBytes);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    public async Task InflightBoundaryRejectsExcessAndReleasesEverySlot(int count)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Connector(Response()) { Hold = release };
        using var fetcher = Fetcher(new Resolver([Public]), connector);
        var tasks = Enumerable.Range(0, Math.Min(count, 64)).Select(i => fetcher.GetAsync($"http://controlled.example/{i}", default)).ToList();
        try
        {
            Assert.Equal(Math.Min(count, 64), fetcher.InflightCount);
            if (count == 65)
                Assert.Equal("fetch-capacity", (await Assert.ThrowsAsync<ApiProblem>(() =>
                    fetcher.GetAsync("http://controlled.example/overflow", default))).Code);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, fetcher.InflightCount);
        Assert.Equal(Math.Min(count, 64), connector.Endpoints.Count);
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public async Task RedirectBoundaryCountsEveryHop(int redirects, bool allowed)
    {
        var responses = Enumerable.Range(0, redirects).Select(i => Response(302, $"Location: http://controlled.example/hop-{i}\r\n"))
            .Append(Response()).ToArray();
        var connector = new Connector(responses);
        using var fetcher = Fetcher(new Resolver([Public]), connector);
        if (allowed) Assert.Equal(200, (await fetcher.GetAsync("http://controlled.example/start", default)).Status);
        else Assert.Equal("redirect-policy", (await Assert.ThrowsAsync<ApiProblem>(() => fetcher.GetAsync("http://controlled.example/start", default))).Code);
        Assert.Equal(Math.Min(redirects + 1, 4), connector.Endpoints.Count);
        Assert.Equal(0, fetcher.InflightCount);
    }

    [Fact]
    public async Task Repeated304FailsExplicitlyAndDoesNotLeakInflightState()
    {
        var connector = new Connector(Response(304), Response(304), Response());
        using var fetcher = Fetcher(new Resolver([Public]), connector);
        Assert.Equal("invalid-304", (await Assert.ThrowsAsync<ApiProblem>(() => fetcher.GetAsync("http://controlled.example/repeated", default))).Code);
        Assert.Equal(0, fetcher.InflightCount);
        Assert.Equal(0, fetcher.CachedBytes);
        Assert.Equal(200, (await fetcher.GetAsync("http://controlled.example/repeated", default)).Status);
    }

    [Fact]
    public async Task NotificationVerificationCannotReuseAnEarlierFreshCachedSource()
    {
        var connector = new Connector(Response(headers: "Cache-Control: max-age=60\r\n"), Response(410));
        using var fetcher = Fetcher(new Resolver([Public]), connector);
        Assert.Equal(200, (await fetcher.GetAsync("http://controlled.example/withdrawn", default)).Status);
        Assert.Equal(200, (await fetcher.GetAsync("http://controlled.example/withdrawn", default)).Status);
        Assert.Single(connector.Endpoints);
        Assert.Equal(410, (await fetcher.GetFreshAsync("http://controlled.example/withdrawn", default)).Status);
        Assert.Equal(2, connector.Endpoints.Count);
        Assert.Equal(0, fetcher.CachedBytes);
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(3, false)]
    [InlineData(4, false)]
    [InlineData(5, false)]
    public async Task ActualConcurrentConnectionsRespectOriginAndGlobalCaps(int count, bool sameOrigin)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connector = new Connector(Response()) { Hold = release };
        using var fetcher = Fetcher(new Resolver([Public]), connector);
        var expected = Math.Min(count, sameOrigin ? 2 : 4);
        var tasks = Enumerable.Range(0, count).Select(i => fetcher.GetAsync(
            sameOrigin ? $"http://controlled.example/{i}" : $"http://origin-{i}.controlled.example:{8000 + i}/", default)).ToArray();
        try
        {
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            for (var i = 0; i < expected; i++) await connector.Started.Reader.ReadAsync(wait.Token);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, connector.Maximum);
        Assert.Equal(0, fetcher.InflightCount);
    }

    [Fact]
    public async Task TenSecondOverallDeadlineUsesInjectedTimeAndReleasesAdmission()
    {
        var clock = new FakeTimeProvider();
        var connector = new Connector(Response()) { Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        using var fetcher = new SafeRemoteFetcher(new InstanceOptions(), clock, new Resolver([Public]), connector);
        var request = fetcher.GetAsync("http://controlled.example/deadline", default);
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await connector.Started.Reader.ReadAsync(wait.Token);
        clock.Advance(TimeSpan.FromMilliseconds(9999));
        Assert.False(request.IsCompleted);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal("remote-timeout", (await Assert.ThrowsAsync<ApiProblem>(() => request.WaitAsync(wait.Token))).Code);
        Assert.Equal(0, fetcher.InflightCount);
    }

    [Fact]
    public async Task MetricsMeasureBytesWithoutRetainingBodyOrUrlLabels()
    {
        const string sentinel = "FOREIGN_METRIC_BODY_SENTINEL";
        var measurements = new ConcurrentQueue<(string Name, double Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener { InstrumentPublished = (instrument, current) =>
        {
            if (instrument.Meter.Name == "FeedConversations") current.EnableMeasurementEvents(instrument);
        } };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => measurements.Enqueue((instrument.Name, value, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => measurements.Enqueue((instrument.Name, value, tags.ToArray())));
        listener.Start();
        var connector = new Connector(Response(bytes: Encoding.UTF8.GetBytes(sentinel)));
        using var fetcher = Fetcher(new Resolver([Public]), connector);
        await fetcher.GetAsync("http://controlled.example/metric-source", default);
        Assert.Contains(measurements, m => m.Name == "remote.response.bytes" && m.Value == Encoding.UTF8.GetByteCount(sentinel));
        Assert.Contains(measurements, m => m.Name == "remote.duration");
        Assert.DoesNotContain(measurements, m => m.Tags.Any(t => t.Value?.ToString()?.Contains(sentinel, StringComparison.Ordinal) == true
            || t.Value?.ToString()?.Contains("controlled.example", StringComparison.Ordinal) == true));
    }
}
