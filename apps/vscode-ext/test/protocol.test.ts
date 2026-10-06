import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Protocol, Rpc, assertCapabilities, symbolDto, symbolText, StatusDto, JobDto, StaleSessionError, aiResult } from '../src/protocol';
const status: StatusDto = { protocolVersion: 1, sessionId: 's1', state: 'ready', roots: ['/project'], files: 1, symbols: 1 };
const symbol = { id: 'opaque', name: 'AActor', kind: 'class', macroName: 'UCLASS', file: '/project/Actor.h', line: 4, bases: [], specifiers: [] };
const job: JobDto = { id: 'j1', sessionId: 's1', kind: 'reindex', state: 'queued' };
class MockRpc implements Rpc {
    calls: { method: string; params: object }[] = [];
    constructor(public values: Record<string, unknown>) {}
    async request(method: string, params: object): Promise<unknown> { this.calls.push({ method, params }); return this.values[method]; }
}
test('custom client sends exactly the protocol-v1 method and DTO envelopes', async () => {
    const rpc = new MockRpc({ 'penguin/status': status, 'penguin/symbols': [symbol], 'penguin/symbol': symbol,
        'penguin/inheritance': { bases: ['UObject'], derived: [symbol] }, 'penguin/reindex': job, 'penguin/job': job,
        'penguin/cancelJob': { cancelled: true }, 'penguin/ai': { ...job, kind: 'ai' } });
    const client = new Protocol(rpc, () => true);
    await client.status(); await client.symbols('Actor'); await client.symbol('opaque'); await client.inheritance('AActor');
    await client.reindex(); await client.job('j1'); await client.cancelJob('j1'); await client.ai('explain', 'selected'); await client.ai('generate', '', 'make code');
    assert.deepEqual(rpc.calls, [
        { method: 'penguin/status', params: {} }, { method: 'penguin/symbols', params: { query: 'Actor', limit: 100 } },
        { method: 'penguin/symbol', params: { id: 'opaque' } }, { method: 'penguin/inheritance', params: { name: 'AActor' } },
        { method: 'penguin/reindex', params: {} }, { method: 'penguin/job', params: { id: 'j1' } },
        { method: 'penguin/cancelJob', params: { id: 'j1' } },
        { method: 'penguin/ai', params: { task: 'explain', source: 'selected' } },
        { method: 'penguin/ai', params: { task: 'generate', source: '', instruction: 'make code' } }
    ]);
});
test('stale and expired references are errors, not invented metadata', async () => {
    const rpc = new MockRpc({ 'penguin/status': status, 'penguin/symbol': null, 'penguin/job': null });
    const client = new Protocol(rpc, () => true);
    await client.status();
    await assert.rejects(client.symbol('old'), /expired/);
    await assert.rejects(client.job('old'), /expired/);
    assert.throws(() => symbolDto({ ...symbol, line: 0 }), /invalid/);
    assert.equal(symbolDto(symbol).documentation, undefined);
    assert.equal(symbolDto({ ...symbol, documentation: null }).documentation, null);
});
test('in-flight responses cannot cross a generation or server session', async () => {
    let current = true;
    const client = new Protocol({ request: async () => { current = false; return status; } }, () => current);
    await assert.rejects(client.status(), StaleSessionError);
    const rpc = new MockRpc({ 'penguin/status': status });
    const stable = new Protocol(rpc, () => true); await stable.status();
    rpc.values['penguin/status'] = { ...status, sessionId: 's2' };
    await assert.rejects(stable.status(), StaleSessionError);
    rpc.values['penguin/reindex'] = { ...job, sessionId: 's2' };
    await assert.rejects(stable.reindex(), StaleSessionError);
});
test('capabilities require explicit v1 FULL UTF-16 support', () => {
    const capabilities = { experimental: { penguin: { protocolVersion: 1 } }, textDocumentSync: { change: 1, openClose: true }, positionEncoding: 'utf-16' };
    assert.doesNotThrow(() => assertCapabilities(capabilities));
    assert.doesNotThrow(() => assertCapabilities({ ...capabilities, textDocumentSync: 1 }));
    assert.throws(() => assertCapabilities({ ...capabilities, experimental: { penguin: { protocolVersion: 2 } } }), /Unsupported/);
    assert.throws(() => assertCapabilities({ ...capabilities, textDocumentSync: 2 }), /FULL/);
    assert.throws(() => assertCapabilities({ ...capabilities, positionEncoding: 'utf-8' }), /UTF-16/);
});
test('plain output preserves malicious markup as inert text and AI payloads are bounded', async () => {
    const html = '<script>alert(1)</script>[run](command:evil)';
    assert.ok(symbolText(symbolDto({ ...symbol, documentation: html })).includes(html));
    assert.deepEqual(aiResult({ text: html, model: 'local' }), { text: html, model: 'local' });
    assert.throws(() => aiResult({ text: 'x'.repeat(1024 * 1024 + 1), model: 'local' }), /invalid/);
    const client = new Protocol(new MockRpc({}), () => true);
    await assert.rejects(client.ai('explain', 'x'.repeat(65537)), /64 KiB/);
    await assert.rejects(client.symbols('x'.repeat(513)), /512/);
});

test('structured stale-reference errors tell the user to refresh the search', async () => {
    const client = new Protocol({ request: async () => { throw { data: { code: 'staleReference' }, message: 'gone' }; } }, () => true);
    await assert.rejects(client.symbol('expired'), /Search Unreal Symbols again/);
});
