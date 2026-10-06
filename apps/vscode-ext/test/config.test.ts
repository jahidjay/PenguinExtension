import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as path from 'node:path';
import { readSettings, binaryPath, initializationOptions, validateEndpoint, ConfigurationReader } from '../src/config';
const reader = (values: Record<string, unknown> = {}): ConfigurationReader => ({ get: <T>(key: string, fallback: T): T => (values[key] ?? fallback) as T });
test('untrusted workspaces never read settings', () => {
    let read = false;
    assert.throws(() => readSettings(false, () => { read = true; return reader(); }), /Trust/);
    assert.equal(read, false);
});
test('initialization uses fixed penguin envelope and explicitly disabled AI', () => {
    const settings = readSettings(true, () => reader());
    assert.deepEqual(initializationOptions(settings), { penguin: { adapter: 'vscode', engineRoots: [], style: {}, ai: { enabled: false, endpoint: 'http://127.0.0.1:11434', model: 'qwen2.5-coder:3b' } } });
});
test('documented default is configured but inference stays opt-in', () => {
    assert.equal(readSettings(true, () => reader()).ai.enabled, false);
    assert.equal(readSettings(true, () => reader({ 'ai.enabled': true })).ai.model, 'qwen2.5-coder:3b');
    assert.throws(() => readSettings(true, () => reader({ 'ai.enabled': true, 'ai.model': '' })), /explicitly installed/);
    for (const model of ['m'.repeat(129), 'model with space', 'model?key=x', '../model']) {
        assert.throws(() => readSettings(true, () => reader({ 'ai.enabled': true, 'ai.model': model })), /Invalid local model/);
    }
    assert.equal(readSettings(true, () => reader({ 'ai.enabled': true, 'ai.model': 'local-code:3b' })).ai.model, 'local-code:3b');
});
test('endpoint only accepts loopback without secrets', () => {
    for (const url of ['http://localhost:11434', 'http://127.0.0.1:8080/', 'http://127.9.8.7', 'http://[::1]:8443', 'http://[0:0:0:0:0:0:0:1]']) { assert.doesNotThrow(() => validateEndpoint(url)); }
    for (const url of ['https://example.com', 'https://localhost', 'file:///model', 'http://localhost.evil', 'http://localhost@evil.com', 'http://u:p@localhost', 'http://localhost?key=x', 'http://localhost#x', 'http://localhost/api', 'http://localhost//', 'http://localhost:0', 'http://localhost:65536', 'http://127.1', 'http://127.000.0.1', 'http://2130706433', 'http://127.256.0.1', 'http://[::ffff:127.0.0.1]', 'http://[::2]', ' http://localhost', 'bad']) { assert.throws(() => validateEndpoint(url), url); }
});
test('binary path uses extension-host platform and architecture; absolute override', () => {
    const settings = readSettings(true, () => reader());
    assert.equal(binaryPath('/extension', settings, 'win32', 'x64'), path.join('/extension', 'bin', 'win32-x64', 'penguin-lsp.exe'));
    assert.equal(binaryPath('/extension', settings, 'linux', 'arm64'), path.join('/extension', 'bin', 'linux-arm64', 'penguin-lsp'));
    const override = path.resolve('trusted-server');
    assert.equal(binaryPath('/extension', { ...settings, serverPath: override }), override);
    assert.throws(() => readSettings(true, () => reader({ 'server.path': './relative' })), /absolute/);
    assert.throws(() => readSettings(true, () => reader({ engineRoots: ['relative'] })), /absolute/);
});
test('style is forwarded as opaque configuration and roots are deduplicated', () => {
    const root = path.resolve('Engine');
    const settings = readSettings(true, () => reader({ engineRoots: [root, root], style: { customRule: true } }));
    assert.deepEqual(settings.engineRoots, [root]);
    assert.deepEqual(initializationOptions(settings).penguin.style, { customRule: true });
});
