import { afterEach, describe, expect, it, vi } from "vitest";
import { ApiClient } from "../../src/web/src/api/client";

function client() {
  return new ApiClient({ id: "a", label: "A", prefix: "/connections/a", publicOrigin: "https://a.example" }, new AbortController().signal);
}
afterEach(() => { vi.unstubAllGlobals(); vi.useRealTimers(); });
describe("create-command uncertainty", () => {
  it("reuses one intent after a lost response, then allocates a key for the next explicit intent", async () => {
    const keys: string[] = [];
    vi.stubGlobal("fetch", vi.fn(async (_url: string, options: RequestInit) => {
      keys.push(new Headers(options.headers).get("Idempotency-Key")!);
      if (keys.length === 1) throw new TypeError("Synthetic lost response.");
      return Response.json({ id: "current-resource" });
    }));
    const api = client();
    await expect(api.command("/subscriptions", { url: "https://controlled.example/feed" })).rejects.toThrow();
    await api.command("/subscriptions", { url: "https://controlled.example/feed" });
    await api.command("/subscriptions", { url: "https://controlled.example/feed" });
    expect(keys[0]).toBe(keys[1]);
    expect(keys[2]).not.toBe(keys[1]);
  });
  it("does not silently replace an uncertain intent with edited input", async () => {
    const fetch = vi.fn(async () => { throw new TypeError("Synthetic lost response."); });
    vi.stubGlobal("fetch", fetch);
    const api = client();
    await expect(api.command("/views", { name: "First" })).rejects.toThrow();
    await expect(api.command("/views", { name: "Changed" })).rejects.toMatchObject({ code: "unreconciled-command" });
    expect(fetch).toHaveBeenCalledTimes(1);
  });
  it("refuses blind resubmission after receipt expiry", async () => {
    vi.useFakeTimers();
    const fetch = vi.fn(async () => { throw new TypeError("Synthetic lost response."); });
    vi.stubGlobal("fetch", fetch);
    const api = client();
    await expect(api.command("/views", { name: "First" })).rejects.toThrow();
    vi.setSystemTime(Date.now() + 24 * 60 * 60 * 1000);
    await expect(api.command("/views", { name: "First" })).rejects.toMatchObject({ code: "idempotency-expired" });
    expect(fetch).toHaveBeenCalledTimes(1);
  });
});
