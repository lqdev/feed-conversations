using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Text;
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
        public TaskCompletionSource? Hold { get; set; }
        private int calls;
        public async ValueTask<Stream> ConnectAsync(IPEndPoint endpoint, CancellationToken token)
        {
            Endpoints.Enqueue(endpoint);
            var index = Math.Min(Interlocked.Increment(ref calls) - 1, responses.Length - 1);
            if (Hold is not null) await Hold.Task.WaitAsync(token);
            return new ScriptedStream(responses[index]);
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
}
