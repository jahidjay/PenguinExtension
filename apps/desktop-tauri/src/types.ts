export interface SymbolDto {
  id: string;
  name: string;
  kind: string;
  macroName: string;
  file: string;
  line: number;
  typeName?: string | null;
  specifiers: { key: string; value?: string | null }[];
  bases: string[];
  signature?: string | null;
  documentation?: string | null;
  owner?: string | null;
  qualifiedName?: string | null;
}
export interface Status {
  protocolVersion: number;
  sessionId: string;
  state: string;
  roots: string[];
  files: number;
  symbols: number;
  message?: string | null;
}
export interface Job {
  id: string;
  sessionId: string;
  kind: string;
  state: "queued" | "running" | "succeeded" | "failed" | "cancelled";
  result?: {
    text?: string;
    model?: string;
    indexed?: number;
    unchanged?: number;
    removed?: number;
    errors?: string[];
  } | null;
  error?: string | null;
}
export interface Inheritance {
  bases: string[];
  derived: SymbolDto[];
  derivedAvailable?: boolean;
  truncated?: boolean;
}
export interface Diagnostic {
  code: string;
  severity: string;
  message: string;
  line: number;
  column: number;
}
export interface StyleResult {
  file: string;
  diagnostics: Diagnostic[];
}
export interface Settings {
  aiEnabled: boolean;
  endpoint: string;
  model: string;
  namingChecks: boolean;
}
export const DEFAULT_SETTINGS: Settings = {
  aiEnabled: false,
  endpoint: "http://127.0.0.1:11434",
  model: "qwen2.5-coder:3b",
  namingChecks: false,
};
export interface DesktopApi {
  settings(): Promise<Settings>;
  saveSettings(settings: Settings): Promise<void>;
  open(): Promise<Status | null>;
  close(sessionId: string): Promise<void>;
  status(sessionId: string): Promise<Status>;
  search(sessionId: string, query: string): Promise<SymbolDto[]>;
  symbol(sessionId: string, id: string): Promise<SymbolDto | null>;
  inheritance(sessionId: string, id: string): Promise<Inheritance>;
  reindex(sessionId: string): Promise<Job>;
  job(sessionId: string, id: string): Promise<Job | null>;
  cancel(sessionId: string, id: string): Promise<boolean>;
  style(sessionId: string, file: string): Promise<StyleResult>;
  ai(
    sessionId: string,
    task: "explain" | "generate",
    source: string,
    instruction: string,
  ): Promise<Job>;
}
