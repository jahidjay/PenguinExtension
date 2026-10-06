import { afterEach, expect, it, vi } from "vitest";
import { DesktopController } from "../src/controller";
import { mount } from "../src/view";
import {
  DEFAULT_SETTINGS,
  type DesktopApi,
  type SymbolDto,
} from "../src/types";
const evil = '<img src=x onerror="alert(1)"><script>bad()</script>';
const symbol: SymbolDto = {
  id: "ref",
  name: evil,
  kind: "class",
  macroName: "UCLASS",
  file: "C:/Game/" + evil + ".h",
  line: 2,
  specifiers: [{ key: evil, value: evil }],
  bases: [evil],
  documentation: evil,
  signature: evil,
};
let c: DesktopController;
afterEach(() => {
  c.dispose();
  document.body.replaceChildren();
});
function setup() {
  document.body.innerHTML = '<div id="app"></div>';
  const api = {
    settings: vi.fn().mockResolvedValue(DEFAULT_SETTINGS),
    status: vi.fn(),
    search: vi.fn().mockResolvedValue([]),
    open: vi.fn().mockResolvedValue(null),
    close: vi.fn(),
    reindex: vi.fn(),
    cancel: vi.fn(),
    job: vi.fn(),
    symbol: vi.fn(),
    inheritance: vi.fn(),
    style: vi.fn(),
    ai: vi.fn(),
    saveSettings: vi.fn(),
  } satisfies DesktopApi;
  c = new DesktopController(api);
  mount(document.getElementById("app")!, c);
  return api;
}
it("renders source and model strings only as text, never markup", () => {
  setup();
  c.state.selected = symbol;
  c.state.symbols = [symbol];
  c.state.aiJob = {
    id: "j",
    sessionId: "s",
    kind: "ai",
    state: "succeeded",
    result: { text: evil },
  };
  c.state.style = {
    file: symbol.file,
    diagnostics: [
      { code: evil, severity: "warning", message: evil, line: 1, column: 1 },
    ],
  };
  c.dismiss();
  expect(document.querySelector("#detail-name")?.textContent).toBe(evil);
  expect(document.querySelector("#ai-output")?.textContent).toBe(evil);
  expect(document.querySelectorAll("script,img")).toHaveLength(0);
  expect(document.querySelector("#documentation")?.textContent).toBe(evil);
});
it("disables workspace-only commands and AI initially", () => {
  setup();
  for (const id of [
    "close",
    "reindex",
    "refresh",
    "check-style",
    "explain",
    "generate",
  ])
    expect((document.getElementById(id) as HTMLButtonElement).disabled).toBe(
      true,
    );
});
it("navigates sections using keyboard-accessible buttons", () => {
  setup();
  (
    document.querySelector('[data-tab="settings"]') as HTMLButtonElement
  ).click();
  expect(document.getElementById("settings-panel")?.hidden).toBe(false);
  expect(document.getElementById("symbols-panel")?.hidden).toBe(true);
  expect(
    document
      .querySelector('[data-tab="settings"]')
      ?.getAttribute("aria-current"),
  ).toBe("page");
});
it("clears focused source context on session closure", async () => {
  const api = setup();
  c.state.status = {
    protocolVersion: 1,
    sessionId: "s",
    roots: ["C:/Game"],
    state: "ready",
    files: 1,
    symbols: 1,
  };
  c.state.source = evil;
  c.dismiss();
  const source = document.getElementById("ai-source") as HTMLTextAreaElement;
  source.focus();
  await c.close();
  expect(api.close).toHaveBeenCalledWith("s");
  expect(source.value).toBe("");
});
it("uses the exact edited form settings on explicit save", async () => {
  const api = setup();
  (document.getElementById("ai-toggle") as HTMLInputElement).checked = true;
  (document.getElementById("model") as HTMLInputElement).value =
    "my-local-model";
  document
    .getElementById("settings-form")
    ?.dispatchEvent(new Event("submit", { cancelable: true }));
  await Promise.resolve();
  expect(api.saveSettings).toHaveBeenCalledWith({
    ...DEFAULT_SETTINGS,
    model: "my-local-model",
    aiEnabled: true,
  });
});

it("status updates preserve keyboard focus in the symbol list", () => {
  setup();
  c.state.symbols = [symbol];
  c.state.status = {
    protocolVersion: 1,
    sessionId: "s",
    state: "ready",
    roots: ["C:/Game"],
    files: 1,
    symbols: 1,
  };
  c.dismiss();
  const row = document.querySelector<HTMLButtonElement>(".symbol-row")!;
  row.focus();
  c.dismiss();
  expect(document.activeElement).toBe(row);
});
