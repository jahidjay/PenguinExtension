import { afterEach, describe, expect, it, vi } from "vitest";
import { DesktopController } from "../src/controller";
import {
  DEFAULT_SETTINGS,
  type DesktopApi,
  type SymbolDto,
  type Status,
  type Job,
} from "../src/types";
const status: Status = {
  protocolVersion: 1,
  sessionId: "session1",
  state: "ready",
  roots: ["C:/Game"],
  files: 1,
  symbols: 2,
};
const symbol: SymbolDto = {
  id: "ref1",
  name: "AActor",
  kind: "class",
  macroName: "UCLASS",
  file: "C:/Game/Actor.h",
  line: 1,
  specifiers: [],
  bases: ["UObject"],
};
const job: Job = {
  id: "job1",
  sessionId: "session1",
  kind: "ai",
  state: "running",
};
function deferred<T>() {
  let resolve!: (v: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}
function mockApi(): DesktopApi {
  return {
    settings: vi.fn().mockResolvedValue({ ...DEFAULT_SETTINGS }),
    saveSettings: vi.fn().mockResolvedValue(undefined),
    open: vi.fn().mockResolvedValue(status),
    close: vi.fn().mockResolvedValue(undefined),
    status: vi.fn().mockResolvedValue(status),
    search: vi.fn().mockResolvedValue([symbol]),
    symbol: vi.fn().mockResolvedValue(symbol),
    inheritance: vi.fn().mockResolvedValue({ bases: ["UObject"], derived: [] }),
    reindex: vi.fn().mockResolvedValue({ ...job, kind: "index" }),
    job: vi.fn().mockResolvedValue(job),
    cancel: vi.fn().mockResolvedValue(true),
    style: vi.fn().mockResolvedValue({ file: symbol.file, diagnostics: [] }),
    ai: vi.fn().mockResolvedValue(job),
  };
}
let c: DesktopController;
afterEach(() => {
  c?.dispose();
  vi.useRealTimers();
});
async function setup() {
  const api = mockApi();
  c = new DesktopController(api);
  await c.initialize();
  await c.open();
  return api;
}
describe("desktop lifecycle and jobs", () => {
  it("AI is off by default and makes no calls without consent", async () => {
    const api = await setup();
    await c.startAi("explain");
    expect(api.ai).not.toHaveBeenCalled();
  });
  it("opens, searches and inspects real API metadata", async () => {
    await setup();
    expect(c.state.symbols).toEqual([symbol]);
    await c.select(symbol);
    expect(c.state.selected).toEqual(symbol);
    expect(c.state.inheritance?.bases).toEqual(["UObject"]);
    expect(c.state.file).toBe(symbol.file);
  });
  it("discards a search response after closing the workspace", async () => {
    const api = await setup();
    const d = deferred<SymbolDto[]>();
    vi.mocked(api.search).mockReturnValueOnce(d.promise);
    const pending = c.search();
    await c.close();
    d.resolve([symbol]);
    await pending;
    expect(c.state.status).toBeNull();
    expect(c.state.symbols).toEqual([]);
  });
  it("newer search wins even if an older query finishes last", async () => {
    const api = await setup();
    const d = deferred<SymbolDto[]>();
    vi.mocked(api.search)
      .mockReturnValueOnce(d.promise)
      .mockResolvedValueOnce([]);
    const old = c.search();
    await c.search();
    d.resolve([symbol]);
    await old;
    expect(c.state.symbols).toEqual([]);
  });
  it("debounces search input", async () => {
    const api = await setup();
    vi.useFakeTimers();
    c.setQuery("A");
    c.setQuery("Ac");
    c.setQuery("Actor");
    await vi.advanceTimersByTimeAsync(210);
    expect(api.search).toHaveBeenLastCalledWith("session1", "Actor");
    expect(api.search).toHaveBeenCalledTimes(2);
  });
  it("discards details from a previous session", async () => {
    const api = await setup();
    const d = deferred<SymbolDto>();
    vi.mocked(api.symbol).mockReturnValueOnce(d.promise);
    const pending = c.select(symbol);
    await c.close();
    d.resolve(symbol);
    await pending;
    expect(c.state.selected).toBeNull();
    expect(api.inheritance).not.toHaveBeenCalled();
  });
  it("keeps the current workspace when the native picker is cancelled", async () => {
    const api = await setup();
    vi.mocked(api.open).mockResolvedValueOnce(null);
    await c.open();
    expect(c.state.status?.sessionId).toBe("session1");
  });
  it("does not retain a stale workspace after an open failure", async () => {
    const api = await setup();
    vi.mocked(api.open).mockRejectedValueOnce(new Error("Cache busy"));
    await c.open();
    expect(c.state.status).toBeNull();
    expect(c.state.error).toBe("Cache busy");
  });
  it("closing drops late style results", async () => {
    const api = await setup();
    const d = deferred<{ file: string; diagnostics: [] }>();
    vi.mocked(api.style).mockReturnValueOnce(d.promise);
    const pending = c.checkStyle();
    await c.close();
    d.resolve({ file: symbol.file, diagnostics: [] });
    await pending;
    expect(c.state.style).toBeNull();
  });
  it("cancellation is terminal even when an in-flight poll returns success", async () => {
    const api = await setup();
    c.state.settings.aiEnabled = true;
    await c.startAi("explain");
    const d = deferred<Job>();
    vi.mocked(api.job).mockReturnValueOnce(d.promise);
    const pending = c.poll();
    await Promise.resolve();
    await c.cancel("aiJob");
    d.resolve({ ...job, state: "succeeded", result: { text: "late" } });
    await pending;
    expect(c.state.aiJob?.state).toBe("cancelled");
    expect(c.state.aiJob?.result).toBeNull();
  });
  it("does not poll terminal jobs", async () => {
    const api = await setup();
    c.state.aiJob = { ...job, state: "succeeded" };
    await c.poll();
    expect(api.job).not.toHaveBeenCalled();
  });
  it("marks evicted jobs as failed rather than polling forever", async () => {
    const api = await setup();
    c.state.aiJob = job;
    vi.mocked(api.job).mockResolvedValueOnce(null);
    await c.poll();
    expect(c.state.aiJob?.state).toBe("failed");
    expect(c.state.aiJob?.error).toContain("expired");
  });
  it("saves settings only after closing and clears old previews", async () => {
    const api = await setup();
    c.state.aiJob = { ...job, state: "succeeded", result: { text: "old" } };
    await c.saveSettings({ ...DEFAULT_SETTINGS, aiEnabled: true });
    expect(c.state.status).toBeNull();
    expect(c.state.aiJob).toBeNull();
    expect(api.close).toHaveBeenCalledBefore(vi.mocked(api.saveSettings));
    expect(c.state.settings.aiEnabled).toBe(true);
  });
  it("prevents duplicate AI and index starts", async () => {
    const api = await setup();
    c.state.settings.aiEnabled = true;
    await c.startAi("explain");
    await c.startAi("generate");
    await c.reindex();
    await c.reindex();
    expect(api.ai).toHaveBeenCalledTimes(1);
    expect(api.reindex).toHaveBeenCalledTimes(1);
  });
  it("keeps active jobs and preview context when the picker is cancelled", async () => {
    const api = await setup();
    c.state.aiJob = job;
    c.state.source = "context";
    vi.mocked(api.open).mockResolvedValueOnce(null);
    await c.open();
    expect(c.state.aiJob).toEqual(job);
    expect(c.state.source).toBe("context");
  });
  it("failed workspace replacement clears all previous source and previews", async () => {
    const api = await setup();
    c.state.source = "old context";
    c.state.selected = symbol;
    c.state.aiJob = {
      ...job,
      state: "succeeded",
      result: { text: "old preview" },
    };
    vi.mocked(api.open).mockRejectedValueOnce(new Error("Missing root"));
    await c.open();
    expect(c.state.source).toBe("");
    expect(c.state.selected).toBeNull();
    expect(c.state.aiJob).toBeNull();
  });
});
