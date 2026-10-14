using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using PenguinExtention.Models;

namespace PenguinExtention.Services
{
    /// <summary>
    /// General C/C++ declaration extractor for non-Unreal code (SDL2, plain C++ games, libraries).
    /// One linear pass over comment/string-blanked text with a brace-scope stack — no AST, no backtracking.
    /// Finds namespaces' contents, classes/structs/unions (+ first base), enums and enumerators, free and member
    /// functions (declarations and Owner::Name definitions), typedef/using aliases, variables and #define macros.
    /// Function bodies are skipped, so locals never become symbols.
    /// </summary>
    internal static class CppSymbolExtractor
    {
        private enum ScopeKind { Namespace, Class, Enum, Function, Block, Extern }

        private sealed class Scope
        {
            public ScopeKind Kind;
            public string Name;
            public bool TypedefPending;   // "typedef struct { ... } Name;"
            public bool IsStruct;
            public UnrealSymbolKind TypeKind;
            public int StartIndex;
        }

        private static readonly Regex RxDefine = new Regex(@"^[ \t]*#[ \t]*define[ \t]+([A-Za-z_]\w*)(\([^)\r\n]*\))?", RegexOptions.Compiled | RegexOptions.Multiline);
        private static readonly Regex RxTypeHead = new Regex(
            @"^(?<td>typedef\s+)?(?<kw>class|struct|union)\s+(?:(?:[A-Z_][A-Z0-9_]*|alignas\s*\([^)]*\)|\[\[[^\]]*\]\])\s+)*(?<name>[A-Za-z_]\w*)?\s*(?:final\s*)?(?::\s*(?:(?:public|protected|private|virtual)\s+)*(?<base>[\w:<>, ]+?))?\s*$",
            RegexOptions.Compiled);
        private static readonly Regex RxEnumHead = new Regex(
            @"^(?<td>typedef\s+)?enum\b\s*(?:class\s+|struct\s+)?(?<name>[A-Za-z_]\w*)?\s*(?::\s*[\w:\s]+)?$", RegexOptions.Compiled);
        // typedef Ret (CALLCONV * Name)(args)
        private static readonly Regex RxFunctionPointerName = new Regex(@"\(\s*(?:[A-Za-z_]\w*\s+)?\*\s*([A-Za-z_]\w*)\s*\)", RegexOptions.Compiled);
        private static readonly Regex RxNamespaceHead = new Regex(@"^(?:inline\s+)?namespace(?:\s+(?<name>[\w:]+))?\s*$", RegexOptions.Compiled);
        private static readonly Regex RxUsingAlias = new Regex(@"^using\s+(?<name>[A-Za-z_]\w*)\s*=", RegexOptions.Compiled);
        private static readonly Regex RxLeadingNoise = new Regex(
            @"^(?:(?:public|protected|private)\s*:\s*|template\s*<[^{};]*?>\s*|\[\[[^\]]*\]\]\s*|__declspec\s*\([^)]*\)\s*)+", RegexOptions.Compiled);

        private static readonly HashSet<string> NotNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "for", "while", "switch", "return", "catch", "sizeof", "decltype", "static_assert", "alignof", "new", "delete",
            "else", "do", "case", "default", "goto", "throw", "typeid", "noexcept", "alignas", "operator", "defined", "__attribute__",
            "__declspec", "static_cast", "dynamic_cast", "reinterpret_cast", "const_cast", "co_await", "co_return", "co_yield", "requires"
        };
        private static readonly HashSet<string> StorageWords = new HashSet<string>(StringComparer.Ordinal)
        {
            "extern", "static", "inline", "virtual", "explicit", "constexpr", "consteval", "constinit", "friend", "mutable",
            "thread_local", "register", "volatile", "const", "typename"
        };

        public static void Extract(string content, string filePath, bool isExternal, List<UnrealSymbol> symbols, List<UnrealClassInfo> classInfos)
        {
            var now = DateTime.UtcNow;
            var lineStarts = LineStarts(content);
            var code = Blank(content);

            // #define macros (from raw text so the name is exact)
            foreach (Match m in RxDefine.Matches(content))
            {
                var name = m.Groups[1].Value;
                if (name.EndsWith("_H", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_H_", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith("_HPP", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_INCLUDED", StringComparison.OrdinalIgnoreCase)) continue; // include guards
                int line = LineOf(lineStarts, m.Index);
                var lineEnd = content.IndexOf('\n', m.Index);
                var text = (lineEnd < 0 ? content.Substring(m.Index) : content.Substring(m.Index, lineEnd - m.Index)).Trim();
                symbols.Add(Make(name, UnrealSymbolKind.Macro, filePath, line, null, text.Length > 200 ? text.Substring(0, 200) : text, null,
                    Comment(content, lineStarts, line), isExternal, now));
            }

            var scopes = new List<Scope> { new Scope { Kind = ScopeKind.Namespace } };
            var stmt = new StringBuilder();
            int stmtStart = -1;

            for (int i = 0; i < code.Length; i++)
            {
                char c = code[i];
                var top = scopes[scopes.Count - 1];

                // Function bodies / plain blocks: only track braces.
                if (top.Kind == ScopeKind.Function || top.Kind == ScopeKind.Block)
                {
                    if (c == '{') scopes.Add(new Scope { Kind = ScopeKind.Block });
                    else if (c == '}') scopes.RemoveAt(scopes.Count - 1);
                    continue;
                }

                if (c == '#') { while (i < code.Length && code[i] != '\n') { if (code[i] == '\\' && i + 1 < code.Length && code[i + 1] == '\n') i++; i++; } continue; }

                if (c == '{' || c == ';' || c == '}' || (c == ',' && top.Kind == ScopeKind.Enum))
                {
                    var text = Normalize(stmt.ToString());
                    int at = stmtStart < 0 ? i : stmtStart;
                    stmt.Clear(); stmtStart = -1;

                    if (top.Kind == ScopeKind.Enum)
                    {
                        if (c != '{') AddEnumerator(text, top, filePath, lineStarts, at, isExternal, now, symbols, content);
                        if (c == '}') CloseScope(scopes, code, ref i, filePath, lineStarts, isExternal, now, symbols, content);
                        else if (c == '{') scopes.Add(new Scope { Kind = ScopeKind.Block });
                        continue;
                    }

                    if (c == '{') { scopes.Add(Open(text, top, scopes, filePath, lineStarts, at, isExternal, now, symbols, classInfos, content)); continue; }
                    if (c == ';') { Declare(text, top, scopes, filePath, lineStarts, at, isExternal, now, symbols, content); continue; }
                    // '}'
                    if (scopes.Count > 1) CloseScope(scopes, code, ref i, filePath, lineStarts, isExternal, now, symbols, content);
                    continue;
                }

                if (stmtStart < 0 && !char.IsWhiteSpace(c)) stmtStart = i;
                if (stmtStart >= 0) stmt.Append(c);
                if (stmt.Length > 4000) { stmt.Clear(); stmtStart = -1; } // runaway (macro soup); resync at next terminator
            }
        }

        private static Scope Open(string text, Scope parent, List<Scope> scopes, string file, int[] lineStarts, int at, bool ext, DateTime now,
            List<UnrealSymbol> symbols, List<UnrealClassInfo> infos, string content)
        {
            var head = RxLeadingNoise.Replace(text, "");
            Match m;

            // extern "C" { — the literal is blanked to spaces by Blank(), so match the quotes only.
            if (Regex.IsMatch(head, "^extern\\s*\"[^\"]*\"$") || head == "extern") return new Scope { Kind = ScopeKind.Extern };
            if ((m = RxNamespaceHead.Match(head)).Success) return new Scope { Kind = ScopeKind.Namespace, Name = m.Groups["name"].Value };

            if ((m = RxEnumHead.Match(head)).Success)
            {
                var name = m.Groups["name"].Success ? m.Groups["name"].Value : null;
                if (name != null) symbols.Add(Make(name, UnrealSymbolKind.Enum, file, LineOf(lineStarts, at), OwnerOf(scopes), head, null, Comment(content, lineStarts, LineOf(lineStarts, at)), ext, now));
                return new Scope { Kind = ScopeKind.Enum, Name = name, TypedefPending = m.Groups["td"].Success && name == null, TypeKind = UnrealSymbolKind.Enum, StartIndex = at };
            }

            if ((m = RxTypeHead.Match(head)).Success)
            {
                var name = m.Groups["name"].Success ? m.Groups["name"].Value : null;
                var kind = m.Groups["kw"].Value == "class" ? UnrealSymbolKind.Class : UnrealSymbolKind.Struct;
                if (name != null && !IsAllCapsMacro(name))
                {
                    int line = LineOf(lineStarts, at);
                    symbols.Add(Make(name, kind, file, line, OwnerOf(scopes), head, null, Comment(content, lineStarts, line), ext, now));
                    var baseName = m.Groups["base"].Success ? m.Groups["base"].Value.Split(',')[0].Trim() : null;
                    if (baseName != null) { int lt = baseName.IndexOf('<'); if (lt > 0) baseName = baseName.Substring(0, lt).Trim(); }
                    infos.Add(new UnrealClassInfo { ClassName = name, BaseClass = string.IsNullOrEmpty(baseName) ? null : baseName });
                }
                return new Scope { Kind = ScopeKind.Class, Name = name, IsStruct = kind == UnrealSymbolKind.Struct, TypedefPending = m.Groups["td"].Success, TypeKind = kind, StartIndex = at };
            }

            // Function definition (free, member inline, or Owner::Name out-of-class)
            if (TryFunction(head, out var fname, out var owner, out var ret, out var signature, parent.Kind == ScopeKind.Class ? parent.Name : null))
            {
                if (owner == null && parent.Kind == ScopeKind.Class) owner = parent.Name;
                int line = LineOf(lineStarts, at);
                symbols.Add(Make(fname, UnrealSymbolKind.Function, file, line, owner, signature, ret, Comment(content, lineStarts, line), ext, now));
                return new Scope { Kind = ScopeKind.Function };
            }

            // Brace initializers, lambdas, unknown macro blocks: skip contents.
            return new Scope { Kind = ScopeKind.Block };
        }

        private static void Declare(string text, Scope top, List<Scope> scopes, string file, int[] lineStarts, int at, bool ext, DateTime now,
            List<UnrealSymbol> symbols, string content)
        {
            var s = RxLeadingNoise.Replace(text, "");
            if (s.Length == 0) return;
            int line = LineOf(lineStarts, at);
            var owner = OwnerOf(scopes);
            Match m;

            if (s.StartsWith("typedef "))
            {
                var fp = RxFunctionPointerName.Match(s);
                var name = fp.Success ? fp.Groups[1].Value : LastIdentifierOfDeclarator(s);
                if (name != null) symbols.Add(Make(name, UnrealSymbolKind.Typedef, file, line, owner, s, null, Comment(content, lineStarts, line), ext, now));
                return;
            }
            if ((m = RxUsingAlias.Match(s)).Success)
            {
                symbols.Add(Make(m.Groups["name"].Value, UnrealSymbolKind.Typedef, file, line, owner, s, null, Comment(content, lineStarts, line), ext, now));
                return;
            }
            if (s.StartsWith("using ") || s.StartsWith("friend ") || s.StartsWith("return") || s.StartsWith("static_assert") || s.StartsWith("goto ")) return;

            // Forward declarations: "class Foo;" / "struct SDL_Window;" — not definitions.
            if (Regex.IsMatch(s, @"^(?:class|struct|union|enum(?:\s+class)?)\s+[A-Za-z_]\w*$")) return;

            if (TryFunction(s, out var fname, out var fowner, out var ret, out var signature, top.Kind == ScopeKind.Class ? top.Name : null))
            {
                symbols.Add(Make(fname, UnrealSymbolKind.Function, file, line, fowner ?? owner, signature, ret, Comment(content, lineStarts, line), ext, now));
                return;
            }

            // Variables: "Type Name [= init] [, Name2]" at namespace or class scope.
            if (top.Kind != ScopeKind.Namespace && top.Kind != ScopeKind.Class && top.Kind != ScopeKind.Extern) return;
            // "int x, y = 2, *p;" — first declarator carries the type, the rest share it.
            var declarators = SplitTopLevel(s);
            string typePart = null;
            var kind = top.Kind == ScopeKind.Class ? UnrealSymbolKind.Property : UnrealSymbolKind.Variable;
            for (int d = 0; d < declarators.Count; d++)
            {
                var decl = declarators[d];
                int eq = TopLevelIndexOf(decl, '=');
                if (eq >= 0) decl = decl.Substring(0, eq);
                int brace = decl.IndexOf('{');
                if (brace >= 0) decl = decl.Substring(0, brace);
                if (decl.IndexOf('(') >= 0) return; // function pointers / macro calls: too ambiguous
                var vname = LastIdentifierOfDeclarator(decl);
                if (vname == null || NotNames.Contains(vname) || IsAllCapsMacro(vname) && top.Kind != ScopeKind.Class) return;
                if (d == 0)
                {
                    typePart = decl.Substring(0, decl.LastIndexOf(vname, StringComparison.Ordinal)).Trim();
                    if (typePart.Length == 0) return;
                }
                var stars = decl.Substring(0, decl.LastIndexOf(vname, StringComparison.Ordinal)).Trim();
                var declType = d == 0 ? typePart : StripStorage(typePart).TrimEnd('*', '&', ' ') + (stars.Length > 0 ? " " + stars : "");
                symbols.Add(Make(vname, kind, file, line, owner, (declType + " " + vname).Trim(), StripStorage(declType), Comment(content, lineStarts, line), ext, now));
            }
        }

        private static List<string> SplitTopLevel(string s)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '<' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == '>' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0) { parts.Add(s.Substring(start, i - start)); start = i + 1; }
            }
            parts.Add(s.Substring(start));
            return parts;
        }

        private static void AddEnumerator(string text, Scope enumScope, string file, int[] lineStarts, int at, bool ext, DateTime now, List<UnrealSymbol> symbols, string content)
        {
            var s = text.Trim();
            if (s.Length == 0) return;
            int eq = s.IndexOf('=');
            var name = (eq >= 0 ? s.Substring(0, eq) : s).Trim();
            if (name.Length == 0 || !OccurrenceScanner.IsIdentStart(name[0]) || !name.All(OccurrenceScanner.IsIdentPart)) return;
            int line = LineOf(lineStarts, at);
            symbols.Add(Make(name, UnrealSymbolKind.EnumValue, file, line, enumScope.Name, s, null, Comment(content, lineStarts, line), ext, now));
        }

        /// <summary>Pops a scope at '}'; for "typedef struct/enum {...} Name;" records Name.</summary>
        private static void CloseScope(List<Scope> scopes, string code, ref int i, string file, int[] lineStarts, bool ext, DateTime now, List<UnrealSymbol> symbols, string content)
        {
            var closing = scopes[scopes.Count - 1];
            scopes.RemoveAt(scopes.Count - 1);
            if (!closing.TypedefPending) return;
            int semi = code.IndexOf(';', i + 1);
            if (semi < 0 || semi - i > 400) return;
            var tail = code.Substring(i + 1, semi - i - 1);
            var name = tail.Split(',').Select(t => LastIdentifierOfDeclarator(t)).FirstOrDefault(n => n != null);
            i = semi; // the declarator list belonged to the typedef
            if (name == null || name == closing.Name) return; // "typedef struct X {...} X;" already recorded
            int line = LineOf(lineStarts, closing.StartIndex);
            var kind = closing.Kind == ScopeKind.Enum ? UnrealSymbolKind.Enum : closing.TypeKind;
            symbols.Add(Make(name, kind, file, line, OwnerOf(scopes), "typedef " + (kind == UnrealSymbolKind.Enum ? "enum" : "struct") + " { … } " + name, null,
                Comment(content, lineStarts, line), ext, now));
            if (closing.Kind == ScopeKind.Enum)
                foreach (var s in symbols.Where(s => s.Kind == UnrealSymbolKind.EnumValue && s.OwnerClass == null && s.FilePath == file && s.LineNumber >= line))
                {
                    s.OwnerClass = name;
                    s.FullyQualifiedName = name + "::" + s.Name;
                }
        }

        /// <summary>Parses "[storage] Ret [Owner::]Name(params) [quals]" — requires a return type unless ctor/dtor/Owner::.</summary>
        internal static bool TryFunction(string s, out string name, out string owner, out string ret, out string signature, string enclosingClass = null)
        {
            name = owner = ret = signature = null;
            int open = FirstTopLevelParen(s);
            if (open <= 0) return false;
            int close = MatchParen(s, open);
            if (close < 0) return false;
            var before = s.Substring(0, open).TrimEnd();
            if (before.Length == 0 || before.IndexOf('=') >= 0 || before.EndsWith(">") && before.IndexOf("operator", StringComparison.Ordinal) < 0) return false;

            // Name (possibly qualified, possibly operator / destructor)
            int e = before.Length, st = e;
            while (st > 0 && (OccurrenceScanner.IsIdentPart(before[st - 1]) || before[st - 1] == '~')) st--;
            if (st == e) return false;
            var simple = before.Substring(st);
            var lead = before.Substring(0, st).TrimEnd();
            if (lead.EndsWith("::"))
            {
                var q = lead.Substring(0, lead.Length - 2).TrimEnd();
                int qs = q.Length;
                while (qs > 0 && (OccurrenceScanner.IsIdentPart(q[qs - 1]) || q[qs - 1] == '>' || q[qs - 1] == '<')) qs--;
                owner = q.Substring(qs);
                int lt = owner.IndexOf('<'); if (lt > 0) owner = owner.Substring(0, lt);
                lead = q.Substring(0, qs).TrimEnd();
                // Drop enclosing namespace qualifiers: "int Engine::Player::Get" → owner Player, lead "int"
                while (lead.EndsWith("::"))
                {
                    var ns = lead.Substring(0, lead.Length - 2).TrimEnd();
                    int ns0 = ns.Length;
                    while (ns0 > 0 && OccurrenceScanner.IsIdentPart(ns[ns0 - 1])) ns0--;
                    lead = ns.Substring(0, ns0).TrimEnd();
                }
            }
            if (NotNames.Contains(simple) || simple.Length == 0 || char.IsDigit(simple[0])) return false;

            var retType = StripStorage(lead);
            bool ctorLike = owner != null || simple.StartsWith("~") || simple == enclosingClass;
            if (retType.Length == 0 && !ctorLike) return false;          // macro call like FOO(x);
            if (retType.Length > 0 && !Regex.IsMatch(retType, @"[A-Za-z_]")) return false;
            if (IsAllCapsMacro(simple) && retType.Length == 0) return false;
            // "Type Name(args)" with literals in args is a constructor-style variable init, not a declaration.
            var args = s.Substring(open + 1, close - open - 1).Trim();
            if (args.Length > 0 && (char.IsDigit(args[0]) || args[0] == '"' || args[0] == '\'' || args == "true" || args == "false" || args == "nullptr")) return false;

            name = simple;
            ret = retType.Length == 0 ? null : retType;
            var quals = s.Substring(close + 1).Trim();
            int colon = quals.IndexOf(':'); // ctor initializer list
            if (colon >= 0 && (colon + 1 >= quals.Length || quals[colon + 1] != ':')) quals = quals.Substring(0, colon).Trim();
            signature = (lead.Length > 0 ? lead + " " : "") + (owner != null ? owner + "::" : "") + simple + "(" + args + ")" + (quals.Length > 0 ? " " + quals : "");
            if (signature.Length > 300) signature = signature.Substring(0, 300) + "…";
            return true;
        }

        // ── helpers ─────────────────────────────────────────────────

        private static UnrealSymbol Make(string name, UnrealSymbolKind kind, string file, int line, string owner, string signature, string ret,
            string comment, bool ext, DateTime now) => new UnrealSymbol
        {
            Name = name, Kind = kind, FilePath = file, LineNumber = line, OwnerClass = owner,
            FullyQualifiedName = owner != null ? owner + "::" + name : name,
            Signature = signature, ReturnType = ret, Comment = comment, IsEngineSymbol = ext, LastIndexed = now
        };

        private static string OwnerOf(List<Scope> scopes)
        {
            for (int i = scopes.Count - 1; i >= 0; i--)
                if (scopes[i].Kind == ScopeKind.Class && scopes[i].Name != null) return scopes[i].Name;
            return null;
        }

        private static string StripStorage(string type)
        {
            var words = type.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(w => !StorageWords.Contains(w) && !(IsAllCapsMacro(w) && w.Length > 3 && w != "BOOL" && w != "DWORD" && w != "HWND" && w != "UINT"));
            return string.Join(" ", words).Trim();
        }

        private static bool IsAllCapsMacro(string w) => w.Length > 1 && w.All(ch => char.IsUpper(ch) || char.IsDigit(ch) || ch == '_') && w.Any(char.IsLetter);

        private static string LastIdentifierOfDeclarator(string s)
        {
            var t = s.Trim().TrimEnd(';').Trim();
            // strip array suffixes and bit-fields
            int bracket = t.IndexOf('['); if (bracket >= 0) t = t.Substring(0, bracket).TrimEnd();
            int colon = t.LastIndexOf(':');
            if (colon > 0 && t[colon - 1] != ':' && (colon + 1 >= t.Length || t[colon + 1] != ':')) t = t.Substring(0, colon).TrimEnd();
            int e = t.Length;
            while (e > 0 && !OccurrenceScanner.IsIdentPart(t[e - 1])) { if (t[e - 1] == ')' ) return null; e--; }
            int st = e;
            while (st > 0 && OccurrenceScanner.IsIdentPart(t[st - 1])) st--;
            if (st == e) return null;
            var id = t.Substring(st, e - st);
            return OccurrenceScanner.IsIdentStart(id[0]) && !NotNames.Contains(id) && !StorageWords.Contains(id) ? id : null;
        }

        private static int FirstTopLevelParen(string s)
        {
            int angle = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '<') angle++;
                else if (s[i] == '>' && angle > 0) angle--;
                else if (s[i] == '(' && angle == 0)
                {
                    // skip __declspec(...)/__attribute__((...))/alignas(...) prefixes
                    var before = s.Substring(0, i).TrimEnd();
                    if (before.EndsWith("__declspec") || before.EndsWith("__attribute__") || before.EndsWith("alignas") || before.EndsWith("DECLSPEC_DEPRECATED"))
                    { int close = MatchParen(s, i); if (close < 0) return -1; i = close; continue; }
                    return i;
                }
            }
            return -1;
        }

        private static int MatchParen(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        private static int TopLevelIndexOf(string s, char target)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '<' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == '>' || c == ']' || c == '}') depth--;
                else if (c == target && depth <= 0 && !(target == '=' && i + 1 < s.Length && s[i + 1] == '=')) return i;
            }
            return -1;
        }

        private static string Normalize(string s)
        {
            var sb = new StringBuilder(s.Length);
            bool space = false;
            foreach (var ch in s)
            {
                if (char.IsWhiteSpace(ch)) { space = sb.Length > 0; continue; }
                if (space) { sb.Append(' '); space = false; }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        /// <summary>Copy of the text with comments and string/char literal contents replaced by spaces (newlines kept).</summary>
        internal static string Blank(string s)
        {
            var b = s.ToCharArray();
            int n = b.Length;
            for (int i = 0; i < n; i++)
            {
                char c = b[i];
                if (c == '/' && i + 1 < n && b[i + 1] == '/') { while (i < n && b[i] != '\n') b[i++] = ' '; continue; }
                if (c == '/' && i + 1 < n && b[i + 1] == '*')
                {
                    while (i < n && !(b[i] == '*' && i + 1 < n && b[i + 1] == '/')) { if (b[i] != '\n') b[i] = ' '; i++; }
                    if (i < n) { b[i] = ' '; if (i + 1 < n) b[i + 1] = ' '; i++; }
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    if (c == '\'' && i > 0 && char.IsLetterOrDigit(b[i - 1]) && i + 1 < n && char.IsLetterOrDigit(b[i + 1])) continue; // 1'000
                    int j = i + 1;
                    while (j < n && b[j] != c && b[j] != '\n') { if (b[j] == '\\' && j + 1 < n) { b[j] = ' '; j++; } b[j] = ' '; j++; }
                    i = j;
                }
            }
            return new string(b);
        }

        private static int[] LineStarts(string s)
        {
            var starts = new List<int> { 0 };
            for (int i = 0; i < s.Length; i++) if (s[i] == '\n') starts.Add(i + 1);
            return starts.ToArray();
        }

        private static int LineOf(int[] starts, int index)
        {
            int idx = Array.BinarySearch(starts, index);
            return (idx >= 0 ? idx : ~idx - 1) + 1;
        }

        /// <summary>/** */, /* */ or // comment block directly above a 1-based line.</summary>
        private static string Comment(string content, int[] starts, int line)
        {
            int l = line - 2; // previous line, 0-based
            var parts = new List<string>();
            for (int guard = 0; l >= 0 && guard < 30; guard++, l--)
            {
                int s = starts[l], e = l + 1 < starts.Length ? starts[l + 1] : content.Length;
                var t = content.Substring(s, e - s).Trim();
                if (t.StartsWith("//")) { parts.Insert(0, t.TrimStart('/').Trim()); continue; }
                if (t.EndsWith("*/"))
                {
                    var block = new List<string>();
                    for (; l >= 0 && guard < 60; l--, guard++)
                    {
                        s = starts[l]; e = l + 1 < starts.Length ? starts[l + 1] : content.Length;
                        var bt = content.Substring(s, e - s).Trim();
                        block.Insert(0, bt.TrimStart('/', '*').TrimEnd('/', '*').Trim());
                        if (bt.StartsWith("/*")) break;
                    }
                    parts.InsertRange(0, block.Where(x => x.Length > 0));
                }
                break;
            }
            var text = string.Join("\n", parts).Trim();
            return text.Length == 0 ? null : text.Length > 1500 ? text.Substring(0, 1500) + "…" : text;
        }
    }
}
