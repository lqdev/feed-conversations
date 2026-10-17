export interface Connection { id: string; label: string; prefix: string; publicOrigin: string }
export interface Account { id: string; handle: string; displayName: string; bio: string; directoryVisible: boolean; isAdmin: boolean; revision: number }
export type Kind = "post" | "reply" | "like" | "bookmark";
export type TargetIdentity = { kind: "permalink"; value: string } | { kind: "feed-guid"; feedUrl: string; guid: string };
export interface Relationship { kind: Exclude<Kind, "post">; target: { permalink: string; identity: TargetIdentity } }
export interface Post {
  id: string; guid: string; permalink: string; author: { id: string; handle: string; displayName: string };
  publishedAt: string; updatedAt: string; revision: number; kind: Kind; title: string | null;
  markdown: string | null; html: string | null; relationship: Relationship | null;
}
export interface Subscription { id: string; url: string; label: string; group: string | null; revision: number }
export interface ViewDefinition {
  version: 1; name: string; includeLocal: boolean; sourceFeedUrls: string[]; textContains: string | null;
  textExcludes: string | null; sort: "published-desc"; entryKinds: Kind[];
}
export interface SavedView { id: string; name: string; revision: number; etag: string; definition: ViewDefinition }
export interface Snapshot { posts: Post[]; subscriptions: Subscription[]; views: SavedView[]; cursor: string }
export interface RemoteEntry {
  identity: { kind: string; value: string; feedUrl: string | null; guid: string | null };
  permalink: string; kind: Kind | "unknown"; semanticType: string; semanticStatus: string; authorUrl: string | null;
  publishedAt: string | null; updatedAt: string | null; title: string | null; html: string;
  targetUrl: string | null; commentsUrl: string | null;
}
export interface SourceResult {
  feedUrl: string; status: "fresh" | "stale" | "failed" | "rate-limited"; observedAt: string;
  freshUntil: string | null; items: RemoteEntry[]; error: string | null;
}
export interface ReadingResult { sources: SourceResult[]; coverage: { selectedSources: number; maxSources: number; maxItems: number; perSourceWindow: number } }
export interface Page<T> { items: T[]; nextCursor: string | null }
export class ApiError extends Error {
  constructor(public readonly status: number, public readonly code: string, title: string) { super(title); }
}
export class ApiClient {
  private csrf = "";
  constructor(readonly connection: Connection, private readonly lifetime: AbortSignal) {}
  async token(): Promise<void> {
    const result = await this.get<{ token: string }>("/auth/csrf");
    this.csrf = result.value.token;
  }
  get<T>(path: string) { return this.request<T>(path); }
  command<T>(path: string, body?: unknown, method = "POST", etag?: string, key?: string) {
    return this.request<T>(path, method, body, etag, key);
  }
  async request<T>(path: string, method = "GET", body?: unknown, etag?: string, key?: string): Promise<{ value: T; etag: string | null }> {
    const headers = new Headers();
    if (method !== "GET") headers.set("X-CSRF-TOKEN", this.csrf);
    if (etag) headers.set("If-Match", etag);
    if (key) headers.set("Idempotency-Key", key);
    const raw = body instanceof Blob;
    if (body !== undefined) headers.set("Content-Type", raw ? "application/zip" : "application/json");
    const response = await fetch(`${this.connection.prefix}/api/v1${path}`, {
      method, credentials: "same-origin", cache: "no-store", headers, signal: this.lifetime,
      body: body === undefined ? undefined : raw ? body : JSON.stringify(body),
    });
    if (!response.ok) {
      const problem: { code?: string; title?: string } = await response.json().catch(() => ({}));
      throw new ApiError(response.status, problem.code ?? `http-${response.status}`, problem.title ?? `Request failed (${response.status}).`);
    }
    return { value: response.status === 204 || response.headers.get("content-length") === "0" ? undefined as T : await response.json() as T,
      etag: response.headers.get("ETag") };
  }
  async downloadArchive(): Promise<Blob> {
    const response = await fetch(`${this.connection.prefix}/api/v1/export`, { credentials: "same-origin", cache: "no-store", signal: this.lifetime });
    if (!response.ok) throw new ApiError(response.status, "archive-export-failed", "Archive export failed; confirm your password first.");
    return response.blob();
  }
  async importArchive(blob: Blob, checksum?: string): Promise<{ checksum: string; new: number; unchanged: number; conflicts: number; alreadyImported: boolean }> {
    const headers = new Headers({ "Content-Type": "application/zip", "X-CSRF-TOKEN": this.csrf });
    if (checksum) headers.set("X-Archive-SHA256", checksum);
    const response = await fetch(`${this.connection.prefix}/api/v1/import/${checksum ? "commit" : "preview"}`, {
      method: "POST", headers, credentials: "same-origin", cache: "no-store", body: blob, signal: this.lifetime,
    });
    if (!response.ok) {
      const problem = await response.json() as { title?: string; code?: string };
      throw new ApiError(response.status, problem.code ?? "archive-failed", problem.title ?? "Archive operation failed.");
    }
    return response.json() as Promise<{ checksum: string; new: number; unchanged: number; conflicts: number; alreadyImported: boolean }>;
  }
}
