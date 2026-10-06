import * as vscode from 'vscode';
import { Backend } from './backend';
import { registerCommands } from './commands';
let backend: Backend | undefined;
export async function activate(context: vscode.ExtensionContext): Promise<void> {
    const output = vscode.window.createOutputChannel('Penguin');
    const status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 10);
    status.name = 'Penguin backend status';
    status.command = 'penguin.status';
    status.text = 'Penguin: starting';
    status.show();
    context.subscriptions.push(output, status);
    backend = new Backend(context, output, status);
    registerCommands(context, backend);
    await backend.restart();
}
export async function deactivate(): Promise<void> {
    const active = backend;
    backend = undefined;
    await active?.dispose();
}
