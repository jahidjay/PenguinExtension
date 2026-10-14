import * as vscode from 'vscode';
import { Backend } from './backend';
import { buildNameMap, ColorKind } from './unrealScanner';

/** Every indexed Unreal name with its kind (penguin/names), refreshed every 10 s and only when it changed. */
export class NameStore implements vscode.Disposable {
    private readonly changed = new vscode.EventEmitter<void>();
    readonly onDidChange = this.changed.event;
    /** Raw (name, kind) pairs as the backend reported them. */
    pairs: ReadonlyArray<readonly [string, string]> = [];
    /** name → color kind, clash-resolved, for coloring. */
    colors: ReadonlyMap<string, ColorKind> = new Map();
    private revision: string | undefined;
    private poll: NodeJS.Timeout | undefined;
    private fetching = false;
    private disposed = false;

    constructor(private readonly backend: Backend) { void this.refresh(); }

    async refresh(): Promise<void> {
        if (this.poll) { clearTimeout(this.poll); this.poll = undefined; }
        if (this.disposed) { return; }
        if (!this.fetching && vscode.workspace.isTrusted) {
            this.fetching = true;
            try {
                const result = await this.backend.getSession().protocol.names(this.revision);
                if (result.names) {
                    this.pairs = result.names;
                    this.colors = buildNameMap(result.names);
                    this.changed.fire();
                }
                this.revision = result.revision;
            } catch {
                this.revision = undefined; // backend not ready or restarting: keep what we have, try again later
            } finally { this.fetching = false; }
        }
        if (!this.disposed) { this.poll = setTimeout(() => { void this.refresh(); }, 10_000); }
    }

    dispose(): void {
        this.disposed = true;
        if (this.poll) { clearTimeout(this.poll); }
        this.changed.dispose();
    }
}
