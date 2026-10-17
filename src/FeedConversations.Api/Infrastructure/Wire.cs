using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FeedConversations.Api.Infrastructure;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string ETag(string type, string id, int revision) => $"\"{Hash($"{type}:{id}:{revision}")}\"";
    public static void CheckMatch(HttpRequest request, string type, string id, int revision)
    {
        if (!request.Headers.ContainsKey("If-Match")) throw new ApiProblem(428, "revision-required", "If-Match is required.");
        if (request.Headers.IfMatch != ETag(type, id, revision)) throw new ApiProblem(412, "revision-stale", "The resource has changed.");
    }
}
