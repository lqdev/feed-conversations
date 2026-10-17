[CmdletBinding()]
param([switch] $Standalone)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$process = $null
$private = Join-Path $root ".runtime\acceptance-$([Guid]::NewGuid().ToString('N'))"
$environment = @{}
foreach ($name in @('ASPNETCORE_ENVIRONMENT', 'ASPNETCORE_URLS', 'ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL',
    'ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL', 'Instance__Id', 'Instance__PublicBaseUrl', 'Instance__ProxyPath',
    'Instance__FrontendOrigin', 'Instance__DataDirectory', 'FC_ACCEPTANCE_LOG_DIRECTORY')) {
    $environment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
function Run([string] $Executable) {
    $arguments = $args
    $application = (Get-Command $Executable -CommandType Application | Select-Object -First 1).Source
    & $application @arguments
    if ($LASTEXITCODE -ne 0) { throw "Acceptance command failed ($LASTEXITCODE)." }
}
Push-Location $root
try {
    foreach ($port in @(7440, 7441, 7442, 7541, 7542, 7641, 7642, 17440, 17441, 17442)) {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
        $listener.ExclusiveAddressUse = $true
        try { $listener.Start() }
        catch { throw "Required local test port $port is occupied. Stop its owner explicitly; this script will not reuse or kill it." }
        finally { $listener.Stop() }
    }
    Run dotnet restore FeedConversations.slnx --locked-mode
    Run dotnet build FeedConversations.slnx -c Release --no-restore --nologo
    Run dotnet test tests\FeedConversations.UnitTests -c Release --no-build --nologo
    Run dotnet test tests\FeedConversations.IntegrationTests -c Release --no-build --nologo
    Run npm run typecheck --workspace '@feed-conversations/web'
    Run npm run test --workspace '@feed-conversations/web' -- --run
    Run npm run build --workspace '@feed-conversations/web'
    & (Join-Path $PSScriptRoot 'Initialize-Dev.ps1')
    New-Item -ItemType Directory $private | Out-Null
    $env:FC_ACCEPTANCE_LOG_DIRECTORY = $private
    $env:ASPNETCORE_URLS = 'https://localhost:17440'
    $env:ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL = 'https://localhost:17441'
    $env:ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL = 'https://localhost:17442'
    $dll = Join-Path $root 'src\FeedConversations.AppHost\bin\Release\net10.0\FeedConversations.AppHost.dll'
    $process = Start-Process dotnet -ArgumentList "`"$dll`"" -WorkingDirectory $root -PassThru -NoNewWindow `
        -RedirectStandardOutput (Join-Path $private 'apphost.stdout.log') -RedirectStandardError (Join-Path $private 'apphost.stderr.log')
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    $ready = $false
    $curl = (Get-Command $(if ($IsWindows) { 'curl.exe' } else { 'curl' }) -CommandType Application | Select-Object -First 1).Source
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw 'The owned Aspire test topology exited before readiness. Inspect its private .runtime logs.' }
        $ready = $true
        foreach ($url in @('https://localhost:7441/health', 'https://localhost:7442/health', 'https://localhost:7440/index.html')) {
            & $curl --fail --silent --cacert (Join-Path $root '.runtime\localhost.crt') --max-time 2 $url *> $null
            if ($LASTEXITCODE -ne 0) { $ready = $false; break }
        }
        if ($ready) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'The owned Aspire topology did not become ready within 120 seconds.' }
    Run npm run test:browser
    Run dotnet test tests\FeedConversations.AcceptanceTests -c Release --no-build --nologo
    $process.Kill($true)
    $process.WaitForExit()
    $process = $null
    if ($Standalone) { & (Join-Path $PSScriptRoot 'Test-Standalone.ps1') }
    Write-Host 'Backend, frontend, controlled four-user browser, public RSS walker and storage checks passed.'
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $process.Kill($true)
        $process.WaitForExit()
    }
    foreach ($entry in $environment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process')
    }
    if (Test-Path $private) { Remove-Item -LiteralPath $private -Recurse -Force }
    Pop-Location
}
