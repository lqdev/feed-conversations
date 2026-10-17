import type { Post, Snapshot, SourceResult } from "../api/client";

export interface Change { resourceType: string; resourceId: string; revision: number; operation: "upsert" | "delete" | "invalidate" }
export function shouldApply(revisions: ReadonlyMap<string, number>, change: Change): boolean {
  return change.operation === "invalidate" || (revisions.get(`${change.resourceType}:${change.resourceId}`) ?? 0) < change.revision;
}
export function mergeSources(previous: readonly SourceResult[], incoming: readonly SourceResult[]): SourceResult[] {
  return incoming.map(source => {
    const old = previous.find(p => p.feedUrl === source.feedUrl);
    return source.status === "rate-limited" && old ? { ...old, status: "stale", error: source.error } : source;
  });
}
export function currentPosts(posts: readonly Post[]): Post[] {
  return [...new Map(posts.map(post => [post.id, post])).values()].sort((a, b) =>
    b.publishedAt.localeCompare(a.publishedAt) || b.id.localeCompare(a.id)).slice(0, 50);
}
export function normalizeSnapshot(snapshot: Snapshot): Snapshot { return { ...snapshot, posts: currentPosts(snapshot.posts) }; }
