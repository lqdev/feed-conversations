import { describe, expect, it } from "vitest";
import { evaluate, type TimelineEntry } from "../../src/web/src/views/evaluator";
import { shouldApply, mergeSources } from "../../src/web/src/state/reducers";
import type { SourceResult, ViewDefinition } from "../../src/web/src/api/client";
const view: ViewDefinition = { version: 1, name: "Test", includeLocal: true, sourceFeedUrls: ["https://p.example/feed"],
  textContains: "hello", textExcludes: "excluded", sort: "published-desc", entryKinds: ["post", "reply"] };
const entry: TimelineEntry = { key: "a", permalink: "https://a.example/post/a", kind: "post", label: "Alice",
  publishedAt: "2026-10-16T00:00:00Z", title: null, html: "Hello", targetUrl: null, sourceFeedUrl: null, local: null, remote: null };
describe("reactive membership and revision semantics", () => {
  it("recomputes text membership after edits", () => {
    expect(evaluate([entry], view, text => text)).toHaveLength(1);
    expect(evaluate([{ ...entry, html: "Goodbye" }], view, text => text)).toHaveLength(0);
  });
  it("composes kind, include, exclude and source predicates", () => {
    expect(evaluate([{ ...entry, html: "Hello excluded" }], view, text => text)).toHaveLength(0);
    expect(evaluate([{ ...entry, kind: "like" }], view, text => text)).toHaveLength(0);
    expect(evaluate([{ ...entry, sourceFeedUrl: "https://other.example/feed" }], view, text => text)).toHaveLength(0);
  });
  it("sorts on publication, not modification", () => {
    expect(evaluate([entry, { ...entry, key: "b", publishedAt: "2026-10-17T00:00:00Z" }], view, text => text).map(e => e.key)).toEqual(["b", "a"]);
  });
  it("ignores duplicated revision but always applies invalidation", () => {
    const revisions = new Map([["post:a", 2]]);
    expect(shouldApply(revisions, { resourceType: "post", resourceId: "a", revision: 2, operation: "upsert" })).toBe(false);
    expect(shouldApply(revisions, { resourceType: "post", resourceId: "a", revision: 2, operation: "invalidate" })).toBe(true);
  });
  it("retains transient prior result on throttling with honest stale status", () => {
    const previous: SourceResult = { feedUrl: "https://p.example/feed", status: "fresh", observedAt: "", freshUntil: null, items: [], error: null };
    expect(mergeSources([previous], [{ ...previous, status: "rate-limited", error: "interval" }])[0].status).toBe("stale");
  });
});
