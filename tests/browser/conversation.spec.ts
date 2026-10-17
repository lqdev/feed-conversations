import { expect, test, type Browser, type BrowserContext, type Page } from "@playwright/test";
import { readFileSync, writeFileSync } from "node:fs";
import { get } from "node:https";
import type { ClientRequest } from "node:http";
import { resolve } from "node:path";

const certificate = readFileSync(resolve(".runtime/localhost.crt"));
interface Credential { handle: string; password: string }
const credentials = ["a", "b"].flatMap(instance =>
  (JSON.parse(readFileSync(resolve(`.runtime/${instance}/demo-credentials.json`), "utf8")) as Credential[])
    .map(credential => ({ ...credential, instance })));
async function login(browser: Browser, handle: string): Promise<{ page: Page; context: BrowserContext }> {
  const credential = credentials.find(c => c.handle === handle)!;
  const context = await browser.newContext();
  const page = await context.newPage();
  page.on("pageerror", error => console.log(`Browser runtime error: ${error.message}`));
  await page.goto("https://localhost:7440");
  await page.getByLabel("Connection", { exact: true }).selectOption(credential.instance);
  await page.getByLabel("Handle", { exact: true }).fill(handle);
  await page.getByLabel("Password", { exact: true }).fill(credential.password);
  await page.getByRole("button", { name: "Log in", exact: true }).click();
  await expect(page.getByRole("button", { name: "Log out", exact: true })).toBeVisible();
  await expect(page.getByRole("status")).toContainText("live");
  return { page, context };
}
async function publish(page: Page, markdown: string): Promise<string> {
  await page.getByRole("button", { name: "Timeline", exact: true }).click();
  await page.getByLabel("Your Markdown", { exact: true }).fill(markdown);
  await page.getByRole("button", { name: "Publish publicly", exact: true }).click();
  const article = page.locator("#timeline > article").filter({ hasText: markdown }).first();
  await expect(article).toBeVisible();
  return (await article.getByRole("link", { name: "Permalink", exact: true }).getAttribute("href"))!;
}
function publicGet(url: string): Promise<string> {
  return new Promise((resolveResponse, reject) => {
    get(url, { ca: certificate, rejectUnauthorized: true }, response => {
      const chunks: Buffer[] = [];
      response.on("data", (chunk: Buffer) => chunks.push(chunk));
      response.on("end", () => response.statusCode === 200 ? resolveResponse(Buffer.concat(chunks).toString("utf8")) : reject(new Error(`HTTP ${response.statusCode}`)));
    }).on("error", reject).setTimeout(15000, function (this: ClientRequest) { this.destroy(new Error("Controlled-origin GET timed out.")); });
  });
}
async function reply(page: Page, target: string, markdown: string): Promise<string> {
  console.log("Selecting reply composer.");
  await page.getByRole("button", { name: "Timeline", exact: true }).click();
  console.log("Timeline composer selected.");
  await page.getByLabel("Publication kind", { exact: true }).selectOption("reply", { timeout: 5000 });
  console.log("Setting reply target.");
  await page.getByLabel("Target permalink (required for relations)", { exact: true }).fill(target);
  console.log("Submitting authored reply.");
  return publish(page, markdown);
}

test("real four-user A/B read, publish, Webmention and public RSS-only traversal", async ({ browser }) => {
  const sessions: BrowserContext[] = [];
  try {
    const alice = await login(browser, "alice"); sessions.push(alice.context);
    const beforeB = await alice.page.evaluate(async () => (await fetch("/connections/b/api/v1/me", { cache: "no-store" })).status);
    expect(beforeB).toBe(401);
    const bob = await login(browser, "bob"); sessions.push(bob.context);
    const carol = await login(browser, "carol"); sessions.push(carol.context);
    const david = await login(browser, "david"); sessions.push(david.context);
    const unique = crypto.randomUUID();
    const rootText = `A root ${unique}`;
    const root = await publish(alice.page, rootText);
    console.log("A publication verified; opening B saved-source reader.");
    await carol.page.getByRole("button", { name: "Subscriptions", exact: true }).click();
    await carol.page.getByLabel("Feed URL", { exact: true }).fill("https://localhost:7441/users/alice/rss.xml");
    await carol.page.getByLabel("Feed label", { exact: true }).fill("Alice on A");
    await carol.page.getByRole("button", { name: "Subscribe", exact: true }).click();
    await carol.page.getByRole("button", { name: "Timeline", exact: true }).click();
    await carol.page.getByLabel("Active view", { exact: true }).selectOption("following");
    await expect(carol.page.locator("#timeline > article").filter({ hasText: rootText })).toBeVisible({ timeout: 45_000 });
    console.log("B transient reader verified; publishing cross-instance reply.");
    const carolReply = await reply(carol.page, root, `Carol response ${unique}`);
    console.log("B reply published; awaiting approved A reference.");
    await expect.poll(() => publicGet(`${root}/comments.xml`), { timeout: 30_000 }).toContain(carolReply);
    const davidReply = await reply(david.page, carolReply, `David response ${unique}`);
    const bobReply = await reply(bob.page, davidReply, `Bob response ${unique}`);
    console.log("Four-user chain published; verifying B reference.");
    await expect.poll(() => publicGet(`${davidReply}/comments.xml`), { timeout: 30_000 }).toContain(bobReply);
    const bRoot = await publish(david.page, `Reverse B root ${unique}`);
    const reverseReply = await reply(alice.page, bRoot, `Reverse Alice response ${unique}`);
    console.log("Reverse-root reply published; verifying public feeds.");
    await expect.poll(() => publicGet(`${bRoot}/comments.xml`), { timeout: 30_000 }).toContain(reverseReply);
    // The independent walker consumes only public XML, never JSON or producer helpers.
    const walker = await alice.page.context().newPage();
    const seen = new Set<string>(); const queue = [root];
    while (queue.length > 0 && seen.size < 20) {
      const url = queue.shift()!;
      if (seen.has(url)) continue;
      seen.add(url);
      const xml = await publicGet(`${url}/comments.xml`);
      const children = await walker.evaluate(text => {
        const document = new DOMParser().parseFromString(text, "application/xml");
        if (document.querySelector("parsererror")) throw new Error("Invalid RSS.");
        return Array.from(document.querySelectorAll("channel > item")).map(item => {
          const category = item.getElementsByTagNameNS("http://www.w3.org/2005/Atom", "category")[0]?.getAttribute("term");
          const author = Array.from(item.getElementsByTagNameNS("http://www.w3.org/2005/Atom", "link")).find(link => link.getAttribute("rel") === "https://schema.org/author")?.getAttribute("href");
          const link = item.querySelector(":scope > link")?.textContent;
          if (category !== "https://schema.org/Comment" || !author || !link) throw new Error("Missing origin reply metadata.");
          return link;
        });
      }, xml);
      queue.push(...children);
    }
    expect([...seen]).toEqual(expect.arrayContaining([root, carolReply, davidReply, bobReply]));
    writeFileSync(resolve(".runtime/walker-target.json"), JSON.stringify({ root, chain: [root, carolReply, davidReply, bobReply], reverseRoot: bRoot, reverseReply }));
    for (const { page } of [alice, bob, carol, david]) {
      expect(await page.evaluate(() => ({ local: { ...localStorage }, session: { ...sessionStorage } }))).toEqual({ local: {}, session: {} });
    }
  } finally {
    for (const context of sessions) await context.close();
  }
});

test("B commands publish without remote reading", async ({ browser }) => {
  const { page, context } = await login(browser, "carol");
  try {
    const target = await publish(page, `Standalone B note ${crypto.randomUUID()}`);
    await reply(page, target, `Standalone B reply ${crypto.randomUUID()}`);
    console.log("B local publication passed.");
  } finally { await context.close(); }
});

test("public likes and bookmarks do not become replies; withdrawal recreates a new identity", async ({ browser }) => {
  const sessions: BrowserContext[] = [];
  try {
    const alice = await login(browser, "alice"); sessions.push(alice.context);
    const carol = await login(browser, "carol"); sessions.push(carol.context);
    const david = await login(browser, "david"); sessions.push(david.context);
    const unique = crypto.randomUUID();
    const rootText = `Synthetic action target ${unique}`;
    const root = await publish(alice.page, rootText);
    const beforeEdit = await publicGet(`${root}/rss.xml`);
    async function relation(page: Page, kind: "like" | "bookmark", note = ""): Promise<string> {
      await page.getByRole("button", { name: "Timeline", exact: true }).click();
      await page.getByLabel("Publication kind", { exact: true }).selectOption(kind);
      await page.getByLabel("Target permalink (required for relations)", { exact: true }).fill(root);
      if (kind === "bookmark") await page.getByLabel("Your Markdown", { exact: true }).fill(note);
      else await expect(page.getByLabel("Your Markdown", { exact: true })).toBeDisabled();
      await page.getByRole("button", { name: "Publish publicly", exact: true }).click();
      const article = page.locator("#timeline > article").filter({
        has: page.getByRole("link", { name: kind === "like" ? "Liked target" : "Bookmarked target", exact: true }),
      }).filter({ has: page.locator(`a[href="${root}"]`) }).first();
      await expect(article).toBeVisible();
      return (await article.getByRole("link", { name: "Permalink", exact: true }).getAttribute("href"))!;
    }
    const like = await relation(carol.page, "like");
    const bookmarkNote = `Own bookmark note ${unique}`;
    const bookmark = await relation(david.page, "bookmark", bookmarkNote);
    const metadata = (kind: string) => publicGet(root.replace("/post/", "/api/v1/posts/") + `/interactions?kind=${kind}`)
      .then(text => JSON.parse(text) as { count: number });
    await expect.poll(() => metadata("like").then(value => value.count), { timeout: 30_000 }).toBe(1);
    await expect.poll(() => metadata("bookmark").then(value => value.count), { timeout: 30_000 }).toBe(1);
    const comments = await publicGet(`${root}/comments.xml`);
    expect(comments).not.toContain(like);
    expect(comments).not.toContain(bookmark);
    const rss = await publicGet(`${bookmark}/rss.xml`);
    expect(rss).toContain("https://schema.org/BookmarkAction");
    expect(rss).toContain(bookmarkNote);
    expect(rss).not.toContain(rootText);
    const likeArticle = carol.page.locator(`#timeline > article[data-permalink="${like}"]`);
    await likeArticle.getByRole("button", { name: "Withdraw", exact: true }).click();
    await expect(likeArticle).toHaveCount(0);
    await expect.poll(() => metadata("like").then(value => value.count), { timeout: 30_000 }).toBe(0);
    const replacement = await relation(carol.page, "like");
    expect(replacement).not.toBe(like);
    await expect.poll(() => metadata("like").then(value => value.count), { timeout: 30_000 }).toBe(1);
    const status = await carol.page.evaluate(async url => (await fetch(url)).status, like.replace("https://localhost:7442", "/connections/b"));
    expect(status).toBe(410);
    const rootArticle = alice.page.locator(`#timeline > article[data-permalink="${root}"]`);
    await rootArticle.getByRole("button", { name: "Edit", exact: true }).click();
    const edited = `Edited owned target ${unique}`;
    await rootArticle.getByLabel("Edit Markdown", { exact: true }).fill(edited);
    await rootArticle.getByRole("button", { name: "Save changes", exact: true }).click();
    await expect(alice.page.locator("#timeline")).toContainText(edited);
    const afterEdit = await publicGet(`${root}/rss.xml`);
    const dates = await alice.page.evaluate(({ before, after }) => [before, after].map(text => {
      const xml = new DOMParser().parseFromString(text, "application/xml");
      return { guid: xml.querySelector("item > guid")?.textContent, published: xml.querySelector("item > pubDate")?.textContent,
        updated: xml.getElementsByTagNameNS("http://www.w3.org/2005/Atom", "updated")[0]?.textContent };
    }), { before: beforeEdit, after: afterEdit });
    expect(dates[0].guid).toBe(dates[1].guid);
    expect(dates[0].published).toBe(dates[1].published);
    expect(dates[0].updated).not.toBe(dates[1].updated);
    expect(afterEdit).toContain(edited);
  } finally {
    for (const context of sessions) await context.close();
  }
});

test("Carol edit revalidates on A and withdrawal leaves David's descendant reachable", async ({ browser }) => {
  const sessions: BrowserContext[] = [];
  try {
    const alice = await login(browser, "alice"); sessions.push(alice.context);
    const carol = await login(browser, "carol"); sessions.push(carol.context);
    const david = await login(browser, "david"); sessions.push(david.context);
    const unique = crypto.randomUUID();
    const root = await publish(alice.page, `Edit/delete root ${unique}`);
    const parent = await reply(carol.page, root, `Carol original ${unique}`);
    const childText = `David independent descendant ${unique}`;
    const child = await reply(david.page, parent, childText);
    await expect.poll(() => publicGet(`${root}/comments.xml`), { timeout: 30_000 }).toContain(parent);
    const before = await publicGet(`${parent}/rss.xml`);
    const article = carol.page.locator(`#timeline > article[data-permalink="${parent}"]`);
    await article.getByRole("button", { name: "Edit", exact: true }).click();
    const edited = `Carol edited ${unique}`;
    await article.getByLabel("Edit Markdown", { exact: true }).fill(edited);
    await article.getByRole("button", { name: "Save changes", exact: true }).click();
    await expect(carol.page.locator("#timeline")).toContainText(edited);
    await expect.poll(() => publicGet(`${root}/comments.xml`), { timeout: 30_000 }).toContain(edited);
    const after = await publicGet(`${parent}/rss.xml`);
    const identities = await carol.page.evaluate(({ before, after }) => [before, after].map(text => {
      const xml = new DOMParser().parseFromString(text, "application/xml");
      return { guid: xml.querySelector("item > guid")?.textContent, published: xml.querySelector("item > pubDate")?.textContent,
        updated: xml.getElementsByTagNameNS("http://www.w3.org/2005/Atom", "updated")[0]?.textContent };
    }), { before, after });
    expect(identities[0].guid).toBe(identities[1].guid);
    expect(identities[0].published).toBe(identities[1].published);
    expect(identities[0].updated).not.toBe(identities[1].updated);
    await carol.page.locator(`#timeline > article[data-permalink="${parent}"]`).getByRole("button", { name: "Withdraw", exact: true }).click();
    await expect.poll(() => publicGet(`${root}/comments.xml`), { timeout: 30_000 }).not.toContain(parent);
    expect(await publicGet(`${child}/rss.xml`)).toContain(childText);
    await david.page.getByRole("button", { name: "Thread", exact: true }).click();
    await david.page.getByLabel("Thread permalink", { exact: true }).fill(child);
    await david.page.getByRole("button", { name: "Load thread", exact: true }).click();
    await expect(david.page.locator("article").filter({ hasText: "reply: tombstone" })).toBeVisible();
    await expect(david.page.locator("article:visible").filter({ hasText: childText })).toBeVisible();
  } finally {
    for (const context of sessions) await context.close();
  }
});
