using System.IO.Compression;
using System.Text;
using System.Text.Json;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Portability;
using FeedConversations.Api.Syndication;

namespace FeedConversations.UnitTests;

public sealed class ArchiveTests
{
    private static readonly ArchiveCodec Codec = new(new OpmlCodec());
    private static AccountArchive Archive(int count)
    {
        var date = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new(new(1, "synthetic-export", "synthetic-instance", "alice", "https://a.example", "local-sequence:0", count, []),
            new("Alice", "", false), Enumerable.Range(0, count).Select(i =>
            {
                var url = $"https://a.example/post/{new Guid(i, 0, 0, new byte[8])}";
                return new ArchivePost(url, url, date, date, 1, "post", null, "Owned synthetic text.", null, null, null);
            }).ToList(), [], []);
    }
    private static byte[] Repack(byte[] original, string file, string replacement, int padding = 0)
    {
        using var input = new MemoryStream(original);
        using var source = new ZipArchive(input);
        var files = source.Entries.ToDictionary(e => e.FullName, e =>
        {
            using var stream = e.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        });
        files[file] = Encoding.UTF8.GetBytes(replacement + new string(' ', padding));
        if (file != "manifest.json")
        {
            var manifest = JsonSerializer.Deserialize<ArchiveManifest>(files["manifest.json"], Wire.Json)!;
            manifest.Checksums[file] = ArchiveCodec.Hash(files[file]);
            files["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, Wire.Json);
        }
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var pair in files)
            {
                using var stream = zip.CreateEntry(pair.Key, CompressionLevel.Fastest).Open();
                stream.Write(pair.Value);
            }
        return output.ToArray();
    }
    [Theory]
    [InlineData(19999)]
    [InlineData(20000)]
    [InlineData(20001)]
    public void PostLimitIsInclusiveAndNeverSilentlyTruncated(int count)
    {
        var archive = Archive(count);
        if (count > ResourceLimits.ArchivePosts)
            Assert.Equal(413, Assert.Throws<ApiProblem>(() => Codec.Write(archive)).Status);
        else Assert.Equal(count, Codec.Read(Codec.Write(archive)).Posts.Count);
    }
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void DecompressedByteLimitIsMeasuredAcrossAllFiles(int offset)
    {
        var original = Codec.Write(Archive(0));
        using var memory = new MemoryStream(original);
        using var zip = new ZipArchive(memory);
        var length = zip.Entries.Sum(e => e.Length);
        var bytes = Repack(original, "views.json", "[]", checked((int)(ResourceLimits.ArchiveBytes - length + offset)));
        if (offset > 0) Assert.Equal(413, Assert.Throws<ApiProblem>(() => Codec.Read(bytes)).Status);
        else Assert.Empty(Codec.Read(bytes).Posts);
    }
    [Theory]
    [InlineData("manifest.json", "{}")]
    [InlineData("manifest.json", "null")]
    [InlineData("profile.json", "{}")]
    [InlineData("profile.json", "{\"displayName\":null,\"bio\":\"\",\"directoryVisible\":false}")]
    [InlineData("posts.json", "[null]")]
    [InlineData("views.json", "[null]")]
    public void ChecksummedMalformedSchemasProduceExplicitDomainFailures(string file, string value)
    {
        var bytes = Repack(Codec.Write(Archive(0)), file, value);
        Assert.Equal(400, Assert.Throws<ApiProblem>(() => Codec.Read(bytes)).Status);
    }
}
