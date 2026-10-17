# Architecture and data ownership

The API is one .NET 10 process per SQLite database, not replicated writers.
Minimal API route groups call feature services directly. Identity uses EF Core;
owned publications, reference metadata, command receipts, notification intents
and the local journal share short database transactions. Network requests never
run inside those transactions. SQLite uses WAL, foreign keys, a five-second
busy timeout and FULL synchronous writes.

The independent Vite/TypeScript client uses RxJS for replaceable reads and
bounded current state. Each configured connection has its own `InstanceSession`,
fetch cancellation lifetime, CSRF token, snapshot and EventSource. A fixed
same-origin proxy strips `/connections/a` or `/connections/b`; it is not an
arbitrary destination proxy. Cookies differ by instance name, path and Data
Protection application/key directory. Different ports alone are not isolation.

## Publication and interaction flow

1. An authenticated owner submits a stable create-intent UUID. Content, receipt,
   journal invalidation and required notification job commit together.
2. A worker claims a bounded job with a lease token. Discovery and HTTP form
   delivery happen after the claim transaction closes.
3. A receiving instance queues only source/target metadata. Verification
   retrieves current source evidence, not a previously cached pre-edit body.
4. An actual backlink and scoped HTML or matching origin RSS evidence determine
   the kind. Trusted exact origins can auto-approve; other sources require the
   target author or an administrator. Repeated valid notifications retain
   approval; changed/conflicting semantics do not.
5. Public comments RSS transiently hydrates only approved reply references.
   Active reads can queue re-verification. Outages produce explicit link-only
   placeholders, never a durable foreign-body fallback or inferred deletion.

Local interactions update the same reference index without self-HTTP.
Likes/bookmarks are publications and metadata associations, not parent edges.
Their targets and kinds are immutable; withdrawing and recreating allocates a
new identity.

## Transient boundary

The shared transport validates DNS addresses and pins sockets to checked IPs,
including every redirect and Webmention POST. Cookies, ambient proxies and
automatic redirects are disabled. A Development-only exact-origin certificate
exception supports the controlled loopback topology; Production rejects it.

The cache retains bounded raw byte arrays only: 32 MiB, 128 entries, 64 in-flight
keys, freshness at most 60 seconds. Parsed bodies live only in the current
request/browser state. No foreign bodies enter SQLite, files, logs, metrics,
browser storage or a service worker. Media becomes user-activated links.

Thread traversal shares depth 16, 200 nodes, 20 remote fetches and a ten-second
deadline. Identities retain feed scope; redirects, names and opaque GUID text
alone do not prove aliases. A relay hint must be checked at its origin before
it supplies a relationship. Completeness means known advertised inputs within
these bounds, never the whole internet.

## Local streaming

SQLite journal rows are compact identifier/revision invalidations, not content
history. Capacity-one observer channels are wake-up hints; five-second durable
rechecks cover lost hints. Snapshot reads include the journal high-water mark
in one read transaction. Protected cursors contain stable instance identity,
fresh startup incarnation and sequence. Restart/older backup, retention gaps
and impossible future cursors require a new snapshot.

Private rows are audience-filtered; checkpoints can advance without exposing
their identifiers. Heartbeats have no cursor. Disposal, account disable and
security-stamp changes close observation; disabled accounts retain independently
visible publications unless those publications are separately moderated.

See [protocol](protocol.md), [operations](operations.md) and
[portability](portability.md) for wire and recovery boundaries.
