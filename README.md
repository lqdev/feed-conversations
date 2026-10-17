# feed-conversations

A multi-user, feeds-native publishing and conversation application.

**Status: implemented MVP with controlled end-to-end acceptance.** Two independent
instances run four-user conversations, edits, withdrawals, transient reading and
recovery. No public application has been deployed.

The application uses .NET 10 ASP.NET Core Minimal APIs, SQLite, an independent
TypeScript/RxJS browser client, and Aspire for local two-instance development.
It publishes public notes, replies, likes, and bookmarks, and reads RSS/Atom
feeds without permanently archiving other publishers' content.

See [the implementation plan](docs/implementation-plan.md) for the complete
requirements, wire contracts, delivery milestones, limits, and acceptance cases.
See [acceptance evidence](docs/acceptance-demo.md) for the A01-A36 mapping,
executed checks and interoperability limits.

## Local development

Requires .NET SDK 10.0.402, Node 22.23.2, npm, and PowerShell 7.
No Docker is required for the process demo.

```powershell
dotnet tool restore
dotnet restore FeedConversations.slnx --locked-mode
dotnet build FeedConversations.slnx -c Release --no-restore
npm ci
npm exec playwright install chromium
.\scripts\Initialize-Dev.ps1
.\scripts\Start-Demo.ps1
```

The frontend is `https://localhost:7440`, A is `:7441`, and B is `:7442`.
The initializer generates synthetic demo credentials privately under `.runtime`
and separate databases/keys for both instances. Do not publish that directory.
For an interactive browser, trust your development certificate explicitly with
`dotnet dev-certs https --trust`. Controlled browser tests instead pin only its
public key; safe A/B transport trusts only that certificate at exact configured
Development origins. Production refuses these exceptions.

```powershell
# With no demo already running:
.\scripts\Test-All.ps1
# Also exercise non-root containers, independent static hosting and older restore:
.\scripts\Test-All.ps1 -Standalone
```

Executed evidence: 72 unit checks, 73 integration checks, 13 frontend checks,
4 real-browser scenarios and 2 independent .NET acceptance checks. The separate
standalone scenario passes before and after an actual older-backup restore.
Release builds have zero warnings/errors; TypeScript checks include the client,
proxy configuration and browser tests. The runner orders the public RSS walker
after the browser creates its synthetic A/B chain and stops its owned helpers.

See [protocol details](docs/protocol.md) for the application-specific profile and
the restored-copy authority boundary.
See [architecture](docs/architecture.md), [operations](docs/operations.md),
[account portability](docs/portability.md) and
[compatibility](docs/compatibility.md) for deployment and authority boundaries.

## Boundaries

- RSS/HTML describe publications; Webmention provides interaction discovery.
- Locally authored content is durable; foreign bodies are transient and bounded.
- The Schema.org/Atom RSS binding is application-defined, not a universal standard.
- No ActivityPub, broker, private messaging, or permanent foreign-content archive.
- No credentials, real account data, or runtime configuration are included.
- All identities, URLs, and timestamps in protocol examples are synthetic.

## License

A source license has not yet been selected. Publication of this source does not
grant an open-source license. A license must be selected before an open-source
software release.
