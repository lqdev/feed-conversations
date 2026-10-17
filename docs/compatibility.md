# Compatibility

The app publishes RSS 2.0 and HTML and reads bounded RSS and native Atom.
Its Atom/Schema.org relation binding is application-defined; recognizing those
terms is not generic RDF reasoning, ActivityPub or universal reader support.
See [the exact profile](protocol.md).

Independent fixtures cover BlogPosting, SocialMediaPosting, Comment, LikeAction,
BookmarkAction, namespace aliases, opaque GUID scopes, threading, incomplete
properties and conflicting/nested evidence. BlogPosting is read-only metadata;
authored notes do not become blog posts because of their title or length.
Unknown ordinary feeds remain readable without invented interaction support.

Core RSS descriptions and display Markdown include readable relation context.
Unsupported or class-stripping readers can still follow target/permalink links.
The independent .NET acceptance walker uses public RSS only, without credentials,
JSON APIs or producer mapping helpers, to traverse both A/B directions and check
origin GUID, profile, comments pointer and RSS source attribution.

Webmention delivery uses standard source/target form posts. HTTP 2xx means
accepted, not displayed. Generic backlinks are mentions, not replies; missing
endpoints are unsupported. External publishers need no account or mutual follow.
Profile URLs remain attribution claims, not authenticated human identities.

Only controlled A/B/stub origins receive automated network writes. No live
third-party federation or independent GUI-reader ingestion has been exercised.
Fixture compatibility is not a claim of full rss.chat/RSC API or firehose support.
HTTP/1.1 is sufficient; HTTP/2/3 at an edge changes no federation semantics.

Development and automated process acceptance currently use the documented
Windows/PowerShell topology and fixed checked ports. Production backend/static
separation is tested using non-root Linux containers behind a controlled HTTPS
proxy. The standalone harness itself is a PowerShell/Windows operator command.
