using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace FeedConversations.AcceptanceTests;

public sealed class PublicWalkerTests
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Source = "https://source.scripting.com/";
    private static readonly HashSet<string> Origins = ["https://localhost:7441", "https://localhost:7442"];
    private sealed record Targets(string Root, string[] Chain, string ReverseRoot, string ReverseReply);
    private static string RootDirectory()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "FeedConversations.slnx"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
    private static HttpClient Client()
    {
        var certificate = X509CertificateLoader.LoadCertificateFromFile(Path.Combine(RootDirectory(), ".runtime", "localhost.crt"));
        var handler = new HttpClientHandler { UseCookies = false, UseProxy = false, AllowAutoRedirect = false };
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, errors) =>
        {
            if (cert is null || (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(certificate);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(cert);
        };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    }
    private static async Task<string> Get(HttpClient client, string url)
    {
        var uri = new Uri(url);
        Assert.Contains(uri.GetLeftPart(UriPartial.Authority), Origins);
        Assert.Empty(uri.UserInfo);
        Assert.DoesNotContain("/api/", uri.AbsolutePath);
        using var response = await client.GetAsync(uri);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Content.Headers.ContentLength is null or <= 1024 * 1024);
        var text = await response.Content.ReadAsStringAsync();
        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(text), 1, 1024 * 1024);
        return text;
    }
    private static XDocument Xml(string text)
    {
        using var reader = XmlReader.Create(new StringReader(text), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 1024 * 1024 });
        return XDocument.Load(reader);
    }
    private static List<XElement> Items(string text) => Xml(text).Root!.Element("channel")!.Elements("item").ToList();
    private static string Link(XElement item) => item.Element("link")!.Value;
    private static string Author(XElement item) => Assert.Single(item.Elements(Atom + "link"),
        e => (string?)e.Attribute("rel") == "https://schema.org/author").Attribute("href")!.Value;
    private static void SameOriginMetadata(XElement relayed, XElement original)
    {
        Assert.Equal(original.Element("guid")!.Value, relayed.Element("guid")!.Value);
        Assert.Equal((string?)original.Element("guid")!.Attribute("isPermaLink"), (string?)relayed.Element("guid")!.Attribute("isPermaLink"));
        Assert.Equal(Link(original), Link(relayed));
        Assert.Equal(Author(original), Author(relayed));
        Assert.Equal((string?)original.Element(Source + "comments")?.Attribute("feedUrl"),
            (string?)relayed.Element(Source + "comments")?.Attribute("feedUrl"));
    }

    [Fact]
    public async Task IndependentPublicRssWalkerReachesBothDirectionsAndPreservesOriginAttribution()
    {
        var input = Path.Combine(RootDirectory(), ".runtime", "walker-target.json");
        Assert.True(File.Exists(input), "Run scripts\\Test-All.ps1, or start the demo and run browser acceptance first.");
        var targets = JsonSerializer.Deserialize<Targets>(await File.ReadAllTextAsync(input), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        using var client = Client();
        foreach (var (root, expected) in new[] { (targets.Root, targets.Chain), (targets.ReverseRoot, new[] { targets.ReverseRoot, targets.ReverseReply }) })
        {
            var queue = new Queue<string>();
            var visited = new HashSet<string>();
            queue.Enqueue(root);
            while (queue.TryDequeue(out var permalink))
            {
                if (!visited.Add(permalink)) continue;
                Assert.InRange(visited.Count, 1, 20);
                var own = Assert.Single(Items(await Get(client, permalink + "/rss.xml")));
                Assert.Equal(permalink, Link(own));
                Assert.Equal(permalink, own.Element("guid")!.Value);
                Assert.Null(own.Element("guid")!.Attribute("isPermaLink"));
                Assert.NotNull(own.Element("pubDate"));
                Assert.NotNull(own.Element(Atom + "updated"));
                var pointer = own.Element(Source + "comments")!;
                Assert.NotNull(pointer);
                var replies = Items(await Get(client, pointer.Attribute("feedUrl")!.Value));
                Assert.Equal(int.Parse(pointer.Attribute("count")!.Value, System.Globalization.CultureInfo.InvariantCulture), replies.Count);
                foreach (var reply in replies)
                {
                    Assert.Equal("https://schema.org/Comment", Assert.Single(reply.Elements(Atom + "category")).Attribute("term")!.Value);
                    Assert.Equal(permalink, Assert.Single(reply.Elements(Atom + "link"),
                        e => (string?)e.Attribute("rel") == "https://schema.org/parentItem").Attribute("href")!.Value);
                    var origin = Assert.Single(Items(await Get(client, Link(reply) + "/rss.xml")));
                    SameOriginMetadata(reply, origin);
                    if (new Uri(Link(reply)).Authority != new Uri(permalink).Authority)
                        Assert.Equal(Author(origin) + "/rss.xml", reply.Element("source")!.Attribute("url")!.Value);
                    queue.Enqueue(Link(reply));
                }
            }
            Assert.All(expected, url => Assert.Contains(url, visited));
        }
    }
}
