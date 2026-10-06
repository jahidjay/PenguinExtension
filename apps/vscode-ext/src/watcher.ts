import * as path from 'node:path';
export type ChangeType = 1 | 2 | 3;
export interface FileChange { uri: string; type: ChangeType }
export const watchGlob = '**/*.{h,hpp,hh,hxx,c,cpp,cc,cxx,inl,uproject,uplugin}';
const excluded = new Set(['.git', '.vs', '.idea', '.vscode', 'node_modules', 'binaries', 'intermediate', 'saved', 'deriveddatacache', 'target']);
export function isWatchable(file: string): boolean {
    const parts = file.split(String.fromCharCode(92)).join('/').toLowerCase().split('/');
    const name = parts.at(-1) ?? '';
    return !parts.some(part => excluded.has(part)) && !/\.(generated\.h|gen\.cpp)$/.test(name) && /\.(h|hpp|hh|hxx|c|cpp|cc|cxx|inl|uproject|uplugin)$/.test(name);
}
export function insideRoots(file: string, roots: readonly string[]): boolean {
    return roots.some(root => { const relative = path.relative(root, file); return relative === '' || (!relative.startsWith(`..${path.sep}`) && relative !== '..' && !path.isAbsolute(relative)); });
}
export function coalesce(previous: ChangeType | undefined, next: ChangeType): ChangeType | undefined {
    if (previous === 1 && next === 3) { return undefined; }
    if (previous === 1 && next === 2) { return 1; }
    if (previous === 3 && next === 1) { return 2; }
    if (previous === 3 && next === 2) { return 3; }
    return next;
}
export class ChangeBatcher {
    private readonly pending = new Map<string, ChangeType>();
    private timer: NodeJS.Timeout | undefined;
    private deadline: NodeJS.Timeout | undefined;
    private disposed = false;
    private chain = Promise.resolve();
    constructor(private readonly send: (changes: FileChange[]) => Promise<void>, private readonly error: (error: unknown) => void,
        private readonly debounceMs = 250, private readonly maximumWaitMs = 1000) {}
    add(uri: string, type: ChangeType): void {
        if (this.disposed) { return; }
        const merged = coalesce(this.pending.get(uri), type);
        if (merged === undefined) { this.pending.delete(uri); } else { this.pending.set(uri, merged); }
        if (this.timer) { clearTimeout(this.timer); }
        this.timer = setTimeout(() => this.flush(), this.debounceMs);
        this.deadline ??= setTimeout(() => this.flush(), this.maximumWaitMs);
    }
    flush(): void {
        this.clearTimers();
        if (this.disposed || !this.pending.size) { return; }
        const changes = [...this.pending].map(([uri, type]) => ({ uri, type }));
        this.pending.clear();
        this.chain = this.chain.then(async () => {
            for (let i = 0; i < changes.length && !this.disposed; i += 128) { await this.send(changes.slice(i, i + 128)); }
        }).catch(this.error);
    }
    private clearTimers(): void {
        if (this.timer) { clearTimeout(this.timer); this.timer = undefined; }
        if (this.deadline) { clearTimeout(this.deadline); this.deadline = undefined; }
    }
    dispose(): void { this.disposed = true; this.clearTimers(); this.pending.clear(); }
    async idle(): Promise<void> { await this.chain; }
}
