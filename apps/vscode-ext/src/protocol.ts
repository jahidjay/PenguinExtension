export const methods = {
    status: 'penguin/status', symbols: 'penguin/symbols', symbol: 'penguin/symbol',
    inheritance: 'penguin/inheritance', reindex: 'penguin/reindex', job: 'penguin/job',
    cancelJob: 'penguin/cancelJob', ai: 'penguin/ai'
} as const;
export interface SymbolDto {
    id: string; name: string; kind: 'class' | 'struct' | 'interface' | 'enum' | 'function' | 'property' | 'delegate';
    macroName: string; file: string; line: number; typeName?: string | null;
    specifiers: { key: string; value?: string | null }[]; bases: string[];
    signature?: string | null; documentation?: string | null; owner?: string | null; qualifiedName?: string | null;
}
export interface StatusDto {
    protocolVersion: 1; sessionId: string; state: 'ready' | 'indexing' | 'busy' | 'unavailable' | 'stopping';
    roots: string[]; files: number; symbols: number; message?: string | null;
}
export interface JobDto {
    id: string; sessionId: string; kind: string;
    state: 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled'; result?: unknown; error?: unknown;
}
export interface InheritanceDto { bases: string[]; derived: SymbolDto[] }
export interface Rpc { request(method: string, params: object, signal?: AbortSignal): Promise<unknown> }
export class StaleSessionError extends Error {
    constructor() { super('The Penguin session changed. Refresh the search or retry the command.'); }
}
function invalid(name: string): never { throw new Error(`Penguin returned an invalid ${name} DTO. Check that the packaged backend implements protocol v1.`); }
export function object(value: unknown): value is Record<string, unknown> { return !!value && typeof value === 'object' && !Array.isArray(value); }
function strings(value: unknown): value is string[] { return Array.isArray(value) && value.every(item => typeof item === 'string'); }
function optionalStrings(value: Record<string, unknown>, keys: string[]): boolean {
    return keys.every(key => value[key] === undefined || value[key] === null || typeof value[key] === 'string');
}
export function statusDto(value: unknown): StatusDto {
    if (!object(value) || value.protocolVersion !== 1 || typeof value.sessionId !== 'string' || !value.sessionId ||
        !['ready', 'indexing', 'busy', 'unavailable', 'stopping'].includes(String(value.state)) || !strings(value.roots) ||
        !Number.isSafeInteger(value.files) || Number(value.files) < 0 || !Number.isSafeInteger(value.symbols) || Number(value.symbols) < 0 || !optionalStrings(value, ['message'])) { return invalid('status'); }
    return value as unknown as StatusDto;
}
export function symbolDto(value: unknown): SymbolDto {
    if (!object(value) || !['id', 'name', 'macroName', 'file'].every(key => typeof value[key] === 'string') || !value.id ||
        !['class', 'struct', 'interface', 'enum', 'function', 'property', 'delegate'].includes(String(value.kind)) ||
        !Number.isSafeInteger(value.line) || Number(value.line) < 1 || !strings(value.bases) || !Array.isArray(value.specifiers) ||
        !value.specifiers.every(item => object(item) && typeof item.key === 'string' && optionalStrings(item, ['value'])) ||
        !optionalStrings(value, ['typeName', 'signature', 'documentation', 'owner', 'qualifiedName'])) { return invalid('symbol'); }
    return value as unknown as SymbolDto;
}
export function symbolsDto(value: unknown): SymbolDto[] {
    if (!Array.isArray(value)) { return invalid('symbols'); }
    return value.map(symbolDto);
}
export function jobDto(value: unknown): JobDto {
    if (!object(value) || !['id', 'sessionId', 'kind'].every(key => typeof value[key] === 'string' && value[key] !== '') ||
        !['queued', 'running', 'succeeded', 'failed', 'cancelled'].includes(String(value.state))) { return invalid('job'); }
    return value as unknown as JobDto;
}
export function aiResult(value: unknown): { text: string; model: string } {
    if (!object(value) || typeof value.text !== 'string' || typeof value.model !== 'string' || Buffer.byteLength(value.text, 'utf8') > 1024 * 1024) { return invalid('AI result'); }
    return { text: value.text, model: value.model };
}
export function assertCapabilities(value: unknown): void {
    if (!object(value) || !object(value.experimental) || !object(value.experimental.penguin) || value.experimental.penguin.protocolVersion !== 1) {
        throw new Error('Unsupported Penguin protocol. This extension requires capabilities.experimental.penguin.protocolVersion = 1; no legacy fallback was activated.');
    }
    const sync = value.textDocumentSync;
    if (sync !== 1 && !(object(sync) && sync.change === 1 && sync.openClose === true)) {
        throw new Error('Penguin must advertise FULL text synchronization with open/close support.');
    }
    if (value.positionEncoding !== undefined && value.positionEncoding !== 'utf-16') { throw new Error('Penguin protocol v1 requires UTF-16 positions.'); }
}
export class Protocol {
    private sessionId: string | undefined;
    constructor(private readonly rpc: Rpc, private readonly current: () => boolean) {}
    assertCurrent(): void { if (!this.current()) { throw new StaleSessionError(); } }
    private async call(method: string, params: object, signal?: AbortSignal): Promise<unknown> {
        this.assertCurrent();
        try {
            const value = await this.rpc.request(method, params, signal);
            this.assertCurrent();
            return value;
        } catch (error) {
            this.assertCurrent();
            if (object(error) && object(error.data) && error.data.code === 'staleReference') {
                throw new Error('Symbol reference expired. Run Penguin: Search Unreal Symbols again.');
            }
            throw error;
        }
    }
    async status(signal?: AbortSignal): Promise<StatusDto> {
        const result = statusDto(await this.call(methods.status, {}, signal));
        if (this.sessionId && this.sessionId !== result.sessionId) { throw new StaleSessionError(); }
        this.sessionId = result.sessionId;
        return result;
    }
    async symbols(query: string, signal?: AbortSignal): Promise<SymbolDto[]> {
        if (Buffer.byteLength(query, 'utf8') > 512) { throw new Error('Symbol query must be at most 512 UTF-8 bytes.'); }
        return symbolsDto(await this.call(methods.symbols, { query, limit: 100 }, signal));
    }
    async symbol(id: string): Promise<SymbolDto> {
        const result = await this.call(methods.symbol, { id });
        if (result === null) { throw new Error('Symbol reference expired. Run Penguin: Search Unreal Symbols again.'); }
        return symbolDto(result);
    }
    async inheritance(name: string): Promise<InheritanceDto> {
        const result = await this.call(methods.inheritance, { name });
        if (!object(result) || !strings(result.bases)) { return invalid('inheritance'); }
        return { bases: result.bases, derived: symbolsDto(result.derived) };
    }
    private checkedJob(value: unknown): JobDto {
        const job = jobDto(value);
        if (!this.sessionId || this.sessionId !== job.sessionId) { throw new StaleSessionError(); }
        return job;
    }
    async reindex(signal?: AbortSignal): Promise<JobDto> { return this.checkedJob(await this.call(methods.reindex, {}, signal)); }
    async ai(task: 'explain' | 'generate', source: string, instruction?: string, signal?: AbortSignal): Promise<JobDto> {
        if (Buffer.byteLength(source, 'utf8') > 64 * 1024) { throw new Error('Select at most 64 KiB of source for local AI.'); }
        if (instruction && Buffer.byteLength(instruction, 'utf8') > 4096) { throw new Error('AI instruction must be at most 4096 UTF-8 bytes.'); }
        return this.checkedJob(await this.call(methods.ai, { task, source, ...(instruction === undefined ? {} : { instruction }) }, signal));
    }
    async job(id: string, signal?: AbortSignal): Promise<JobDto> {
        const result = await this.call(methods.job, { id }, signal);
        if (result === null) { throw new Error('Penguin job expired or was invalidated. Retry the command.'); }
        const job = this.checkedJob(result);
        if (job.id !== id) { return invalid('job id'); }
        return job;
    }
    async cancelJob(id: string): Promise<boolean> {
        const result = await this.call(methods.cancelJob, { id });
        if (!object(result) || typeof result.cancelled !== 'boolean') { return invalid('cancel response'); }
        return result.cancelled;
    }
}
export function plainError(error: unknown): string {
    if (error instanceof Error) { return error.message; }
    if (typeof error === 'string') { return error; }
    if (object(error) && typeof error.message === 'string') { return error.message; }
    return JSON.stringify(error) ?? 'Unknown error';
}
export function symbolText(symbol: SymbolDto): string {
    const lines = [symbol.qualifiedName ?? symbol.name, `Kind: ${symbol.kind}`, `Macro: ${symbol.macroName}`, `Location: ${symbol.file}:${symbol.line}`];
    for (const [label, value] of [['Type', symbol.typeName], ['Owner', symbol.owner], ['Signature', symbol.signature]] as const) {
        if (value !== null && value !== undefined) { lines.push(`${label}: ${value}`); }
    }
    if (symbol.specifiers.length) { lines.push('Specifiers: ' + symbol.specifiers.map(s => s.value == null ? s.key : `${s.key}=${s.value}`).join(', ')); }
    if (symbol.bases.length) { lines.push('Bases: ' + symbol.bases.join(', ')); }
    if (symbol.documentation != null) { lines.push('', symbol.documentation); }
    return lines.join('\n');
}
