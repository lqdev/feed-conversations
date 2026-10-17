[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$process = $null
$environment = @{}
foreach ($name in @('ASPNETCORE_URLS', 'ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL', 'ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL')) {
    $environment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
Push-Location $root
try {
    if (-not (Test-Path .runtime\localhost.crt)) { throw 'Run scripts\Initialize-Dev.ps1 first.' }
    foreach ($port in @(7440, 7441, 7442, 7541, 7542, 7641, 7642, 17440, 17441, 17442)) {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $port)
        $listener.ExclusiveAddressUse = $true
        try { $listener.Start() }
        catch { throw "Required demo port $port is occupied; stop its owner explicitly." }
        finally { $listener.Stop() }
    }
    $env:ASPNETCORE_URLS = 'https://localhost:17440'
    $env:ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL = 'https://localhost:17441'
    $env:ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL = 'https://localhost:17442'
    $dll = Join-Path $root 'src\FeedConversations.AppHost\bin\Release\net10.0\FeedConversations.AppHost.dll'
    $process = Start-Process dotnet -ArgumentList "`"$dll`"" -WorkingDirectory $root -PassThru -NoNewWindow
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    $curl = (Get-Command $(if ($IsWindows) { 'curl.exe' } else { 'curl' }) -CommandType Application | Select-Object -First 1).Source
    $ready = $false
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw 'Aspire exited before readiness.' }
        $ready = $true
        foreach ($url in @('https://localhost:7441/health', 'https://localhost:7442/health', 'https://localhost:7440/index.html')) {
            & $curl --fail --silent --cacert (Join-Path $root '.runtime\localhost.crt') --max-time 2 $url *> $null
            if ($LASTEXITCODE -ne 0) { $ready = $false; break }
        }
        if ($ready) { break }
        Start-Sleep -Milliseconds 250
    }
    if (-not $ready) { throw 'The demo did not become ready within 120 seconds.' }
    Write-Host 'Ready: A :7441, B :7442, UI :7440. Ctrl+C stops this owned topology.'
    while (-not $process.WaitForExit(250)) { }
    if ($process.ExitCode -ne 0) { throw 'Aspire exited unsuccessfully.' }
}
finally {
    if ($null -ne $process -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
    foreach ($entry in $environment.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value, 'Process') }
    Pop-Location
}
