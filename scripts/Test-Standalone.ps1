[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$dockerExecutable = (Get-Command docker -CommandType Application | Select-Object -First 1).Source
$root = Split-Path $PSScriptRoot -Parent
$suffix = [Guid]::NewGuid().ToString('N').Substring(0, 12)
$prefix = "fc-standalone-$suffix"
$private = Join-Path (Join-Path $root '.runtime') $prefix
$image = "feed-conversations:acceptance-$suffix"
$containers = [Collections.Generic.List[string]]::new()
$volumes = [Collections.Generic.List[string]]::new()
$networkCreated = $false
$imageCreated = $false
function Docker {
    & $dockerExecutable @args
    if ($LASTEXITCODE -ne 0) { throw "A scoped Docker operation failed ($LASTEXITCODE)." }
}
function Ready([string] $url) {
    & curl.exe --fail --silent --show-error --cacert (Join-Path $root '.runtime\localhost.crt') --max-time 2 --retry 30 --retry-delay 1 --retry-connrefused $url
    if ($LASTEXITCODE -ne 0) { throw "Controlled readiness check failed: $url" }
}
Push-Location $root
try {
    $context = Docker context show
    $endpoint = Docker context inspect $context --format '{{.Endpoints.docker.Host}}'
    if ($endpoint -notmatch '^(npipe|unix):') { throw 'Standalone acceptance requires a local Docker socket, not a remote execution host.' }
    if (-not (Test-Path src\web\dist\index.html)) { throw 'Build the static frontend first.' }
    if (-not (Test-Path .runtime\localhost.crt)) { throw 'Run scripts\Initialize-Dev.ps1 to export the local test certificate.' }
    New-Item -ItemType Directory $private | Out-Null
    Copy-Item src\web\dist (Join-Path $private 'web') -Recurse
    $configuration = @"
https://localhost:443 {
    tls /certs/localhost.crt /certs/localhost.key
    reverse_proxy $prefix-a`:8080 {
        flush_interval -1
    }
}
https://localhost:444 {
    tls /certs/localhost.crt /certs/localhost.key
    reverse_proxy $prefix-b`:8080 {
        flush_interval -1
    }
}
https://localhost:445 {
    tls /certs/localhost.crt /certs/localhost.key
    handle_path /connections/a/* {
        reverse_proxy $prefix-a`:8080 {
            flush_interval -1
        }
    }
    handle_path /connections/b/* {
        reverse_proxy $prefix-b`:8080 {
            flush_interval -1
        }
    }
    handle {
        root * /srv/web
        header Cache-Control no-store
        file_server
    }
}
"@
    $configuration | Set-Content (Join-Path $private 'Caddyfile') -Encoding utf8NoBOM
    Docker build --quiet --file src\FeedConversations.Api\Dockerfile --tag $image .
    $imageCreated = $true
    Docker network create $prefix | Out-Null
    $networkCreated = $true
    $subnet = Docker network inspect $prefix --format '{{(index .IPAM.Config 0).Subnet}}'
    $address = [Net.IPAddress]::Parse($subnet.Split('/')[0]).GetAddressBytes()
    $address[3] = 10
    $proxy = ([Net.IPAddress]::new($address)).ToString()
    $edgeArgs = @('run', '--detach', '--name', "$prefix-edge", '--network', $prefix, '--ip', $proxy,
        '--publish', '127.0.0.1::443', '--publish', '127.0.0.1::444', '--publish', '127.0.0.1::445',
        '--mount', "type=bind,source=$private\Caddyfile,target=/etc/caddy/Caddyfile,readonly",
        '--mount', "type=bind,source=$private\web,target=/srv/web,readonly",
        '--mount', "type=bind,source=$root\.runtime\localhost.crt,target=/certs/localhost.crt,readonly",
        '--mount', "type=bind,source=$root\.runtime\localhost.key,target=/certs/localhost.key,readonly", 'caddy:2.11.7')
    Docker @edgeArgs | Out-Null
    $containers.Add("$prefix-edge")
    $aPort = (Docker port "$prefix-edge" 443/tcp).Split(':')[-1]
    $bPort = (Docker port "$prefix-edge" 444/tcp).Split(':')[-1]
    $uiPort = (Docker port "$prefix-edge" 445/tcp).Split(':')[-1]
    $frontend = "https://localhost:$uiPort"
    $origins = @{ a = "https://localhost:$aPort"; b = "https://localhost:$bPort" }
    $connections = @()
    $credentials = @()
    foreach ($instance in @('a', 'b')) {
        $volume = "$prefix-$instance-data"
        Docker volume create $volume | Out-Null
        $volumes.Add($volume)
        $settings = @('--env', "Instance__Id=standalone-$instance", '--env', "Instance__PublicBaseUrl=$($origins[$instance])",
            '--env', "Instance__FrontendOrigin=$frontend", '--env', "Instance__ProxyPath=/connections/$instance",
            '--env', 'Instance__DataDirectory=/data', '--env', "Instance__TrustedProxies__0=$proxy",
            '--mount', "type=volume,source=$volume,target=/data")
        Docker run --rm @settings --env ASPNETCORE_ENVIRONMENT=Development $image --seed-demo
        Docker create --name "$prefix-copy-$instance" @settings $image | Out-Null
        $containers.Add("$prefix-copy-$instance")
        Docker cp "$prefix-copy-$instance`:/data/demo-credentials.json" (Join-Path $private "$instance-credentials.json")
        $credentials += @(Get-Content (Join-Path $private "$instance-credentials.json") -Raw | ConvertFrom-Json) |
            ForEach-Object { @{ instance = $instance; handle = $_.handle; password = $_.password } }
        Docker run --rm @settings --entrypoint sh $image -c 'rm /data/demo-credentials.json'
        Docker run --detach --name "$prefix-$instance" --network $prefix @settings --env ASPNETCORE_ENVIRONMENT=Production $image | Out-Null
        $containers.Add("$prefix-$instance")
        $connections += @{ id = $instance; label = "Standalone $instance"; prefix = "/connections/$instance"; publicOrigin = $origins[$instance] }
    }
    $connections | ConvertTo-Json | Set-Content (Join-Path $private 'web\connections.json') -Encoding utf8NoBOM
    foreach ($origin in $origins.Values) { Ready "$origin/health" }
    Ready "$frontend/index.html"
    Docker exec "$prefix-a" sh -c 'test "$(id -u)" != 0 && ! command -v node && test ! -f /app/FeedConversations.AppHost.dll'
    Docker exec "$prefix-a" dotnet FeedConversations.Api.dll --backup /data/acceptance-backup.db
    $env:FC_STANDALONE_FIXTURE = Join-Path $private 'fixture.json'
    @{ frontend = $frontend; origins = $origins; credentials = $credentials; phase = 'before-restore' } |
        ConvertTo-Json -Depth 5 | Set-Content $env:FC_STANDALONE_FIXTURE -Encoding utf8NoBOM
    npm run test:standalone
    if ($LASTEXITCODE -ne 0) { throw 'Standalone browser acceptance failed.' }
    Docker stop "$prefix-a" | Out-Null
    Docker run --rm --mount "type=volume,source=$prefix-a-data,target=/data" --entrypoint sh $image -c 'rm -f /data/app.db-wal /data/app.db-shm; cp /data/acceptance-backup.db /data/app.db'
    Docker start "$prefix-a" | Out-Null
    Ready "$($origins.a)/health"
    $fixture = Get-Content $env:FC_STANDALONE_FIXTURE -Raw | ConvertFrom-Json
    $fixture.phase = 'after-restore'
    $fixture | ConvertTo-Json -Depth 8 | Set-Content $env:FC_STANDALONE_FIXTURE -Encoding utf8NoBOM
    npm run test:standalone
    if ($LASTEXITCODE -ne 0) { throw 'Restored-backup browser acceptance failed.' }
    Write-Host 'Standalone non-root APIs, independent static UI, trusted HTTPS proxy, isolated auth/SSE, and older-backup reset passed.'
}
finally {
    $cleanupFailures = [Collections.Generic.List[string]]::new()
    foreach ($container in $containers) {
        & $dockerExecutable rm --force $container | Out-Null
        if ($LASTEXITCODE -ne 0) { $cleanupFailures.Add($container) }
    }
    foreach ($volume in $volumes) {
        & $dockerExecutable volume rm $volume | Out-Null
        if ($LASTEXITCODE -ne 0) { $cleanupFailures.Add($volume) }
    }
    if ($networkCreated) {
        & $dockerExecutable network rm $prefix | Out-Null
        if ($LASTEXITCODE -ne 0) { $cleanupFailures.Add($prefix) }
    }
    if ($imageCreated) {
        & $dockerExecutable image rm $image | Out-Null
        if ($LASTEXITCODE -ne 0) { $cleanupFailures.Add($image) }
    }
    Remove-Item Env:FC_STANDALONE_FIXTURE -ErrorAction SilentlyContinue
    if (Test-Path $private) { Remove-Item -LiteralPath $private -Recurse -Force }
    Pop-Location
    if ($cleanupFailures.Count -ne 0) { throw "Scoped standalone cleanup failed: $($cleanupFailures -join ', ')." }
}
