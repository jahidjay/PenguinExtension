import * as vscode from 'vscode';
import { NameStore } from './nameStore';
import { ColorKind, scanDocument } from './unrealScanner';

// Unreal coloring as editor decorations: they layer on top of whatever colors the C++ extension (cpptools/clangd)
// provides, which a second semantic-token provider could not. Colors are theme colors ("penguin.*"), so Penguin
// Dark/Light and any other theme can set them, and users can override them in workbench.colorCustomizations.

const kinds: ColorKind[] = ['macro', 'class', 'struct', 'enum', 'enumValue', 'delegate', 'function', 'property', 'specifier', 'metaKey'];
const colorIds: Record<ColorKind, string> = {
    macro: 'penguin.ueMacro', class: 'penguin.class', struct: 'penguin.struct', enum: 'penguin.enum',
    enumValue: 'penguin.enumValue', delegate: 'penguin.delegate', function: 'penguin.function', property: 'penguin.property',
    specifier: 'penguin.specifier', metaKey: 'penguin.metaKey'
};
const languages = new Set(['cpp', 'c']);
const maxDocumentLength = 2_000_000;

export class UnrealColoring implements vscode.Disposable {
    private readonly types = new Map<ColorKind, vscode.TextEditorDecorationType>();
    private readonly disposables: vscode.Disposable[] = [];
    private timer: NodeJS.Timeout | undefined;

    constructor(private readonly names: NameStore) {
        for (const kind of kinds) {
            this.types.set(kind, vscode.window.createTextEditorDecorationType({
                color: new vscode.ThemeColor(colorIds[kind]),
                ...(kind === 'macro' ? { fontWeight: 'bold' } : {}),
                rangeBehavior: vscode.DecorationRangeBehavior.ClosedClosed
            }));
        }
        this.disposables.push(
            names.onDidChange(() => this.schedule(0)),
            vscode.window.onDidChangeVisibleTextEditors(() => this.schedule(0)),
            vscode.workspace.onDidChangeTextDocument(event => {
                if (vscode.window.visibleTextEditors.some(editor => editor.document === event.document)) { this.schedule(150); }
            }),
            vscode.workspace.onDidChangeConfiguration(event => { if (event.affectsConfiguration('penguin.unrealColoring')) { this.schedule(0); } }));
        this.schedule(0);
    }

    private get enabled(): boolean { return vscode.workspace.getConfiguration('penguin').get<boolean>('unrealColoring.enabled', true); }

    private schedule(delay: number): void {
        if (this.timer) { clearTimeout(this.timer); }
        this.timer = setTimeout(() => { this.timer = undefined; this.paintAll(); }, delay);
    }

    private paintAll(): void {
        for (const editor of vscode.window.visibleTextEditors) { this.paint(editor); }
    }

    private paint(editor: vscode.TextEditor): void {
        const document = editor.document;
        if (!languages.has(document.languageId)) { return; }
        const ranges = new Map<ColorKind, vscode.Range[]>(kinds.map(kind => [kind, []]));
        if (this.enabled && document.getText().length <= maxDocumentLength) {
            const lines: string[] = [];
            for (let i = 0; i < document.lineCount; i++) { lines.push(document.lineAt(i).text); }
            scanDocument(lines, this.names.colors, (line, start, length, kind) =>
                ranges.get(kind)!.push(new vscode.Range(line, start, line, start + length)));
        }
        for (const kind of kinds) { editor.setDecorations(this.types.get(kind)!, ranges.get(kind)!); }
    }

    dispose(): void {
        if (this.timer) { clearTimeout(this.timer); }
        this.disposables.forEach(d => d.dispose());
        this.types.forEach(t => t.dispose());
    }
}
