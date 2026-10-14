// Unreal coloring scanner: a port of the Visual Studio extension's UnrealClassifier.Scan. Linear, comment- and
// string-aware; tracks UPROPERTY(...)/meta=(...) argument lists across lines. No VS Code imports (unit-tested).

export type ColorKind = 'macro' | 'class' | 'struct' | 'enum' | 'enumValue' | 'delegate' | 'function' | 'property' | 'specifier' | 'metaKey';

/** Index kind strings (penguin/names) → color, in clash priority order (earlier wins for the same name). */
const kindPriority: Array<[string, ColorKind]> = [
    ['class', 'class'], ['interface', 'class'], ['struct', 'struct'], ['enum', 'enum'], ['delegate', 'delegate'],
    ['function', 'function'], ['enumValue', 'enumValue'], ['enumerator', 'enumValue'], ['property', 'property']
];

export function buildNameMap(pairs: ReadonlyArray<readonly [string, string]>): Map<string, ColorKind> {
    const rank = new Map(kindPriority.map(([kind], index) => [kind, index]));
    const best = new Map<string, number>();
    for (const [name, kind] of pairs) {
        const r = rank.get(kind);
        if (r === undefined) { continue; }
        const current = best.get(name);
        if (current === undefined || r < current) { best.set(name, r); }
    }
    const result = new Map<string, ColorKind>();
    for (const [name, r] of best) { result.set(name, kindPriority[r]![1]); }
    return result;
}

const macros = new Set([
    'UCLASS', 'USTRUCT', 'UENUM', 'UFUNCTION', 'UPROPERTY', 'UINTERFACE', 'UDELEGATE', 'UMETA', 'UPARAM',
    'GENERATED_BODY', 'GENERATED_UCLASS_BODY', 'GENERATED_USTRUCT_BODY', 'GENERATED_IINTERFACE_BODY', 'GENERATED_UINTERFACE_BODY',
    'UE_LOG', 'UE_CLOG', 'UE_LOGFMT', 'TEXT', 'LOCTEXT', 'NSLOCTEXT', 'check', 'checkf', 'checkSlow', 'verify', 'verifyf',
    'ensure', 'ensureMsgf', 'ensureAlways', 'ensureAlwaysMsgf', 'unimplemented', 'checkNoEntry', 'checkNoReentry',
    'DECLARE_LOG_CATEGORY_EXTERN', 'DEFINE_LOG_CATEGORY', 'DEFINE_LOG_CATEGORY_STATIC', 'DECLARE_CYCLE_STAT',
    'SCOPE_CYCLE_COUNTER', 'QUICK_SCOPE_CYCLE_COUNTER', 'TRACE_CPUPROFILER_EVENT_SCOPE', 'FORCEINLINE', 'FORCENOINLINE',
    'IMPLEMENT_MODULE', 'IMPLEMENT_PRIMARY_GAME_MODULE', 'IMPLEMENT_GAME_MODULE', 'ENUM_CLASS_FLAGS',
    'GET_MEMBER_NAME_CHECKED', 'GET_FUNCTION_NAME_CHECKED', 'PURE_VIRTUAL', 'UE_DEPRECATED', 'CORE_API', 'ENGINE_API'
]);
/** Reflection macros whose argument list is specifiers (keys) and meta=(...) entries. */
const specifierMacros = new Set(['UPROPERTY', 'UFUNCTION', 'UCLASS', 'USTRUCT', 'UENUM', 'UINTERFACE', 'UPARAM', 'UMETA', 'UDELEGATE']);

/** Where the scanner is inside a reflection macro's argument list. */
export interface MacroState {
    depth: number;      // paren depth inside a specifier macro; 0 = outside
    metaDepth: number;  // depth of the meta=( ... ) contents; 0 = not inside one
    pending: boolean;   // saw UPROPERTY etc., waiting for its '('
    expectKey: boolean; // the next identifier is an item key (specifier or meta key)
    afterMeta: boolean; // the last key was "meta", so the next '(' opens meta entries
}
export const initialState = (): MacroState => ({ depth: 0, metaDepth: 0, pending: false, expectKey: false, afterMeta: false });

export type Emit = (start: number, length: number, kind: ColorKind) => void;
const isDigit = (c: string): boolean => c >= '0' && c <= '9';
const isIdentStart = (c: string): boolean => /[A-Za-z_]/.test(c);
const isIdent = (c: string): boolean => /[A-Za-z0-9_]/.test(c);
const isSpace = (c: string): boolean => c === ' ' || c === '\t' || c === '\r' || c === '\n' || c === '\f' || c === '\v';

/** Scans one line (no newline), advancing `state` and reporting colored identifiers. */
export function scanLine(text: string, state: MacroState, names: ReadonlyMap<string, ColorKind> | undefined, emit: Emit | undefined): void {
    let i = 0;
    const n = text.length;
    while (i < n) {
        const c = text[i]!;
        if (c === '/' && text[i + 1] === '/') { return; }
        if (c === '/' && text[i + 1] === '*') {
            const end = text.indexOf('*/', i + 2);
            if (end < 0) { return; }
            i = end + 2;
            continue;
        }
        if (c === '"' || c === '\'') {
            i = skipQuoted(text, i);
            if (state.depth > 0) { state.expectKey = false; state.afterMeta = false; }
            state.pending = false;
            continue;
        }
        if (isDigit(c)) {
            while (i < n && (isIdent(text[i]!) || text[i] === '.' || text[i] === '\'')) { i++; }
            continue;
        }
        if (isIdentStart(c)) {
            const start = i;
            while (i < n && isIdent(text[i]!)) { i++; }
            identifier(text, start, text.slice(start, i), state, names, emit);
            continue;
        }
        if (state.depth > 0 || state.pending) { punctuation(c, state); }
        i++;
    }
}

/** Scans a whole document; multi-line comments are skipped and macro state carries across lines. */
export function scanDocument(lines: readonly string[], names: ReadonlyMap<string, ColorKind> | undefined,
    emit: (line: number, start: number, length: number, kind: ColorKind) => void, maxLineLength = 4000): void {
    const state = initialState();
    let inBlockComment = false;
    for (let ln = 0; ln < lines.length; ln++) {
        let text = lines[ln]!;
        if (text.length > maxLineLength) { Object.assign(state, initialState()); inBlockComment = false; continue; }
        let offset = 0;
        if (inBlockComment) {
            const end = text.indexOf('*/');
            if (end < 0) { continue; }
            inBlockComment = false;
            offset = end + 2;
            text = ' '.repeat(offset) + text.slice(offset);
        }
        const open = unterminatedBlockComment(text);
        if (open >= 0) { inBlockComment = true; text = text.slice(0, open); }
        scanLine(text, state, names, emit ? (start, length, kind) => emit(ln, start, length, kind) : undefined);
    }
}

function identifier(text: string, start: number, word: string, state: MacroState,
    names: ReadonlyMap<string, ColorKind> | undefined, emit: Emit | undefined): void {
    state.pending = false;
    if (state.depth > 0 && state.expectKey) {
        const meta = state.metaDepth > 0 && state.depth === state.metaDepth;
        emit?.(start, word.length, meta ? 'metaKey' : 'specifier');
        state.afterMeta = !meta && state.depth === 1 && word.toLowerCase() === 'meta';
        state.expectKey = false;
        return;
    }
    state.afterMeta = false;
    if (state.depth === 0 && specifierMacros.has(word)) {
        emit?.(start, word.length, 'macro');
        state.pending = true;
        return;
    }
    if (!emit) { return; }
    if (macros.has(word) || word.startsWith('DECLARE_')) { emit(start, word.length, 'macro'); return; }
    const kind = names?.get(word);
    if (!kind) { return; }
    // Member names (Value, Index, X...) would color unrelated locals; only trust obj->Name / obj.Name.
    if (kind === 'property' && !afterMemberAccess(text, start)) { return; }
    emit(start, word.length, kind);
}

function punctuation(c: string, state: MacroState): void {
    if (state.pending) {
        if (c === '(') { Object.assign(state, initialState(), { depth: 1, expectKey: true }); }
        else if (!isSpace(c)) { state.pending = false; }
        return;
    }
    switch (c) {
        case '(':
            state.depth++;
            state.expectKey = state.afterMeta;
            if (state.afterMeta) { state.metaDepth = state.depth; }
            state.afterMeta = false;
            break;
        case ')':
            if (state.depth === state.metaDepth) { state.metaDepth = 0; }
            state.depth--;
            state.expectKey = false;
            state.afterMeta = false;
            if (state.depth <= 0) { Object.assign(state, initialState()); }
            break;
        case ',':
            state.expectKey = state.depth === 1 || state.depth === state.metaDepth;
            state.afterMeta = false;
            break;
        case '=':
            state.expectKey = false; // keep afterMeta: "meta = ("
            break;
        default:
            if (!isSpace(c)) { state.expectKey = false; state.afterMeta = false; }
    }
}

function afterMemberAccess(text: string, start: number): boolean {
    let j = start - 1;
    while (j >= 0 && isSpace(text[j]!)) { j--; }
    if (j < 0) { return false; }
    return text[j] === '.' || (text[j] === '>' && j > 0 && text[j - 1] === '-');
}

/** Index just past the string/char literal starting at `i` (or end of line). */
function skipQuoted(text: string, i: number): number {
    const quote = text[i++];
    while (i < text.length) {
        const c = text[i++];
        if (c === '\\') { i++; }
        else if (c === quote) { break; }
    }
    return Math.min(i, text.length);
}

/** Start of a "/*" on this line that is not closed on it (ignoring strings and // comments), or -1. */
function unterminatedBlockComment(text: string): number {
    let i = 0;
    while (i < text.length) {
        const c = text[i]!;
        if (c === '/' && text[i + 1] === '/') { return -1; }
        if (c === '"' || c === '\'') { i = skipQuoted(text, i); continue; }
        if (c === '/' && text[i + 1] === '*') {
            const end = text.indexOf('*/', i + 2);
            if (end < 0) { return i; }
            i = end + 2;
            continue;
        }
        i++;
    }
    return -1;
}
