import * as vscode from 'vscode';
import * as fs from 'node:fs/promises';
import { constants } from 'node:fs';
import { spawn, ChildProcessWithoutNullStreams } from 'node:child_process';
import { LanguageClient, LanguageClientOptions, InitializeParams, Message, MessageReader, MessageWriter,
    StreamMessageReader, StreamMessageWriter, ErrorAction, CloseAction } from 'vscode-languageclient/node';
import { binaryPath, initializationOptions, readSettings, Settings } from './config';
import { assertCapabilities, plainError, Protocol, StaleSessionError, StatusDto, object } from './protocol';
import { bounded } from './jobs';
import { ChangeBatcher, insideRoots, isWatchable, watchGlob } from './watcher';
import { OrderedLifecycle } from './lifecycle';
import { WireIds } from './wire';

class SnapshotClient extends LanguageClient {
    folders: vscode.WorkspaceFolder[] = [];
    protected override fillInitializeParams(params: InitializeParams): void {
        super.fillInitializeParams(params);
        params.workspaceFolders = this.folders.map(folder => ({ uri: folder.uri.toString(), name: folder.name }));
        params.rootUri = this.folders[0]?.uri.toString() ?? null;
        params.rootPath = this.folders[0]?.uri.fsPath ?? null;
        params.capabilities.general = { ...params.capabilities.general, positionEncodings: ['utf-16'] };
        if (params.capabilities.workspace?.didChangeWatchedFiles) {
            params.capabilities.workspace.didChangeWatchedFiles.dynamicRegistration = false;
        }
    }
}
export interface Session {
    client: LanguageClient; protocol: Protocol; settings: Settings; roots: string[];
    current: () => boolean; jobs: Set<AbortController>;
}
interface OwnedSession extends Session {
    alive: boolean; ready: boolean; watchers: vscode.Disposable[]; process: ChildProcessWithoutNullStreams | undefined;
    statusTimer: NodeJS.Timeout | undefined;
}
export class Backend {
    private session: OwnedSession | undefined;
    private readonly lifecycle: OrderedLifecycle;
    private readonly listeners: vscode.Disposable[] = [];
    constructor(private readonly context: vscode.ExtensionContext, readonly output: vscode.OutputChannel,
        private readonly statusBar: vscode.StatusBarItem) {
        this.lifecycle = new OrderedLifecycle(() => this.stop(), current => this.start(current), error => this.report(error));
        this.listeners.push(vscode.workspace.onDidChangeWorkspaceFolders(() => { void this.restart(); }),
            vscode.workspace.onDidChangeConfiguration(event => { if (event.affectsConfiguration('penguin')) { void this.restart(); } }),
            vscode.workspace.onDidGrantWorkspaceTrust(() => { void this.restart(); }));
    }
    restart(): Promise<void> { return this.lifecycle.restart(); }
    getSession(): Session {
        if (!vscode.workspace.isTrusted) { throw new Error('Trust this workspace before using Penguin.'); }
        const session = this.session;
        if (!session?.current() || !session.ready || !session.client.isRunning()) { throw new Error('Penguin backend is unavailable. Open Penguin Output or run Penguin: Restart Backend.'); }
        return session;
    }
    report(error: unknown): void {
        const message = plainError(error);
        this.output.appendLine(`[error] ${message}`);
        this.statusBar.text = 'Penguin: error';
        this.statusBar.tooltip = message;
        void vscode.window.showErrorMessage(`Penguin: ${message}`, 'Show Output').then(action => {
            if (action === 'Show Output') { this.output.show(true); }
        });
    }
    updateStatus(status: StatusDto): void {
        this.statusBar.text = `Penguin: ${status.state}`;
        this.statusBar.tooltip = `${status.files} files, ${status.symbols} symbols${status.message ? '\n' + status.message : ''}`;
    }
    private async start(generationCurrent: () => boolean): Promise<void> {
        if (!vscode.workspace.isTrusted) {
            this.statusBar.text = 'Penguin: trust required';
            this.statusBar.tooltip = 'Trust the workspace to enable Penguin. No settings read or process started.';
            return;
        }
        const folders = [...(vscode.workspace.workspaceFolders ?? [])];
        if (folders.some(folder => folder.uri.scheme !== 'file')) { throw new Error('Penguin requires filesystem workspace folders on this extension host.'); }
        const settings = readSettings(vscode.workspace.isTrusted, () => vscode.workspace.getConfiguration('penguin'));
        const roots = [...new Set([...folders.map(folder => folder.uri.fsPath), ...settings.engineRoots])];
        if (!roots.length) { this.statusBar.text = 'Penguin: open a folder'; this.statusBar.tooltip = 'Open a project folder or configure explicit engine roots.'; return; }
        if (roots.length > 16) { throw new Error('Penguin supports at most 16 workspace and engine roots.'); }
        const command = binaryPath(this.context.extensionPath, settings);
        try {
            const stat = await fs.stat(command);
            if (!stat.isFile()) { throw new Error('not a regular file'); }
            await fs.access(command, process.platform === 'win32' ? constants.F_OK : constants.X_OK);
        } catch {
            throw new Error(`Backend executable unavailable: ${command}. Package bin/${process.platform}-${process.arch}/penguin-lsp${process.platform === 'win32' ? '.exe' : ''}, or set penguin.server.path to a trusted absolute executable path. No fallback was started.`);
        }
        for (const root of roots) { if (!(await fs.stat(root)).isDirectory()) { throw new Error(`Penguin root is not a directory: ${root}`); } }
        if (!generationCurrent() || !vscode.workspace.isTrusted) { return; }
        this.statusBar.text = 'Penguin: starting';
        let owned: OwnedSession;
        const current = (): boolean => generationCurrent() && vscode.workspace.isTrusted && !!owned?.alive;
        const plainMarkdown = (value: vscode.MarkdownString): vscode.MarkdownString => {
            const safe = new vscode.MarkdownString();
            safe.appendText(value.value);
            safe.isTrusted = false;
            safe.supportHtml = false;
            return safe;
        };
        const completion = (item: vscode.CompletionItem): vscode.CompletionItem => {
            if (item.documentation instanceof vscode.MarkdownString) { item.documentation = plainMarkdown(item.documentation); }
            // This server contract provides completion data, not executable commands.
            delete item.command;
            return item;
        };
        const options: LanguageClientOptions = {
            documentSelector: [{ language: 'cpp', scheme: 'file' }, { language: 'cpp', scheme: 'untitled' },
                { language: 'c', scheme: 'file', pattern: '**/*.{h,hpp,hh,hxx}' }, { language: 'c', scheme: 'untitled' }],
            initializationOptions: initializationOptions(settings), outputChannel: this.output,
            revealOutputChannelOn: 4,
            initializationFailedHandler: () => false,
            errorHandler: {
                error: error => { if (current()) { this.report(error); } return { action: ErrorAction.Shutdown }; },
                closed: () => { if (current()) { this.report(new Error('The backend exited unexpectedly. Run Penguin: Restart Backend; no automatic legacy fallback is used.')); this.invalidate(owned); } return { action: CloseAction.DoNotRestart }; }
            },
            middleware: {
                provideHover: async (document, position, token, next) => {
                    const result = await next(document, position, token);
                    if (result) { result.contents = result.contents.map(content => content instanceof vscode.MarkdownString ? plainMarkdown(content) : content); }
                    return result;
                },
                provideCompletionItem: async (document, position, context, token, next) => {
                    const result = await next(document, position, context, token);
                    if (Array.isArray(result)) { return result.map(completion); }
                    if (result) { result.items = result.items.map(completion); }
                    return result;
                },
                resolveCompletionItem: async (item, token, next) => {
                    const result = await next(item, token);
                    return result ? completion(result) : result;
                },
                workspace: {
                // Root/config changes are applied only by an ordered stop/start, never mid-session.
                didChangeWorkspaceFolders: async () => {},
                didChangeConfiguration: async () => {},
                workspaceFolders: async () => folders.map(folder => ({ uri: folder.uri.toString(), name: folder.name })),
                configuration: async params => params.items.map(item => {
                    if (!current()) { return null; }
                    const snapshot = initializationOptions(settings);
                    if (!item.section) { return snapshot; }
                    if (item.section === 'penguin') { return snapshot.penguin; }
                    if (item.section === 'penguin.style') { return settings.style; }
                    if (item.section === 'penguin.ai') { return settings.ai; }
                    if (item.section === 'penguin.engineRoots') { return settings.engineRoots; }
                    return null;
                })
            } }
        };
        const client = new SnapshotClient('penguin', 'Penguin', async () => {
            if (!current()) { throw new StaleSessionError(); }
            const child = spawn(command, [], { cwd: roots[0], shell: false, windowsHide: true, stdio: 'pipe' });
            owned.process = child;
            child.stderr.setEncoding('utf8');
            child.stderr.on('data', (text: string) => this.output.append(text));
            await new Promise<void>((resolve, reject) => { child.once('spawn', resolve); child.once('error', reject); });
            child.on('error', error => { if (current()) { this.report(error); } });
            const wire = new WireIds();
            const reader = new StreamMessageReader(child.stdout);
            const writer = new StreamMessageWriter(child.stdin);
            const adaptedReader: MessageReader = {
                onError: reader.onError, onClose: reader.onClose, onPartialMessage: reader.onPartialMessage,
                listen: callback => reader.listen(message => {
                    const converted = wire.inbound(message as unknown as Record<string, unknown>);
                    if (converted.method === 'textDocument/publishDiagnostics' && object(converted.params)) {
                        if (!current()) { return; }
                        const params = converted.params;
                        if (typeof params.version === 'number') {
                            const document = vscode.workspace.textDocuments.find(doc => doc.uri.toString() === params.uri);
                            if (params.version < 0 || (document && params.version < document.version)) { return; }
                        }
                    }
                    callback(converted as unknown as Message);
                }),
                dispose: () => reader.dispose()
            };
            const adaptedWriter: MessageWriter = {
                onError: writer.onError, onClose: writer.onClose,
                write: message => writer.write(wire.outbound(message as unknown as Record<string, unknown>) as unknown as Message),
                end: () => writer.end(), dispose: () => writer.dispose()
            };
            return { reader: adaptedReader, writer: adaptedWriter };
        }, options);
        client.folders = folders;
        const protocol = new Protocol({ request: async (method, params, signal) => {
            const token = new vscode.CancellationTokenSource();
            try {
                return await bounded(client.sendRequest<unknown>(method, params, token.token), 10_000, signal, () => token.cancel());
            } finally { token.dispose(); }
        } }, current);
        owned = { client, protocol, settings, roots, current, alive: true, ready: false, jobs: new Set(), watchers: [], process: undefined, statusTimer: undefined };
        this.session = owned;
        try {
            await bounded(client.start(), 20_000);
            if (!current()) { await this.stop(); return; }
            assertCapabilities(client.initializeResult?.capabilities);
            this.updateStatus(await protocol.status());
            if (!current()) { await this.stop(); return; }
            owned.ready = true;
            this.installWatchers(owned);
            this.scheduleStatus(owned);
            this.output.appendLine(`Penguin protocol v1 started (${process.platform}-${process.arch}); roots: ${roots.join(', ')}`);
        } catch (error) { await this.stop(); throw error; }
    }
    private installWatchers(session: OwnedSession): void {
        const batcher = new ChangeBatcher(async changes => {
            if (session.current()) { await bounded(session.client.sendNotification('workspace/didChangeWatchedFiles', { changes }), 10_000); }
        }, error => { if (session.current()) { this.report(error); } });
        session.watchers.push(batcher);
        for (const root of session.roots) {
            const watcher = vscode.workspace.createFileSystemWatcher(new vscode.RelativePattern(root, watchGlob));
            const event = (uri: vscode.Uri, type: 1 | 2 | 3): void => {
                if (session.current() && uri.scheme === 'file' && isWatchable(uri.fsPath) && insideRoots(uri.fsPath, session.roots)) {
                    batcher.add(uri.toString(), type);
                }
            };
            session.watchers.push(watcher, watcher.onDidCreate(uri => event(uri, 1)), watcher.onDidChange(uri => event(uri, 2)), watcher.onDidDelete(uri => event(uri, 3)));
        }
    }
    private scheduleStatus(session: OwnedSession): void {
        session.statusTimer = setTimeout(() => {
            if (!session.current()) { return; }
            void session.protocol.status().then(status => {
                if (session.current()) { this.updateStatus(status); this.scheduleStatus(session); }
            }).catch(error => { if (session.current()) { this.report(error); } });
        }, 5000);
    }
    private invalidate(session: OwnedSession): void {
        session.alive = false;
        session.ready = false;
        session.jobs.forEach(job => job.abort());
        session.watchers.forEach(watcher => watcher.dispose());
        session.watchers.length = 0;
        if (session.statusTimer) { clearTimeout(session.statusTimer); session.statusTimer = undefined; }
    }
    private async stop(): Promise<void> {
        const session = this.session;
        if (!session) { return; }
        this.session = undefined;
        this.invalidate(session);
        try { await bounded(session.client.stop(2000), 3000); }
        catch (error) { this.output.appendLine(`Shutdown: ${plainError(error)}`); }
        finally {
            const child = session.process;
            if (child && child.exitCode === null && child.signalCode === null) {
                await new Promise<void>(resolve => {
                    const timer = setTimeout(() => { child.removeListener('exit', finish); resolve(); }, 500);
                    const finish = (): void => { clearTimeout(timer); resolve(); };
                    child.once('exit', finish);
                });
                if (child.exitCode === null && child.signalCode === null) { child.kill('SIGKILL'); this.output.appendLine('Stopped a backend that did not exit after shutdown.'); }
            }
            try { await bounded(session.client.dispose(), 3000); }
            catch (error) { this.output.appendLine(`Client disposal: ${plainError(error)}`); }
        }
    }
    async dispose(): Promise<void> { this.listeners.forEach(listener => listener.dispose()); await this.lifecycle.dispose(); }
}
