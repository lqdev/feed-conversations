import { expect, test, type Browser, type BrowserContext, type Page } from "@playwright/test";
import { readFileSync, writeFileSync } from "node:fs";
import { get } from "node:https";
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
    }).on("error", reject).setTimeout(15000, function () { this.destroy(new Error("Controlled-origin GET timed out.")); });
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
    for (const context of sessions) {
      await Promise.race([context.close(), new Promise(resolve => setTimeout(resolve, 3000))]);
    }
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
