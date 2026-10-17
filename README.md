# feed-conversations

A multi-user, feeds-native publishing and conversation application.

**Status: MVP implementation in progress; not release-ready.** The real two-instance
publishing/reading/Webmention loop runs. The complete acceptance matrix and
operational/recovery gates in the implementation plan are not yet satisfied.

The application uses .NET 10 ASP.NET Core Minimal APIs, SQLite, an independent
TypeScript/RxJS browser client, and Aspire for local two-instance development.
It publishes public notes, replies, likes, and bookmarks, and reads RSS/Atom
feeds without permanently archiving other publishers' content.

See [the implementation plan](docs/implementation-plan.md) for the complete
requirements, wire contracts, delivery milestones, limits, and acceptance cases.
Passing a build or the first conversation loop is not full-MVP completion.

## Local development

Requires .NET SDK 10.0.402, Node 22.23.2, npm, and PowerShell 7.
No Docker is required for the process demo.

```powershell
dotnet tool restore
dotnet restore FeedConversations.slnx --locked-mode
dotnet build FeedConversations.slnx -c Release --no-restore
npm ci
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
dotnet test tests\FeedConversations.UnitTests -c Release --no-build
dotnet test tests\FeedConversations.IntegrationTests -c Release --no-build
npm run typecheck --workspace @feed-conversations/web
npm run test --workspace @feed-conversations/web -- --run
npm run build --workspace @feed-conversations/web
npm exec playwright install chromium
# With the process demo running:
npm run test:browser
dotnet test tests\FeedConversations.AcceptanceTests -c Release --no-build
```

Current executed evidence: 44 unit checks, 25 integration checks, 9 frontend
checks, 2 real-browser checks, and a separate .NET public RSS-only walker pass.
These cover four-user A/B conversations, reverse-direction delivery, pinned
retrieval/cache limits, protocol conflicts, durable SSE rechecks/revocation,
restore conflicts, and empty browser storage. The independent walker consumes
the synthetic chain produced by browser acceptance; run them in the shown order.
This is partial acceptance evidence, not a claim that A01-A36 all pass.

See [protocol details](docs/protocol.md) for the application-specific profile and
the restored-copy authority boundary.

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
