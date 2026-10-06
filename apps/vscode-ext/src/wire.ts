import { object } from './protocol';
let requestSequence = 0;
let documentSequence = 0;
// languageclient owns all synchronization. Rewrite only wire identifiers, not content or ranges,
// because VS Code keeps document versions and languageclient restarts its RPC counter per client.
export class WireIds {
    private readonly outgoing = new Map<string | number, number>();
    private readonly incoming = new Map<number, string | number>();
    private readonly versions = new Map<string, Map<number, number>>();
    outbound(message: Record<string, unknown>): Record<string, unknown> {
        let result = message;
        if (typeof message.method === 'string' && (typeof message.id === 'number' || typeof message.id === 'string')) {
            const id = ++requestSequence;
            this.outgoing.set(message.id, id); this.incoming.set(id, message.id);
            result = { ...message, id };
        }
        if (message.method === '$/cancelRequest' && object(message.params)) {
            const id = message.params.id;
            if (typeof id === 'number' || typeof id === 'string') {
                result = { ...result, params: { ...message.params, id: this.outgoing.get(id) ?? id } };
            }
        }
        if (['textDocument/didOpen', 'textDocument/didChange'].includes(String(message.method)) && object(message.params) && object(message.params.textDocument)) {
            const document = message.params.textDocument;
            if (typeof document.uri === 'string' && typeof document.version === 'number') {
                const version = ++documentSequence;
                const versions = this.versions.get(document.uri) ?? new Map<number, number>();
                versions.set(version, document.version);
                if (versions.size > 256) { const first = versions.keys().next().value; if (first !== undefined) { versions.delete(first); } }
                this.versions.set(document.uri, versions);
                result = { ...result, params: { ...message.params, textDocument: { ...document, version } } };
            }
        }
        if (message.method === 'textDocument/didClose' && object(message.params) && object(message.params.textDocument) && typeof message.params.textDocument.uri === 'string') {
            this.versions.delete(message.params.textDocument.uri);
        }
        return result;
    }
    inbound(message: Record<string, unknown>): Record<string, unknown> {
        if (message.method === undefined && typeof message.id === 'number') {
            const id = this.incoming.get(message.id);
            if (id !== undefined) { this.incoming.delete(message.id); this.outgoing.delete(id); return { ...message, id }; }
        }
        if (message.method === 'textDocument/publishDiagnostics' && object(message.params) && typeof message.params.uri === 'string' && typeof message.params.version === 'number') {
            const version = this.versions.get(message.params.uri)?.get(message.params.version);
            // Old diagnostics outside the retained window must not become current diagnostics.
            return { ...message, params: { ...message.params, version: version ?? -1 } };
        }
        return message;
    }
}
