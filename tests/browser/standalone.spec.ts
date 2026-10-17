import { expect, test, type Browser, type BrowserContext } from "@playwright/test";
import { readFileSync, writeFileSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { get } from "node:https";
import type { ClientRequest } from "node:http";

interface Fixture {
  frontend: string;
  origins: { a: string; b: string };
  credentials: { instance: string; handle: string; password: string }[];
  phase: string;
  cursor?: string;
  publication?: string;
}
const path = process.env.FC_STANDALONE_FIXTURE;
if (!path) throw new Error("Run scripts\\Test-Standalone.ps1 to create isolated production-like test resources.");
const fixture = JSON.parse(readFileSync(path, "utf8")) as Fixture;
const storage = join(dirname(path), "browser-state.json");
const certificate = readFileSync(resolve(".runtime/localhost.crt"));

async function login(browser: Browser, handle: string) {
  const credential = fixture.credentials.find(item => item.handle === handle)!;
  const context = await browser.newContext();
  const page = await context.newPage();
  await page.goto(fixture.frontend);
  await page.getByLabel("Connection", { exact: true }).selectOption(credential.instance);
  await page.getByLabel("Handle", { exact: true }).fill(handle);
  await page.getByLabel("Password", { exact: true }).fill(credential.password);
  await page.getByRole("button", { name: "Log in", exact: true }).click();
  await expect(page.getByRole("button", { name: "Log out", exact: true })).toBeVisible();
  await expect(page.getByRole("status")).toContainText("live");
  return { page, context };
}
function publicGet(url: string): Promise<string> {
  if (!Object.values(fixture.origins).some(origin => new URL(url).origin === origin)) throw new Error("Uncontrolled origin.");
  return new Promise((resolveResponse, reject) => get(url, { ca: certificate, rejectUnauthorized: true }, response => {
    const chunks: Buffer[] = [];
    response.on("data", (data: Buffer) => chunks.push(data));
    response.on("end", () => response.statusCode === 200 ? resolveResponse(Buffer.concat(chunks).toString("utf8"))
      : reject(new Error(`Controlled public GET failed (${response.statusCode}).`)));
  }).on("error", reject).setTimeout(10_000, function (this: ClientRequest) { this.destroy(new Error("Public GET deadline.")); }));
}

test("standalone static UI and production proxy preserve auth, streaming and restored-database resets", async ({ browser }) => {
  const contexts: BrowserContext[] = [];
  try {
    if (fixture.phase === "after-restore") {
      const context = await browser.newContext({ storageState: storage }); contexts.push(context);
      const page = await context.newPage();
      await page.goto(fixture.frontend);
      const result = await page.evaluate(async oldCursor => {
        const me = await fetch("/connections/a/api/v1/me", { cache: "no-store" });
        const state = await fetch("/connections/a/api/v1/state", { cache: "no-store" });
        const snapshot = await state.json() as { posts: unknown[]; cursor: string };
        const abort = new AbortController();
        try {
          const response = await fetch(`/connections/a/api/v1/changes?after=${encodeURIComponent(oldCursor!)}`, {
            signal: AbortSignal.any([abort.signal, AbortSignal.timeout(10_000)]), cache: "no-store",
          });
          const body = await response.text();
          return { me: me.status, count: snapshot.posts.length, cursor: snapshot.cursor, body };
        } finally { abort.abort(); }
      }, fixture.cursor);
      expect(result.me).toBe(200);
      expect(result.count).toBe(0);
      expect(result.cursor).not.toBe(fixture.cursor);
      expect(result.body).toContain("event: reset-required");
      expect(result.body).toContain("incarnation-changed");
      expect(result.body).not.toContain("event: change");
      return;
    }
    const alice = await login(browser, "alice"); contexts.push(alice.context);
    expect(await alice.page.evaluate(async () => (await fetch("/connections/b/api/v1/me")).status)).toBe(401);
    const csrfIsolation = await alice.page.evaluate(async credential => {
      const token = await (await fetch("/connections/a/api/v1/auth/csrf")).json() as { token: string };
      return (await fetch("/connections/b/api/v1/auth/login", { method: "POST", headers: {
        "Content-Type": "application/json", "X-CSRF-TOKEN": token.token,
      }, body: JSON.stringify({ handle: credential.handle, password: credential.password }) })).status;
    }, fixture.credentials.find(item => item.handle === "carol")!);
    expect(csrfIsolation).toBe(403);
    const bob = await login(browser, "bob"); contexts.push(bob.context);
    const carol = await login(browser, "carol"); contexts.push(carol.context);
    const text = `Standalone production note ${crypto.randomUUID()}`;
    await alice.page.getByLabel("Your Markdown", { exact: true }).fill(text);
    await alice.page.getByRole("button", { name: "Publish publicly", exact: true }).click();
    const own = alice.page.locator("#timeline > article").filter({ hasText: text });
    await expect(own).toBeVisible();
    await expect(bob.page.locator("#timeline > article").filter({ hasText: text })).toBeVisible();
    const permalink = (await own.getByRole("link", { name: "Permalink", exact: true }).getAttribute("href"))!;
    expect(new URL(permalink).origin).toBe(fixture.origins.a);
    expect(await publicGet(permalink)).toContain(text);
    expect(await publicGet(`${permalink}/rss.xml`)).toContain(text);
    const privateResponse = await alice.page.evaluate(async () => {
      const response = await fetch("/connections/a/api/v1/state", { cache: "no-store" });
      const state = await response.json() as { cursor: string };
      return { cache: response.headers.get("cache-control"), cursor: state.cursor };
    });
    expect(privateResponse.cache).toBe("no-store");
    for (const { page } of [alice, bob, carol])
      expect(await page.evaluate(() => ({ ...localStorage, ...sessionStorage }))).toEqual({});
    await alice.context.storageState({ path: storage });
    fixture.cursor = privateResponse.cursor;
    fixture.publication = permalink;
    writeFileSync(path, JSON.stringify(fixture));
  } finally {
    for (const context of contexts) {
      for (const page of context.pages()) await page.goto("about:blank");
      await context.close();
    }
  }
});
