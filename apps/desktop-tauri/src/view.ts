import { DesktopController, terminal } from "./controller";
import type { Job } from "./types";
// HTML is used only for this trusted shell; all IPC strings are rendered with textContent.
export function mount(
  root: HTMLElement,
  controller: DesktopController,
): () => void {
  root.innerHTML = `<div class="shell">
 <aside class="sidebar"><div class="brand"><span class="brand-mark">P</span><span>Penguin<small>UNREAL WORKSPACE</small></span></div>
 <div class="side-label">WORKSPACE</div><div id="root-name" class="root-name"></div><div id="root-path" class="root-path"></div>
 <button id="open" class="primary wide">Open workspace</button><button id="close" class="quiet wide">Close workspace</button>
 <nav aria-label="Workspace sections"><button data-tab="symbols" class="nav-item active">Symbols <span>01</span></button><button data-tab="style" class="nav-item">Style checks <span>02</span></button><button data-tab="ai" class="nav-item">Local AI <span>03</span></button><button data-tab="settings" class="nav-item">Settings <span>04</span></button></nav>
 <div class="local-note"><span class="dot"></span> Local-first, read-only<p>Saved files only. Reindex explicitly after changes in your editor.</p></div></aside>
 <main><header class="topbar"><span id="section-label">Symbol explorer</span><div class="status-wrap"><span class="dot"></span><span id="status">Not connected</span><button id="reindex">Reindex</button></div></header>
 <div id="error-banner" class="banner error" role="alert" hidden><span id="error"></span><button id="dismiss">Dismiss</button></div><div id="notice" class="banner notice" role="status" hidden></div>
 <div id="index-banner" class="banner" hidden><span id="index-summary"></span><button id="cancel-index">Cancel indexing</button></div>
 <section id="symbols-panel" class="panel"><div class="page-heading"><div><div class="eyebrow">EXPLORE YOUR CODE</div><h1>Symbols, in context.</h1><p>Reflection metadata, declarations and inheritance in one place.</p></div><span id="index-count" class="muted"></span></div>
 <div class="explorer"><div class="symbol-list"><label for="search">Find a symbol</label><input id="search" type="search" maxlength="256" placeholder="Search classes, functions, properties…" autocomplete="off"><div class="list-caption"><span id="search-count"></span><button id="refresh" class="text-button">Refresh</button></div><div id="results" role="list" aria-label="Search results"></div></div>
 <article class="details"><div id="detail-empty" class="empty"><div class="empty-glyph">{ }</div><h2>Get the bigger picture</h2><p>Open a workspace, index its headers, then select a symbol to inspect its declaration.</p></div><div id="detail-content" hidden><div id="detail-kind" class="eyebrow"></div><h2 id="detail-name"></h2><p id="detail-file" class="file-path"></p><pre id="signature" class="code"></pre><h3>Documentation</h3><p id="documentation" class="preserve"></p><h3>Reflection specifiers</h3><div id="specifiers" class="chips"></div><h3>Inheritance</h3><p class="muted">Syntax-established relationships, not full C++ type resolution.</p><div id="bases"></div><div id="derived"></div><div class="actions"><button id="inspect-style">Check this file</button><button id="inspect-ai">Explain with local AI</button></div></div></article></div></section>
 <section id="style-panel" class="panel" hidden><div class="page-heading"><div><div class="eyebrow">SMALL CHECKS, CLEAR SIGNAL</div><h1>Reflection style.</h1><p>Review one saved header. No files are changed.</p></div></div><div class="surface"><label for="style-file">Workspace header path</label><div class="input-action"><input id="style-file" placeholder="Select a symbol, or enter an absolute .h / .hpp path" maxlength="4096"><button id="check-style" class="primary">Run checks</button></div><p class="muted">Only bounded UTF-8 headers inside your root are readable. Symlinks and junctions are rejected.</p><div id="style-results" aria-live="polite" class="result-region"></div></div></section>
 <section id="ai-panel" class="panel" hidden><div class="page-heading"><div><div class="eyebrow">YOU ASK. YOU REVIEW.</div><h1>A local second opinion.</h1><p>Explain a declaration or generate a proposal. Output stays a preview.</p></div><span id="ai-enabled" class="pill"></span></div><div class="ai-grid"><div class="surface"><label for="ai-source">Context to send to your local model</label><textarea id="ai-source" rows="12" maxlength="32768" spellcheck="false" placeholder="Select a symbol or paste a bounded source excerpt…"></textarea><label for="ai-instruction">Instruction (optional)</label><textarea id="ai-instruction" rows="3" maxlength="2048"></textarea><div class="actions"><button id="explain" class="primary">Explain</button><button id="generate">Generate preview</button><button id="cancel-ai" hidden>Cancel</button></div><p class="muted">No service startup, downloads, execution, or automatic edits.</p></div><div class="surface preview"><div class="preview-heading"><h2>Preview</h2><span id="ai-status" class="muted"></span></div><pre id="ai-output" class="preview-text"></pre></div></div></section>
 <section id="settings-panel" class="panel" hidden><div class="page-heading"><div><div class="eyebrow">LOCAL BY DESIGN</div><h1>Your environment.</h1><p>Saved atomically; applies to your next workspace.</p></div></div><form id="settings-form" class="surface settings"><label class="checkbox"><input id="ai-toggle" type="checkbox">Enable explicit local AI requests</label><p class="muted">Off by default. Use your existing local Ollama installation and model. Nothing is installed or started for you.</p><label for="endpoint">Loopback Ollama endpoint</label><input id="endpoint" type="url" required maxlength="512"><label for="model">Model name</label><input id="model" required maxlength="128"><label class="checkbox"><input id="naming" type="checkbox">Enable optional naming checks</label><div class="settings-warning">Saving closes the active workspace and cancels its jobs. Reopen a workspace afterwards.</div><button id="save-settings" type="submit" class="primary">Save local settings</button></form></section>
 <footer><span>Penguin Desktop · 0.1</span><span>Explicit disk refresh · Preview-only AI</span></footer></main></div>`;
  const el = <T extends HTMLElement = HTMLElement>(id: string) =>
    root.querySelector<T>(`#${id}`)!;
  const text = (id: string, value: string) => {
    el(id).textContent = value;
  };
  const disable = (id: string, value: boolean) => {
    el<HTMLButtonElement>(id).disabled = value;
  };
  const input = (id: string, value: string) => {
    const node = el<HTMLInputElement>(id);
    if (node.value !== value) node.value = value;
  };
  let previousSettings = "";
  let previousResults = "";
  function showTab(name: string) {
    for (const key of ["symbols", "style", "ai", "settings"])
      el(`${key}-panel`).hidden = key !== name;
    root.querySelectorAll<HTMLButtonElement>("[data-tab]").forEach((button) => {
      button.classList.toggle("active", button.dataset.tab === name);
      button.setAttribute(
        "aria-current",
        button.dataset.tab === name ? "page" : "false",
      );
    });
    text(
      "section-label",
      (
        {
          symbols: "Symbol explorer",
          style: "Style checks",
          ai: "Local AI",
          settings: "Settings",
        } as Record<string, string>
      )[name],
    );
  }
  root
    .querySelectorAll<HTMLButtonElement>("[data-tab]")
    .forEach((button) =>
      button.addEventListener("click", () => showTab(button.dataset.tab!)),
    );
  const on = (id: string, fn: () => unknown) =>
    el(id).addEventListener("click", () => void fn());
  on("open", () => controller.open());
  on("close", () => controller.close());
  on("reindex", () => controller.reindex());
  on("refresh", () => controller.search());
  on("dismiss", () => controller.dismiss());
  on("cancel-index", () => controller.cancel("indexJob"));
  on("cancel-ai", () => controller.cancel("aiJob"));
  on("check-style", () => controller.checkStyle());
  on("explain", () => controller.startAi("explain"));
  on("generate", () => controller.startAi("generate"));
  on("inspect-style", () => {
    showTab("style");
    void controller.checkStyle();
  });
  on("inspect-ai", () => {
    showTab("ai");
    el("ai-source").focus();
  });
  el<HTMLInputElement>("search").addEventListener("input", (e) =>
    controller.setQuery((e.target as HTMLInputElement).value),
  );
  for (const [id, key] of [
    ["style-file", "file"],
    ["ai-source", "source"],
    ["ai-instruction", "instruction"],
  ] as const)
    el(id).addEventListener("input", (e) => {
      controller.state[key] = (e.target as HTMLInputElement).value;
    });
  el("settings-form").addEventListener("submit", (event) => {
    event.preventDefault();
    void controller.saveSettings({
      aiEnabled: el<HTMLInputElement>("ai-toggle").checked,
      endpoint: el<HTMLInputElement>("endpoint").value,
      model: el<HTMLInputElement>("model").value,
      namingChecks: el<HTMLInputElement>("naming").checked,
    });
  });
  function jobSummary(job: Job): string {
    if (job.error) return `${job.state}: ${job.error}`;
    if (job.state === "succeeded" && job.kind === "index")
      return `Index complete · ${job.result?.indexed ?? 0} updated · ${job.result?.unchanged ?? 0} unchanged · ${job.result?.removed ?? 0} removed${job.result?.errors?.length ? " · " + job.result.errors.join("; ") : ""}`;
    return `Index job: ${job.state}`;
  }
  function render() {
    const s = controller.state,
      ready = !!s.status && !s.transitioning,
      selected = s.selected;
    const rootPath =
      s.status?.roots.join(" / ") || "Choose a project or source directory.";
    text(
      "root-name",
      s.status?.roots[0]?.split(/[\/]/).filter(Boolean).pop() ||
        "No workspace open",
    );
    text("root-path", rootPath);
    el("root-path").title = rootPath;
    text(
      "status",
      s.transitioning
        ? "Changing workspace…"
        : s.status?.state || "Not connected",
    );
    text(
      "index-count",
      s.status ? `${s.status.files} files · ${s.status.symbols} symbols` : "",
    );
    for (const id of ["close", "refresh", "search", "check-style"])
      disable(id, !ready);
    disable("open", s.transitioning || s.saving || s.aiStarting || s.indexing);
    disable(
      "reindex",
      !ready || s.indexing || !!(s.indexJob && !terminal(s.indexJob)),
    );
    disable("check-style", !ready || s.checking);
    text(
      "open",
      s.transitioning
        ? "Please wait…"
        : s.status
          ? "Change workspace"
          : "Open workspace",
    );
    el("error-banner").hidden = !s.error;
    text("error", s.error);
    el("notice").hidden = !s.notice;
    text("notice", s.notice);
    el("index-banner").hidden = !s.indexJob;
    if (s.indexJob) {
      text("index-summary", jobSummary(s.indexJob));
      el("cancel-index").hidden = terminal(s.indexJob);
    }
    input("search", s.query);
    text(
      "search-count",
      s.searching
        ? "Searching…"
        : `${s.symbols.length} results${s.symbols.length === 100 ? " · refine to see more" : ""}`,
    );
    const resultKey = JSON.stringify([
      s.symbols,
      selected?.id,
      ready,
      s.searching,
    ]);
    if (resultKey !== previousResults) {
      previousResults = resultKey;
      const results = el("results");
      results.replaceChildren();
      if (!s.symbols.length) {
        const message = document.createElement("p");
        message.className = "empty-list";
        message.textContent = !ready
          ? "Open a workspace to explore saved headers."
          : s.searching
            ? "Looking for symbols…"
            : "No matches. Try a different prefix or run Reindex.";
        results.append(message);
      }
      for (const symbol of s.symbols) {
        const row = document.createElement("button");
        row.className = "symbol-row";
        row.setAttribute("role", "listitem");
        row.classList.toggle("selected", selected?.id === symbol.id);
        const name = document.createElement("strong");
        name.textContent = symbol.name;
        const meta = document.createElement("small");
        meta.textContent = `${symbol.kind} · ${symbol.file.split(/[\/]/).pop()}:${symbol.line}`;
        row.append(name, meta);
        row.addEventListener("click", () => void controller.select(symbol));
        results.append(row);
      }
    }
    el("detail-empty").hidden = !!selected;
    el("detail-content").hidden = !selected;
    if (selected) {
      text("detail-kind", `${selected.macroName} / ${selected.kind}`);
      text("detail-name", selected.qualifiedName || selected.name);
      text("detail-file", `${selected.file}:${selected.line}`);
      text(
        "signature",
        selected.signature ||
          "Signature metadata is not available for this declaration.",
      );
      text(
        "documentation",
        selected.documentation || "No attached documentation was found.",
      );
      const chips = el("specifiers");
      chips.replaceChildren();
      for (const spec of selected.specifiers) {
        const chip = document.createElement("span");
        chip.className = "chip";
        chip.textContent =
          spec.value == null ? spec.key : `${spec.key} = ${spec.value}`;
        chips.append(chip);
      }
      if (!selected.specifiers.length) chips.textContent = "No specifiers.";
      text(
        "bases",
        `Bases: ${(s.inheritance?.bases || selected.bases).join(", ") || "None recorded"}`,
      );
      text(
        "derived",
        s.inspecting
          ? "Loading relationships…"
          : s.inheritance?.derivedAvailable === false
            ? "Derived-class lookup is not available in this core version."
            : `Derived: ${s.inheritance?.derived.map((item) => item.name).join(", ") || "None recorded"}`,
      );
    }
    input("style-file", s.file);
    text("check-style", s.checking ? "Checking…" : "Run checks");
    const diagnostics = el("style-results");
    diagnostics.replaceChildren();
    if (s.style) {
      const heading = document.createElement("p");
      heading.textContent = s.style.diagnostics.length
        ? `${s.style.diagnostics.length} findings · ${s.style.file}`
        : "No enabled-rule findings in this saved header.";
      diagnostics.append(heading);
      for (const item of s.style.diagnostics) {
        const row = document.createElement("div");
        row.className = "diagnostic";
        const title = document.createElement("strong");
        title.textContent = `${item.code} · ${item.severity} · ${item.line}:${item.column}`;
        const body = document.createElement("p");
        body.textContent = item.message;
        row.append(title, body);
        diagnostics.append(row);
      }
    } else
      diagnostics.textContent =
        "Run checks to review reflection specifiers and optional naming rules.";
    input("ai-source", s.source);
    input("ai-instruction", s.instruction);
    text(
      "ai-enabled",
      s.settings.aiEnabled ? "Enabled · loopback only" : "Disabled in settings",
    );
    text(
      "ai-status",
      s.aiStarting ? "Starting…" : s.aiJob?.state || "Not requested",
    );
    const busy = s.aiStarting || !!(s.aiJob && !terminal(s.aiJob));
    for (const id of ["explain", "generate"])
      disable(id, !ready || !s.settings.aiEnabled || busy);
    el("cancel-ai").hidden = !s.aiJob || terminal(s.aiJob);
    text(
      "ai-output",
      s.aiJob?.error ||
        s.aiJob?.result?.text ||
        (s.aiJob?.state === "cancelled"
          ? "Request cancelled. No output was applied."
          : "Your model response will appear here as plain text."),
    );
    const settingsKey = JSON.stringify(s.settings);
    if (previousSettings !== settingsKey) {
      el<HTMLInputElement>("ai-toggle").checked = s.settings.aiEnabled;
      input("endpoint", s.settings.endpoint);
      input("model", s.settings.model);
      el<HTMLInputElement>("naming").checked = s.settings.namingChecks;
      previousSettings = settingsKey;
    }
    disable("save-settings", s.saving || s.transitioning);
    text("save-settings", s.saving ? "Saving…" : "Save local settings");
  }
  const unsubscribe = controller.subscribe(render);
  render();
  showTab("symbols");
  return unsubscribe;
}
