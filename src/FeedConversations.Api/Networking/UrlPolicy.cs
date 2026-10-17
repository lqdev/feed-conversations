using System.Net;
using System.Text.RegularExpressions;
using FeedConversations.Api.Infrastructure;

namespace FeedConversations.Api.Networking;

public static partial class UrlPolicy
{
    public static Uri Parse(string text)
    {
        text = text.Trim();
        if (text.Length > 4096 || text.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\')
            || BadEscape().IsMatch(text) || !Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0 || string.IsNullOrEmpty(uri.Host))
            throw ApiProblem.Invalid("invalid-url");
        return uri;
    }

    public static string Key(string value)
    {
        var uri = Parse(value);
        // URI normalizes host/scheme/default port; path/query/fragment are not case folded.
        return uri.GetComponents(UriComponents.SchemeAndServer, UriFormat.UriEscaped)
            + value.Trim()[AuthorityEnd(value.Trim())..];
    }

    private static int AuthorityEnd(string value)
    {
        var start = value.IndexOf("://", StringComparison.Ordinal) + 3;
        var end = value.IndexOfAny(['/', '?', '#'], start);
        return end < 0 ? value.Length : end;
    }

    public static bool PublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        var b = address.GetAddressBytes();
        if (b.Length == 4)
            return b[0] is not (0 or 10 or 127) && b[0] < 224
                && !(b[0] == 169 && b[1] == 254)
                && !(b[0] == 172 && b[1] is >= 16 and <= 31)
                && !(b[0] == 192 && b[1] == 168)
                && !(b[0] == 100 && b[1] is >= 64 and <= 127)
                && !(b[0] == 192 && b[1] == 0)
                && !(b[0] == 198 && b[1] is 18 or 19);
        return !address.Equals(IPAddress.IPv6Any) && !address.Equals(IPAddress.IPv6None)
            && !address.IsIPv6LinkLocal && !address.IsIPv6Multicast && !address.IsIPv6SiteLocal
            && (b[0] & 0xfe) != 0xfc && b[0] is >= 0x20 and <= 0x3f;
    }

    [GeneratedRegex("%(?![0-9a-fA-F]{2})")]
    private static partial Regex BadEscape();
}
