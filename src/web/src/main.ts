import DOMPurify from "dompurify";
import { combineLatest, Subscription as RxSubscription } from "rxjs";
import { ApiClient, ApiError, commandWasRejected, type Account, type Connection, type CreatePostCommand, type Post, type Relationship, type ViewDefinition } from "./api/client";
import { InstanceSession, localView } from "./sessions/InstanceSession";
import { evaluate, localEntry, remoteEntries, type TimelineEntry } from "./views/evaluator";
import "./styles.css";

const root = document.querySelector<HTMLElement>("#app")!;
let current: InstanceSession | null = null;
let uiSubscriptions = new RxSubscription();
let selected: Connection;
let generation = 0;
let pendingKey = crypto.randomUUID();
let pendingRelationship: Relationship | null = null;
let loginLifetime: AbortController | null = null;

function el<K extends keyof HTMLElementTagNameMap>(tag: K, text?: string): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (text !== undefined) node.textContent = text;
  return node;
}
function link(text: string, url: string): HTMLAnchorElement {
  const node = el("a", text);
  const parsed = new URL(url);
  if (!["http:", "https:"].includes(parsed.protocol)) throw new Error("Unsafe link.");
  node.href = parsed.href;
  node.rel = "noopener noreferrer";
  node.target = "_blank";
  return node;
}
function content(html: string | null): HTMLDivElement {
  const node = el("div"); node.className = "content";
  node.innerHTML = DOMPurify.sanitize(html ?? "", { FORBID_TAGS: ["img", "video", "audio", "iframe", "style", "form"],
    FORBID_ATTR: ["style", "class", "id"], ADD_ATTR: ["target"] });
  return node;
}
function button(text: string, action: () => void | Promise<void>): HTMLButtonElement {
  const node = el("button", text);
  node.type = "button";
  node.addEventListener("click", () => { void run(node, action); });
  return node;
}
const alert = el("p"); alert.setAttribute("role", "alert");
const status = el("p"); status.setAttribute("role", "status");
function report(error: unknown): void { alert.textContent = error instanceof ApiError ? `${error.message} [${error.code}]` : String(error); }
async function run(control: HTMLButtonElement | null, action: () => void | Promise<void>): Promise<void> {
  alert.textContent = "";
  if (control) control.disabled = true;
  const captured = generation;
  try { await action(); }
  catch (error) { if (captured === generation) report(error); }
  finally { if (control) control.disabled = false; }
}
function field(form: HTMLFormElement, name: string, label: string, type = "text", value = ""): HTMLInputElement {
  const wrap = el("label", label);
  const input = el("input"); input.name = name; input.type = type; input.value = value;
  wrap.append(input); form.append(wrap); return input;
}
function textarea(form: HTMLFormElement, name: string, label: string, value = ""): HTMLTextAreaElement {
  const wrap = el("label", label);
  const input = el("textarea"); input.name = name; input.value = value; wrap.append(input); form.append(wrap); return input;
}
function submit(form: HTMLFormElement, text: string, action: () => Promise<void>): void {
  const node = el("button", text); node.type = "submit"; form.append(node);
  form.addEventListener("submit", event => { event.preventDefault(); void run(node, action); });
}
function session(): InstanceSession { if (!current) throw new Error("Log in first."); return current; }
function stop(): void { generation++; loginLifetime?.abort(); loginLifetime = null; current?.dispose(); current = null; uiSubscriptions.unsubscribe(); uiSubscriptions = new RxSubscription(); pendingRelationship = null; pendingKey = crypto.randomUUID(); }

function loginPage(connections: Connection[]): void {
  stop();
  loginLifetime = new AbortController();
  const clients = new Map(connections.map(connection => [connection.id, new ApiClient(connection, loginLifetime!.signal)]));
  root.replaceChildren(el("h1", "Feed conversations"), alert, status);
  status.textContent = "Invite-only accounts. Publications, likes, and bookmarks are public.";
  const selector = el("select"); selector.setAttribute("aria-label", "Connection");
  for (const connection of connections) {
    const option = el("option", connection.label); option.value = connection.id; selector.append(option);
  }
  selected = connections[0]; selector.value = selected.id;
  selector.onchange = () => { selected = connections.find(c => c.id === selector.value)!; generation++; };
  root.append(selector);
  const login = el("form");
  const handle = field(login, "handle", "Handle"); handle.autocomplete = "username"; handle.required = true;
  const password = field(login, "password", "Password", "password"); password.autocomplete = "current-password"; password.required = true;
  submit(login, "Log in", async () => {
    const captured = generation;
    const connection = selected;
    const api = clients.get(connection.id)!;
    await api.token();
    const result = await api.command<Account>("/auth/login", { handle: handle.value, password: password.value });
    password.value = "";
    if (captured !== generation) return;
    await workspace(connection, result.value, connections);
  });
  const invite = el("form"); invite.append(el("h2", "Redeem an invite"));
  const code = field(invite, "code", "One-use invite code");
  const newHandle = field(invite, "handle", "New handle");
  const newPassword = field(invite, "password", "New password (12+ characters)", "password");
  submit(invite, "Create account", async () => {
    const captured = generation; const connection = selected;
    const api = clients.get(connection.id)!;
    await api.token();
    const result = await api.command<Account>("/auth/redeem-invite", { code: code.value, handle: newHandle.value, password: newPassword.value, displayName: newHandle.value });
    newPassword.value = ""; code.value = "";
    if (captured === generation) await workspace(connection, result.value, connections);
  });
  root.append(login, invite);
}

async function workspace(connection: Connection, account: Account, connections: Connection[]): Promise<void> {
  stop();
  const active = new InstanceSession(connection, account);
  current = active;
  const header = el("header");
  header.append(el("h1", "Feed conversations"), el("span", `${account.displayName} @ ${connection.label}`),
    button("Log out", async () => { await active.api.command("/auth/logout"); loginPage(connections); }));
  const nav = el("nav");
  const pages = new Map<string, HTMLElement>();
  function page(name: string): HTMLElement {
    const node = el("section"); node.hidden = pages.size !== 0; node.append(el("h2", name)); pages.set(name, node);
    nav.append(button(name, () => {
      for (const [key, panel] of pages) panel.hidden = key !== name;
      active.viewObserved.next(name === "Timeline");
    }));
    return node;
  }
  const timeline = page("Timeline"), subscriptions = page("Subscriptions"), views = page("Views"), thread = page("Thread"),
    moderation = page("Moderation"), profile = page("Profile"), archive = page("Archive");
  const admin = account.isAdmin ? page("Admin") : null;
  root.replaceChildren(header, alert, status, nav, ...pages.values());
  const composer = el("form"); composer.append(el("h3", "Publish a note or reply"));
  const kindLabel = el("label", "Publication kind"); const kind = el("select");
  kind.id = "publication-kind"; kindLabel.htmlFor = kind.id;
  for (const [value, label] of [["post", "Note"], ["reply", "Reply"], ["like", "Like (public)"], ["bookmark", "Bookmark (public)"]]) {
    const option = el("option", label); option.value = value; kind.append(option);
  }
  composer.append(kindLabel, kind);
  const target = field(composer, "target", "Target permalink (required for relations)", "url");
  const title = field(composer, "title", "Optional authored title");
  const markdown = textarea(composer, "markdown", "Your Markdown");
  let pendingPublication: { command: CreatePostCommand; key: string } | null = null;
  kind.onchange = () => {
    title.disabled = markdown.disabled = kind.value === "like";
    if (kind.value === "post") pendingRelationship = null;
  };
  submit(composer, "Publish publicly", async () => {
    const publicationKind = kind.value;
    if (publicationKind !== "post" && publicationKind !== "reply" && publicationKind !== "like" && publicationKind !== "bookmark") throw new Error("Invalid publication kind.");
    const relation: Relationship | null = publicationKind === "post" ? null : pendingRelationship?.kind === publicationKind && pendingRelationship.target.permalink === target.value
      ? pendingRelationship : { kind: publicationKind, target: { permalink: target.value, identity: { kind: "permalink", value: target.value } } };
    const command: CreatePostCommand = {
      title: kind.value === "like" || !title.value ? null : title.value,
      markdown: kind.value === "like" || !markdown.value ? null : markdown.value, relationship: relation,
    };
    const intent = pendingPublication ?? { command, key: pendingKey };
    pendingPublication = intent;
    for (const control of [kind, target, title, markdown]) control.disabled = true;
    let result;
    try {
      result = await active.api.command<{ post: Post; delivery: { state: string } }>("/posts", intent.command, "POST", undefined, intent.key);
    } catch (error) {
      if (commandWasRejected(error)) {
        pendingPublication = null;
        pendingKey = crypto.randomUUID();
        kind.disabled = target.disabled = false;
        title.disabled = markdown.disabled = kind.value === "like";
        throw error;
      }
      throw new Error("Publication outcome is uncertain. Retry submits the identical intent; do not create a replacement until reconciled.", { cause: error });
    }
    pendingPublication = null;
    pendingKey = crypto.randomUUID(); pendingRelationship = null; markdown.value = title.value = target.value = "";
    kind.value = "post"; kind.disabled = target.disabled = title.disabled = markdown.disabled = false;
    status.textContent = `Published. Delivery: ${result.value.delivery.state}.`;
    active.invalidate();
  });
  const viewSelect = el("select"); viewSelect.setAttribute("aria-label", "Active view");
  const coverage = el("p"); const entries = el("div"); entries.id = "timeline";
  timeline.append(viewSelect, composer, coverage, entries);
  viewSelect.onchange = () => {
    const snapshot = active.snapshot.value;
    const definition = viewSelect.value === "local" ? localView : viewSelect.value === "following"
      ? { ...localView, name: "Following", sourceFeedUrls: snapshot.subscriptions.slice(0, 10).map(s => s.url) }
      : snapshot.views.find(v => v.id === viewSelect.value)?.definition ?? localView;
    active.selectedView.next(definition);
  };
  function chooseReply(entry: TimelineEntry, relationKind: "reply" | "like" | "bookmark"): void {
    if (pendingPublication) throw new Error("Retry the pending publication before starting another intent.");
    kind.value = relationKind; target.value = entry.permalink;
    const identity = entry.remote?.identity;
    pendingRelationship = { kind: relationKind, target: { permalink: entry.permalink,
      identity: identity?.kind === "feed-guid" && identity.feedUrl && identity.guid
        ? { kind: "feed-guid", feedUrl: identity.feedUrl, guid: identity.guid }
        : { kind: "permalink", value: entry.permalink } } };
    pendingKey = crypto.randomUUID(); kind.onchange?.(new Event("change"));
    timeline.hidden = false; for (const [name, node] of pages) if (name !== "Timeline") node.hidden = true;
    active.viewObserved.next(true);
    markdown.focus();
  }
  function renderEntry(entry: TimelineEntry): HTMLElement {
    const article = el("article"); article.dataset.permalink = entry.permalink;
    article.append(el("p", `${entry.label} · ${entry.kind} · ${entry.publishedAt}`));
    if (entry.title) article.append(el("h3", entry.title));
    if (entry.targetUrl) article.append(link(`${entry.kind === "reply" ? "Reply to" : entry.kind === "like" ? "Liked" : "Bookmarked"} target`, entry.targetUrl));
    article.append(content(entry.html), link("Permalink", entry.permalink));
    const actions = el("div"); actions.className = "actions";
    actions.append(button("Reply", () => chooseReply(entry, "reply")), button("Like publicly", () => chooseReply(entry, "like")),
      button("Bookmark publicly", () => chooseReply(entry, "bookmark")), button("Open thread", () => showThread(entry.permalink)));
    if (entry.local?.author.id === account.id) {
      const own = entry.local;
      actions.append(button("Edit", async () => {
        const latest = await active.api.get<Post>(`/posts/${own.id}`);
        if (!latest.etag) throw new Error("Publication revision is unavailable.");
        const editor = el("form");
        const newTitle = field(editor, "title", "Edit title", "text", latest.value.title ?? "");
        const newMarkdown = textarea(editor, "markdown", "Edit Markdown", latest.value.markdown ?? "");
        if (own.kind === "like") { newTitle.disabled = true; newMarkdown.disabled = true; }
        submit(editor, "Save changes", async () => {
          await active.api.command(`/posts/${own.id}`, { title: newTitle.value || null, markdown: newMarkdown.value || null }, "PATCH", latest.etag ?? undefined);
          editor.remove(); active.invalidate();
        });
        article.append(editor);
      }), button("Withdraw", async () => {
        const latest = await active.api.get<Post>(`/posts/${own.id}`);
        await active.api.command(`/posts/${own.id}`, undefined, "DELETE", latest.etag ?? undefined);
        active.invalidate();
      }), button("Delivery status", async () => {
        const result = await active.api.get<unknown>(`/posts/${own.id}/delivery`);
        status.textContent = JSON.stringify(result.value, null, 2);
      }), button("Retry failed delivery", async () => { await active.api.command(`/posts/${own.id}/delivery/retry`); status.textContent = "Retry queued."; }));
    }
    article.append(actions); return article;
  }
  uiSubscriptions.add(combineLatest([active.snapshot, active.sources, active.selectedView]).subscribe(([snapshot, sources, view]) => {
    const plain = (html: string) => new DOMParser().parseFromString(html, "text/html").body.textContent ?? "";
    const result = evaluate([...snapshot.posts.map(localEntry), ...remoteEntries(sources)], view, plain);
    entries.replaceChildren(...result.map(renderEntry));
    coverage.textContent = `Loaded ${result.length} entries; newest 50 per source, at most 10 sources / 500 entries. `
      + sources.map(source => `${source.feedUrl}: ${source.status}${source.error ? ` (${source.error})` : ""}`).join("; ");
  }));
  const sourcesList = el("div"); subscriptions.append(sourcesList);
  const addSource = el("form");
  const sourceUrl = field(addSource, "url", "Feed URL", "url");
  const label = field(addSource, "label", "Feed label");
  submit(addSource, "Subscribe", async () => { await active.api.command("/subscriptions", { url: sourceUrl.value, label: label.value, group: null }); active.invalidate(); });
  const opml = el("form"); const opmlText = textarea(opml, "opml", "OPML text");
  const opmlSelection = el("div");
  let selectedUrls: string[] = [];
  submit(opml, "Preview OPML", async () => {
    const result = await active.api.command<{ entries: { url: string; label: string }[] }>("/opml/preview", { text: opmlText.value });
    selectedUrls = result.value.entries.map(e => e.url);
    opmlSelection.replaceChildren(...result.value.entries.map(entry => {
      const wrap = el("label", entry.label); const checkbox = el("input"); checkbox.type = "checkbox"; checkbox.checked = true;
      checkbox.onchange = () => { selectedUrls = checkbox.checked ? [...selectedUrls, entry.url] : selectedUrls.filter(url => url !== entry.url); };
      wrap.prepend(checkbox); return wrap;
    }));
  });
  opml.append(opmlSelection, button("Import selected subscriptions", async () => {
    const result = await active.api.command<{ imported: number }>("/opml/import", { text: opmlText.value, selectedUrls });
    status.textContent = `Imported ${result.value.imported} subscriptions. No feed retrieval was started.`; active.invalidate();
  }));
  const exportOpml = el("a", "Export private subscription OPML"); exportOpml.href = `${connection.prefix}/api/v1/opml/export`;
  subscriptions.append(addSource, opml, exportOpml);
  const viewsList = el("div"); const viewForm = el("form");
  const definition = textarea(viewForm, "definition", "Declarative view JSON", JSON.stringify(localView, null, 2));
  submit(viewForm, "Create view", async () => { await active.api.command("/views", JSON.parse(definition.value) as ViewDefinition); active.invalidate(); });
  views.append(viewsList, viewForm);
  uiSubscriptions.add(active.snapshot.subscribe(snapshot => {
    const previous = viewSelect.value;
    viewSelect.replaceChildren();
    for (const [id, name] of [["local", "Local"], ["following", "Following"], ...snapshot.views.map(v => [v.id, v.name])]) {
      const option = el("option", name); option.value = id; viewSelect.append(option);
    }
    viewSelect.value = [...viewSelect.options].some(o => o.value === previous) ? previous : "local";
    sourcesList.replaceChildren(...snapshot.subscriptions.map(source => {
      const row = el("p", `${source.label}: ${source.url} `);
      row.append(button("Unsubscribe", async () => { await active.api.command(`/subscriptions/${source.id}`, undefined, "DELETE"); active.invalidate(); }));
      return row;
    }));
    viewsList.replaceChildren(...snapshot.views.map(view => {
      const row = el("section"); row.append(el("h3", view.name));
      const edit = el("form"); const json = textarea(edit, "definition", "Edit view JSON", JSON.stringify(view.definition, null, 2));
      submit(edit, "Update view", async () => { await active.api.command(`/views/${view.id}`, JSON.parse(json.value) as ViewDefinition, "PATCH", view.etag); active.invalidate(); });
      row.append(edit, button("Delete view", async () => { await active.api.command(`/views/${view.id}`, undefined, "DELETE", view.etag); active.invalidate(); }));
      return row;
    }));
    if (previous !== "local") viewSelect.onchange?.(new Event("change"));
  }));
  const threadForm = el("form"); const threadUrl = field(threadForm, "url", "Thread permalink", "url");
  const threadOutput = el("div");
  async function showThread(url: string): Promise<void> {
    for (const [name, node] of pages) node.hidden = name !== "Thread";
    active.viewObserved.next(false);
    threadUrl.value = url;
    const result = await active.api.get<{ complete: boolean; partialReasons: string[]; nodes: {
      permalink: string; kind: string; availability: string; parentIdentity: { value: string; feedUrl?: string; guid?: string } | null;
      publication: { title: string | null; html: string | null } | null;
    }[] }>(`/thread?url=${encodeURIComponent(url)}`);
    threadOutput.replaceChildren(el("p", `Known-input completeness: ${result.value.complete}; ${result.value.partialReasons.join(", ")}`),
      ...result.value.nodes.map(node => {
        const parent = node.parentIdentity;
        const row = el("article", `${node.kind}: ${node.availability}${parent ? `; parent ${parent.guid ? `${parent.feedUrl} · ${parent.guid}` : parent.value}` : ""} `);
        if (node.publication?.title) row.append(el("h3", node.publication.title));
        if (node.publication?.html) row.append(content(node.publication.html));
        row.append(link("Origin publication", node.permalink)); return row;
      }));
  }
  submit(threadForm, "Load thread", () => showThread(threadUrl.value));
  thread.append(threadForm, threadOutput);
  const pending = el("div");
  moderation.append(button("Refresh pending mentions", async () => {
    const result = await active.api.get<{ id: string; sourceUrl: string; kind: string; error: string | null }[]>("/interactions/pending");
    pending.replaceChildren(...result.value.map(reference => {
      const row = el("p", `${reference.kind} ${reference.error ?? ""} `); row.append(link("Source", reference.sourceUrl),
        button("Approve", async () => { await active.api.command(`/interactions/${reference.id}/approve`); row.remove(); }),
        button("Hide association", async () => { await active.api.command(`/interactions/${reference.id}/hide`); row.remove(); }));
      return row;
    }));
  }), pending);
  const profileForm = el("form"); const displayName = field(profileForm, "displayName", "Display name", "text", account.displayName);
  const bio = textarea(profileForm, "bio", "Biography", account.bio);
  const visibilityLabel = el("label", "List my public feed in the member directory");
  const visibility = el("input"); visibility.type = "checkbox"; visibility.checked = account.directoryVisible; visibilityLabel.prepend(visibility); profileForm.append(visibilityLabel);
  submit(profileForm, "Save profile", async () => {
    const latest = await active.api.get<Account>("/me");
    await active.api.command("/me", { displayName: displayName.value, bio: bio.value, directoryVisible: visibility.checked }, "PATCH", latest.etag ?? undefined);
    status.textContent = "Profile updated.";
  });
  const passwordForm = el("form"); const oldPassword = field(passwordForm, "password", "Current password", "password");
  const newPassword = field(passwordForm, "newPassword", "New password", "password");
  submit(passwordForm, "Change password", async () => {
    await active.api.command("/auth/change-password", { password: oldPassword.value, newPassword: newPassword.value });
    oldPassword.value = newPassword.value = ""; await active.api.token(); status.textContent = "Password changed.";
  });
  profile.append(profileForm, passwordForm);
  const confirmForm = el("form"); const confirmation = field(confirmForm, "password", "Confirm password for archives", "password");
  submit(confirmForm, "Confirm password", async () => {
    await active.api.command("/auth/confirm-password", { password: confirmation.value, newPassword: null });
    confirmation.value = ""; await active.api.token(); status.textContent = "Archive access confirmed for five minutes.";
  });
  const archiveFile = el("input"); archiveFile.type = "file"; archiveFile.accept = ".zip"; archiveFile.setAttribute("aria-label", "Account ZIP archive");
  let previewHash = ""; let previewFile: File | null = null;
  archive.append(confirmForm, button("Export my account", async () => download(await active.api.downloadArchive(), "account-v1.zip")), archiveFile,
    button("Preview restore", async () => {
      const file = archiveFile.files?.[0]; if (!file) throw new Error("Choose an archive.");
      const preview = await active.api.importArchive(file); previewHash = preview.checksum; previewFile = file;
      status.textContent = JSON.stringify(preview);
    }), button("Commit previewed restore", async () => {
      if (!previewFile || !previewHash) throw new Error("Preview an archive first.");
      const result = await active.api.importArchive(previewFile, previewHash);
      status.textContent = JSON.stringify(result); previewFile = null; previewHash = ""; active.invalidate();
    }));
  if (admin) {
    const inviteOutput = el("pre");
    admin.append(button("Create one-use invite", async () => {
      const result = await active.api.command<{ code: string | null }>("/admin/invites");
      inviteOutput.textContent = result.value.code ?? "The earlier invite was created, but its one-time code cannot be recovered. A separate intent can issue another.";
    }), inviteOutput);
    const accountForm = el("form"); const accountId = field(accountForm, "id", "Account ID to disable/enable");
    accountForm.append(button("Disable account", async () => { await active.api.command(`/admin/accounts/${encodeURIComponent(accountId.value)}/disable`); status.textContent = "Account disabled."; }),
      button("Enable account", async () => { await active.api.command(`/admin/accounts/${encodeURIComponent(accountId.value)}/enable`); status.textContent = "Account enabled."; }));
    const postId = field(accountForm, "postId", "Publication UUID to hide/unhide");
    accountForm.append(button("Hide publication", async () => { await active.api.command(`/admin/posts/${postId.value}/hide`); active.invalidate(); }),
      button("Unhide publication", async () => { await active.api.command(`/admin/posts/${postId.value}/unhide`); active.invalidate(); }),
      button("Inspect job status", async () => { const result = await active.api.get<unknown>("/admin/jobs"); status.textContent = JSON.stringify(result.value, null, 2); }));
    admin.append(accountForm);
  }
  uiSubscriptions.add(active.errors.subscribe(message => { alert.textContent = message; }));
  uiSubscriptions.add(active.connectionState.subscribe(value => {
    if (value === "unauthenticated") { loginPage(connections); alert.textContent = "Session expired or access was revoked. Log in again."; }
    else status.textContent = `Connection: ${value}.`;
  }));
  await active.start();
}
function download(blob: Blob, name: string): void {
  const url = URL.createObjectURL(blob); const anchor = el("a"); anchor.href = url; anchor.download = name; anchor.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
void (async () => {
  try {
    const response = await fetch("/connections.json", { cache: "no-store" });
    if (!response.ok) throw new Error("Connection configuration unavailable.");
    const connections = await response.json() as Connection[];
    if (connections.length === 0 || connections.some(c => !/^[a-z0-9-]+$/.test(c.id) || c.prefix !== `/connections/${c.id}` || new URL(c.publicOrigin).origin !== c.publicOrigin))
      throw new Error("Invalid fixed connection configuration.");
    loginPage(connections);
  } catch (error) { root.replaceChildren(el("h1", "Feed conversations"), alert); report(error); }
})();
