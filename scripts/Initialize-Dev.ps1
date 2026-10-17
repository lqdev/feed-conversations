[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$runtime = Join-Path $root '.runtime'
New-Item -ItemType Directory -Force $runtime | Out-Null
Push-Location $root
try {
    dotnet dev-certs https --check
    if ($LASTEXITCODE -ne 0) { throw 'Create a local development certificate with dotnet dev-certs https.' }
    dotnet dev-certs https --export-path (Join-Path $runtime 'localhost.crt') --format PEM --no-password
    if ($LASTEXITCODE -ne 0) { throw 'Development certificate export failed.' }
    foreach ($instance in @('a', 'b')) {
        $env:ASPNETCORE_ENVIRONMENT = 'Development'
        $env:Instance__Id = "demo-$instance"
        $env:Instance__PublicBaseUrl = "https://localhost:$(if ($instance -eq 'a') {7441} else {7442})"
        $env:Instance__ProxyPath = "/connections/$instance"
        $env:Instance__FrontendOrigin = 'https://localhost:7440'
        $env:Instance__DataDirectory = Join-Path $runtime $instance
        dotnet run --project src\FeedConversations.Api -c Release --no-build --no-launch-profile -- --seed-demo
        if ($LASTEXITCODE -ne 0) { throw "Seed failed for $instance." }
    }
    Write-Host 'Generated credentials are in .runtime\a and .runtime\b. Never publish those directories.'
}
finally { Pop-Location }
