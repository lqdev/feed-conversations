# feed-conversations

A planned multi-user, feeds-native publishing and conversation application.

**Status: design and implementation plan only. No application has been implemented.**

The proposed MVP uses .NET 10 ASP.NET Core Minimal APIs, SQLite, an independent
TypeScript/RxJS browser client, and Aspire for local two-instance development.
It will publish public notes, replies, likes, and bookmarks, and read RSS/Atom
feeds without permanently archiving other publishers' content.

See [the implementation plan](docs/implementation-plan.md) for the proposed
architecture, wire contracts, delivery milestones, limits, and acceptance cases.
Commands in that document are implementation targets, not runnable software
provided by this repository.

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
