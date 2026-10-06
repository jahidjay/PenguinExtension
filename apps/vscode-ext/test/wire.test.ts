import { test } from 'node:test';
import assert from 'node:assert/strict';
import { WireIds } from '../src/wire';
import { object } from '../src/protocol';
test('request ids stay unique across connections while responses and cancel use local ids', () => {
    const first = new WireIds(); const second = new WireIds();
    const a = first.outbound({ jsonrpc: '2.0', id: 0, method: 'initialize', params: {} });
    const b = second.outbound({ jsonrpc: '2.0', id: 0, method: 'initialize', params: {} });
    assert.notEqual(a.id, b.id);
    const cancel = first.outbound({ method: '$/cancelRequest', params: { id: 0 } });
    assert.deepEqual(cancel.params, { id: a.id });
    assert.equal(first.inbound({ jsonrpc: '2.0', id: a.id, result: {} }).id, 0);
    const serverResponse = { id: 'server-1', result: [] };
    assert.deepEqual(first.outbound(serverResponse), serverResponse);
    assert.equal(first.inbound({ id: 8, method: 'workspace/configuration' }).id, 8);
});
test('FULL open/change versions do not reset when client restarts; UTF-16 positions untouched', () => {
    const first = new WireIds(); const second = new WireIds();
    const params = { textDocument: { uri: 'file:///a.h', languageId: 'cpp', version: 1, text: 'hello' } };
    const a = first.outbound({ method: 'textDocument/didOpen', params });
    const b = second.outbound({ method: 'textDocument/didOpen', params });
    assert.ok(object(a.params) && object(a.params.textDocument));
    assert.ok(object(b.params) && object(b.params.textDocument));
    assert.notEqual(a.params.textDocument.version, b.params.textDocument.version);
    const position = { textDocument: { uri: 'file:///a.h' }, position: { line: 1, character: 7 } };
    assert.deepEqual(first.outbound({ id: 3, method: 'textDocument/definition', params: position }).params, position);
    const diagnostics = first.inbound({ method: 'textDocument/publishDiagnostics', params: { uri: 'file:///a.h', version: a.params.textDocument.version, diagnostics: [] } });
    assert.ok(object(diagnostics.params)); assert.equal(diagnostics.params.version, 1);
});
