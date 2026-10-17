# Account portability

Archive operations require authentication, CSRF and a password confirmation
within five minutes. ZIP v1 contains exactly `manifest.json`, `profile.json`,
`posts.json`, `subscriptions.opml` and `views.json`. It includes current owned
notes, replies, likes, bookmarks, tombstones and prior restore provenance.

The manifest identifies the source instance/account/origin, export, record count,
local consistency sequence and SHA-256 of every payload file. That sequence is
not a resumable stream cursor. No credentials, cookies, keys, jobs, moderation
notes, remote bodies or target previews are exported.

Export first checks a SQLite UTF-8 byte lower bound and record count in a
consistent snapshot. Bounded serialization runs after the read transaction
closes. The implementation uses bounded memory rather than a temporary file,
avoiding abandoned archive files; both ZIP size and total decompressed bytes
are limited to 25 MiB. The record cap is 20,000 inclusive. Oversized state fails,
never silently truncates.

Preview validates the exact allowlisted entries, duplicate/path protections,
lengths, total decompressed bytes, checksums, strict schemas, UTC dates, source
routes, GUID uniqueness, content/target rules, settings and associations.
Commit requires the same uploaded bytes and preview checksum plus a create
intent UUID. It revalidates current destination revisions inside its atomic
write; a browser-edited preview is not authority.

Restored publications preserve original GUID/provenance and receive new local
row IDs/permalinks. When the GUID differs, RSS emits `isPermaLink="false"`.
HTML/RSS mark restored source authority unverified: possession of an archive
does not prove control of the old origin. Only proven within-archive permalink
relationships are rewritten; opaque feed scopes are not guessed.

Restoring identical bytes is a no-op. Later archives conflict with independent
destination edits or immutable kind/target changes. Cross-account identity and
active like/bookmark uniqueness conflicts fail atomically. Local reference
indices are rebuilt under quotas; old external targets are not notified.
Failure leaves destination content/settings/mappings/import receipts unchanged.

Foreign content remains dependent on its original publisher. Domain routing,
unavailable foreign bodies and original-origin authority cannot be migrated
merely by importing an archive. See [operator recovery](operations.md#backup-and-restore)
for full database backup and incarnation reset.
