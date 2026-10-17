var builder = DistributedApplication.CreateBuilder(args);
var root = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));
var runtime = Path.Combine(root, ".runtime");
var a = builder.AddProject<Projects.FeedConversations_Api>("api-a", launchProfileName: null)
    .WithHttpsEndpoint(port: 7441, targetPort: 7541)
    .WithHttpEndpoint(targetPort: 7641)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Instance__Id", "demo-a")
    .WithEnvironment("Instance__PublicBaseUrl", "https://localhost:7441")
    .WithEnvironment("Instance__ProxyPath", "/connections/a")
    .WithEnvironment("Instance__FrontendOrigin", "https://localhost:7440")
    .WithEnvironment("Instance__DataDirectory", Path.Combine(runtime, "a"))
    .WithEnvironment("Instance__DevelopmentCaFile", Path.Combine(runtime, "localhost.crt"))
    .WithEnvironment("Instance__DevelopmentOrigins__0", "https://localhost:7442")
    .WithEnvironment("Instance__TrustedOrigins__0", "https://localhost:7442")
    .WithHttpHealthCheck("/health", endpointName: "http");
var b = builder.AddProject<Projects.FeedConversations_Api>("api-b", launchProfileName: null)
    .WithHttpsEndpoint(port: 7442, targetPort: 7542)
    .WithHttpEndpoint(targetPort: 7642)
    .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Development")
    .WithEnvironment("Instance__Id", "demo-b")
    .WithEnvironment("Instance__PublicBaseUrl", "https://localhost:7442")
    .WithEnvironment("Instance__ProxyPath", "/connections/b")
    .WithEnvironment("Instance__FrontendOrigin", "https://localhost:7440")
    .WithEnvironment("Instance__DataDirectory", Path.Combine(runtime, "b"))
    .WithEnvironment("Instance__DevelopmentCaFile", Path.Combine(runtime, "localhost.crt"))
    .WithEnvironment("Instance__DevelopmentOrigins__0", "https://localhost:7441")
    .WithEnvironment("Instance__TrustedOrigins__0", "https://localhost:7441")
    .WithHttpHealthCheck("/health", endpointName: "http");
builder.AddViteApp("web", Path.Combine(root, "src", "web"))
    .WithEnvironment("API_A", a.GetEndpoint("https"))
    .WithEnvironment("API_B", b.GetEndpoint("https"))
    .WithEnvironment("DEV_HTTPS", "true")
    .WithEnvironment("DEV_CERT", Path.Combine(runtime, "localhost.crt"))
    .WithEnvironment("DEV_KEY", Path.Combine(runtime, "localhost.key"))
    .WithEnvironment("NODE_EXTRA_CA_CERTS", Path.Combine(runtime, "localhost.crt"))
    .WithHttpEndpoint(port: 7440, env: "PORT", isProxied: false)
    .WaitFor(a).WaitFor(b);
builder.Build().Run();
