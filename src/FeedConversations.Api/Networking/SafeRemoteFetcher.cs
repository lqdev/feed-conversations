using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Infrastructure;

namespace FeedConversations.Api.Networking;

public sealed record RemoteDocument(Uri FinalUrl, int Status, string ContentType, byte[] Bytes,
    string[] LinkHeaders, DateTime ObservedAt, DateTime FreshUntil, bool NoStore, TimeSpan? RetryAfter);

public interface IRemoteFetcher
{
    bool TryGetCached(string url, out RemoteDocument? document) { document = null; return false; }
    Task<RemoteDocument> GetAsync(string url, CancellationToken token);
    Task<RemoteDocument> PostAsync(string url, string source, string target, CancellationToken token);
}

public sealed class SafeRemoteFetcher(InstanceOptions options, TimeProvider clock) : IRemoteFetcher, IDisposable
{
    private readonly SemaphoreSlim global = new(4, 4);
    private readonly object gate = new();
    private readonly Dictionary<string, (RemoteDocument Document, long Used)> cache = [];
    private readonly Dictionary<string, Task<RemoteDocument>> inflight = [];
    private readonly Dictionary<string, (SemaphoreSlim Semaphore, int Users)> origins = [];
    private long bytes;
    private long tick;
    public long CachedBytes { get { lock (gate) return bytes; } }
    public int InflightCount { get { lock (gate) return inflight.Count; } }
    public bool TryGetCached(string url, out RemoteDocument? document)
    {
        lock (gate)
        {
            if (cache.TryGetValue(UrlPolicy.Key(url), out var entry)
                && entry.Document.ObservedAt.AddSeconds(30) > clock.GetUtcNow().UtcDateTime)
            {
                document = entry.Document;
                return true;
            }
            document = null;
            return false;
        }
    }

    public async Task<RemoteDocument> GetAsync(string url, CancellationToken token)
    {
        var key = UrlPolicy.Key(url);
        Task<RemoteDocument> task;
        lock (gate)
        {
            if (cache.TryGetValue(key, out var hit) && hit.Document.FreshUntil > clock.GetUtcNow().UtcDateTime)
            {
                cache[key] = (hit.Document, ++tick);
                return hit.Document;
            }
            if (!inflight.TryGetValue(key, out task!))
            {
                if (inflight.Count >= 64) throw new ApiProblem(503, "fetch-capacity", "Remote retrieval is busy.");
                task = FetchAndCacheAsync(key, url);
                inflight.Add(key, task);
            }
        }
        // Each consumer cancels only its wait, not another consumer's shared retrieval.
        return await task.WaitAsync(token);
    }

    private async Task<RemoteDocument> FetchAndCacheAsync(string key, string url)
    {
        await Task.Yield();
        try
        {
            var document = await SendAsync(url, null, null, CancellationToken.None);
            lock (gate)
            {
                if (cache.Remove(key, out var old)) bytes -= old.Document.Bytes.Length;
                if (!document.NoStore && document.Status == 200)
                {
                    while (cache.Count >= 128 || bytes + document.Bytes.Length > ResourceLimits.CacheBytes)
                    {
                        var victim = cache.MinBy(pair => pair.Value.Used);
                        bytes -= victim.Value.Document.Bytes.Length;
                        cache.Remove(victim.Key);
                    }
                    cache[key] = (document, ++tick);
                    bytes += document.Bytes.Length;
                }
            }
            return document;
        }
        finally { lock (gate) inflight.Remove(key); }
    }

    public Task<RemoteDocument> PostAsync(string url, string source, string target, CancellationToken token) =>
        SendAsync(url, source, target, token);

    private async Task<RemoteDocument> SendAsync(string url, string? source, string? target, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await global.WaitAsync(deadline.Token);
            try
            {
                var current = UrlPolicy.Parse(url);
                for (var redirects = 0; ; redirects++)
                {
                    var origin = current.GetLeftPart(UriPartial.Authority);
                    SemaphoreSlim perOrigin;
                    lock (gate)
                    {
                        if (!origins.TryGetValue(origin, out var slot))
                        {
                            if (origins.Count >= 128) throw new ApiProblem(503, "origin-capacity", "Too many active origins.");
                            slot = (new SemaphoreSlim(2, 2), 0);
                        }
                        origins[origin] = (slot.Semaphore, slot.Users + 1);
                        perOrigin = slot.Semaphore;
                    }
                    try
                    {
                        await perOrigin.WaitAsync(deadline.Token);
                        try
                        {
                            using var handler = new SocketsHttpHandler
                            {
                                AllowAutoRedirect = false,
                                UseCookies = false,
                                UseProxy = false,
                                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
                                ConnectTimeout = TimeSpan.FromSeconds(5),
                                ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, origin, ct)
                            };
                            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                            if (options.DevelopmentCaFile is not null && options.DevelopmentOrigins.Contains(origin, StringComparer.Ordinal))
                            {
                                handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                                {
                                    if (certificate is null || (errors & ~SslPolicyErrors.RemoteCertificateChainErrors) != SslPolicyErrors.None) return false;
                                    using var root = X509CertificateLoader.LoadCertificateFromFile(options.DevelopmentCaFile);
                                    using var chain = new X509Chain();
                                    chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                                    chain.ChainPolicy.CustomTrustStore.Add(root);
                                    chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                                    using var leaf = new X509Certificate2(certificate);
                                    return chain.Build(leaf);
                                };
                            }
                            using var request = new HttpRequestMessage(source is null ? HttpMethod.Get : HttpMethod.Post, current);
                            request.Headers.UserAgent.ParseAdd("FeedConversations/0.1");
                            request.Headers.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml, text/xml, text/html");
                            if (source is not null)
                                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                                { ["source"] = source, ["target"] = target! });
                            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                            var status = (int)response.StatusCode;
                            if (status is 301 or 302 or 303 or 307 or 308)
                            {
                                if (redirects >= 3 || response.Headers.Location is null
                                    || source is not null && status is not (307 or 308))
                                    throw new ApiProblem(502, "redirect-policy", "Remote redirect limit or method policy failed.");
                                current = UrlPolicy.Parse(new Uri(current, response.Headers.Location).AbsoluteUri);
                                continue;
                            }
                            var type = response.Content.Headers.ContentType?.MediaType ?? "";
                            if (source is null && status == 200 && type is not ("text/html" or "application/xhtml+xml"
                                or "application/rss+xml" or "application/atom+xml" or "application/xml" or "text/xml"))
                                throw new ApiProblem(502, "remote-content-type", "Unsupported remote content type.");
                            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
                            using var buffer = new MemoryStream();
                            var block = new byte[16 * 1024];
                            int read;
                            while ((read = await stream.ReadAsync(block, deadline.Token)) != 0)
                            {
                                if (buffer.Length + read > ResourceLimits.RemoteBytes)
                                    throw new ApiProblem(502, "remote-too-large", "Remote representation exceeds the byte limit.");
                                buffer.Write(block, 0, read);
                            }
                            var now = clock.GetUtcNow().UtcDateTime;
                            var age = response.Headers.CacheControl?.MaxAge ?? TimeSpan.FromSeconds(30);
                            age = TimeSpan.FromSeconds(Math.Clamp(age.TotalSeconds, 0, 60));
                            var noStore = response.Headers.CacheControl?.NoStore == true
                                || response.Headers.CacheControl?.NoCache == true;
                            var retry = response.Headers.RetryAfter?.Delta
                                ?? (response.Headers.RetryAfter?.Date - clock.GetUtcNow());
                            return new RemoteDocument(current, status, type, buffer.ToArray(),
                                response.Headers.TryGetValues("Link", out var links) ? links.Take(32).ToArray() : [],
                                now, now + age, noStore, retry);
                        }
                        finally { perOrigin.Release(); }
                    }
                    finally
                    {
                        lock (gate)
                        {
                            var slot = origins[origin];
                            if (slot.Users == 1) { origins.Remove(origin); slot.Semaphore.Dispose(); }
                            else origins[origin] = (slot.Semaphore, slot.Users - 1);
                        }
                    }
                }
            }
            finally { global.Release(); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new ApiProblem(502, "remote-timeout", "Remote retrieval timed out."); }
        catch (HttpRequestException) { throw new ApiProblem(502, "remote-transport", "Remote retrieval failed."); }
        catch (SocketException) { throw new ApiProblem(502, "remote-dns", "Remote address resolution failed."); }
    }

    private async ValueTask<Stream> ConnectAsync(DnsEndPoint destination, string origin, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(destination.Host, token);
        var developmentAllowed = options.DevelopmentOrigins.Contains(origin, StringComparer.Ordinal);
        if (addresses.Length == 0 || !developmentAllowed && addresses.Any(a => !UrlPolicy.PublicAddress(a)))
            throw new ApiProblem(502, "destination-blocked", "Remote destination is not public.");
        // The socket uses the address validated here, never a subsequent DNS lookup.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addresses[0], destination.Port), token);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch { socket.Dispose(); throw; }
    }

    public void Dispose() => global.Dispose();
}
