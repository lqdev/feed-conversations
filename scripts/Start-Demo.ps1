[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not (Test-Path .runtime\localhost.crt)) { throw 'Run scripts\Initialize-Dev.ps1 first.' }
    $env:ASPNETCORE_URLS = 'https://localhost:17440'
    $env:ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL = 'https://localhost:17441'
    $env:ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL = 'https://localhost:17442'
    Write-Host 'Starting A :7441, B :7442, UI :7440. Ctrl+C stops this process topology.'
    dotnet src\FeedConversations.AppHost\bin\Release\net10.0\FeedConversations.AppHost.dll
    if ($LASTEXITCODE -ne 0) { throw 'Aspire exited unsuccessfully.' }
}
finally { Pop-Location }
