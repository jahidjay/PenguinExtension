import * as vscode from 'vscode';
import * as path from 'node:path';
import { Backend } from './backend';
import { accessors, declarationAt, enclosingClass, implementation, sourceCandidates, ufunctionAbove } from './codegen';
import { findSymbol } from './explorer';

// Generate Implementation, Generate Getter/Setter and Unreal signature help — the free Visual Studio features,
// on top of the text helpers in codegen.ts.

const headerExtensions = new Set(['.h', '.hpp', '.hh', '.hxx', '.inl']);

async function exists(uri: vscode.Uri): Promise<boolean> {
    try { await vscode.workspace.fs.stat(uri); return true; } catch { return false; }
}

async function generateImplementation(): Promise<void> {
    const editor = vscode.window.activeTextEditor;
    if (!editor) { return; }
    const document = editor.document;
    if (!headerExtensions.has(path.extname(document.fileName).toLowerCase())) {
        void vscode.window.showWarningMessage('Penguin: put the cursor on a function declaration in a header (.h).');
        return;
    }
    const lines = Array.from({ length: document.lineCount }, (_, i) => document.lineAt(i).text);
    const line = editor.selection.active.line;
    const declaration = declarationAt(lines, line);
    const owner = enclosingClass(lines, line);
    if (!declaration || !owner) {
        void vscode.window.showWarningMessage('Penguin: put the cursor on a member function declaration inside a class.');
        return;
    }
    const definitions = implementation(declaration.text, owner, ufunctionAbove(lines, declaration.startLine));
    if (typeof definitions === 'string') { void vscode.window.showWarningMessage(`Penguin: ${definitions}`); return; }

    let target: vscode.Uri | undefined;
    for (const candidate of sourceCandidates(document.fileName, path.sep)) {
        const uri = vscode.Uri.file(candidate);
        if (await exists(uri)) { target = uri; break; }
    }
    if (!target) {
        const create = vscode.Uri.file(sourceCandidates(document.fileName, path.sep)[document.fileName.includes(`${path.sep}Public${path.sep}`) ? 1 : 0]!);
        const choice = await vscode.window.showInformationMessage(`No .cpp found for ${path.basename(document.fileName)}. Create ${vscode.workspace.asRelativePath(create)}?`, 'Create');
        if (choice !== 'Create') { return; }
        const include = `#include "${path.basename(document.fileName)}"\n`;
        await vscode.workspace.fs.writeFile(create, Buffer.from(include, 'utf8'));
        target = create;
    }
    const source = await vscode.workspace.openTextDocument(target);
    const existing = source.getText();
    const signature = definitions[0]!.split('\n')[0]!;
    const qualified = /(\w+::~?\w+)\(/.exec(signature)?.[1];
    if (qualified && existing.includes(qualified + '(')) {
        const at = source.positionAt(existing.indexOf(qualified + '('));
        const shown = await vscode.window.showTextDocument(source);
        shown.selection = new vscode.Selection(at, at);
        shown.revealRange(new vscode.Range(at, at), vscode.TextEditorRevealType.InCenter);
        void vscode.window.showInformationMessage(`Penguin: ${qualified} is already implemented.`);
        return;
    }
    const end = source.positionAt(existing.length);
    const prefix = existing.length === 0 || existing.endsWith('\n\n') ? '' : existing.endsWith('\n') ? '\n' : '\n\n';
    const edit = new vscode.WorkspaceEdit();
    edit.insert(target, end, prefix + definitions.join('\n'));
    await vscode.workspace.applyEdit(edit);
    const shown = await vscode.window.showTextDocument(source);
    const bodyLine = source.lineCount - 2 - (definitions.length > 1 ? definitions[1]!.split('\n').length : 0);
    const position = new vscode.Position(Math.max(0, bodyLine), 1);
    shown.selection = new vscode.Selection(position, position);
    shown.revealRange(new vscode.Range(position, position), vscode.TextEditorRevealType.InCenter);
}

async function generateGetterSetter(): Promise<void> {
    const editor = vscode.window.activeTextEditor;
    if (!editor) { return; }
    const document = editor.document;
    const line = editor.selection.active.line;
    let lineText = document.lineAt(line).text;
    // Cursor on the UPROPERTY(...) line: use the declaration below it.
    let declarationLine = line;
    if (lineText.trim().startsWith('UPROPERTY') && !lineText.includes(';') && line + 1 < document.lineCount) {
        declarationLine = line + 1;
        lineText = document.lineAt(declarationLine).text;
    }
    const indent = /^\s*/.exec(lineText)![0];
    const result = accessors(lineText, indent);
    if (typeof result === 'string') { void vscode.window.showWarningMessage(`Penguin: ${result}`); return; }
    await editor.edit(builder => builder.insert(document.lineAt(declarationLine).range.end, '\n' + result.text));
    vscode.window.setStatusBarMessage(`Penguin: generated Get${result.name}/Set${result.name}`, 4000);
}

/** Signature help after "(" / "," for indexed Unreal functions: declaration and docs from the index. */
class SignatureHelp implements vscode.SignatureHelpProvider {
    constructor(private readonly backend: Backend) {}

    async provideSignatureHelp(document: vscode.TextDocument, position: vscode.Position): Promise<vscode.SignatureHelp | undefined> {
        const text = document.getText(new vscode.Range(new vscode.Position(Math.max(0, position.line - 10), 0), position));
        let depth = 0, comma = 0, i = text.length - 1;
        for (; i >= 0; i--) {
            const c = text[i];
            if (c === ')') { depth++; }
            else if (c === '(') { if (depth === 0) { break; } depth--; }
            else if (c === ',' && depth === 0) { comma++; }
            else if (c === ';' || c === '{' || c === '}') { return undefined; }
        }
        if (i < 0) { return undefined; }
        const name = /(\w+)\s*$/.exec(text.slice(0, i))?.[1];
        if (!name || /^(if|for|while|switch|return|sizeof|UPROPERTY|UFUNCTION|UCLASS|USTRUCT|UENUM|TEXT)$/.test(name)) { return undefined; }
        let symbol;
        try { symbol = await findSymbol(this.backend, name, 'function'); } catch { return undefined; }
        if (!symbol || symbol.kind !== 'function' || !symbol.signature) { return undefined; }
        const info = new vscode.SignatureInformation(symbol.signature, symbol.documentation ? new vscode.MarkdownString(symbol.documentation) : undefined);
        const open = symbol.signature.indexOf('(');
        const close = symbol.signature.lastIndexOf(')');
        if (open >= 0 && close > open) {
            const params = symbol.signature.slice(open + 1, close);
            let depthP = 0, start = 0;
            const ranges: Array<[number, number]> = [];
            for (let k = 0; k <= params.length; k++) {
                const c = params[k];
                if (c === '(' || c === '<') { depthP++; } else if (c === ')' || c === '>') { depthP--; }
                if ((c === ',' && depthP === 0) || k === params.length) {
                    const raw = params.slice(start, k);
                    const lead = raw.length - raw.trimStart().length;
                    if (raw.trim()) { ranges.push([open + 1 + start + lead, open + 1 + start + lead + raw.trim().length]); }
                    start = k + 1;
                }
            }
            info.parameters = ranges.map(range => new vscode.ParameterInformation(range));
        }
        const help = new vscode.SignatureHelp();
        help.signatures = [info];
        help.activeSignature = 0;
        help.activeParameter = Math.min(comma, Math.max(0, info.parameters.length - 1));
        return help;
    }
}

export function registerGenerators(context: vscode.ExtensionContext, backend: Backend): void {
    const run = (body: () => Promise<void>) => async () => { try { await body(); } catch (error) { backend.report(error); } };
    context.subscriptions.push(
        vscode.commands.registerCommand('penguin.generateImplementation', run(generateImplementation)),
        vscode.commands.registerCommand('penguin.generateGetterSetter', run(generateGetterSetter)),
        vscode.languages.registerSignatureHelpProvider([{ language: 'cpp' }, { language: 'c' }], new SignatureHelp(backend), '(', ','));
}
