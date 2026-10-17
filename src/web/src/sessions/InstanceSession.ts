import { BehaviorSubject, EMPTY, Subject, Subscription as RxSubscription, catchError, combineLatest, debounceTime, distinctUntilChanged,
  from, fromEvent, map, merge, of, switchMap, timer } from "rxjs";
import { ApiClient, type Account, type Connection, type ReadingResult, type Snapshot, type SourceResult, type ViewDefinition } from "../api/client";
import { mergeSources, normalizeSnapshot, shouldApply, type Change } from "../state/reducers";

const empty: Snapshot = { posts: [], subscriptions: [], views: [], cursor: "" };
export const localView: ViewDefinition = { version: 1, name: "Local", includeLocal: true, sourceFeedUrls: [],
  textContains: null, textExcludes: null, sort: "published-desc", entryKinds: ["post", "reply", "like", "bookmark"] };
export class InstanceSession {
  readonly snapshot = new BehaviorSubject<Snapshot>(empty);
  readonly sources = new BehaviorSubject<SourceResult[]>([]);
  readonly selectedView = new BehaviorSubject<ViewDefinition>(localView);
  readonly errors = new Subject<string>();
  readonly connectionState = new BehaviorSubject("connecting");
  private readonly lifetime = new AbortController();
  readonly api: ApiClient;
  private readonly subscriptions = new RxSubscription();
  private readonly invalidations = new Subject<void>();
  private stream: EventSource | null = null;
  private revisions = new Map<string, number>();
  private stopped = false;
  private refreshGeneration = 0;
  constructor(readonly connection: Connection, readonly account: Account) {
    this.api = new ApiClient(connection, this.lifetime.signal);
  }
  async start(): Promise<void> {
    await this.api.token();
    await this.refresh();
    this.openStream();
    this.subscriptions.add(this.invalidations.pipe(debounceTime(100), switchMap(() =>
      from(this.refresh()).pipe(catchError(error => { this.errors.next(String(error)); return EMPTY; })))).subscribe());
    const visible = merge(of(!document.hidden), fromEvent(document, "visibilitychange").pipe(map(() => !document.hidden)));
    this.subscriptions.add(combineLatest([this.selectedView, visible]).pipe(
      map(([view, shown]) => ({ urls: view.sourceFeedUrls, shown })),
      distinctUntilChanged((a, b) => a.shown === b.shown && a.urls.join("\n") === b.urls.join("\n")),
      switchMap(({ urls, shown }) => {
        this.sources.next([]);
        if (!shown || urls.length === 0) return EMPTY;
        return timer(0, 30_000).pipe(switchMap(() => from(this.api.command<ReadingResult>("/reading/resolve", { feedUrls: urls }))
          .pipe(catchError(error => { this.errors.next(String(error)); return EMPTY; }))));
      }),
    ).subscribe(result => this.sources.next(mergeSources(this.sources.value, result.value.sources))));
  }
  async refresh(): Promise<void> {
    const generation = ++this.refreshGeneration;
    const result = await this.api.get<Snapshot>("/state");
    if (this.stopped || generation !== this.refreshGeneration) return;
    const snapshot = normalizeSnapshot(result.value);
    this.snapshot.next(snapshot);
    for (const post of snapshot.posts) this.revisions.set(`post:${post.id}`, post.revision);
    this.connectionState.next("live");
  }
  invalidate(): void { this.invalidations.next(); }
  private openStream(): void {
    if (this.stopped) return;
    this.stream?.close();
    this.stream = new EventSource(`${this.connection.prefix}/api/v1/changes?after=${encodeURIComponent(this.snapshot.value.cursor)}`);
    this.stream.addEventListener("change", event => {
      const change = JSON.parse((event as MessageEvent<string>).data) as Change;
      if (shouldApply(this.revisions, change)) {
        this.revisions.set(`${change.resourceType}:${change.resourceId}`, change.revision);
        this.invalidate();
      }
    });
    this.stream.addEventListener("reset-required", () => {
      this.stream?.close();
      this.recover();
    });
    this.stream.onerror = () => { this.stream?.close(); this.recover(); };
  }
  private recover(): void {
    this.connectionState.next("reconnecting");
    this.subscriptions.add(timer(1000).pipe(switchMap(() => from(this.api.get<Account>("/me"))),
      switchMap(() => from(this.refresh())), catchError(error => {
        this.errors.next(`Session recovery failed: ${String(error)}`);
        this.dispose();
        return EMPTY;
      })).subscribe(() => this.openStream()));
  }
  dispose(): void {
    if (this.stopped) return;
    this.stopped = true;
    this.lifetime.abort();
    this.stream?.close();
    this.subscriptions.unsubscribe();
    this.snapshot.next(empty);
    this.sources.next([]);
    this.revisions.clear();
    this.connectionState.next("closed");
  }
}
