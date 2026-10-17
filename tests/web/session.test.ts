import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { InstanceSession } from "../../src/web/src/sessions/InstanceSession";
import type { Account, Connection, Snapshot } from "../../src/web/src/api/client";

class Visibility extends EventTarget { hidden = false; }
class Events extends EventTarget {
  static instances: Events[] = [];
  onerror: (() => void) | null = null;
  onopen: (() => void) | null = null;
  closed = false;
  constructor(readonly url: string) { super(); Events.instances.push(this); }
  close(): void { this.closed = true; }
}
const connection: Connection = { id: "a", label: "A", prefix: "/connections/a", publicOrigin: "https://a.example" };
const account: Account = { id: "alice", handle: "alice", displayName: "Alice", bio: "", directoryVisible: false, isAdmin: false, revision: 1 };
const snapshot: Snapshot = { posts: [], subscriptions: [], views: [], cursor: "opaque" };
let visibility: Visibility;
let reads: AbortSignal[];
let meStatus: number;
let meRequests: number;
let active: InstanceSession;
beforeEach(() => {
  vi.useFakeTimers();
  visibility = new Visibility(); reads = []; meStatus = 200; meRequests = 0; Events.instances = [];
  vi.stubGlobal("document", visibility);
  vi.stubGlobal("EventSource", Events);
  vi.stubGlobal("fetch", vi.fn(async (url: string, options: RequestInit) => {
    if (url.endsWith("/auth/csrf")) return Response.json({ token: "synthetic-csrf" });
    if (url.endsWith("/state")) return Response.json(snapshot);
    if (url.endsWith("/me")) { meRequests++; return Response.json({ code: "session-probe", title: "Synthetic status" }, { status: meStatus }); }
    if (url.endsWith("/reading/resolve")) {
      reads.push(options.signal!);
      return new Promise<Response>((_, reject) => options.signal!.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError"))));
    }
    throw new Error("Unexpected test request.");
  }));
  active = new InstanceSession(connection, account);
});
afterEach(() => { active.dispose(); vi.unstubAllGlobals(); vi.useRealTimers(); });

describe("instance observation lifetime", () => {
  it("closing both viewers stops every refresh while subscriptions alone never ingest", async () => {
    const second = new InstanceSession(connection, account);
    try {
      await active.start(); await second.start();
      expect(reads).toHaveLength(0);
      for (const viewer of [active, second])
        viewer.selectedView.next({ ...viewer.selectedView.value, sourceFeedUrls: ["https://controlled.example/shared"] });
      await vi.advanceTimersByTimeAsync(0);
      expect(reads).toHaveLength(2);
      active.dispose(); second.dispose();
      expect(reads.every(signal => signal.aborted)).toBe(true);
      await vi.advanceTimersByTimeAsync(90_000);
      expect(reads).toHaveLength(2);
    } finally { second.dispose(); }
  });

  it("stops refresh and aborts replaceable reads when the timeline is unobserved", async () => {
    await active.start();
    active.selectedView.next({ ...active.selectedView.value, sourceFeedUrls: ["https://controlled.example/feed"] });
    await vi.advanceTimersByTimeAsync(0);
    expect(reads).toHaveLength(1);
    active.viewObserved.next(false);
    expect(reads[0].aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(90_000);
    expect(reads).toHaveLength(1);
    active.viewObserved.next(true);
    await vi.advanceTimersByTimeAsync(0);
    expect(reads).toHaveLength(2);
    visibility.hidden = true; visibility.dispatchEvent(new Event("visibilitychange"));
    expect(reads[1].aborted).toBe(true);
    await vi.advanceTimersByTimeAsync(90_000);
    expect(reads).toHaveLength(2);
  });

  it("backs off transport recovery rather than logging out on a temporary failure", async () => {
    await active.start();
    meStatus = 503;
    Events.instances[0].onerror!();
    await vi.advanceTimersByTimeAsync(1000);
    expect(meRequests).toBe(1);
    expect(active.connectionState.value).toBe("reconnecting");
    await vi.advanceTimersByTimeAsync(1999);
    expect(meRequests).toBe(1);
    meStatus = 200;
    await vi.advanceTimersByTimeAsync(1);
    expect(meRequests).toBe(2);
    expect(Events.instances).toHaveLength(2);
  });

  it("clears transient state and requests a new login after auth revocation", async () => {
    await active.start();
    meStatus = 401;
    Events.instances[0].onerror!();
    await vi.advanceTimersByTimeAsync(1000);
    expect(active.connectionState.value).toBe("unauthenticated");
    expect(active.snapshot.value.cursor).toBe("");
    expect(active.sources.value).toEqual([]);
    expect(Events.instances[0].closed).toBe(true);
    await vi.advanceTimersByTimeAsync(60_000);
    expect(meRequests).toBe(1);
  });

  it("dispose aborts commands and rejects late snapshot responses from an old connection", async () => {
    await active.start();
    const pending = active.refresh();
    active.dispose();
    await pending;
    expect(active.snapshot.value.cursor).toBe("");
    expect(active.connectionState.value).toBe("closed");
    expect(Events.instances[0].closed).toBe(true);
  });
});
