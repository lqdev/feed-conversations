using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeedConversations.Api.Configuration;
using FeedConversations.Api.Infrastructure;
using FeedConversations.Api.Posts;
using FeedConversations.Api.Reading;
using FeedConversations.Api.Syndication;

namespace FeedConversations.Api.Portability;

public sealed record ArchiveProfile(string DisplayName, string Bio, bool DirectoryVisible);
public sealed record ArchivePost(string Guid, string Permalink, DateTime PublishedAt, DateTime UpdatedAt, int Revision,
    string Kind, string? Title, string? Markdown, Relationship? Relationship, DateTime? DeletedAt, string? Provenance);
public sealed record ArchiveManifest(int Version, string ExportId, string SourceInstance, string SourceAccount,
    string PublicBaseUrl, string Cursor, int PostCount, Dictionary<string, string> Checksums);
public sealed record AccountArchive(ArchiveManifest Manifest, ArchiveProfile Profile, List<ArchivePost> Posts,
    List<OpmlEntry> Subscriptions, List<ViewDefinition> Views);
public sealed class ArchiveCodec(OpmlCodec opml)
{
    private static readonly string[] Names = ["manifest.json", "profile.json", "posts.json", "subscriptions.opml", "views.json"];
    public byte[] Write(AccountArchive archive)
    {
        var files = new Dictionary<string, byte[]>
        {
            ["profile.json"] = JsonSerializer.SerializeToUtf8Bytes(archive.Profile, Wire.Json),
            ["posts.json"] = JsonSerializer.SerializeToUtf8Bytes(archive.Posts, Wire.Json),
            ["subscriptions.opml"] = Encoding.UTF8.GetBytes(opml.Write(archive.Subscriptions)),
            ["views.json"] = JsonSerializer.SerializeToUtf8Bytes(archive.Views, Wire.Json)
        };
        var manifest = archive.Manifest with { Checksums = files.ToDictionary(f => f.Key, f => Hash(f.Value)) };
        files["manifest.json"] = JsonSerializer.SerializeToUtf8Bytes(manifest, Wire.Json);
        if (files.Values.Sum(v => (long)v.Length) > ResourceLimits.ArchiveBytes || archive.Posts.Count > ResourceLimits.ArchivePosts)
            throw new ApiProblem(413, "archive-too-large", "The full archive exceeds its limit.");
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var file in files)
            {
                var entry = zip.CreateEntry(file.Key, CompressionLevel.Fastest);
                using var stream = entry.Open();
                stream.Write(file.Value);
            }
        if (memory.Length > ResourceLimits.ArchiveBytes) throw new ApiProblem(413, "archive-too-large", "Compressed archive exceeds its byte limit.");
        return memory.ToArray();
    }
    public AccountArchive Read(byte[] bytes)
    {
        if (bytes.Length > ResourceLimits.ArchiveBytes) throw new ApiProblem(413, "archive-too-large", "The archive exceeds its byte limit.");
        try
        {
            using var input = new MemoryStream(bytes);
            using var zip = new ZipArchive(input, ZipArchiveMode.Read);
            if (zip.Entries.Count != Names.Length || zip.Entries.Select(e => e.FullName).Distinct().Count() != Names.Length
                || zip.Entries.Any(e => !Names.Contains(e.FullName))) throw ApiProblem.Invalid("archive-entries");
            var files = new Dictionary<string, byte[]>();
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (entry.Length > ResourceLimits.ArchiveBytes || (total += entry.Length) > ResourceLimits.ArchiveBytes)
                    throw new ApiProblem(413, "archive-too-large", "Decompressed archive exceeds its limit.");
                using var stream = entry.Open();
                using var buffer = new MemoryStream();
                var block = new byte[16384];
                int read;
                while ((read = stream.Read(block)) > 0)
                {
                    if (buffer.Length + read > entry.Length) throw ApiProblem.Invalid("archive-length");
                    buffer.Write(block, 0, read);
                }
                files.Add(entry.FullName, buffer.ToArray());
            }
            T Read<T>(string name) => JsonSerializer.Deserialize<T>(files[name], Wire.Json) ?? throw ApiProblem.Invalid("archive-json");
            var manifest = Read<ArchiveManifest>("manifest.json");
            if (manifest.Version != 1 || manifest.ExportId.Length > 64 || manifest.SourceInstance.Length is 0 or > 64
                || manifest.SourceAccount.Length is 0 or > 200 || manifest.Cursor.Length > 8192
                || manifest.PublicBaseUrl.Length > 4096 || manifest.Checksums.Count != 4
                || files.Any(f => f.Key != "manifest.json" && (!manifest.Checksums.TryGetValue(f.Key, out var hash) || hash != Hash(f.Value))))
                throw ApiProblem.Invalid("archive-checksum");
            Networking.UrlPolicy.Parse(manifest.PublicBaseUrl);
            var posts = Read<List<ArchivePost>>("posts.json");
            if (posts.Count != manifest.PostCount || posts.Count > ResourceLimits.ArchivePosts || posts.Any(p => p is null)
                || posts.Select(p => p.Guid).Distinct().Count() != posts.Count
                || posts.Select(p => Networking.UrlPolicy.Key(p.Permalink)).Distinct().Count() != posts.Count)
                throw ApiProblem.Invalid("archive-post-identities");
            var profile = Read<ArchiveProfile>("profile.json");
            if (profile.DisplayName.Length > 200 || profile.Bio.Length > 4000) throw ApiProblem.Invalid("archive-profile");
            SemanticProfile.ValidateText(profile.DisplayName, 16384, "archive-profile");
            SemanticProfile.ValidateText(profile.Bio, 16384, "archive-profile");
            var subscriptions = opml.Parse(Encoding.UTF8.GetString(files["subscriptions.opml"])).ToList();
            var views = Read<List<ViewDefinition>>("views.json");
            if (views.Count > 25 || views.Any(v => v is null) || views.Select(v => v.Name).Distinct().Count() != views.Count) throw ApiProblem.Invalid("archive-views");
            var subscribed = subscriptions.Select(s => Networking.UrlPolicy.Key(s.Url)).ToHashSet();
            foreach (var view in views) view.Validate(subscribed);
            foreach (var post in posts)
            {
                if (post.Guid.Length is 0 or > 4096 || post.Revision < 1 || post.Kind is not ("post" or "reply" or "like" or "bookmark")
                    || post.Kind == "post" && post.Relationship is not null || post.Kind != "post" && post.Relationship?.Kind != post.Kind)
                    throw ApiProblem.Invalid("archive-post");
                SemanticProfile.ValidateText(post.Guid, 16384, "archive-guid");
                Networking.UrlPolicy.Parse(post.Permalink);
                var sourceOrigin = Networking.UrlPolicy.Parse(manifest.PublicBaseUrl).GetLeftPart(UriPartial.Authority);
                var permalink = Networking.UrlPolicy.Parse(post.Permalink);
                var segments = permalink.AbsolutePath.Split('/');
                if (permalink.GetLeftPart(UriPartial.Authority) != sourceOrigin || permalink.Query.Length != 0 || permalink.Fragment.Length != 0
                    || segments.Length != 3 || segments[1] != "post" || !Guid.TryParseExact(segments[2], "D", out _)
                    || post.PublishedAt.Kind != DateTimeKind.Utc || post.UpdatedAt.Kind != DateTimeKind.Utc
                    || post.UpdatedAt < post.PublishedAt || post.DeletedAt is { Kind: not DateTimeKind.Utc }
                    || post.DeletedAt is not null && (post.Title is not null || post.Markdown is not null))
                    throw ApiProblem.Invalid("archive-origin-or-dates");
                if (post.DeletedAt is null) PostService.ValidateContent(post.Kind, post.Title, post.Markdown);
                if (post.Relationship is { } relationship)
                {
                    Networking.UrlPolicy.Parse(relationship.Target.Permalink);
                    var identity = relationship.Target.Identity.AsEntry();
                    if (identity.Kind == "permalink")
                    {
                        Networking.UrlPolicy.Parse(identity.Value);
                        if (Networking.UrlPolicy.Key(identity.Value) != Networking.UrlPolicy.Key(relationship.Target.Permalink))
                            throw ApiProblem.Invalid("archive-target-identity");
                    }
                    else Networking.UrlPolicy.Parse(identity.FeedUrl!);
                }
            }
            return new AccountArchive(manifest, profile, posts, subscriptions, views);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or NotSupportedException or KeyNotFoundException)
        { throw ApiProblem.Invalid("invalid-archive"); }
    }
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
