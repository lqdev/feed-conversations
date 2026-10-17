import type { Kind, Post, RemoteEntry, SourceResult, ViewDefinition } from "../api/client";
export interface TimelineEntry {
  key: string; permalink: string; kind: Kind | "unknown"; label: string; publishedAt: string;
  title: string | null; html: string; targetUrl: string | null; sourceFeedUrl: string | null;
  local: Post | null; remote: RemoteEntry | null;
}
export function localEntry(post: Post): TimelineEntry {
  return { key: `local:${post.id}`, permalink: post.permalink, kind: post.kind,
    label: post.author.displayName, publishedAt: post.publishedAt, title: post.title, html: post.html ?? "",
    targetUrl: post.relationship?.target.permalink ?? null, sourceFeedUrl: null, local: post, remote: null };
}
export function remoteEntries(sources: readonly SourceResult[]): TimelineEntry[] {
  return sources.flatMap(source => source.items.map(entry => ({
    key: `feed:${entry.identity.feedUrl}:${entry.identity.guid ?? entry.identity.value}`,
    permalink: entry.permalink, kind: entry.kind, label: `${entry.authorUrl ?? "Unverified author"} (${entry.semanticType.split("/").pop()})`,
    publishedAt: entry.publishedAt ?? "", title: entry.title, html: entry.html, targetUrl: entry.targetUrl,
    sourceFeedUrl: source.feedUrl, local: null, remote: entry,
  })));
}
export function evaluate(entries: readonly TimelineEntry[], view: ViewDefinition, text: (html: string) => string): TimelineEntry[] {
  return entries.filter(entry => {
    if (entry.sourceFeedUrl === null ? !view.includeLocal : !view.sourceFeedUrls.includes(entry.sourceFeedUrl)) return false;
    if (entry.kind === "unknown" ? view.entryKinds.length !== 4 : !view.entryKinds.includes(entry.kind)) return false;
    const content = [entry.title ?? "", text(entry.html), entry.targetUrl ?? ""].join(" ").toLocaleLowerCase("en-US");
    return (!view.textContains || content.includes(view.textContains.toLocaleLowerCase("en-US")))
      && (!view.textExcludes || !content.includes(view.textExcludes.toLocaleLowerCase("en-US")));
  }).sort((a, b) => b.publishedAt.localeCompare(a.publishedAt) || a.key.localeCompare(b.key)).slice(0, 500);
}
