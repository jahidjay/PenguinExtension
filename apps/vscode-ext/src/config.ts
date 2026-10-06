import * as path from 'node:path';
import { isIP } from 'node:net';

export interface Settings {
    serverPath: string;
    engineRoots: string[];
    style: Record<string, unknown>;
    ai: { enabled: boolean; endpoint: string; model: string };
}
export interface ConfigurationReader { get<T>(key: string, fallback: T): T }

// Trust is checked before even invoking the settings reader.
export function readSettings(trusted: boolean, reader: () => ConfigurationReader): Settings {
    if (!trusted) { throw new Error('Trust this workspace before using Penguin settings or starting its backend.'); }
    const config = reader();
    const serverPath = config.get<unknown>('server.path', '');
    const engineRoots = config.get<unknown>('engineRoots', []);
    const style = config.get<unknown>('style', {});
    const enabled = config.get<unknown>('ai.enabled', false);
    const endpoint = config.get<unknown>('ai.endpoint', 'http://127.0.0.1:11434');
    const model = config.get<unknown>('ai.model', 'qwen2.5-coder:3b');
    if (typeof serverPath !== 'string' || (serverPath !== '' && !path.isAbsolute(serverPath))) {
        throw new Error('penguin.server.path must be empty or an absolute executable path on this extension host.');
    }
    if (!Array.isArray(engineRoots) || engineRoots.some(root => typeof root !== 'string' || !path.isAbsolute(root))) {
        throw new Error('penguin.engineRoots must contain only absolute directory paths.');
    }
    if (!style || typeof style !== 'object' || Array.isArray(style)) { throw new Error('penguin.style must be an object.'); }
    if (typeof enabled !== 'boolean' || typeof endpoint !== 'string' || typeof model !== 'string') {
        throw new Error('Invalid penguin.ai configuration.');
    }
    validateEndpoint(endpoint);
    if (enabled && !model.trim()) { throw new Error('Set penguin.ai.model to an explicitly installed local model before enabling AI.'); }
    if (model !== '' && !/^[A-Za-z0-9][A-Za-z0-9_.:/-]{0,127}$/.test(model)) { throw new Error('Invalid local model name.'); }
    return { serverPath, engineRoots: [...new Set(engineRoots as string[])], style: style as Record<string, unknown>, ai: { enabled, endpoint, model: model.trim() } };
}
export function validateEndpoint(endpoint: string): void {
    // Match the backend's origin-only contract before URL normalization can hide
    // numeric IP aliases, whitespace, paths, or credentials.
    const error = () => new Error('penguin.ai.endpoint must be an HTTP loopback origin without credentials, path, query, or fragment.');
    if (endpoint.length > 256 || /[^\x21-\x7e]/.test(endpoint)) { throw error(); }
    const parts = /^http:\/\/(localhost|127(?:\.[0-9]{1,3}){3}|\[[0-9A-Fa-f:]+\])(?::([0-9]+))?\/?$/.exec(endpoint);
    if (!parts) { throw error(); }
    const host = parts[1];
    if (host === undefined) { throw error(); }
    if (host.startsWith('127.') && isIP(host) !== 4) { throw error(); }
    if (host.startsWith('[') && isIP(host.slice(1, -1)) !== 6) { throw error(); }
    if (parts[2] !== undefined && (Number(parts[2]) < 1 || Number(parts[2]) > 65535)) { throw error(); }
    let url: URL;
    try { url = new URL(endpoint); } catch { throw error(); }
    if (host.startsWith('[') && url.hostname !== '[::1]') { throw error(); }
}
export function binaryPath(extensionPath: string, settings: Settings, platform: string = process.platform, arch: string = process.arch): string {
    return settings.serverPath || path.join(extensionPath, 'bin', `${platform}-${arch}`, platform === 'win32' ? 'penguin-lsp.exe' : 'penguin-lsp');
}
export function initializationOptions(settings: Settings): { penguin: { adapter: 'vscode'; engineRoots: string[]; style: Record<string, unknown>; ai: Settings['ai'] } } {
    return { penguin: { adapter: 'vscode', engineRoots: settings.engineRoots, style: settings.style, ai: settings.ai } };
}
