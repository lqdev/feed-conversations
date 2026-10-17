# Publication and conversation protocol

The API publishes RSS 2.0 and independent HTML. It reads RSS and native Atom;
it does not publish Atom, ActivityPub, or a universal RDF representation.
All external retrieval and Webmention delivery use the same DNS-pinned,
bounded transport without ambient cookies, proxies, or credentials.

## Application-defined Atom/Schema.org binding

Direct item children in `http://www.w3.org/2005/Atom` carry exact Schema.org
IRIs. Prefix spelling is irrelevant; namespace URIs are not interchangeable.

| Kind | Category under `https://schema.org/` | Profile rel | Target rel |
|---|---|---|---|
| Authored note / remote social note | SocialMediaPosting | author | none |
| Remote article, read only | BlogPosting | author | none |
| Reply | Comment | author | parentItem |
| Basic like | LikeAction | agent | object |
| Public bookmark | BookmarkAction | agent | object |

Repeated equivalent declarations are deduplicated. Conflicting types, targets,
or explicit XML/HTML/threading evidence are ambiguous. Missing required profile
or target properties are incomplete. Neither result establishes a verified
relationship. An object link alone, arbitrary topic/category, or title does not
invent a relationship. Without a supported profile, genuine threading or scoped
h-entry evidence can classify an interaction; otherwise content stays readable
and unclassified.

Reply-only source/RFC threading points to the actual parent. Source extension
input accepts exactly the HTTP/HTTPS forms of `source.scripting.com`, with or
without a trailing slash; output uses `https://source.scripting.com/`.
Opaque parent GUIDs remain scoped by feed URL and are not converted to global
permalinks. An unrepresentable opaque RFC threading `ref` is omitted, while
`href` still supplies the actual parent permalink.

Each owned publication has its own immutable GUID, permalink, author, original
publication timestamp, revision, singleton, and direct-reply feed. Edits do not
change identity or publication order. Likes and bookmarks are public
publications, not private saves, and never become replies to their targets.
They can receive replies of their own.

## Identity, display, and origin authority

GUID text and the original `isPermaLink` attribute are preserved exactly.
`isPermaLink="false"` makes even URL-shaped text opaque. Equal opaque GUIDs from
different feed scopes are not the same identity. Redirects alone do not prove
aliases. Profile URLs are attribution claims, not global human identity.

Public descriptions and HTML contain a single outer h-entry with generated
identity, author, timestamps, and relation properties. Authored Markdown cannot
inject protocol-control classes. Semantic extraction happens before display
sanitization, excludes nested quoted entries, and rejects a conflicting explicit
outer permalink. Display HTML cannot execute scripts or auto-load media;
safe external media become user-activated links.

Restored copies keep the original GUID, allocate a new local permalink, and
mark the GUID `isPermaLink="false"` when it differs. To distinguish an explicitly
restored copy from an unrelated legitimate opaque GUID, owned RSS adds
`authority="unverified-restore"` in `urn:feed-conversations:provenance:1`;
the outer HTML entry adds `data-origin-authority="unverified-restore"`.
These supplementary provenance markers do not replace the Atom/Schema.org
binding. This application's Webmention verifier does not promote such a copy
to authoritative reply/like/bookmark evidence. Readers unaware of the markers
can display the publication but must not infer original-domain control.

## Notification, relay, and completeness

Webmention is an unauthenticated notification, never proof by itself.
Verification fetches the source, proves its actual backlink, scopes matching
h-entry evidence, and checks matching singleton evidence when advertised.
Untrusted origins require target-author/admin approval. Repeated notifications
update one reference. Verified absence of a backlink or HTTP 410 withdraws it;
timeouts, rate limits, and server errors do not mean deletion.

Comments feeds contain one level of known approved direct replies. Remote
bodies are hydrated transiently from approved references, not stored locally.
Relayed items preserve origin GUID, permalink, author, and comments pointer,
plus core RSS source attribution when the origin author feed is known.
Unavailable sources are link-only placeholders with explicit partial status.
Author/aggregate feeds contain only locally owned publications.

Thread traversal verifies relay hints at origin, joins permitted local
references, and shares depth/node/fetch/deadline budgets. `complete` means the
currently known advertised inputs resolved within those budgets, not global
internet completeness. Cycles, ambiguous identities, outages, and exhausted
budgets remain explicit partial results.

Synthetic fixtures and independent XML/browser walkers provide specific
compatibility evidence. They are not claims of all-reader or live federation
compatibility.
