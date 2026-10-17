# feed-conversations: approved MVP implementation contract

## Status

This document was approved before application code existed. The repository now
contains the API, browser client, Aspire topology, migrations and executable
checks. Requirements below remain the acceptance contract, not an automatic
claim that every gate has passed. See README and the acceptance evidence for
current implementation/verification status. Source publication was subsequently
authorized; application hosting and third-party notification tests were not.

## Goal and confirmed choices

Build a separate multi-user publication and conversation application using
.NET 10/C# 14 ASP.NET Core Minimal APIs, EF Core/SQLite and Identity, a separately
built TypeScript/Vite/RxJS client, and an Aspire two-instance development setup.
Each instance can host multiple users. Publish public notes, replies, public
bookmarks, and one basic like; read both blog posts and social notes.

Use synthetic Alice/Bob on A and Carol/David on B for acceptance tests.
Demonstrate cross-instance conversations without mutual follows, with public
RSS/HTML and Webmention. Keep foreign bodies transient rather than building
permanent remote archives.

The following design includes explicit assumptions and proposed defaults.
Dependency versions and target-specific APIs must be checked during
implementation. Do not treat illustrative examples as deployed endpoints.
Use a new implementation checkout; do not modify unrelated projects.
All protocol identities and account names in examples are synthetic.

## Publisher interoperability grounding

The application-defined Atom/Schema.org RSS binding has been exercised by an
independent publisher. It distinguishes BlogPosting, SocialMediaPosting,
Comment, LikeAction, and BookmarkAction without changing core item identities.
That publisher implementation is not this application. Use synthetic fixtures
in automated tests and optional read-only live checks; do not publish test
content or send unsolicited notifications to third-party services.

## 3. Scope and explicit exclusions

### Included in the complete MVP

1. Per-instance invite-only Identity password accounts, profile settings, directory opt-in, and minimal admin controls. Multiple users share an instance.
2. Create/edit/delete notes and replies; publish/retract public bookmarks and one basic like. Optional title on notes/replies/bookmarks, optional note on bookmarks, no authored body/title on likes.
3. Public HTML, author/aggregate/singleton RSS, direct-reply RSS, and instance member-feed OPML directory.
4. RSS/Atom reading, subscriptions, private OPML preview/import/export, declarative saved views, and active-view refresh.
5. Webmention send/receive/verification/moderation for replies, likes, bookmarks, and generic mentions; bounded cross-origin conversation assembly.
6. Reactive independent client, durable local-change SSE, transient remote observations, and explicit failures/partial results.
7. Versioned account export/restore and tested operator database backup/restore.
8. Aspire development, two-instance/four-account acceptance, standalone backend/static-client deployment examples and CI.

### Excluded

Blog/article publishing mode; media uploads/mirroring; private posts/bookmarks/DMs; reposts, emoji reactions, RSVP; notifications inbox/ranking; public signup; email delivery/password reset email; IndieAuth/passkeys/OAuth/API tokens; Micropub/Microsub; ActivityPub; WebSub/rssCloud; NATS or any broker; Rx.NET; permanent foreign-body storage/history; automatic full-feed acquisition; global conversation completeness; arbitrary-origin browser attachment/open proxies; seamless domain migration; replicated writers sharing SQLite; public application hosting.

Invites gate account creation, not public reading or external participation. Alice's account on A is not a global account usable on B. An external publisher needs no receiving-instance account to send a valid public Webmention.

## 4. Architecture, dependencies, and layout

```text
TypeScript + Vite + RxJS browser client
  /connections/a/api/v1/... -> configured same-origin proxy -> API A -> A SQLite
  /connections/b/api/v1/... -> configured same-origin proxy -> API B -> B SQLite

API A <--- public HTTP HTML/RSS + Webmention ---> API B / external publishers

Minimal endpoints -> feature services -> EF Core transactions
                                   -> local invalidation journal + durable jobs
SafeRemoteFetcher -> transient byte-bounded cache -> feed/HTML parsers
Workers -> leases/retries; Channels -> wake-up hints, not durable delivery
```

Choose .NET 10/C# 14, nullable/analyzers, deterministic Release builds, warning discipline for application code. Pin SDK `10.0.402` with `rollForward: latestPatch` initially; upgrades are explicit. Resolve current compatible stable dependency versions at scaffold time, pin them, and commit locks. No guessed version numbers, previews, floating wildcards or blanket warning suppression.

Dependencies: EF Core 10 SQLite/Design, Identity EF stores, ASP.NET Core OpenAPI and target-.NET-10 validation support, Markdig, HtmlSanitizer, AngleSharp; official Aspire hosting/JavaScript/testing and OpenTelemetry defaults; xUnit, Microsoft.AspNetCore.Mvc.Testing, TimeProvider test fakes. Frontend: TypeScript, Vite, RxJS, DOMPurify, openapi-typescript, Vitest, Playwright. XML uses System.Xml/LINQ to XML with external entities/DTDs disabled. No general RDF processor or runtime schema/context fetching.

Current docs confirm `TypedResults.ServerSentEvents` with `System.Net.ServerSentEvents.SseItem<T>` and `IAsyncEnumerable<T>`, and official `Aspire.Hosting.JavaScript` `AddViteApp`. Validation docs span framework versions: verify the .NET 10-specific `AddValidation` package/API by a compile smoke; do not add a guessed `Microsoft.AspNetCore.Http.Validation` dependency or import a newer experimental API from an unversioned example. Use explicit domain validation regardless of annotations.

```text
<project-root>\
  FeedConversations.slnx
  global.json / Directory.Build.props / Directory.Packages.props
  NuGet.Config / .editorconfig / .gitignore / README.md
  package.json / package-lock.json
  .config\dotnet-tools.json
  src\
    FeedConversations.AppHost\AppHost.cs
    FeedConversations.ServiceDefaults\Extensions.cs
    FeedConversations.Api\
      Program.cs
      Configuration\InstanceOptions.cs / ResourceLimits.cs
      Data\AppDbContext.cs / Entities\ / Migrations\
      Accounts\AccountEndpoints.cs / AccountService.cs / AdminCommands.cs
      Posts\PostEndpoints.cs / PostService.cs / PostContracts.cs
      Syndication\FeedReader.cs / RssWriter.cs / OpmlCodec.cs / SemanticProfile.cs
      PublicDocuments\PublicEndpoints.cs / PublicationHtml.cs
      Networking\SafeRemoteFetcher.cs / RemoteCache.cs / DestinationPolicy.cs
      Webmentions\WebmentionEndpoints.cs / WebmentionProtocol.cs / MentionWorker.cs
      Conversations\ThreadAssembler.cs / InteractionService.cs
      Reading\ReadingEndpoints.cs / SubscriptionService.cs / ViewDefinitions.cs
      Streaming\ChangeJournal.cs / ChangeEndpoints.cs / ObserverSignals.cs
      Portability\ArchiveCodec.cs / RestoreService.cs
      Operations\HealthChecks.cs / BackupCommands.cs
      appsettings.json / appsettings.Development.json / Dockerfile
    web\
      package.json / tsconfig.json / vite.config.ts / index.html
      public\connections.example.json
      src\
        main.ts / api\generated.ts / api\client.ts
        sessions\InstanceSession.ts / state\reducers.ts
        views\evaluator.ts / components\ / pages\ / styles.css
  tests\
    FeedConversations.UnitTests\
    FeedConversations.IntegrationTests\
    FeedConversations.AcceptanceTests\
    fixtures\ / web\ / browser\
  scripts\Initialize-Dev.ps1 / Start-Demo.ps1 / Test-All.ps1
  deploy\Caddyfile.example / compose.yaml
  docs\architecture.md / protocol.md / rss-relationships-v1.md
       compatibility.md / operations.md / portability.md / acceptance-demo.md
  .github\workflows\ci.yml
```

Keep feature services inside the API project. No generic repositories, mediator framework, plugin system, or extra microservices. Introduce narrow interfaces for external transport, time and protocol parsing where tests need them.

Minimal API rules: route groups and typed request/result records; thin handlers; `AddProblemDetails`/`IExceptionHandler`; structured non-content logs; explicit owner checks; cancellation through all async work; scoped DbContext per operation; background workers create scopes. Never hold a database transaction across HTTP or SSE.

HTTP/1.1 is the compatibility floor; HTTP/2 where edge supports it, HTTP/3 optional. Transport version is not federation or message delivery. No HTTP server push dependency.

## 5. Identity and shared entry domain

Local post ID is an application-generated UUID. New owned publication:

- Permalink and immutable RSS GUID: `{PublicBaseUrl}/post/{uuid}`.
- Hosted author profile: `/users/{handle}`; handle is immutable in MVP.
- Author feed `/users/{handle}/rss.xml`; local aggregate `/users/rss.xml`.
- Singleton `/post/{id}/rss.xml`; direct replies `/post/{id}/comments.xml`.
- Public directory `/data/subs.opml`; Webmention endpoint `/webmention`.

Production requires configured HTTPS `PublicBaseUrl`; never derive identity from untrusted Host/forwarded headers. Public route/path comparison must use parsed exact normalized local routes, not string-prefix ownership checks.

Shared command:

```json
{
  "title": null,
  "markdown": "My response.",
  "relationship": {
    "kind": "reply",
    "target": {
      "permalink": "https://publisher.example/post/123",
      "identity": { "kind": "permalink", "value": "https://publisher.example/post/123" }
    }
  }
}
```

`relationship` is null for notes. Kind is `reply | like | bookmark`, never inferred from body links. Target identity is either the permalink variant above or `{kind:"feed-guid", feedUrl:"...", guid:"..."}` plus the separate permalink for public linking/delivery. Local targets resolve to local immutable IDs server-side. GUID text is preserved exactly; URL-shaped GUIDs with `isPermaLink=false` are opaque too. Never join by bare opaque text across unrelated feed scopes.

Structured remote reply identities must resolve to the matching item/permalink before commit. Fetch before, not inside, the write transaction. Ambiguous/invalid structured references return a specific 422; origin failures return 502. A reply to an ordinary non-feed URL can use permalink identity without proving feed membership. Likes/bookmarks accept safe syntactic public HTTP(S) target URLs without requiring reachable content or Webmention support; asynchronous notification can be unsupported/failed.

Title/Markdown rules: note/reply require nonblank Markdown; optional title; bookmark allows an optional authored title/note; like rejects authored title/body. Never automatically store a target title/preview/image. All four own GUID, permalink, dates, revision, author and tombstone. Kind/target are immutable after publication; withdraw and create another to change them.

Conservative URL keys: trim; validate absolute HTTP(S), reject userinfo, controls, bad escaping and spaces. Normalize scheme/host/default port only for comparison where appropriate, preserve path/query/fragment meaning, and retain original validated target spelling for wire output. Do not resolve redirects as proof of arbitrary identity aliases. Fragment-bearing backlinks require explicit matching rules: compare full target reference where supplied; local route ownership uses its document URL without treating a fragment as a different owned post.

Like/bookmark create-if-absent uniqueness: owner + relationship kind + target identity key for all nondeleted records including admin-hidden. Concurrent requests return the same active entry, not duplicate publications/jobs. Hidden existing entries return a moderation restriction rather than creating another. Withdrawal is 410; recreating allocates a new GUID.

## 6. Data model and transactions

SQLite: WAL, foreign_keys ON, busy timeout, synchronous FULL, one writer process per file, migrations and short transactions. UTC timestamps; checked enum strings; bounded text; indices/keyset pagination; explicit optimistic integer revisions.

| Table | Required fields / constraints |
|---|---|
| Identity/Accounts | Standard Identity credential/security stamp/lockout tables; unique normalized immutable handle; display name, bio, directory visibility, disabled state. No public email/password metadata. |
| Invites | ID, cryptographic token hash, expiry, creator, atomic consumed-by account; token shown once. |
| Posts | ID, owner, immutable origin GUID, current local URL, published/updated UTC, revision, nullable title/Markdown/sanitized HTML, nullable relation/target union/local target ID, deletedAt, moderation-hidden actor/reason/time, restore provenance. Unique local GUID; owner+published+ID and target+kind indices; active like/bookmark unique partial index. |
| Subscriptions | ID, owner, retrieval URL/key, label/group, revision; unique owner+URL; no remote item history. |
| SavedViews | ID, owner, name/key, schema version, validated definition JSON, revision. |
| InteractionReferences | Local target ID, source URL/key, optional local source ID, verified kind/state, remote scoped identity/author-feed reference if verified, bounded evidence URL metadata, observedAt/error, visible/pending/hidden/withdrawn status. Unique target+source. Never store foreign body/snippet/raw document. |
| MentionJobs | Direction, source/target URLs, local source revision if any, state, attempt, dueAt, leaseUntil/token, bounded error, timestamps; coalescing key direction+source+target. |
| LocalChanges | Monotonic sequence, event/resource kind, resource ID/revision, public or owning-account audience, compact metadata, time. No foreign content or publication history. |
| InstanceState | Stable instance ID, schema/init state. Startup stream incarnation is new on every process start. |
| CommandReceipts | Owner+operation+idempotency key unique, request hash, resource ID/result metadata, created/expiry. Request mismatch 409; resource replay returns current state, never resurrects deletion. |
| ImportRuns | Owner+archive checksum unique, source identity, completion counters; do not retain archive. |
| RestoredPostMappings | Owner+source instance/account+original GUID unique, destination post, imported revision/hash, destination revision at import; detect reimport conflicts. |

Owned post change + journal entry + required remote notification intent commit together. Check capacity and uniqueness inside the transaction. If a local source targets a local post, write its InteractionReferences row atomically instead of making self-HTTP requests; the target author can hide the association without modifying the source publication. Use this one index for counts and metadata, not a union that double counts owned sources.

Post creation accepts an idempotency key; retain receipt 24h, max 1,000/account. After expiry the client must reconcile uncertain state rather than blindly submit a new key. Edits/deletes require If-Match: missing 428, stale 412. Moderator hide is distinct from author deletion; hidden content returns a safe unavailable representation without exporting another author's private moderation notes.

## 7. Resource limits and safe retrieval

Validate options at startup; fail explicitly on limits, never silently truncate a mutation. Retrieval/traversal can return explicit partial results.

| Resource | Default and boundary behavior |
|---|---|
| Local Markdown/title | 256 KiB UTF-8; 500 Unicode scalars title; reject illegal XML characters, never alter identities to serialize. |
| Subscriptions/views | 100/25 per account. |
| Remote HTTP | 1 MiB decompressed, 10s overall per request, max 3 redirects; 4 concurrent/instance, 2/origin. |
| Cache | Dedicated byte-accounted 32 MiB LRU, freshness at most 60s; bound entry/inflight counts too. No disk backing. |
| Active reading | Max 10 selected feeds/view, 50 newest items/source, 500 normalized items total; require narrower selection, show window coverage. |
| Refresh | Active demand only, not more often than 30s/source; respect longer freshness/Retry-After. |
| Thread | Depth 16, 200 nodes, 20 remote fetches total, 10s aggregate assembly deadline. |
| References | 200 total/target, 20,000/instance, 500 pending/instance, across all relation kinds including owned sources. |
| Jobs | 1,000 outstanding/instance; atomic capacity reservation; inbound full returns 503+Retry-After, never false 202. |
| Delivery | Initial attempt then up to 5 retries: 5s, 30s, 2m, 10m, 30m with bounded jitter; terminal after sixth failure. Respect Retry-After. Explicit owner retry after quota check. |
| Stream | 3/account, 100/instance; per-observer wake-up capacity 1, durable recheck at most 5s, heartbeat 15s. |
| Journal | 10,000 records or 24h, whichever retention boundary reached first; reset old cursors. |
| Status retention | Completed jobs 7 days; compact terminal errors 30 days, bounded history cap 10,000/instance. |
| Archives | 25 MiB decompressed, 20,000 posts; reject oversized export/import atomically, no silently partial archive. |
| XML/OPML structure | Max depth 64, 100,000 elements/document within byte cap; 100 imported subscriptions. |
| Request rate defaults | Login 5/min per account+IP; invite redemption 5/min/IP; inbound mentions 30/min/IP; remote-resolution requests 30/min/account; anonymous thread reads 30/min/IP; public feed reads 120/min/IP. Return 429+Retry-After. |

All external I/O uses one SafeRemoteFetcher, including feeds, HTML discovery, Webmention POST, verification and conversation traversal:

- HTTP(S) only; no credentials/ambient cookies/auth; reject private/loopback/link-local/multicast/unspecified/metadata destinations including IPv4-mapped IPv6.
- Resolve and validate destination addresses; pin connection to a validated address through controlled socket connection callback, preserving hostname TLS/SNI validation. Revalidate every redirect, endpoint and connection; auto-redirect disabled.
- Disable proxies inherited from environment for the safe transport unless explicitly configured with equivalent enforcement. Do not route validation around an untrusted proxy.
- Development exception is exact allowed origins/ports for A/B and stubs, gated Development; refuse permissive private-network policy in Production.
- Enforce decompressed bytes incrementally, content type, cancellation/connect/deadline and graph-wide budgets. No retry-on-POST middleware.
- Coalesce requests by retrieval key. One caller canceling does not cancel work still serving others. Bound inflight work; surface queue/timeout errors.
- Cache byte accounting includes retained raw document bytes and normalized body strings, not just response Content-Length. Failed/inflight keys and validators must also have bounded counts and expiry.
- Cache-Control no-store respected. Keep validators only with their representation; 304 without retained body triggers one unconditional fetch.
- XML: DtdProcessing.Prohibit, XmlResolver null, bounded reader counts/depth; no entity expansion. HTML uses inert parsing, not browser execution.
- Sanitize Markdown/rendered foreign HTML. Remove scripts/handlers/iframes/styles and unsafe URLs; do not auto-load remote images/video. Show user-activated external media links. Parse semantic properties before display sanitization with nested-entry scoping.
- Never persist remote bodies in SQLite, files, browser storage/service workers, logs, telemetry, exception dumps, or jobs. Test artifacts containing synthetic remote fixtures are not production storage.

Public comments feeds may fetch **only already approved source references for that local target**, under the same budgets/cache, so they remain public without becoming arbitrary anonymous fetch proxies. General external feed resolution and foreign-root thread hydration require authenticated users and allowed saved/validated targets.

## 8. Exact publication and consumption contract

### Atom/Schema.org application profile

Namespace `atom = http://www.w3.org/2005/Atom`. Parse direct item children by namespace URI/local name, exact class/property IRIs; never fetch vocabulary URLs.

| Input/output | Category term suffix under `https://schema.org/` | Profile rel suffix | Target rel suffix |
|---|---|---|---|
| New local note / remote social note | SocialMediaPosting | author | None |
| Remote blog post (read only) | BlogPosting | author | None |
| Reply | Comment | author | parentItem |
| Basic like | LikeAction | agent | object |
| Public bookmark | BookmarkAction | agent | object |

Exactly one recognized category and profile link for owned items; exactly one target link for relations. Own profile URL `/users/{handle}`. This is an application-defined RSS binding, not RDF, universal reader support, or ActivityPub. BlogPosting is a subtype of SocialMediaPosting, but readers explicitly recognize both without runtime ontology reasoning. Unknown types remain readable/unclassified.

| Incoming situation | Result |
|---|---|
| Recognized type + required matching links | Classify supported kind; attribution remains a claim, not proof of person identity. |
| Repeated equivalent declarations | Deduplicate. |
| Conflicting types/targets or explicit XML/HTML/threading disagreement | Ambiguous; readable but no verified edge/count. |
| Recognized relation type missing required target | Incomplete; do not select an arbitrary other link. |
| No supported profile | Fall back to supported genuine threading or bounded h-entry evidence, otherwise ordinary readable unknown content. |
| Object link alone, class IRI misused as rel, emoji/title/topic hints | No invented relation. |

Resolve relative URL properties using XML base/final document URL with policy checks; output absolute URLs. Preserve exact GUID/attributes, including opaque values. Parse RSS and native Atom inputs; only RSS output in MVP. Model ordinary remote blog/social types separately in display metadata while `entryKinds: post` covers both.

### Core RSS and HTML

Channel title/link/description, self link, lastBuildDate in RFC 1123/GMT. Item own GUID/link, published date in same format, optional real title, safe readable HTML description; atom:updated in RFC3339 only from known modification timestamp. New own GUID has no explicit isPermaLink=true; restored mismatched GUID sets false.

Generate consistent XML/HTML from one stored relation. Public HTML and RSS description use one outer h-entry, entry u-url, p-author h-card, dt-published/updated, authored e-content and generated relation link: u-in-reply-to, u-like-of, u-bookmark-of. User Markdown cannot inject protocol-control classes or nested outer identity. Only URI-valued actual UID may become u-uid; opaque IDs are not coerced.

Readable descriptions for bodyless likes and bookmarks explicitly say Liked/Bookmarked with target link; no fabricated article title. Exact authored Markdown stays in owned API/archive. Optional display-source Markdown includes a generated target-context link followed by authored text, not copied foreign text; bodyless likes can omit it.

External publisher input source Markdown namespace HTTP remains recognized. New app output uses `https://source.scripting.com/` for its source extension profile; accept exactly documented HTTP/HTTPS slash variants as explicit parser aliases, not generic namespace equivalence.

Reply-only fields:

```xml
<source:inReplyTo>https://a.example/post/parent</source:inReplyTo>
<thr:in-reply-to ref="https://a.example/post/parent"
                 href="https://a.example/post/parent" />
```

`thr = http://purl.org/syndication/thread/1.0`. When actual parent identity is opaque, source:inReplyTo uses `isPermaLink="false"` and preserved ID; thr ref uses known absolute parent identifier only when representable, href uses parent permalink. Do not emit an invalid relative opaque ref or invent its scope from a global ID. Typed API/archive retains feed scope.

Every visible local publication can advertise its own real direct-reply feed:

```xml
<source:comments count="0" feedUrl="https://a.example/post/id/comments.xml" />
```

The address exists even when zero. Count means known approved direct replies, not global internet totals. Likes/bookmarks are never replies to their targets, but can themselves receive replies. Do not emit source:likes (that means a likes-server API, not like-of). No extra source account/self fields required for MVP; avoid optional unverified wire features.

### Complete synthetic feed fixture required before producer implementation

The following is namespace-complete proposed output for all four authored kinds. `a.example` and all values are synthetic. Additional fixture `publisher-five-kinds.xml` includes BlogPosting input using the published website binding. Tests must parse both without calling the producer mapper.

```xml
<?xml version="1.0" encoding="UTF-8"?>
<rss version="2.0"
     xmlns:atom="http://www.w3.org/2005/Atom"
     xmlns:source="https://source.scripting.com/"
     xmlns:thr="http://purl.org/syndication/thread/1.0">
  <channel>
    <title>A community</title>
    <link>https://a.example/</link>
    <description>Public local publications</description>
    <lastBuildDate>Fri, 16 Oct 2026 18:00:00 GMT</lastBuildDate>
    <atom:link rel="self" type="application/rss+xml" href="https://a.example/users/rss.xml" />
    <item>
      <guid>https://a.example/post/note-1</guid>
      <link>https://a.example/post/note-1</link>
      <pubDate>Fri, 16 Oct 2026 17:00:00 GMT</pubDate>
      <atom:category term="https://schema.org/SocialMediaPosting" />
      <atom:link rel="https://schema.org/author" href="https://a.example/users/alice" />
      <source:comments count="1" feedUrl="https://a.example/post/note-1/comments.xml" />
      <description><![CDATA[<article class="h-entry"><a class="u-url" href="https://a.example/post/note-1">Permalink</a><a class="p-author h-card" href="https://a.example/users/alice">Alice</a><time class="dt-published" datetime="2026-10-16T17:00:00Z">Published</time><div class="e-content"><p>Hello open feeds.</p></div></article>]]></description>
      <source:markdown><![CDATA[Hello open feeds.]]></source:markdown>
    </item>
    <item>
      <guid>https://a.example/post/reply-1</guid>
      <link>https://a.example/post/reply-1</link>
      <pubDate>Fri, 16 Oct 2026 16:00:00 GMT</pubDate>
      <atom:category term="https://schema.org/Comment" />
      <atom:link rel="https://schema.org/author" href="https://a.example/users/bob" />
      <atom:link rel="https://schema.org/parentItem" href="https://b.example/post/root" />
      <source:inReplyTo>https://b.example/post/root</source:inReplyTo>
      <thr:in-reply-to ref="https://b.example/post/root" href="https://b.example/post/root" />
      <source:comments count="0" feedUrl="https://a.example/post/reply-1/comments.xml" />
      <description><![CDATA[<article class="h-entry"><a class="u-url" href="https://a.example/post/reply-1">Permalink</a><a class="p-author h-card" href="https://a.example/users/bob">Bob</a><time class="dt-published" datetime="2026-10-16T16:00:00Z">Published</time><p>Reply to <a class="u-in-reply-to" href="https://b.example/post/root">this post</a>.</p><div class="e-content"><p>I agree.</p></div></article>]]></description>
      <source:markdown><![CDATA[Reply to <https://b.example/post/root>

I agree.]]></source:markdown>
    </item>
    <item>
      <guid>https://a.example/post/like-1</guid>
      <link>https://a.example/post/like-1</link>
      <pubDate>Fri, 16 Oct 2026 15:00:00 GMT</pubDate>
      <atom:category term="https://schema.org/LikeAction" />
      <atom:link rel="https://schema.org/agent" href="https://a.example/users/alice" />
      <atom:link rel="https://schema.org/object" href="https://example.org/article?x=1&amp;y=two" />
      <source:comments count="0" feedUrl="https://a.example/post/like-1/comments.xml" />
      <description><![CDATA[<article class="h-entry"><a class="u-url" href="https://a.example/post/like-1">Permalink</a><a class="p-author h-card" href="https://a.example/users/alice">Alice</a><time class="dt-published" datetime="2026-10-16T15:00:00Z">Published</time><p>Liked <a class="u-like-of" href="https://example.org/article?x=1&amp;y=two">this page</a>.</p></article>]]></description>
    </item>
    <item>
      <guid>https://a.example/post/bookmark-1</guid>
      <link>https://a.example/post/bookmark-1</link>
      <pubDate>Fri, 16 Oct 2026 14:00:00 GMT</pubDate>
      <atom:category term="https://schema.org/BookmarkAction" />
      <atom:link rel="https://schema.org/agent" href="https://a.example/users/bob" />
      <atom:link rel="https://schema.org/object" href="https://example.org/reference" />
      <source:comments count="0" feedUrl="https://a.example/post/bookmark-1/comments.xml" />
      <description><![CDATA[<article class="h-entry"><a class="u-url" href="https://a.example/post/bookmark-1">Permalink</a><a class="p-author h-card" href="https://a.example/users/bob">Bob</a><time class="dt-published" datetime="2026-10-16T14:00:00Z">Published</time><p>Bookmarked <a class="u-bookmark-of" href="https://example.org/reference">this page</a>.</p><div class="e-content"><p>Useful reference.</p></div></article>]]></description>
      <source:markdown><![CDATA[Bookmarked <https://example.org/reference>

Useful reference.]]></source:markdown>
    </item>
  </channel>
</rss>
```

Fixture UUID-like short labels are only explanatory; runtime route IDs are real UUIDs. Add a separate local-child fixture matching note-1's count; standalone sample is not a complete conversation archive. Add explicit channel/item RSS `source` attribution to relayed items (below); mixed locally authored aggregate already has per-item profile links.

### Feeds, deletion and conversation relay

Own author/aggregate feeds contain local publications only, newest first, max 50 current visible items; singleton remains available after window expiry. All use same serializer. Conditional GET with ETag hash of actual representation; lastBuildDate comes from latest relevant change (not wall-clock every GET), allowing stable conditional responses.

HTML works unauthenticated without SPA, with canonical/permalink, h-entry/backlink, singleton/author alternate links, and HTTP/HTML rel=webmention discovery. Deleted source returns 410; hidden is safe unavailable; descendants remain independently accessible.

Comments feeds include local replies and approved remote references only. Read reference metadata, close transaction, then bounded transient hydration. Preserve origin GUID, own permalink, profile/author and origin comments pointer. Include RSS core `<source url="ORIGIN_AUTHOR_FEED">Origin author</source>` when known; never substitute the relayer as author or invent unknown fields. No remote bodies in owned author/aggregate feeds. Unavailable source yields clearly labeled link-only placeholder preserving known source reference; it is not a claim of deletion. Count can include unavailable approved references.

Expose partial status through `X-Conversation-Partial` and JSON reasons. Hash resolved bounded output for ETag or use conservative revalidation; never issue 304 based solely on local revision while foreign bodies may change. One comments feed is one level, not recursive crawling.

## 9. Webmention, authority and thread assembly

### Outbound

Publish locally first, with job intent atomically committed. Worker discovers first matching rel=webmention HTTP Link header, otherwise first matching HTML link/anchor in document order; resolve relative endpoints/final URL/base, preserve queries. POST standard form source+target through safe transport. No local cookies/credentials travel to receiver.

States: pending, discovering, delivering, accepted, unsupported, retrying, failed. A 2xx means accepted, not displayed. Permanent 4xx except retryable 408/429 is terminal; no endpoint is unsupported; timeout/429/5xx retry within budget. Edits/deletes requeue/coalesce current source state. Worker lease token guards stale completion; if a newer revision arrived, stale acceptance must not clear its pending intent. Claim/commit recovery after crashes must be tested.

### Inbound

Validate form URLs, actual owned target, size/rate/capacity and source!=target; persist metadata job then 202. Worker retrieves source, proves actual hyperlink to target, selects the source's matching outer h-entry (exclude nested quoted entries). Supported properties or matching origin/singleton RSS type+target can verify kind; feed item must correspond to the source URL. An unrelated aggregate claim is not authority.

Generic backlink => mention, not reply. Missing supported evidence => unclassified/unsupported. Explicit conflicting evidence => ambiguous, no edge/count. A profile URL is attribution, not proof of global human identity.

Configured trusted exact origins may auto-approve; others pending target-author/admin action. Trust does not bypass verification or budgets. Duplicate source+target notifications update one reference. Verified 410 or fetched source without backlink withdraws; timeout/429/5xx does not. Reverify on notification or active refresh, not global background crawl.

Local reference capacity failure rejects owned commands atomically. Remote admission/reclassification quota failures remain explicit to admins and must not evict existing verified references silently.

### Thread assembly

Resolve parents upward and advertised direct replies downward with visited scoped identities and one shared depth/node/fetch/deadline budget. Join approved local references and local replies to foreign roots even before receiver acceptance. Deduplicate by proven identity, not names/time windows. Foreign comments relay is a hint; verify at origin before recording durable authority.

Reply edges only: likes/bookmarks display as separate paged metadata associations. Replying to a like is valid; that like's target is still not its parent.

Return `root`, `nodes`, `complete`, `partialReasons`, and per-node availability. Complete means all currently known advertised inputs resolved within bounds, not every reply on the internet. Cycles/self-parenting/unknown parents terminate with honest context; outage does not delete references. Counts are known approved publications, not unique people or globally current totals.

## 10. Reactive browser, subscriptions and streaming

One `InstanceSession` per active configured connection owns fetches/timers/state/EventSource. Runtime connections.json contains only ID, label, relative proxy prefix and public origin. No arbitrary user-provided proxy destinations.

Private snapshot `/state` reads local page + workspace + journal high-water mark in one SQLite read transaction. Cursor contains instance ID, fresh process startup incarnation, sequence. SSE uses built-in typed API; invalid obsolete cursor emits `reset-required` event then closes. Native EventSource cannot inspect error bodies, so opaque error closes stream and probes `/me` then fetches a snapshot with bounded backoff. Malformed/auth failures remain 400/401/403.

Journal stores compact local identifiers/revisions only. One capacity-1 channel per observer is a wake-up hint, not competing subscribers on one queue; periodic durable recheck covers missed hints. Batch up to 100; checkpoints can advance past filtered private rows without leaking their IDs. Slow/expired/disabled sessions close. Backend restart/rewound backup forces snapshot reset.

SSE event names are `change`, `checkpoint`, `reset-required`, `heartbeat`. Heartbeat is a typed event with `{}` and no ID, not a journal change; it must not advance the cursor. Each change/checkpoint ID is an opaque cursor. A change carries current revision metadata, not a post body. Client ignores already applied resource revisions and refetches current state; it must not infer that every intermediate edit was observed.

```text
id: opaque-cursor
event: change
data: {"resourceType":"post","resourceId":"uuid","revision":2,"operation":"upsert"}

event: reset-required
data: {"reason":"incarnation-changed"}

```

Other operations are `delete` and `invalidate`; private view/subscription resources use the same envelope with owner filtering. `checkpoint` data is `{}`. Reset reasons are `instance-changed`, `incarnation-changed`, `retention-expired`, `future-cursor`, `slow-consumer`. Do not put internal database errors or other users' IDs into stream payloads.

Frontend flow: instance discovery -> user/CSRF -> snapshot -> one SSE -> selected remote resolve. RxJS switchMap for replaceable reads, never for commands assumed cancellable. Pure reducers merge by stable identity+revision. combineLatest recomputes view membership after edits/filter changes. share latest bounded state, no growing history/ReplaySubject. Dispose on logout/connection switch; generation IDs reject late old responses.

Browser refreshes selected feeds only while the view is observed; backend coalesces shared requests/cache and enforces per-source intervals. Subscriptions alone do not launch a background importer. Stop timers when hidden/unobserved, refresh with freshness status on return. No remote history persisted in browser.

Saved view schema:

```json
{
  "version": 1, "name": "Following",
  "includeLocal": true,
  "sourceFeedUrls": ["https://publisher.example/tags/rss/feed.xml"],
  "textContains": null, "textExcludes": null,
  "sort": "published-desc",
  "entryKinds": ["post", "reply", "bookmark", "like"]
}
```

Selected URLs must be owned subscriptions; reject unknown fields/invalid schema, scripts/regex. Text predicates apply invariant case-insensitive matching over plain title/body and explicit target URL, never target content; include/exclude compose with source/kind filters. Remote BlogPosting and SocialMediaPosting both satisfy post kind, retaining separate display labels. Sort published time then scoped stable identity; edits do not bump order. Show loaded windows/coverage and per-source stale/error state, never success-shaped empty results.

Pages: connection/login/invite, timeline/local/following/custom view, subscriptions+OPML preview, composer, thread, delivery state, moderation, profile, archive settings, minimal admin. Mark Like/Public bookmark as public publishing, not private save. Command pending/result/error and idempotency reconciliation are visible. Persist only connection bookmarks/unsent drafts locally.

## 11. API inventory and concrete responses

Private APIs under `/api/v1`; application/json camelCase; bounded keyset pages `{items,nextCursor}`. Public HTML/RSS remain unversioned.

| Method/path | Access and behavior |
|---|---|
| GET /api/v1/instance | Public identity, API/profile capabilities, feed directory URLs; no internal topology. |
| GET /api/v1/auth/csrf | No-store token; usable pre-login; isolated cookie. |
| POST /api/v1/auth/redeem-invite | Single-use handle/password/code; antiforgery; atomic account creation/session. |
| POST /api/v1/auth/login, /logout, /change-password | Cookie Identity; CSRF including login/logout; current password for change. |
| GET/PATCH /api/v1/me | Own metadata/profile visibility; handle immutable; revision guard for profile update. |
| POST /api/v1/admin/invites | Admin; one-time token output; bounded expiry. |
| POST /api/v1/admin/accounts/{id}/disable or /enable | Admin; revoke sessions on disable. |
| POST /api/v1/admin/posts/{id}/hide or /unhide | Admin; moderation distinct from deletion. |
| GET /api/v1/admin/jobs | Admin bounded status; no raw foreign content. |
| GET /api/v1/state | Authenticated consistent local/workspace snapshot+cursor. |
| GET /api/v1/posts[?author=...&cursor=...] | Public local page, max 50. |
| GET /api/v1/posts/{id} | Public current local state, ETag; no remote hydration. |
| POST /api/v1/posts | Auth+CSRF+idempotency; 201+Location for new, 200 existing like/bookmark. |
| PATCH/DELETE /api/v1/posts/{id} | Owner+CSRF+If-Match; immutable relation/target; tombstone delete. |
| GET /api/v1/posts/{id}/delivery | Owner delivery metadata. |
| POST /api/v1/posts/{id}/delivery/retry | Owner+CSRF; quota/backoff policy. |
| GET /api/v1/posts/{id}/interactions?kind=... | Public visible reference metadata/counts/observedAt; no foreign bodies. |
| GET /api/v1/thread?url=... | Public local metadata only; authenticated bounded remote hydration. |
| GET /api/v1/changes?after=... | Authenticated audience-filtered SSE. |
| GET/POST/DELETE /api/v1/subscriptions[/{id}] | Own sources; CSRF on changes. |
| GET/POST/PATCH/DELETE /api/v1/views[/{id}] | Own definitions; revision guards for edits/deletes. |
| POST /api/v1/reading/resolve | Auth+CSRF, selected saved sources; transient per-source results. |
| POST /api/v1/opml/preview, /import | Auth+CSRF, text preview then explicit selected entries. No network fetch on import. |
| GET /api/v1/opml/export | Private subscription OPML attachment. |
| GET /api/v1/interactions/pending | Own target author/admin only. |
| POST /api/v1/interactions/{id}/approve or /hide | Target author/admin+CSRF; cannot promote ambiguous evidence to a fabricated type. |
| POST /api/v1/auth/confirm-password | Recent-auth window 5m for sensitive archive operations. |
| GET /api/v1/export | Auth+recent confirmation; consistent bounded archive. |
| POST /api/v1/import/preview, /commit | Auth+recent confirmation+CSRF; validated dry-run and hash-bound revalidated commit. |
| GET /users/{handle}, /post/{id} | Public independent HTML. |
| GET /users/{handle}/rss.xml, /users/rss.xml | Public owned feeds. |
| GET /post/{id}/rss.xml, /post/{id}/comments.xml | Public singleton/replies under scoped budgets. |
| GET /data/subs.opml | Opted-in author feed directory only. |
| POST /webmention | Anonymous standard form; bounded verification queue. |
| GET /health, /alive | Readiness/liveness; detailed internals restricted. |

POST result shape: `{post, delivery:{state}, created}` with post `{id,guid,permalink,author,publishedAt,updatedAt,revision,kind,title,markdown,html,relationship}`. Public metadata excludes private workspace/moderation reasons. Owner API may expose own Markdown; remote reading DTO is transient and includes source/availability/freshness/semanticStatus.

Use `Idempotency-Key` on every create command, required by the API and generated once by the browser per user intent; missing/invalid key is 400. Use strong opaque ETag values in If-Match, not numeric revision parsing in the browser. `PATCH` modifies only supplied editable fields; the API rejects attempts to change owner/GUID/publishedAt/kind/target.

Reading resolve response is `{sources:[{feedUrl,status,observedAt,freshUntil,items,error}], coverage:{selectedSources,maxSources,maxItems}}`; source status is `fresh | stale | failed | rate-limited`. Partial failures preserve successfully fetched sources and expose machine error codes. Never turn failed sources into apparently successful empty feeds.

Thread node fields are `{identity,permalink,parentIdentity,kind,author,publication,availability}`; publication contains transient rendered content only when allowed/available. Availability is `local | fetched | unavailable | ambiguous | tombstone`. `partialReasons` uses `fetch-budget | node-budget | depth-budget | deadline | origin-unavailable | ambiguous-identity | cycle`. Unknown total absence cannot be represented as a global completeness guarantee.

Error body uses Problem Details `type,title,status,code,traceId,errors` where applicable. 400 syntax/domain fields; 401/403 auth/policy; 404 missing; 409 conflict/quota-specific uniqueness; 410 deleted; 412 stale; 413 bytes; 422 ambiguous structured target; 428 missing revision; 429 rate; 502 origin fetch; 503 local exhausted capacity. No redirect-to-login for APIs and no blanket catch returning empty success.

## 12. Accounts, Aspire and independent deployment

Identity hashing/lockout/session stamps; no custom crypto. First admin is local CLI operation with interactive input, no public first-run endpoint. Invite tokens cryptographic, hashed, one-use, no email required. Recovery is documented operator action revoking sessions. Directory opt-in defaults false; unlisted is not private. Min password 12 characters, Identity hashing/default protections, no logged credentials.

Disabled-state checks apply to every authenticated command, snapshot and stream establishment; periodic stream checks close an already-open session within 5 seconds of disable/security revocation. Do not rely solely on Identity's longer default security-stamp validation interval for this acceptance condition. Password confirmation/antiforgery state is per instance, never shared through frontend storage.

Frontend development HTTPS `localhost:7440`; A `:7441`, B `:7442` subject to port checks; random ports in tests. Ports do not isolate cookies. Cookie names include stable instance ID; browser-facing cookie and antiforgery paths `/connections/a` and `/connections/b`; host-only, Secure, HttpOnly auth cookie, SameSite=Lax. Data Protection keys separate/persistent per backend. Proxy strips prefix but does not spoof Origin; explicitly configure allowed frontend origin/trusted forwarding. Integration-test real CSRF/token/cookie behavior through proxy.

AppHost AddProject same API twice, resource names api-a/api-b, separate SQLite/key directories and options, not replicas of one database. AddViteApp as separate JavaScript resource; explicit targets from known endpoints, readiness WaitFor, no arbitrary proxy URL parameter. Earliest scaffold must start this topology; later T12 hardens it. Process development needs no database container/Docker. HTTPS dev certificate setup documented, no globally disabled TLS validation.

Development-only explicit seed creates Alice/Bob and Carol/David with generated untracked credentials, refuses Production and repeats idempotently. PublicBaseUrl fixed before first authored entry. Scripts track child PIDs/readiness and stop only their own processes.

Production: backend container works without Node/AppHost, static frontend deployable independently; Caddy example fixed connection routing plus canonical public domains. Persist SQLite and Data Protection independently; trust only configured proxies. No public cache on private responses, no SSE buffering. Public XML supports ETag, proper MIME and credential-free optional CORS. No requirement to expose development setup publicly.

Telemetry: latency/bytes/cache size/inflight, queue leases/retries/depth, active observations, journal lag/reset, partial thread reasons. Never content, secrets, raw archives or credentials.

## 13. Export, restore and operator recovery

Account ZIP v1: manifest.json, profile.json, posts.json, subscriptions.opml, views.json. Manifest carries version/export ID/source identity/URLs/consistent cursor/counts/SHA256 per file. Include all owned current entries including deletion markers and restored provenance; likes/bodyless targets must round-trip. No credentials/keys/jobs/cursors/foreign bodies or target snapshots.

Build bounded export from consistent snapshot into temporary private file; close DB read before streaming; cleanup on success/failure/abandoned expiry. If too large fail explicitly, not partial export.

Import validates path traversal/duplicate entries/allowlist/compression sizes/checksums/schema/record counts/ownership/unique associations before any writes. Preview gives new/unchanged/conflicts; commit revalidates the uploaded bytes/hash and current destination revisions, never trusts a client-edited preview. No long-lived server archive required.

Preserve original GUID/source provenance, allocate destination row IDs/permalinks and emit GUID false when different. Do not claim restored copy proves origin control; exclude unverified restored source authority in Webmention classification. Preserve foreign parent scope, rewrite only verified within-archive links where explicitly valid. Do not auto-notify old targets or resurrect a deleted source on replay.

Atomic bounded commit + snapshot invalidation. Identical archive no-op; changed later archive conflicts with independently edited destination, no last-write-wins. Local duplicate GUID across different destination accounts is conflict, not silent reassignment. Rebuild local reference index with quotas before commit. All failures leave destination unchanged.

Operator backup uses SQLite backup API or stopped-service copy, never main file alone while WAL active. Protect/copy runtime identity/config/Data Protection keys separately, no git backups. Restore test into isolated directory and new startup incarnation forces client reset. Restoring data does not restore old-domain routing or remote offline content.

## 14. Implementation tasks and delivery milestones

These task IDs identify implementation work. Dependencies are prerequisites, not just list order. Work in one implementation checkout initially; no automatic large agent workflow. Each task finishes with passing narrow checks and a local commit; a partial task never counts as the MVP. Use reviewed pull requests for implementation changes.

### Milestone 1: foundation that runs

**T01 `bootstrap`** - dependencies none. Create only new target directory in an implementation checkout; scaffold API/ServiceDefaults/AppHost, independent Vite TS workspace, unit/integration/acceptance projects, pinned tools/locks, .gitignore and feature folders. No controllers/weather/demo endpoints. Add basic instance/health routes. Wire api-a/api-b/Vite process topology immediately. Gate: clean restore/build/typecheck/static build; AppHost readiness on all resources with distinct data paths; SDK/dependency API compile smokes. No data/secrets tracked.

**T02 `protocol-contracts`** - T01. Implement typed contracts/options/errors/scoped identities and docs; commit synthetic four-kind output plus five-kind external publisher input, singleton/comments/OPML/Atom/backlink/conflict fixtures before producer code. Include exact type/role/cardinality, source aliases, unknown/incomplete handling and tested URL keys. Gate: independent namespace parser accepts all valid fixtures and rejects conflict/unsafe cases without ontology fetch.

**T03 `storage-identity`** - T01,T02. Entities/migrations/indices/quotas, admin CLI, invite/login/logout/profile/disable, CSRF, isolated sessions, transactional receipts/references/journal/job intents. Gate: concurrent invite redemption one winner, ownership/private workspace isolation, migration/restart, A cookies cannot authenticate B, API 401 not redirect, stale revision behavior.

**T12 first pass `aspire-topology`** - scaffolding in T01, complete after T03,T05. Harden real HTTPS proxy/cookies/keys/seed, random-port tests and independent execution. Gate: one command starts A/B/UI, four separate users, no Docker dependency in process demo and no production seed. Task not marked done until all gates pass.

### Milestone 2: first end-to-end interaction

**T04 `safe-fetch`** - T01,T02. Safe transport/cache/parser and semantic extraction per sections 7-8. Gate: rebinding/redirect/IPv6/XXE/decompression/no-store/304/coalescing tests; no durable foreign bytes; deterministic publisher-five-kind input recognized.

**T05 `publishing-feeds`** - T03,T04. Commands/ownership/revisions/Markdown/sanitization, all four kinds, XML/public HTML, singleton/local author/aggregate/comments local children and directory. Exact mapping and readable fallback, source contexts, tombstones and ETags. Gate: independent consumer parses every representation, edits preserve GUID/publication date, like uniqueness/withdrawal tests, no local self-HTTP.

**T06 `cross-instance-replies`** - T03,T04,T05. Durable job worker/leases, pure IWebmentionProtocol discovery/classification adapter, send/receive/moderation/delivery state, coalescing and withdrawal. Incorporate reference-project concepts without adopting their internal HttpClients. Gate: A root->B reply->A approved reference without follows; malformed/generic/forged/ambiguous/410/timeout/duplicate and crash-during-newer-edit cases.

**T09a `workspace-reading-core`** - T02,T03,T04. Implement subscriptions + bounded resolve first for the browser loop; this is a tracked prerequisite of full T09, not completion of views/OPML.

**T10a `web-first-loop`** - T02,T03,T04,T05,T06,T09a,T12. Deliver connection/login/feed reading/composer/reply/delivery UI before waiting for all final client dependencies. Use request/response state until T08 streaming is integrated, not a throwaway second app. Gate: controlled external feed->authenticated reply->independent public RSS->accepted Webmention; optional read-only external publisher read-only smoke. End-to-end test target is controlled peer, not the live website.

Milestone 2 is demonstrable progress, not a release. Do not claim a complete conversation network, portability or full MVP yet.

### Milestone 3: complete network and reactive workspace

**T07 `thread-assembly`** - T04,T05,T06. Scoped graph, budgets, origin-verified comments relay, orphan/partial states, metadata-only nonreply associations. Gate: A->B->A->B and reverse root traversal via independent RSS walker, cycle/collision/outage tests, no foreign storage.

**T08 `streaming`** - T03,T05. Consistent snapshots, durable journal, typed SSE, per-observer signals, incarnation/checkpoints/reset/auth lifecycle. Gate: snapshot race, lost signal, retention overflow, slow/disconnected stream, restored DB and filtered private event cases.

**T09 completion `workspace-views-opml`** - T02,T03,T04,T09a. Full CRUD/views schema/keyset/revisions/quotas, active resolve, OPML text preview/selected import/export/directory distinction. Gate: nested OPML grouping/repeat-safe dedupe, view predicates compose and no unsolicited feed fetch from import.

**T10 completion `web-client`** - T02,T03,T05,T06,T07,T08,T09,T10a. All pages, pure reducers/RxJS lifecycle, all public actions/retraction/moderation, filter recomputation and errors. Gate: browser real auth/SSE/instance switching, keyboard controls, command ambiguity handling, no durable remote content or passive media requests.

### Milestone 4: portable and operationally reproducible MVP

**T11 `portability`** - T03,T05,T09. Archive codec/preview/revalidate commit, collision/provenance/local index rules and settings UI. Gate: round-trip four kinds/deletes/views, identical import no-op, edited reimport conflict, malicious zip/quotas no write, no old-origin automatic notifications.

**T13 `acceptance`** - T06,T07,T08,T10,T11,T12. Execute all section 15 cases in isolated real HTTP topology + browser; independent parser/walker must not call producer mapping helpers. Gate: complete passing evidence, no third-party writes.

**T14 `operations-ci`** - T04,T06,T08,T11,T12. Standalone container/static/Caddy, CI locked builds/tests, backup/restore and limits docs. Gate: container public HTML independent of SPA, cookie isolation/streaming through production proxy, isolated restored DB reset, no public private caches.

**T15 `release-readiness`** - T13,T14. README walkthrough/protocol/compatibility/operations, fresh checkout reproducibility, checked secrets/artifacts, documented actual interop limits. Gate: all MVP criteria pass; no public deployment performed. Choose license with user before public release; do not silently select MIT.

## 15. Executable acceptance matrix

All network-write tests target isolated A/B/stub origins, not public publishers. Use fake time and observable bounded deadlines, not sleeps. Exercise meaningful limits at limit-1, exact and limit+1.

| ID | Scenario / required assertion |
|---|---|
| A01 | Alice note on A appears with same own identity/author/body in public HTML, singleton, Alice and aggregate RSS. |
| A02 | Bob local reply has own GUID and exact parent; root comments includes it once; count/HTML agree. |
| A03 | Carol reads Alice from B transiently; no remote body written to B SQLite/files/logs/browser. |
| A04 | Carol B reply reaches A via Webmention with no mutual follow; only approved verified reference persists on A. |
| A05 | David replies to Carol on B; nesting discoverable through Carol's advertised comments feed. |
| A06 | Bob replies from A to David B; independent walker reaches Alice->Carol->David->Bob with origin attribution. |
| A07 | Independent David B root receives Alice A reply; no hardcoded directional assumptions. |
| A08 | Carol edits: GUID/published time stable, updated timestamp/body change, A revalidates, view membership recomputes. |
| A09 | Delete Carol: 410/withdrawal, descendants still reachable with missing-parent context; timeout never equals deletion. |
| A10 | Independent RSS-only walker uses public feeds, no credentials/JSON API, scoped identities preserved. |
| A11 | Source/RFC threading + explicit namespace aliases + opaque feed collisions; actual source pointers and Markdown fallback tested. |
| A12 | Plain RSS/Atom without interaction support remains readable; remote delivery honestly unsupported. |
| A13 | Saved source/text/kind filters recompute after edit/rule change; no target-body search. |
| A14 | Directory only opt-in member feeds; private subscriptions stay private; repeated selected OPML import no duplicates/network crawl. |
| A15 | Snapshot-subscribe race/reconnect/double invalidation never loses or duplicates owned state. |
| A16 | Backend restart/older restore forces new snapshot, never accepts impossible future cursor. |
| A17 | B unavailable: partial/source links, no permanent foreign body fallback or false delete; local publishing can queue. |
| A18 | Closing both viewers stops active refresh; subscriptions alone do not ingest; cache still bounded. |
| A19 | Cross-user mutation/workspace denied; independent cookies/CSRF/keys; late responses cannot leak after switch/logout. |
| A20 | Duplicate/forged/abusive/unknown Webmention has verified classification/moderation, quotas and idempotency. |
| A21 | SSRF/redirect/rebinding/IPv6/XXE/XSS/decompression/cycles terminate safely; budgets aggregate across graph. |
| A22 | Archive restores only authored state/settings, with provenance and no credentials/foreign bodies; reimport atomic/idempotent. |
| A23 | Inspect DB/WAL/logs/telemetry/temp/browser storage after tests: no synthetic remote body sentinel retained; metadata quotas enforced. |
| A24 | Measured cache bytes/inflight/request counts respect limits; validators/no-store/coalescing cancellation correct. |
| A25 | Backend+independent static UI work without Aspire/Node on backend; process demo needs no Docker. |
| A26 | Carol likes Alice: owned B bodyless publication, A verified metadata only; no reply count/tree pollution. |
| A27 | David public bookmark with optional authored note; own GUID; no fetched target title/body/image stored. |
| A28 | Parallel/retried local like/bookmark one active source/job/count; hidden cannot bypass uniqueness; no human-name dedupe. |
| A29 | Withdraw/recreate: original 410, remote withdrawn after proof, new GUID, transient error leaves association. |
| A30 | HTML/Markdown-preferring readers retain target context; independent XML and bounded MF2 extraction agree; stripped classes remain readable. |
| A31 | Incomplete/conflicting XML/MF2/threading/nested quotes cannot spoof relationships; generic backlink not a reply. |
| A32 | Kind filter/export/import handles notes/replies/bookmarks/bodyless likes and active uniqueness conflicts. |
| A33 | Like/bookmark ordinary non-feed target publishes without reachable target or account; policy still blocks unsafe fetch. |
| A34 | Publisher-five-kind fixture/live opt-in smoke classifies BlogPosting vs SocialMediaPosting; app authors only notes, not inferred blog posts from length/title. |
| A35 | Idempotency request mismatch/expiry/deleted-resource replay explicit; revision guard and queue-full command leave no partial commit. |
| A36 | Worker leased stale revision cannot clear newer edit/delete intent; restart/retry does not lose committed notification. |

## 16. Commands and completion evidence

Implement these scripts/project names so the handoff is executable. Root npm workspace name `@feed-conversations/web`; root browser script uses `tests\browser`, frontend Vitest includes `tests\web`.

Bootstrap uses ecosystem scaffolding, pins tool versions and then first restore/install; after lockfiles exist CI uses locked commands. Do not run package installation during planning. Build the app solution before --no-build tests.

```powershell
dotnet restore FeedConversations.slnx --locked-mode
dotnet build FeedConversations.slnx -c Release --no-restore
dotnet test tests\FeedConversations.UnitTests -c Release --no-build
dotnet test tests\FeedConversations.IntegrationTests -c Release --no-build
npm ci
npm run typecheck --workspace @feed-conversations/web
npm run test --workspace @feed-conversations/web -- --run
npm run build --workspace @feed-conversations/web
dotnet test tests\FeedConversations.AcceptanceTests -c Release --no-build
npm run test:browser
.\scripts\Start-Demo.ps1
```

Document Playwright browser installation separately; Test-All orchestrates bounded attached test helpers and cleans only their specific artifacts/processes. Start-Demo confirms all readiness endpoints and tells the user how to stop its own resources. Human demo may be left running only when requested.

Per task evidence: commands+outcomes, independent fixture assertions, intentionally untested limits. Final evidence: four-user browser walkthrough, RSS-only walker trace, storage sentinel inspection, measured limit tests, backup/restore, standalone startup and clean-clone reproduction. Do not replace these with "build passes" or producer round-trip alone.

## 17. Reference policy and release boundary

Primary references:

- ASP.NET Core .NET 10 [Minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis?view=aspnetcore-10.0), [SSE](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/responses?view=aspnetcore-10.0), [validation](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/validation?view=aspnetcore-10.0).
- Official [Aspire JavaScript integration](https://aspire.dev/integrations/frameworks/javascript/) and [testing](https://aspire.dev/testing/overview/); verify APIs against pinned stable package version.
- [RSS 2.0](https://www.rssboard.org/rss-specification), [RFC 4287](https://www.rfc-editor.org/rfc/rfc4287), [RFC 4685](https://www.rfc-editor.org/rfc/rfc4685), [source namespace](https://source.scripting.com/), [OPML](https://2005.opml.org/spec2.html).
- [Webmention](https://www.w3.org/TR/webmention/), [h-entry](https://microformats.org/wiki/h-entry), [bookmark](https://indieweb.org/bookmark), Schema.org [BlogPosting](https://schema.org/BlogPosting), [SocialMediaPosting](https://schema.org/SocialMediaPosting), [Comment](https://schema.org/Comment), [LikeAction](https://schema.org/LikeAction), [BookmarkAction](https://schema.org/BookmarkAction).
- rss.chat `0a77f7b0cdb6d61291248ded69daa6b78f10860a`; RSC `be06a5a939a357b090fcf46ed93c71701eaee659`. Fixture-shaped compatibility is not full API/firehose/live federation compatibility.
- WebmentionFs `bbdc1ab811588a8c541f8f3a28304cd8897e3160` (MIT), WebmentionService `d58c648294d0ac7ac2783c4f105335bb0795786f` (no license found at inspection). Reuse protocol concepts/test cases, not their unbounded/internal HttpClients or Azure infrastructure. Implement pure discovery/classification adapter on shared SafeRemoteFetcher. Attribute any actually adapted MIT code; do not copy unlicensed implementation.

No NATS/Rx.NET/ActivityPub is needed to prove this design. RxJS models observed client state; durable SQLite/jobs + Channels/async streams provide backend reliability. RSS publication, notification discovery, identity authority and transient presentation remain separate.

The MVP is done only when every included feature and A01-A36 passes, data/identity and operator boundaries are documented, and the two-instance demo and standalone deployment reproduce. A full independent GUI-reader ingestion test may be reported if performed; XML/HTML fixture compatibility must not be described as all-reader support.
