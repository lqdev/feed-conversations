# MVP acceptance evidence

All automated network writes target controlled A/B or in-process stub origins.
Fixtures use synthetic accounts and generated private credentials. No public
deployment, third-party notification or independent GUI-reader test is implied.

## Reproduction

Use the pinned .NET SDK and Node versions in README, PowerShell 7 and a clean
source directory. Do not copy another installation's databases, keys or secrets.

```powershell
dotnet tool restore
npm ci
npm exec playwright install chromium
.\scripts\Test-All.ps1 -Standalone
```

Docker is needed only for `-Standalone`; omit that switch for the process demo.
The runner performs locked .NET restore, Release build, unit/integration checks,
TypeScript checking, Vitest, static build, owned Aspire readiness, real Chromium
acceptance and the independent public RSS/storage checks. Standalone runs
non-root Production APIs behind a controlled HTTPS proxy and an independently
built static client, then performs SQLite backup and an older-backup restart.
All owned process trees, test containers, volumes and networks are stopped.

Executed suite sizes: 72 unit, 73 integration, 13 frontend, 4 A/B browser
scenarios and 2 independent .NET acceptance checks. The standalone browser
scenario executes separately before and after restore. No tests are skipped.
Builds have zero warnings/errors. These counts are not 36 independent browser
tests: the table below identifies the layered evidence for each requirement.

## A01-A36 evidence map

Paths below are relative to `tests`; unit and integration names identify actual
executed test classes. Browser scenarios are in `browser/conversation.spec.ts`.

| Gate | Executable evidence |
|---|---|
| A01 | `PublicationTests.RealCsrfLoginPublicationRssRevisionAndDeletion`; four-user browser publication; independent `PublicWalkerTests` origin HTML/RSS checks. |
| A02 | `PublicationTests.LocalReplyAndBodylessLikeHaveIndependentIdentityAndCounts`; independent singleton/comments assertions. |
| A03 | Four-user browser selects Alice's saved source from B; `WorkspaceTests.ForeignReadingIsTransientAndExplicitFailuresAreNotEmptySuccess`; real B storage/log sentinel scan. |
| A04 | Four-user browser waits for Carol's actual Webmention on A; `WebmentionTests.VerifiedUntrustedReplyRequiresModerationAndDuplicateWithdrawalIsIdempotent`. |
| A05 | Four-user browser creates David's child; public RSS-only browser and .NET walkers follow advertised comments pointers. |
| A06 | Both independent walkers reach Alice/Carol/David/Bob with origin author/GUID/source metadata. |
| A07 | Four-user browser and .NET walker check the separate B-root/A-reply direction. |
| A08 | Carol edit browser checks stable GUID/pubDate, changed updated/body and A revalidation; `web/state.test.ts` recomputes text membership. |
| A09 | Carol withdrawal browser checks exclusion and David's visible tombstone-parent thread; `ThreadTests.ForeignDeletionProofIsDistinctFromTransientOutage`; descendant mutation regression. |
| A10 | `FeedConversations.AcceptanceTests/PublicWalkerTests.cs` uses public XML and its own parser, without credentials, JSON API or producer helpers. |
| A11 | `SemanticEvidenceTests` checks source aliases, opaque feed scopes, threading and Markdown; `ProtocolTests` independently asserts namespaces/cardinality. |
| A12 | Plain RSS/native Atom fixtures; `WebmentionTests.OrdinaryTargetWithoutEndpointIsUnsupportedRatherThanFalseAcceptance`. |
| A13 | `web/state.test.ts` covers source/include/exclude/kind predicates and edit membership; `WorkspaceTests` persists private declarative rules without fetching targets. |
| A14 | `WorkspaceTests.SubscriptionOpmlAndViewRemainPrivateAndDoNotFetchOnImport` checks directory opt-in, other-member exclusion, repeated selected import, labels/groups and zero crawl. |
| A15 | `StreamingTests.SnapshotSubscribeRaceAndFanoutReadTheDurableJournalOncePerObserver`, missed wakeup recovery; frontend duplicate revision and invalidation checks. |
| A16 | Cursor incarnation/future/retention tests; standalone browser retains the actual old cursor through older-backup recovery and observes reset. |
| A17 | Explicit failed-source and unavailable-node tests; B local publication browser; leased/pending outbound work tests. No outage is classified as deletion. |
| A18 | `web/session.test.ts` closes both viewers and aborts refresh; subscriptions without an observed view perform no acquisition. Cache/inflight limits are measured separately. |
| A19 | Publication ownership/CSRF tests, private workspace assertions, disabled/stamp stream revocation, late-response disposal tests; A/B cookie/CSRF isolation through real process and Production proxies. |
| A20 | `WebmentionTests` covers duplicate/generic/nested/changed evidence, approval, withdrawal and pending admission; quota tests exercise atomic rejection. |
| A21 | `TransportTests` pins checked DNS for GET/redirect/POST and measures byte/deadline/concurrency limits; protocol tests reject unsafe addresses/XML and sanitize HTML; `ThreadTests` bounds shared depth/nodes/fetches and cycles. |
| A22 | `PortabilityTests` restores all four kinds, tombstones, views and subscriptions; later updates/withdrawals, independent-edit conflicts, malformed archives and reimport; 20,000-post commit verified without truncation or notification. |
| A23 | Independent acceptance scans actual B SQLite/WAL/files/captured logs for an A-body sentinel; browser asserts empty local/session storage; metrics tests reject content/URL labels; quota tests measure boundaries. |
| A24 | `TransportTests` measures 32 MiB cache, 64 in-flight admissions, global/per-origin connection concurrency, decompressed bytes, redirects, fresh verification, no-store, repeated 304 and observer cancellation. |
| A25 | Process topology requires no Docker; `Test-Standalone.ps1` executes non-root backend containers without Node/AppHost and a separate static client/proxy. |
| A26 | Real likes/bookmarks browser checks bodyless B like, verified A metadata and no comments pollution; publication/thread tests check distinct association semantics. |
| A27 | Same browser checks David's authored bookmark note, own identity and absence of target body; archive round-trip includes bookmarks. |
| A28 | `PublicationTests.ParallelAssociationIntentsCreateOneActivePublicationAndJobAndCannotBypassHide` for like/bookmark; receipt and target-identity checks. |
| A29 | Real like withdrawal/recreation browser checks old 410, A count withdrawal and new permalink; verification outage regression retains prior reference. |
| A30 | Protocol fixtures and public walker assert relation context, XML/MF2 agreement, source Markdown and independent descriptions; media remains user-activated links. |
| A31 | `SemanticEvidenceTests`, `ProtocolTests` and `WebmentionTests` cover incomplete/conflicting XML, threading, nested quotes, changed-kind revalidation and generic backlinks. |
| A32 | Four-kind views, archive round-trip, malformed schema/identity checks and active-association conflict handling; frontend kind predicates. |
| A33 | Ordinary/unreachable target publication and retry/unsupported tests; transport policy applies equally to endpoint discovery and notification POST. |
| A34 | Independent five-kind publisher fixture distinguishes BlogPosting/SocialMediaPosting/Comment/LikeAction/BookmarkAction; authored kinds exclude BlogPosting. No live opt-in smoke was performed. |
| A35 | `CommandTests` covers mismatch/expiry/deleted-resource reconciliation and one-use invites; publication revision/queue-full tests and frontend uncertain-intent tests prevent blind duplicate creates. |
| A36 | `WebmentionTests.LeasedOldDeliveryCannotClearNewerEditIntent` and `ExpiredLeaseIsRecoveredByNewWorkerAndOldCompletionCannotReplaceIt` verify durable claims and stale-completion guards. |

## Limits and interpretation

Thread results are bounded partial knowledge, never a global completeness claim.
Foreign bodies remain transient; unavailable origins have no permanent snapshot
fallback. Restored publications preserve GUID/provenance but explicitly lack
verified original-origin authority. The application-defined RSS binding is not
universal reader support. See [compatibility](compatibility.md),
[operations](operations.md) and [portability](portability.md).

Local acceptance exercises exact Development-only origin exceptions and a
controlled Production proxy. It does not attest to an operator's future DNS,
TLS, firewall or public-host configuration. CI runs the process gate; standalone
Docker recovery is a separately executable local gate, not a deployment job.
