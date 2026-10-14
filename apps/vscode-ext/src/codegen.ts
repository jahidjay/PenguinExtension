// Text-only code generation for Generate Implementation and Generate Getter/Setter (ports of the Visual Studio
// extension's commands). No VS Code imports, so it is unit-tested.

export interface Declaration { startLine: number; endLine: number; text: string; }

/** The declaration around `line`: from its first line (after any UFUNCTION/blank line) through the ';'. */
export function declarationAt(lines: readonly string[], line: number): Declaration | undefined {
    let start = line;
    while (start > 0) {
        const prev = lines[start - 1]!.trim();
        if (!prev || /[;{}:]$/.test(prev) || /^(UFUNCTION|UPROPERTY|#|\/\/|\*|\/\*)/.test(prev) || /^(public|private|protected)\s*:/.test(prev)) { break; }
        start--;
    }
    let end = line;
    let text = '';
    for (; end < lines.length && end < line + 30; end++) {
        text += (text ? '\n' : '') + stripLineComment(lines[end]!);
        if (/[;{]/.test(stripLineComment(lines[end]!))) { break; }
    }
    if (!/[;]/.test(text) && !/\{/.test(text)) { return undefined; }
    return { startLine: start, endLine: end, text: lines.slice(start, line).map(stripLineComment).join('\n') + (start < line ? '\n' : '') + text };
}

/** Name of the class/struct whose body contains `line` (brace-counted upward), or undefined. */
export function enclosingClass(lines: readonly string[], line: number): string | undefined {
    let depth = 0;
    for (let i = line; i >= 0; i--) {
        const text = stripLineComment(lines[i]!);
        for (let j = text.length - 1; j >= 0; j--) {
            if (text[j] === '}') { depth++; }
            else if (text[j] === '{') {
                if (depth === 0) {
                    const head = [lines[i - 2] ?? '', lines[i - 1] ?? '', text.slice(0, j)].map(stripLineComment).join(' ');
                    const m = /\b(?:class|struct)\s+(?:[A-Z0-9_]+_API\s+)?(\w+)(?:\s+final)?\s*(?::[^{]*)?$/.exec(head.trim());
                    if (m) { return m[1]; }
                    return undefined; // a function/namespace body, not a class
                }
                depth--;
            }
        }
    }
    return undefined;
}

/** UFUNCTION(...) specifier text directly above the declaration (for _Implementation/_Validate), if any. */
export function ufunctionAbove(lines: readonly string[], startLine: number): string {
    for (let i = startLine - 1; i >= 0 && i >= startLine - 6; i--) {
        const t = lines[i]!.trim();
        if (t.startsWith('UFUNCTION')) { return lines.slice(i, startLine).join(' '); }
        if (t && !t.startsWith('//') && !t.endsWith(',') && !t.endsWith('(') && !t.endsWith(')')) { break; }
    }
    return '';
}

/** Out-of-line definition(s) for a member function declaration, or an error message. */
export function implementation(declaration: string, owner: string, ufunction = ''): string[] | string {
    let text = declaration.replace(/\/\*[\s\S]*?\*\//g, ' ').replace(/\s+/g, ' ').trim();
    text = text.replace(/\s*;\s*$/, '').replace(/\s*\{.*$/, '');
    if (/^(UPROPERTY|GENERATED_)/.test(text) || !text.includes('(')) { return 'The cursor is not on a function declaration.'; }
    text = text.replace(/\s*=\s*(0|default|delete)\s*$/, '').replace(/\s*PURE_VIRTUAL\s*\(.*\)\s*$/, '');
    text = text.replace(/\b(virtual|static|explicit|inline|FORCEINLINE|FORCENOINLINE|UE_NODISCARD)\s+/g, '');
    text = text.replace(/\s+\b(override|final)\b/g, '');
    const open = text.indexOf('(');
    const close = matchingParen(text, open);
    if (close < 0) { return 'Could not find the end of the parameter list.'; }
    const head = text.slice(0, open).trim();
    const nameMatch = /(~?\w+|operator\s*\S+)$/.exec(head);
    if (!nameMatch) { return 'Could not find the function name.'; }
    const name = nameMatch[1]!;
    const returnType = head.slice(0, head.length - name.length).trim();
    const params = stripDefaults(text.slice(open + 1, close));
    const tail = text.slice(close + 1).trim(); // const, noexcept, ...
    const isNative = /\bBlueprintNativeEvent\b/.test(ufunction);
    const isRpc = /\b(Server|Client|NetMulticast)\b/.test(ufunction);
    const validate = /\bWithValidation\b/.test(ufunction);
    const body = returnType && returnType !== 'void' && !name.startsWith('~') && name !== owner ? `\treturn {};\n` : '';
    const def = (fn: string, ret: string, inner: string): string =>
        `${ret ? ret + ' ' : ''}${owner}::${fn}(${params})${tail ? ' ' + tail : ''}\n{\n${inner}}\n`;
    const result: string[] = [];
    if (isNative || isRpc) { result.push(def(name + '_Implementation', returnType, body)); }
    else { result.push(def(name, returnType, body)); }
    if (isRpc && validate) { result.push(def(name + '_Validate', 'bool', '\treturn true;\n')); }
    return result;
}

function matchingParen(text: string, open: number): number {
    let depth = 0;
    for (let i = open; i < text.length; i++) {
        if (text[i] === '(') { depth++; }
        else if (text[i] === ')' && --depth === 0) { return i; }
    }
    return -1;
}

/** Removes "= default" values from a parameter list (respecting nested parens/templates/strings). */
export function stripDefaults(params: string): string {
    const parts: string[] = [];
    let depth = 0, current = '', quote = '';
    for (const c of params) {
        if (quote) { current += c; if (c === quote) { quote = ''; } continue; }
        if (c === '"' || c === '\'') { quote = c; current += c; continue; }
        if ('(<[{'.includes(c)) { depth++; } else if (')>]}'.includes(c)) { depth--; }
        if (c === ',' && depth === 0) { parts.push(current); current = ''; continue; }
        current += c;
    }
    if (current.trim()) { parts.push(current); }
    return parts.map(p => {
        let d = 0;
        for (let i = 0; i < p.length; i++) {
            const c = p[i]!;
            if ('(<[{'.includes(c)) { d++; } else if (')>]}'.includes(c)) { d--; }
            else if (c === '=' && d === 0 && p[i + 1] !== '=' && p[i - 1] !== '=' && p[i - 1] !== '!') { return p.slice(0, i).trim(); }
        }
        return p.trim();
    }).join(', ');
}

const primitive = new Set(['bool', 'int', 'int8', 'int16', 'int32', 'int64', 'uint8', 'uint16', 'uint32', 'uint64',
    'float', 'double', 'char', 'TCHAR', 'ANSICHAR', 'WIDECHAR', 'unsigned', 'long', 'short', 'size_t']);

/** Inline Get/Set accessors for a member declaration like `float Health = 100.f;`, or an error message. */
export function accessors(line: string, indent: string): string | { text: string; name: string } {
    const code = stripLineComment(line).trim();
    const m = /^(?:UPROPERTY\s*\(.*\)\s*)?(?:mutable\s+)?(.+?[\s*&>])(\w+)\s*(?:=[^;]*|\{[^;]*\})?;$/.exec(code);
    if (!m || code.includes('(') && !code.startsWith('UPROPERTY')) { return 'Put the cursor on a member variable declaration (e.g. float Health;).'; }
    const type = m[1]!.trim();
    const name = m[2]!;
    // bool bIsDead → IsDead; others keep their name.
    const clean = type === 'bool' && /^b[A-Z]/.test(name) ? name.slice(1) : name;
    const byValue = primitive.has(type) || type.endsWith('*') || /^(E\w+|TEnumAsByte<.*>)$/.test(type) || type.startsWith('TObjectPtr<');
    const getType = byValue ? type : `const ${type}&`;
    const setType = byValue ? type : `const ${type}&`;
    const getter = `${indent}${getType} Get${clean}() const { return ${name}; }`;
    const setter = `${indent}void Set${clean}(${setType} New${clean}) { ${name} = New${clean}; }`;
    return { text: `${getter}\n${setter}`, name: clean };
}

function stripLineComment(text: string): string {
    const i = text.indexOf('//');
    return i < 0 ? text : text.slice(0, i);
}

/** Candidate .cpp paths for a header: same folder, then Public/Classes ↔ Private, then one level up from Private. */
export function sourceCandidates(header: string, sep: string): string[] {
    const dot = header.lastIndexOf('.');
    const base = dot < 0 ? header : header.slice(0, dot);
    const result = [base + '.cpp'];
    const parts = base.split(/[\\/]/);
    for (const [from, to] of [['Public', 'Private'], ['Classes', 'Private']] as const) {
        const index = parts.lastIndexOf(from);
        if (index >= 0) { const copy = [...parts]; copy[index] = to; result.push(copy.join(sep) + '.cpp'); }
    }
    const file = parts[parts.length - 1]!;
    const dir = parts.slice(0, -1);
    result.push([...dir, 'Private', file].join(sep) + '.cpp');
    return [...new Set(result)];
}
