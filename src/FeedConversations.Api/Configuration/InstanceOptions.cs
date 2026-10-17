using System.Text.RegularExpressions;

namespace FeedConversations.Api.Configuration;

public sealed partial class InstanceOptions
{
    public string Id { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
    public string ProxyPath { get; set; } = "";
    public string FrontendOrigin { get; set; } = "";
    public string DataDirectory { get; set; } = "";
    public string[] DevelopmentOrigins { get; set; } = [];
    public string[] TrustedOrigins { get; set; } = [];
    public string? DevelopmentCaFile { get; set; }
    public string BaseUrl => PublicBaseUrl.TrimEnd('/');

    public void Validate(bool development)
    {
        if (!Identifier().IsMatch(Id) || !Uri.TryCreate(PublicBaseUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != "https" && !(development && uri.Scheme == "http")
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || uri.AbsolutePath != "/" || string.IsNullOrWhiteSpace(DataDirectory)
            || !Regex.IsMatch(ProxyPath, @"^/connections/[a-z0-9-]+$"))
            throw new InvalidOperationException("Configure a stable instance ID, public origin, data directory and fixed connection proxy path.");
        if (!development && (DevelopmentOrigins.Length != 0 || DevelopmentCaFile is not null))
            throw new InvalidOperationException("Development network exceptions are forbidden outside Development.");
        foreach (var origin in DevelopmentOrigins.Concat(TrustedOrigins).Append(FrontendOrigin))
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var candidate)
                || candidate.GetLeftPart(UriPartial.Authority) != origin
                || candidate.Scheme is not ("http" or "https") || candidate.UserInfo.Length != 0)
                throw new InvalidOperationException("Origins must be exact HTTP(S) origins, including non-default ports.");
    }

    [GeneratedRegex("^[a-z0-9-]{1,64}$")]
    private static partial Regex Identifier();
}
