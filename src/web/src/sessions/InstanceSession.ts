import { BehaviorSubject, EMPTY, Observable, Subject, Subscription as RxSubscription, catchError, combineLatest, debounceTime, distinctUntilChanged,
  from, fromEvent, map, merge, of, switchMap, timer } from "rxjs";
import { ApiClient, ApiError, type Account, type Connection, type ReadingResult, type Snapshot, type SourceResult, type ViewDefinition } from "../api/client";
import { mergeSources, normalizeSnapshot, shouldApply, type Change } from "../state/reducers";

const empty: Snapshot = { posts: [], subscriptions: [], views: [], cursor: "" };
export const localView: ViewDefinition = { version: 1, name: "Local", includeLocal: true, sourceFeedUrls: [],
  textContains: null, textExcludes: null, sort: "published-desc", entryKinds: ["post", "reply", "like", "bookmark"] };
export class InstanceSession {
  readonly snapshot = new BehaviorSubject<Snapshot>(empty);
  readonly sources = new BehaviorSubject<SourceResult[]>([]);
  readonly selectedView = new BehaviorSubject<ViewDefinition>(localView);
  readonly viewObserved = new BehaviorSubject(true);
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
  private recoveryAttempt = 0;
  private recovering = false;
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
    this.subscriptions.add(combineLatest([this.selectedView, visible, this.viewObserved]).pipe(
      map(([view, shown, observed]) => ({ urls: view.sourceFeedUrls, shown: shown && observed })),
      distinctUntilChanged((a, b) => a.shown === b.shown && a.urls.join("\n") === b.urls.join("\n")),
      switchMap(({ urls, shown }) => {
        this.sources.next([]);
        if (!shown || urls.length === 0) return EMPTY;
        return timer(0, 30_000).pipe(switchMap(() => this.readSources(urls)
          .pipe(catchError(error => { this.errors.next(String(error)); return EMPTY; }))));
      }),
    ).subscribe(result => this.sources.next(mergeSources(this.sources.value, result.value.sources))));
  }
  private readSources(urls: string[]): Observable<{ value: ReadingResult }> {
    return new Observable(observer => {
      const abort = new AbortController();
      void this.api.request<ReadingResult>("/reading/resolve", "POST", { feedUrls: urls }, undefined, undefined, abort.signal)
        .then(result => { observer.next(result); observer.complete(); }, error => observer.error(error));
      return () => abort.abort();
    });
  }
  async refresh(): Promise<void> {
    const generation = ++this.refreshGeneration;
    const result = await this.api.get<Snapshot>("/state");
    if (this.stopped || generation !== this.refreshGeneration) return;
    const snapshot = normalizeSnapshot(result.value);
    this.snapshot.next(snapshot);
    this.revisions.clear();
    for (const post of snapshot.posts) this.revisions.set(`post:${post.id}`, post.revision);
  }
  invalidate(): void { this.invalidations.next(); }
  private openStream(): void {
    if (this.stopped) return;
    this.stream?.close();
    this.stream = new EventSource(`${this.connection.prefix}/api/v1/changes?after=${encodeURIComponent(this.snapshot.value.cursor)}`);
    this.stream.onopen = () => this.connectionState.next("live");
    this.stream.addEventListener("change", event => {
      let change: Change;
      try { change = JSON.parse((event as MessageEvent<string>).data) as Change; }
      catch { this.stream?.close(); this.recover(); return; }
      this.recoveryAttempt = 0;
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
    if (this.stopped || this.recovering) return;
    this.recovering = true;
    this.connectionState.next("reconnecting");
    const delay = Math.min(30_000, 1000 * 2 ** Math.min(this.recoveryAttempt++, 5));
    this.subscriptions.add(timer(delay).pipe(switchMap(() => from(this.api.get<Account>("/me"))),
      switchMap(() => from(this.refresh())), catchError(error => {
        this.errors.next(`Session recovery failed: ${String(error)}`);
        this.recovering = false;
        if (error instanceof ApiError && (error.status === 401 || error.status === 403)) {
          this.dispose();
          this.connectionState.next("unauthenticated");
        } else this.recover();
        return EMPTY;
      })).subscribe(() => { this.recovering = false; this.openStream(); }));
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
