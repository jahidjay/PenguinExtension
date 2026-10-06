import { invoke } from "@tauri-apps/api/core";
import type { DesktopApi } from "./types";
export const api: DesktopApi = {
  settings: () => invoke("desktop_settings"),
  saveSettings: (settings) => invoke("desktop_save_settings", { settings }),
  open: () => invoke("desktop_open"),
  close: (sessionId) => invoke("desktop_close", { sessionId }),
  status: (sessionId) => invoke("desktop_status", { sessionId }),
  search: (sessionId, query) => invoke("desktop_search", { sessionId, query }),
  symbol: (sessionId, id) => invoke("desktop_symbol", { sessionId, id }),
  inheritance: (sessionId, id) =>
    invoke("desktop_inheritance", { sessionId, id }),
  reindex: (sessionId) => invoke("desktop_reindex", { sessionId }),
  job: (sessionId, id) => invoke("desktop_job", { sessionId, id }),
  cancel: (sessionId, id) => invoke("desktop_cancel", { sessionId, id }),
  style: (sessionId, file) => invoke("desktop_style", { sessionId, file }),
  ai: (sessionId, task, source, instruction) =>
    invoke("desktop_ai", { sessionId, task, source, instruction }),
};
