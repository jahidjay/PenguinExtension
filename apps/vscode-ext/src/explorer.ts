import * as vscode from 'vscode';
import { Backend } from './backend';
import { navigate } from './commands';
import { NameStore } from './nameStore';
import { SymbolDto } from './protocol';

// Unreal Explorer (sidebar tree: indexed names grouped by kind, with a filter) and Symbol Inspector (webview with
// the symbol under the cursor). Both mirror the Visual Studio extension's windows of the same names.

const groups: Array<{ label: string; kinds: string[] }> = [
    { label: 'Classes', kinds: ['class', 'interface'] }, { label: 'Structs', kinds: ['struct'] },
    { label: 'Enums', kinds: ['enum'] }, { label: 'Delegates', kinds: ['delegate'] },
    { label: 'Functions', kinds: ['function'] }, { label: 'Properties', kinds: ['property'] }
];
const maxPerGroup = 500;

type Node = { group: string; kinds: string[]; count: number } | { name: string; kind: string } | { more: number };

/** Exact-name lookup: the best indexed symbol called `name` (classes before functions before properties). */
export async function findSymbol(backend: Backend, name: string, preferKind?: string): Promise<SymbolDto | undefined> {
    const results = (await backend.getSession().protocol.symbols(name)).filter(symbol => symbol.name === name);
    const order = ['class', 'interface', 'struct', 'enum', 'delegate', 'function', 'property'];
    results.sort((a, b) => (a.kind === preferKind ? -1 : 0) - (b.kind === preferKind ? -1 : 0) || order.indexOf(a.kind) - order.indexOf(b.kind));
    return results[0];
}

export class ExplorerProvider implements vscode.TreeDataProvider<Node>, vscode.Disposable {
    private readonly changed = new vscode.EventEmitter<Node | undefined>();
    readonly onDidChangeTreeData = this.changed.event;
    private filter = '';
    private readonly subscription: vscode.Disposable;

    constructor(private readonly names: NameStore) { this.subscription = names.onDidChange(() => this.changed.fire(undefined)); }

    setFilter(filter: string): void { this.filter = filter.trim().toLowerCase(); this.changed.fire(undefined); }
    get filterText(): string { return this.filter; }

    private matching(kinds: string[]): string[] {
        const result = new Set<string>();
        for (const [name, kind] of this.names.pairs) {
            if (kinds.includes(kind) && (!this.filter || name.toLowerCase().includes(this.filter))) { result.add(name); }
        }
        return [...result].sort((a, b) => a.localeCompare(b));
    }

    getChildren(node?: Node): Node[] {
        if (!node) {
            return groups.map(g => ({ group: g.label, kinds: g.kinds, count: this.matching(g.kinds).length }))
                .filter(g => g.count > 0 || !this.filter);
        }
        if ('group' in node) {
            const names = this.matching(node.kinds);
            const shown: Node[] = names.slice(0, maxPerGroup).map(name => ({ name, kind: node.kinds[0]! }));
            if (names.length > maxPerGroup) { shown.push({ more: names.length - maxPerGroup }); }
            return shown;
        }
        return [];
    }

    getTreeItem(node: Node): vscode.TreeItem {
        if ('group' in node) {
            const item = new vscode.TreeItem(node.group, this.filter ? vscode.TreeItemCollapsibleState.Expanded : vscode.TreeItemCollapsibleState.Collapsed);
            item.description = String(node.count);
            return item;
        }
        if ('more' in node) {
            const item = new vscode.TreeItem(`… ${node.more} more — use Filter to narrow`);
            item.command = { command: 'penguin.explorer.filter', title: 'Filter' };
            return item;
        }
        const item = new vscode.TreeItem(node.name);
        item.iconPath = new vscode.ThemeIcon(icon(node.kind));
        item.contextValue = 'penguinSymbol';
        item.command = { command: 'penguin.explorer.open', title: 'Go to Definition', arguments: [node.name, node.kind] };
        return item;
    }

    dispose(): void { this.subscription.dispose(); this.changed.dispose(); }
}

function icon(kind: string): string {
    switch (kind) {
        case 'class': case 'interface': return 'symbol-class';
        case 'struct': return 'symbol-structure';
        case 'enum': return 'symbol-enum';
        case 'delegate': return 'symbol-event';
        case 'function': return 'symbol-method';
        default: return 'symbol-field';
    }
}

/** Symbol Inspector: shows the indexed symbol under the cursor (debounced), like the Visual Studio panel. */
export class InspectorProvider implements vscode.WebviewViewProvider, vscode.Disposable {
    private view: vscode.WebviewView | undefined;
    private timer: NodeJS.Timeout | undefined;
    private lastWord = '';
    private readonly disposables: vscode.Disposable[] = [];

    constructor(private readonly backend: Backend) {
        this.disposables.push(vscode.window.onDidChangeTextEditorSelection(event => this.schedule(event.textEditor)));
    }

    resolveWebviewView(view: vscode.WebviewView): void {
        this.view = view;
        view.webview.options = { enableScripts: false, enableCommandUris: ['penguin.explorer.open'] };
        view.webview.html = page('<p class="dim">Move the cursor onto an Unreal class, struct, enum, function or property.</p>');
        const editor = vscode.window.activeTextEditor;
        if (editor) { this.schedule(editor); }
    }

    private schedule(editor: vscode.TextEditor): void {
        if (!this.view?.visible || !['cpp', 'c'].includes(editor.document.languageId)) { return; }
        if (this.timer) { clearTimeout(this.timer); }
        this.timer = setTimeout(() => { void this.update(editor); }, 300);
    }

    private async update(editor: vscode.TextEditor): Promise<void> {
        const range = editor.document.getWordRangeAtPosition(editor.selection.active, /[A-Za-z_][A-Za-z0-9_]*/);
        const word = range ? editor.document.getText(range) : '';
        if (!word || word === this.lastWord || !this.view) { return; }
        this.lastWord = word;
        try {
            const symbol = await findSymbol(this.backend, word);
            if (this.view && this.lastWord === word && symbol) { this.view.webview.html = page(render(symbol)); }
        } catch {
            // backend not ready: leave the last symbol shown
        }
    }

    dispose(): void { if (this.timer) { clearTimeout(this.timer); } this.disposables.forEach(d => d.dispose()); }
}

const esc = (text: string): string => text.replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', '\'': '&#39;' }[c]!));

function render(s: SymbolDto): string {
    const open = `command:penguin.explorer.open?${encodeURIComponent(JSON.stringify([s.name, s.kind]))}`;
    const rows: string[] = [`<h2>${esc(s.name)} <span class="kind">${esc(s.kind)}</span></h2>`];
    if (s.qualifiedName && s.qualifiedName !== s.name) { rows.push(`<div class="dim">${esc(s.qualifiedName)}</div>`); }
    if (s.signature) { rows.push(`<pre>${esc(s.signature)}</pre>`); }
    if (s.documentation) { rows.push(`<p>${esc(s.documentation)}</p>`); }
    if (s.owner) { rows.push(`<div><b>Owner:</b> ${esc(s.owner)}</div>`); }
    if (s.typeName) { rows.push(`<div><b>Type:</b> ${esc(s.typeName)}</div>`); }
    if (s.bases.length) { rows.push(`<div><b>Bases:</b> ${s.bases.map(esc).join(', ')}</div>`); }
    if (s.specifiers.length) {
        rows.push(`<div><b>${esc(s.macroName)}:</b> ${s.specifiers.map(sp => `<code>${esc(sp.key)}${sp.value ? '=' + esc(sp.value) : ''}</code>`).join(' ')}</div>`);
    }
    rows.push(`<p><a href="${open}">${esc(s.file)}:${s.line}</a></p>`);
    return rows.join('\n');
}

function page(body: string): string {
    return `<!DOCTYPE html><html><head><meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline';">
<style>body{font-family:var(--vscode-font-family);font-size:var(--vscode-font-size);color:var(--vscode-foreground);padding:4px 8px}
h2{font-size:1.2em;margin:4px 0}.kind{font-size:.8em;font-weight:normal;opacity:.7}.dim{opacity:.7}
pre{white-space:pre-wrap;background:var(--vscode-textCodeBlock-background);padding:6px;font-family:var(--vscode-editor-font-family)}
code{background:var(--vscode-textCodeBlock-background);padding:0 3px;margin-right:2px}a{color:var(--vscode-textLink-foreground)}</style>
</head><body>${body}</body></html>`;
}

export function registerExplorer(context: vscode.ExtensionContext, backend: Backend, names: NameStore): void {
    const explorer = new ExplorerProvider(names);
    const inspector = new InspectorProvider(backend);
    context.subscriptions.push(explorer, inspector,
        vscode.window.registerTreeDataProvider('penguin.explorer', explorer),
        vscode.window.registerWebviewViewProvider('penguin.inspector', inspector),
        vscode.commands.registerCommand('penguin.explorer.refresh', () => names.refresh()),
        vscode.commands.registerCommand('penguin.explorer.filter', async () => {
            const text = await vscode.window.showInputBox({ prompt: 'Filter Unreal Explorer (empty shows everything)', value: explorer.filterText });
            if (text !== undefined) { explorer.setFilter(text); }
        }),
        vscode.commands.registerCommand('penguin.explorer.open', async (name: string, kind?: string) => {
            try {
                const symbol = await findSymbol(backend, name, kind);
                if (!symbol) { void vscode.window.showInformationMessage(`Penguin: ${name} is not in the index any more.`); return; }
                await navigate(backend.getSession(), symbol);
            } catch (error) { backend.report(error); }
        }));
}
