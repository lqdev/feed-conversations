# Operations

## Controlled development and acceptance

Use .NET SDK 10.0.402, Node 22.23.2 and PowerShell 7. Restore locked dependencies
with `dotnet tool restore`, `dotnet restore FeedConversations.slnx --locked-mode`
and `npm ci`. Install the test browser separately:

```powershell
npm exec playwright install chromium
.\scripts\Test-All.ps1
# Additional isolated local Docker/Caddy/static-client/backup checks:
.\scripts\Test-All.ps1 -Standalone
```

The process runner refuses occupied demo ports instead of reusing or killing
another process. It starts its own Aspire process tree, checks A/B/UI readiness,
runs browser acceptance before the independent RSS walker, and stops that tree
in `finally`. It deletes its specific private captured-log directory.
Development databases and generated credentials remain under ignored `.runtime`.
Do not upload runtime directories or browser storage/test artifacts.

For a human demo, run `Initialize-Dev.ps1` and `Start-Demo.ps1`; Ctrl+C stops
the foreground topology. The demo requires no Docker. The standalone harness
requires a local Docker socket, creates uniquely named loopback-only resources,
uses generated credentials and removes its containers, volumes, network and
temporary image. It sends no third-party notifications and deploys nothing.

## Independent backend and static frontend

`src/FeedConversations.Api/Dockerfile` builds a non-root runtime containing no
Node or AppHost. Build the frontend independently with
`npm run build --workspace @feed-conversations/web` and serve its `dist` tree.
Provide a private `connections.json` with only fixed connection IDs, relative
prefixes and public origins. The static example contains synthetic domains.

`deploy/compose.yaml` and `Caddyfile.example` are operator templates, not a
public deployment instruction performed by acceptance. Copy the Caddy example
to a private `deploy/Caddyfile`, replace synthetic domains and configure DNS/TLS,
volumes and an unused private network before using them. Never check local
configuration into the repository.

Set immutable `Instance__Id`, HTTPS `Instance__PublicBaseUrl`,
`Instance__FrontendOrigin`, `Instance__ProxyPath`, `Instance__DataDirectory` and
explicit `Instance__TrustedProxies__0`. Choose the public origin before any
publication: startup rejects a database/configuration identity mismatch.
Trust only the actual proxy IP; do not accept arbitrary forwarded hosts.
Caddy strips only fixed connection prefixes and uses `flush_interval -1`.
Private APIs are `no-store`; do not buffer/cache SSE or private responses.
Public HTML/RSS works without the SPA or authentication.

Initialize a production administrator locally, with private runtime settings:

```powershell
dotnet FeedConversations.Api.dll --create-admin
```

The command asks for a handle and a non-echoed password. Generate other accounts
through one-use administrator invites. Demo seeding refuses Production.
Disabling an account revokes its sessions; hiding a publication is separate.
There is no email recovery or public bootstrap endpoint. An operator with
private host access can run `dotnet FeedConversations.Api.dll --reset-password`.
It asks for the existing handle and a non-echoed new password, uses Identity
reset tokens internally, revokes prior sessions and does not enable a disabled
account. Do not pass passwords in command-line arguments or shell history.

## Backup and restore

Protect the SQLite database, immutable instance configuration and Data Protection
keys separately. Keys and backups are secrets; restrict filesystem/volume access.
Never copy only an active WAL database's main file.

```powershell
dotnet FeedConversations.Api.dll --backup <new-private-backup-file>
```

This uses the SQLite backup API and refuses to overwrite a destination. For
restore, stop the one writer, preserve the old data privately, install a
consistent backup, remove only that stopped database's obsolete WAL/SHM files,
retain matching identity/configuration/keys and restart. A new process
incarnation forces connected clients to re-snapshot even when keys survive.
The standalone acceptance harness performs an actual older-backup restore.

Account ZIP import is not operator database recovery and does not restore
credentials, routing, jobs or remotely hosted bodies.

## Capacity and observability

Limits are explicit in [the implementation contract](implementation-plan.md#7-resource-limits-and-safe-retrieval).
Creation quota failures do not partially commit owned state. Notification
capacity returns 503 with Retry-After, not false acceptance. Retrieval and graph
limits produce specific failures/partial reasons. Terminal notification status
is bounded and expires; owners can explicitly retry after admission checks.

The `FeedConversations` meter records request duration/bytes, cache bytes,
in-flight retrievals, notification attempts/queue depth, journal observers/lag/
reset reasons and thread partial reasons. Labels contain bounded operation
directions/reasons only, never URLs, handles, content, IDs or credentials.
ASP.NET metrics cover server latency. Remote HTTP tracing and content logging
are deliberately absent; only health requests are traced. Configure a trusted
private OTLP endpoint explicitly if exporting telemetry.

CI runs locked restore/build, unit/integration/frontend checks and the controlled
process/browser/RSS/storage sequence on Windows. The Docker standalone gate is
a separate executable local check; CI does not publish images or applications.
