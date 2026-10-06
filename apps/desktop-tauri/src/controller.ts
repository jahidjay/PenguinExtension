import {
  DEFAULT_SETTINGS,
  type DesktopApi,
  type Settings,
  type Status,
  type SymbolDto,
  type Job,
  type Inheritance,
  type StyleResult,
} from "./types";
export interface ViewState {
  status: Status | null;
  settings: Settings;
  symbols: SymbolDto[];
  selected: SymbolDto | null;
  inheritance: Inheritance | null;
  style: StyleResult | null;
  indexJob: Job | null;
  aiJob: Job | null;
  error: string;
  notice: string;
  query: string;
  source: string;
  instruction: string;
  file: string;
  transitioning: boolean;
  searching: boolean;
  inspecting: boolean;
  checking: boolean;
  saving: boolean;
  aiStarting: boolean;
  indexing: boolean;
}
export function terminal(job: Job): boolean {
  return ["succeeded", "failed", "cancelled"].includes(job.state);
}
export class DesktopController {
  state: ViewState = {
    status: null,
    settings: { ...DEFAULT_SETTINGS },
    symbols: [],
    selected: null,
    inheritance: null,
    style: null,
    indexJob: null,
    aiJob: null,
    error: "",
    notice: "",
    query: "",
    source: "",
    instruction: "",
    file: "",
    transitioning: false,
    searching: false,
    inspecting: false,
    checking: false,
    saving: false,
    aiStarting: false,
    indexing: false,
  };
  private epoch = 0;
  private searchSeq = 0;
  private detailSeq = 0;
  private styleSeq = 0;
  private aiSeq = 0;
  private listeners = new Set<() => void>();
  private timer?: ReturnType<typeof setTimeout>;
  private searchTimer?: ReturnType<typeof setTimeout>;
  constructor(private readonly api: DesktopApi) {}
  subscribe(fn: () => void): () => void {
    this.listeners.add(fn);
    return () => this.listeners.delete(fn);
  }
  private emit() {
    this.listeners.forEach((fn) => fn());
  }
  private fail(error: unknown) {
    this.state.error = error instanceof Error ? error.message : String(error);
    this.emit();
  }
  dismiss() {
    this.state.error = "";
    this.emit();
  }
  async initialize() {
    try {
      this.state.settings = await this.api.settings();
      this.emit();
    } catch (e) {
      this.fail(e);
    }
  }
  private valid(epoch: number, session: string) {
    return (
      this.epoch === epoch &&
      this.state.status?.sessionId === session &&
      !this.state.transitioning
    );
  }
  private clearWork() {
    this.state.symbols = [];
    this.state.selected = null;
    this.state.inheritance = null;
    this.state.style = null;
    this.state.aiJob = null;
    this.state.indexJob = null;
    this.state.source = "";
    this.state.instruction = "";
    this.state.file = "";
    this.state.error = "";
    this.state.notice = "";
    this.state.searching =
      this.state.inspecting =
      this.state.checking =
      this.state.aiStarting =
      this.state.indexing =
        false;
  }
  async open() {
    if (
      this.state.transitioning ||
      this.state.aiStarting ||
      this.state.indexing
    )
      return;
    const epoch = ++this.epoch;
    this.state.transitioning = true;
    clearTimeout(this.timer);
    clearTimeout(this.searchTimer);
    this.state.searching = false;
    this.state.inspecting = false;
    this.state.checking = false;
    this.emit();
    try {
      const status = await this.api.open();
      if (epoch !== this.epoch) return;
      if (status) {
        this.clearWork();
        this.state.status = status;
        this.state.query = "";
        this.state.notice =
          "Workspace opened. Index the saved headers to refresh your symbols.";
      }
    } catch (e) {
      if (epoch === this.epoch) {
        const old = this.state.status?.sessionId;
        this.state.status = null;
        if (old) {
          try {
            await this.api.close(old);
          } catch {
            /* Native open may already have drained it. */
          }
        }
        this.clearWork();
        this.fail(e);
      }
    } finally {
      if (epoch === this.epoch) {
        this.state.transitioning = false;
        this.emit();
        if (this.state.status) {
          void this.search();
          this.schedule();
        }
      }
    }
  }
  async close() {
    if (this.state.transitioning || !this.state.status) return;
    const id = this.state.status.sessionId;
    const epoch = ++this.epoch;
    this.state.transitioning = true;
    clearTimeout(this.timer);
    clearTimeout(this.searchTimer);
    this.state.status = null;
    this.clearWork();
    this.emit();
    try {
      await this.api.close(id);
    } catch (e) {
      if (epoch === this.epoch) this.fail(e);
    } finally {
      if (epoch === this.epoch) {
        this.state.transitioning = false;
        this.emit();
      }
    }
  }
  setQuery(value: string) {
    this.state.query = value;
    this.searchSeq++;
    clearTimeout(this.searchTimer);
    this.searchTimer = setTimeout(() => void this.search(), 200);
  }
  async search() {
    const session = this.state.status?.sessionId;
    if (!session || this.state.transitioning) return;
    const epoch = this.epoch,
      seq = ++this.searchSeq;
    this.state.searching = true;
    this.emit();
    try {
      const rows = await this.api.search(session, this.state.query);
      if (this.valid(epoch, session) && seq === this.searchSeq)
        this.state.symbols = rows;
    } catch (e) {
      if (this.valid(epoch, session) && seq === this.searchSeq) this.fail(e);
    } finally {
      if (this.valid(epoch, session) && seq === this.searchSeq) {
        this.state.searching = false;
        this.emit();
      }
    }
  }
  async select(symbol: SymbolDto) {
    const session = this.state.status?.sessionId;
    if (!session || this.state.transitioning) return;
    const epoch = this.epoch,
      seq = ++this.detailSeq;
    this.state.inspecting = true;
    this.state.selected = null;
    this.state.inheritance = null;
    this.emit();
    try {
      const details = await this.api.symbol(session, symbol.id);
      if (!this.valid(epoch, session) || seq !== this.detailSeq) return;
      if (!details)
        throw new Error(
          "This symbol reference expired. Refresh your search and select it again.",
        );
      this.state.selected = details;
      this.state.file = details.file;
      this.state.source = [
        details.signature || details.name,
        details.documentation || "",
      ]
        .filter(Boolean)
        .join(String.fromCharCode(10, 10));
      this.emit();
      const inheritance = await this.api.inheritance(session, details.id);
      if (this.valid(epoch, session) && seq === this.detailSeq)
        this.state.inheritance = inheritance;
    } catch (e) {
      if (this.valid(epoch, session) && seq === this.detailSeq) this.fail(e);
    } finally {
      if (this.valid(epoch, session) && seq === this.detailSeq) {
        this.state.inspecting = false;
        this.emit();
      }
    }
  }
  async reindex() {
    const session = this.state.status?.sessionId;
    if (
      !session ||
      this.state.transitioning ||
      this.state.indexing ||
      (this.state.indexJob && !terminal(this.state.indexJob))
    )
      return;
    const epoch = this.epoch;
    this.state.indexing = true;
    this.emit();
    try {
      const job = await this.api.reindex(session);
      if (this.valid(epoch, session)) {
        this.state.indexJob = job;
        this.state.notice =
          "Index job started. You can keep browsing while it runs.";
      }
    } catch (e) {
      if (this.valid(epoch, session)) this.fail(e);
    } finally {
      if (this.valid(epoch, session)) {
        this.state.indexing = false;
        this.emit();
      }
    }
  }
  async checkStyle() {
    const session = this.state.status?.sessionId;
    if (!session || this.state.transitioning || this.state.checking) return;
    const epoch = this.epoch,
      seq = ++this.styleSeq;
    this.state.checking = true;
    this.state.style = null;
    this.emit();
    try {
      const result = await this.api.style(session, this.state.file);
      if (this.valid(epoch, session) && seq === this.styleSeq)
        this.state.style = result;
    } catch (e) {
      if (this.valid(epoch, session) && seq === this.styleSeq) this.fail(e);
    } finally {
      if (this.valid(epoch, session) && seq === this.styleSeq) {
        this.state.checking = false;
        this.emit();
      }
    }
  }
  async startAi(task: "explain" | "generate") {
    const session = this.state.status?.sessionId;
    if (
      !session ||
      this.state.transitioning ||
      !this.state.settings.aiEnabled ||
      this.state.aiStarting ||
      (this.state.aiJob && !terminal(this.state.aiJob))
    )
      return;
    const epoch = this.epoch,
      seq = ++this.aiSeq;
    this.state.aiStarting = true;
    this.state.aiJob = null;
    this.emit();
    try {
      const job = await this.api.ai(
        session,
        task,
        this.state.source,
        this.state.instruction,
      );
      if (this.valid(epoch, session) && seq === this.aiSeq)
        this.state.aiJob = job;
    } catch (e) {
      if (this.valid(epoch, session) && seq === this.aiSeq) this.fail(e);
    } finally {
      if (this.valid(epoch, session) && seq === this.aiSeq) {
        this.state.aiStarting = false;
        this.emit();
      }
    }
  }
  async cancel(which: "aiJob" | "indexJob") {
    const session = this.state.status?.sessionId,
      job = this.state[which];
    if (!session || !job || terminal(job)) return;
    const epoch = this.epoch;
    try {
      const cancelled = await this.api.cancel(session, job.id);
      if (
        this.valid(epoch, session) &&
        this.state[which]?.id === job.id &&
        cancelled
      ) {
        this.state[which] = { ...job, state: "cancelled", result: null };
        this.emit();
      }
    } catch (e) {
      if (this.valid(epoch, session)) this.fail(e);
    }
  }
  async saveSettings(settings: Settings) {
    if (this.state.saving || this.state.transitioning) return;
    this.state.saving = true;
    this.emit();
    try {
      if (this.state.status) await this.close();
      await this.api.saveSettings(settings);
      this.state.settings = { ...settings };
      this.state.notice = "Settings saved. Open a workspace to use them.";
    } catch (e) {
      this.fail(e);
    } finally {
      this.state.saving = false;
      this.emit();
    }
  }
  private schedule() {
    clearTimeout(this.timer);
    if (this.state.status && !this.state.transitioning)
      this.timer = setTimeout(() => void this.poll(), 750);
  }
  async poll() {
    const session = this.state.status?.sessionId;
    if (!session || this.state.transitioning) return;
    const epoch = this.epoch;
    try {
      const status = await this.api.status(session);
      if (!this.valid(epoch, session)) return;
      if (status.sessionId !== session || status.protocolVersion !== 1) {
        throw new Error("Unexpected workspace status or protocol version.");
      }
      this.state.status = status;
      for (const key of ["indexJob", "aiJob"] as const) {
        const job = this.state[key];
        if (!job || terminal(job)) continue;
        const next = await this.api.job(session, job.id);
        if (!this.valid(epoch, session)) return;
        // A locally cancelled job cannot be revived by an older in-flight poll.
        if (this.state[key]?.id !== job.id || terminal(this.state[key]!))
          continue;
        if (!next) {
          this.state[key] = {
            ...job,
            state: "failed",
            error: "Job expired. Start a new request.",
          };
          continue;
        }
        if (next.sessionId !== session) continue;
        this.state[key] = next;
        if (key === "indexJob" && terminal(next)) {
          this.detailSeq++;
          this.state.selected = null;
          this.state.inheritance = null;
          this.state.inspecting = false;
          void this.search();
        }
      }
      this.emit();
    } catch (e) {
      if (this.valid(epoch, session)) this.fail(e);
    } finally {
      if (this.valid(epoch, session)) this.schedule();
    }
  }
  dispose() {
    this.epoch++;
    clearTimeout(this.timer);
    clearTimeout(this.searchTimer);
    this.listeners.clear();
  }
}
