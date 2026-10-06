import * as vscode from 'vscode';
import * as path from 'node:path';
import { Backend, Session } from './backend';
import { aiResult, JobDto, plainError, StaleSessionError, SymbolDto, symbolText } from './protocol';
import { CancelledError, pollJob } from './jobs';

// QuickPick labels use codicon syntax; remove that syntax rather than letting backend text decorate UI.
function label(text: string): string { return text.replace(/\$\(/g, '(').replace(/[\r\n]/g, ' '); }
function assertSession(session: Session): void { session.protocol.assertCurrent(); }
async function preview(session: Session, content: string): Promise<void> {
    assertSession(session);
    // Untitled, plain text, separate document: no workspace edit, save, command execution or HTML.
    const document = await vscode.workspace.openTextDocument({ language: 'plaintext', content });
    assertSession(session);
    await vscode.window.showTextDocument(document, { viewColumn: vscode.ViewColumn.Beside, preview: false });
}
async function pickSymbol(session: Session, prompt = 'Search Unreal symbols', initial = ''): Promise<SymbolDto | undefined> {
    const query = await vscode.window.showInputBox({ title: 'Penguin', prompt, value: initial,
        validateInput: value => Buffer.byteLength(value, 'utf8') > 512 ? 'Use at most 512 UTF-8 bytes.' : undefined });
    if (query === undefined) { return undefined; }
    assertSession(session);
    const symbols = await session.protocol.symbols(query);
    if (!symbols.length) { await vscode.window.showInformationMessage('Penguin: No symbols found. Initial indexing may still be in progress.'); return undefined; }
    const selected = await vscode.window.showQuickPick(symbols.map(symbol => ({
        label: label(symbol.qualifiedName ?? symbol.name), description: label(symbol.kind),
        detail: label(`${symbol.file}:${symbol.line}`), symbol
    })), { title: 'Penguin symbols', placeHolder: 'Select a symbol (at most 100 results; refine the query if needed)', matchOnDescription: true, matchOnDetail: true });
    assertSession(session);
    return selected?.symbol;
}
async function navigate(session: Session, symbol: SymbolDto): Promise<void> {
    assertSession(session);
    let uri: vscode.Uri;
    if (symbol.file.startsWith('untitled:')) {
        uri = vscode.Uri.parse(symbol.file);
        if (!vscode.workspace.textDocuments.some(document => document.uri.toString() === uri.toString())) {
            throw new Error('The symbol belongs to an untitled document that is no longer open. Refresh the search.');
        }
    } else {
        if (!path.isAbsolute(symbol.file)) { throw new Error('Backend symbol location is not an absolute native path.'); }
        uri = vscode.Uri.file(symbol.file);
    }
    const document = await vscode.workspace.openTextDocument(uri);
    assertSession(session);
    const editor = await vscode.window.showTextDocument(document, { preview: true });
    assertSession(session);
    const position = new vscode.Position(Math.max(0, Math.min(symbol.line - 1, document.lineCount - 1)), 0);
    editor.selection = new vscode.Selection(position, position);
    editor.revealRange(new vscode.Range(position, position), vscode.TextEditorRevealType.InCenterIfOutsideViewport);
}
async function browseInheritance(session: Session, name: string): Promise<void> {
    const tree = await session.protocol.inheritance(name);
    const items = [
        ...tree.bases.map(base => ({ label: label(base), description: 'Base (resolve by name)', base, symbol: undefined as SymbolDto | undefined })),
        ...tree.derived.map(symbol => ({ label: label(symbol.qualifiedName ?? symbol.name), description: 'Derived', base: undefined as string | undefined, symbol }))
    ];
    if (!items.length) { await vscode.window.showInformationMessage('Penguin: No indexed inheritance relationships. Unresolved bases are not guessed.'); return; }
    const selected = await vscode.window.showQuickPick(items, { title: `Penguin inheritance: ${label(name)}` });
    assertSession(session);
    if (!selected) { return; }
    const symbol = selected.symbol ?? await pickSymbol(session, 'Resolve this base by name', selected.base);
    if (symbol) { await navigate(session, await session.protocol.symbol(symbol.id)); }
}
async function symbolCommand(backend: Backend, mode: 'search' | 'details' | 'inheritance'): Promise<void> {
    const session = backend.getSession();
    const selected = await pickSymbol(session);
    if (!selected) { return; }
    const symbol = await session.protocol.symbol(selected.id);
    if (mode === 'details') { await preview(session, symbolText(symbol)); return; }
    if (mode === 'inheritance') { await browseInheritance(session, symbol.qualifiedName ?? symbol.name); return; }
    const action = await vscode.window.showQuickPick(['Go to Definition', 'Show Details', 'Browse Inheritance'], { title: label(symbol.name) });
    assertSession(session);
    if (action === 'Go to Definition') { await navigate(session, symbol); }
    if (action === 'Show Details') { await preview(session, symbolText(symbol)); }
    if (action === 'Browse Inheritance') { await browseInheritance(session, symbol.qualifiedName ?? symbol.name); }
}
async function runJob(backend: Backend, session: Session, title: string, start: () => Promise<JobDto>): Promise<JobDto> {
    if (session.jobs.size >= 4) { throw new Error('At most four Penguin commands can run at once. Cancel or wait for a job first.'); }
    const controller = new AbortController();
    session.jobs.add(controller);
    try {
        return await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title, cancellable: true }, async (progress, token) => {
            const registration = token.onCancellationRequested(() => controller.abort());
            if (token.isCancellationRequested) { controller.abort(); }
            try {
                assertSession(session);
                if (controller.signal.aborted) { throw new CancelledError(); }
                // Once submitted, wait for the prompt Job response so cancellation can address its ID.
                const initial = await start();
                return await pollJob(session.protocol, initial, { signal: controller.signal, current: session.current,
                    log: message => backend.output.appendLine(message), progress: job => progress.report({ message: job.state }) });
            } finally { registration.dispose(); }
        });
    } finally { session.jobs.delete(controller); }
}
async function aiCommand(backend: Backend, task: 'explain' | 'generate'): Promise<void> {
    const session = backend.getSession();
    if (!session.settings.ai.enabled || !session.settings.ai.model) {
        throw new Error('Local AI is disabled. Configure an explicitly installed penguin.ai.model and loopback penguin.ai.endpoint, then enable penguin.ai.enabled. Penguin never starts a service or downloads a model.');
    }
    const editor = vscode.window.activeTextEditor;
    if (task === 'explain' && (!editor || editor.selection.isEmpty)) { throw new Error('Select C++ source text before running Explain Selection.'); }
    const source = editor && !editor.selection.isEmpty ? editor.document.getText(editor.selection) : '';
    if (Buffer.byteLength(source, 'utf8') > 64 * 1024) { throw new Error('Select at most 64 KiB of source for local AI.'); }
    let instruction: string | undefined;
    if (task === 'generate') {
        instruction = await vscode.window.showInputBox({ title: 'Penguin: Generate Preview', prompt: 'Describe the code to generate. Only selected source is sent as context; no files are automatically changed.',
            validateInput: text => !text.trim() ? 'Enter an instruction.' : Buffer.byteLength(text, 'utf8') > 4096 ? 'Use at most 4096 UTF-8 bytes.' : undefined });
        if (instruction === undefined) { return; }
    }
    assertSession(session);
    const job = await runJob(backend, session, task === 'explain' ? 'Penguin: Explaining selection' : 'Penguin: Generating preview',
        () => session.protocol.ai(task, source, instruction));
    assertSession(session);
    const result = aiResult(job.result);
    await preview(session, `Penguin local AI ${task} preview\nModel: ${result.model}\nUntrusted generated content: review before using. Nothing has been applied or executed.\n\n${result.text}`);
}
export function registerCommands(context: vscode.ExtensionContext, backend: Backend): void {
    const register = (name: string, run: () => Promise<unknown> | unknown): void => {
        context.subscriptions.push(vscode.commands.registerCommand(`penguin.${name}`, async () => {
            try { if (name !== 'showOutput' && !vscode.workspace.isTrusted) { throw new Error('Trust this workspace before using Penguin commands.'); } await run(); }
            catch (error) {
                if (error instanceof CancelledError || error instanceof StaleSessionError) {
                    backend.output.appendLine(plainError(error));
                    await vscode.window.showInformationMessage(plainError(error));
                } else { backend.report(error); }
            }
        }));
    };
    register('showOutput', () => backend.output.show(true));
    register('restart', () => backend.restart());
    register('status', async () => {
        const session = backend.getSession();
        const status = await session.protocol.status();
        backend.updateStatus(status);
        backend.output.appendLine(JSON.stringify(status, null, 2));
        backend.output.show(true);
    });
    register('reindex', async () => {
        const session = backend.getSession();
        const result = await runJob(backend, session, 'Penguin: Reindexing', () => session.protocol.reindex());
        assertSession(session);
        backend.output.appendLine('Reindex completed: ' + JSON.stringify(result.result));
        backend.updateStatus(await session.protocol.status());
        await vscode.window.showInformationMessage('Penguin reindex finished. Counts and any index errors are in Penguin Output.', 'Show Output').then(choice => {
            if (choice) { backend.output.show(true); }
        });
    });
    register('searchSymbols', () => symbolCommand(backend, 'search'));
    register('symbolDetails', () => symbolCommand(backend, 'details'));
    register('inheritance', () => symbolCommand(backend, 'inheritance'));
    register('explainSelection', () => aiCommand(backend, 'explain'));
    register('generatePreview', () => aiCommand(backend, 'generate'));
    register('cancelJobs', () => {
        const session = backend.getSession();
        session.jobs.forEach(job => job.abort());
        backend.output.appendLine(`Cancellation requested for ${session.jobs.size} active command(s).`);
    });
}
