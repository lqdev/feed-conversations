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
```

Current executed evidence: 22 unit checks, 7 integration checks, 5 frontend
checks, and 2 real-browser checks pass, including a four-user A/B conversation,
reverse-direction delivery, a public RSS-only walker, and empty browser storage.
This is partial acceptance evidence, not a claim that A01-A36 all pass.

## Boundaries

- RSS/HTML describe publications; Webmention provides interaction discovery.
- Locally authored content is durable; foreign bodies are transient and bounded.
- The Schema.org/Atom RSS binding is application-defined, not a universal standard.
- No ActivityPub, broker, private messaging, or permanent foreign-content archive.
- No credentials, real account data, or runtime configuration are included.
- All identities, URLs, and timestamps in protocol examples are synthetic.

## License

A source license has not yet been selected. Publication of this plan does not
grant an open-source license. A license must be selected before an open-source
software release.
