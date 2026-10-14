using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PenguinExtention.Models;
using PenguinExtention.Services;

namespace PenguinExtention.Completion
{
    internal enum PenguinItemKind { Smart, Specifier, Member, Snippet, Symbol }

    internal sealed class PenguinItem
    {
        public string Display;
        public string Insert;
        /// <summary>Gray text after the name (kind, owner, "snippet" …).</summary>
        public string Detail;
        /// <summary>Plain-text tooltip for non-symbol items (symbols use <see cref="SymbolTooltip"/>).</summary>
        public string Description;
        public string FilterText;
        public int CaretBack;
        public UnrealSymbol Symbol;
        public PenguinItemKind Kind;
        public int Score;

        /// <summary>One-character glyph for the list (Segoe UI Symbol / emoji).</summary>
        public string Glyph
        {
            get
            {
                switch (Kind)
                {
                    case PenguinItemKind.Smart: return "★";
                    case PenguinItemKind.Specifier: return "⚙";
                    case PenguinItemKind.Snippet: return "✂";
                }
                switch (Symbol?.Kind)
                {
                    case UnrealSymbolKind.Class: case UnrealSymbolKind.Interface: return "◆";
                    case UnrealSymbolKind.Struct: return "◇";
                    case UnrealSymbolKind.Enum: return "▤";
                    case UnrealSymbolKind.EnumValue: return "▫";
                    case UnrealSymbolKind.Function: return "ƒ";
                    case UnrealSymbolKind.Property: case UnrealSymbolKind.Variable: return "●";
                    case UnrealSymbolKind.Delegate: return "⚡";
                    case UnrealSymbolKind.Macro: return "#";
                    default: return "•";
                }
            }
        }
    }

    /// <summary>What the 🐧 list shows for one caret position.</summary>
    internal sealed class PenguinCompletionModel
    {
        /// <summary>Start of the text the list replaces (the identifier being typed).</summary>
        public int SpanStart;
        public string Typed = "";
        public string Context = "";
        public List<PenguinItem> Items = new List<PenguinItem>();
    }

    /// <summary>
    /// Computes the Penguin completion list from buffer text + the Legacy index. Pure text logic (no editor types),
    /// called on a background thread on every keystroke while the list is open.
    /// </summary>
    internal static class PenguinCompletionEngine
    {
        private const int MaxItems = 200;
        private const int MaxMemberItems = 1500;

        private static readonly HashSet<string> Keywords = new HashSet<string>
        {
            "return", "case", "else", "new", "delete", "const", "static", "virtual", "auto", "class", "struct", "enum",
            "public", "private", "protected", "if", "for", "while", "switch", "do", "using", "namespace", "typename",
            "template", "goto", "throw", "sizeof", "co_return", "co_await", "operator", "inline", "explicit", "friend"
        };

        /// <param name="explicitInvoke">Ctrl+Space: open even with nothing typed.</param>
        public static PenguinCompletionModel Compute(string text, int caret, bool explicitInvoke)
        {
            if (caret <= 0 || caret > text.Length) return null;
            var cache = CacheService.Instance;
            var masked = CodeContext.Mask(text);

            // Not inside comments or string literals, not on preprocessor lines.
            if (char.IsWhiteSpace(masked[caret - 1]) && !char.IsWhiteSpace(text[caret - 1])) return null;
            int lineStart = text.LastIndexOf('\n', caret - 1) + 1;
            if (text.Substring(lineStart, caret - lineStart).TrimStart().StartsWith("#")) return null;

            int spanStart = caret;
            while (spanStart > 0 && IsIdent(text[spanStart - 1])) spanStart--;
            if (spanStart < caret && char.IsDigit(text[spanStart])) return null; // a number
            var model = new PenguinCompletionModel { SpanStart = spanStart, Typed = text.Substring(spanStart, caret - spanStart) };
            var before = text.Substring(lineStart, spanStart - lineStart);
            var trimmed = before.TrimEnd();

            // ── 1. Inside UPROPERTY( … ) / UFUNCTION( … ) / meta = ( … ) ──
            var macro = MacroContext(masked, caret);
            if (macro != null)
            {
                model.Context = macro.Value.name + " specifiers";
                AddSpecifiers(model, macro.Value.name, macro.Value.inside);
                return Finish(model, explicitInvoke);
            }
            if (cache == null || !cache.IsLoaded) return null;

            var currentClass = CodeContext.EnclosingClass(masked, caret) ?? EnclosingMemberOwner(masked, caret);

            // ── 2. Owner:: (static members, enum values, Super::) ──
            if (trimmed.EndsWith("::"))
            {
                var owner = IdentBefore(trimmed, trimmed.Length - 2);
                if (owner == null) return null;
                if (owner == "Super") owner = currentClass == null ? null : Chain(cache, currentClass, masked).Skip(1).FirstOrDefault();
                if (owner == null) return null;
                model.Context = owner + "::";
                foreach (var m in MembersWithBases(cache, owner, masked))
                    model.Items.Add(FromSymbol(m, PenguinItemKind.Member));
                return Finish(model, true);
            }

            // ── 3. obj. / obj-> / this-> / Getter()-> ──
            if (trimmed.EndsWith(".") || trimmed.EndsWith("->"))
            {
                int end = trimmed.Length - (trimmed.EndsWith("->") ? 2 : 1);
                var receiver = trimmed.Substring(0, end).TrimEnd();
                bool call = receiver.EndsWith(")");
                if (call)
                {
                    int open = OpenParenBefore(receiver);
                    if (open < 0) return null;
                    receiver = receiver.Substring(0, open).TrimEnd();
                }
                var name = IdentBefore(receiver, receiver.Length);
                if (name == null) return null;
                string type = name == "this" ? currentClass : ResolveType(cache, masked, spanStart, name, currentClass, call);
                if (type == null) return null; // unknown receiver: leave it to Visual Studio's list
                model.Context = type + (trimmed.EndsWith("->") ? "->" : ".");
                foreach (var m in MembersWithBases(cache, type, masked).Where(m => m.Kind != UnrealSymbolKind.EnumValue))
                    model.Items.Add(FromSymbol(m, PenguinItemKind.Member));
                return Finish(model, true);
            }

            // ── 4. Smart suggestions ──
            AddSmart(model, cache, masked, text, spanStart, trimmed, currentClass);

            // ── 5. Class body: override suggestions on an empty line or after "virtual" ──
            if (currentClass != null && InClassBodyDirectly(masked, spanStart, currentClass) &&
                (trimmed.Length == 0 || trimmed.Trim() == "virtual") && (model.Typed.Length > 0 || explicitInvoke || trimmed.Trim() == "virtual"))
                AddOverrides(model, cache, masked, currentClass, trimmed.Trim() == "virtual");

            // ── 6. Plain identifier: snippets + current class members + index (fuzzy) ──
            bool statementStart = trimmed.Length == 0 || trimmed.EndsWith(";") || trimmed.EndsWith("{") || trimmed.EndsWith("}") || trimmed.EndsWith(":");
            if (model.Typed.Length == 0 && !explicitInvoke && model.Items.Count == 0) return null;
            if (statementStart)
                foreach (var s in UnrealSnippets.Matching(model.Typed))
                    model.Items.Add(new PenguinItem
                    {
                        Kind = PenguinItemKind.Snippet, Display = s.Text, Insert = s.Text, FilterText = s.Keyword,
                        Detail = "snippet", Description = s.Description, CaretBack = s.CaretBack
                    });
            if (model.Typed.Length > 0)
            {
                if (currentClass != null)
                    foreach (var m in MembersWithBases(cache, currentClass, masked))
                        if (PenguinMatcher.Score(model.Typed, m.Name) >= 0) model.Items.Add(FromSymbol(m, PenguinItemKind.Member));
                foreach (var s in cache.SearchFuzzy(model.Typed, 150))
                    if (s.Kind != UnrealSymbolKind.EnumValue || string.IsNullOrEmpty(s.OwnerClass))
                        model.Items.Add(FromSymbol(s, PenguinItemKind.Symbol));
            }
            model.Context = "symbols";
            return Finish(model, explicitInvoke);
        }

        /// <summary>Scores, de-duplicates and orders items; null when nothing matches.</summary>
        private static PenguinCompletionModel Finish(PenguinCompletionModel model, bool allowEmptyTyped)
        {
            if (model.Typed.Length == 0 && !allowEmptyTyped && model.Items.All(i => i.Kind != PenguinItemKind.Smart)) return null;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var scored = new List<PenguinItem>();
            foreach (var item in model.Items)
            {
                item.Score = PenguinMatcher.Score(model.Typed, item.FilterText ?? item.Display);
                if (item.Score == PenguinMatcher.NoMatch) continue;
                if (!seen.Add(item.Kind + "|" + item.Insert)) continue;
                scored.Add(item);
            }
            if (model.Context == "symbols" && scored.Any(i => i.Kind == PenguinItemKind.Smart)) model.Context = "smart suggestions";
            if (model.Typed.Length == 0)
            {
                // Member lists (Owner::, obj->) and smart lists: alphabetical like Visual Assist, smart items first.
                model.Items = scored.OrderBy(i => Rank(i.Kind)).ThenBy(i => i.FilterText ?? i.Display, StringComparer.OrdinalIgnoreCase).Take(MaxMemberItems).ToList();
                return model.Items.Count == 0 ? null : model;
            }
            model.Items = scored
                .OrderBy(i => i.Score <= 2 ? 0 : i.Score) // all prefix matches together, then acronyms, humps, substrings
                .ThenBy(i => Rank(i.Kind))
                .ThenBy(i => i.Score)
                .ThenBy(i => i.Score == 3 ? PenguinMatcher.Acronym(i.Display).Length : 0) // "cmc": CMC before CMCS
                .ThenBy(i => i.Symbol != null && i.Symbol.IsEngineSymbol ? 1 : 0)
                .ThenBy(i => KindWeight(i.Symbol))
                .ThenByDescending(i => i.Symbol?.AccessCount ?? 0)
                .ThenBy(i => i.Display.Length)
                .ThenBy(i => i.Display, StringComparer.OrdinalIgnoreCase)
                .Take(MaxItems).ToList();
            return model.Items.Count == 0 ? null : model;
        }

        /// <summary>Types before functions before data: "cmc" means UCharacterMovementComponent, not CurrentMouseCursor.</summary>
        private static int KindWeight(UnrealSymbol s)
        {
            switch (s?.Kind)
            {
                case UnrealSymbolKind.Class: case UnrealSymbolKind.Struct: case UnrealSymbolKind.Interface: case UnrealSymbolKind.Enum: return 0;
                case UnrealSymbolKind.Function: case UnrealSymbolKind.Delegate: case UnrealSymbolKind.Macro: return 1;
                default: return 2;
            }
        }

        private static int Rank(PenguinItemKind kind)
        {
            switch (kind)
            {
                case PenguinItemKind.Smart: case PenguinItemKind.Specifier: return 0;
                case PenguinItemKind.Snippet: return 1;
                case PenguinItemKind.Member: return 2;
                default: return 3;
            }
        }

        private static PenguinItem FromSymbol(UnrealSymbol s, PenguinItemKind kind) => new PenguinItem
        {
            Kind = kind, Symbol = s, Display = s.Name, Insert = s.Name, FilterText = s.Name,
            Detail = string.IsNullOrEmpty(s.OwnerClass) ? s.KindDisplay : s.OwnerClass
        };

        // ── Specifiers ──────────────────────────────────────────────

        private static void AddSpecifiers(PenguinCompletionModel model, string macro, string inside)
        {
            var specifiers = UnrealMacroSpecifiers.GetSpecifiers(macro);
            if (specifiers == null) return;
            var parts = CodeContext.SplitTopLevel(inside);
            var used = new HashSet<string>(parts.Take(parts.Count - 1)
                .Select(p => Regex.Match(p, @"^\s*(\w+)").Groups[1].Value).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);
            foreach (var s in specifiers)
            {
                if (used.Contains(s.Name) && s.Name != "meta") continue;
                var insert = s.InsertText ?? s.Name;
                model.Items.Add(new PenguinItem
                {
                    Kind = PenguinItemKind.Specifier, Display = s.Name, Insert = insert, FilterText = s.Name,
                    Detail = macro == "meta" ? "meta key" : macro, Description = s.Description,
                    CaretBack = insert.EndsWith("\"\"") || insert.EndsWith("()") ? 1 : 0
                });
            }
        }

        /// <summary>(macro name, text between its '(' and the caret) when the caret is inside a reflection macro.</summary>
        internal static (string name, string inside)? MacroContext(string masked, int caret)
        {
            int limit = Math.Max(0, caret - 800), depth = 0;
            for (int i = caret - 1; i >= limit; i--)
            {
                char c = masked[i];
                if (c == ')') depth++;
                else if (c == ';' || c == '{' || c == '}') return null;
                else if (c == '(')
                {
                    if (depth > 0) { depth--; continue; }
                    var name = WordBefore(masked, i);
                    if (name == null) return null;
                    if (UnrealMacroSpecifiers.MacroNames.Contains(name)) return (name, masked.Substring(i + 1, caret - i - 1));
                    if (name == "meta")
                    {
                        int w = masked.LastIndexOf("meta", i, StringComparison.Ordinal);
                        if (w > 0 && MacroContext(masked, w) != null) return ("meta", masked.Substring(i + 1, caret - i - 1));
                    }
                    return null;
                }
            }
            return null;
        }

        private static string WordBefore(string s, int end)
        {
            int i = end - 1;
            while (i >= 0 && char.IsWhiteSpace(s[i])) i--;
            if (i >= 0 && s[i] == '=') { i--; while (i >= 0 && char.IsWhiteSpace(s[i])) i--; }
            int e = i + 1;
            while (i >= 0 && IsIdent(s[i])) i--;
            return e > i + 1 ? s.Substring(i + 1, e - i - 1) : null;
        }

        // ── Members & types ─────────────────────────────────────────

        /// <summary>
        /// <paramref name="type"/> followed by its base classes. For a class the index doesn't know yet (being written
        /// right now), the base comes from its "class X : public Y" head in <paramref name="masked"/>.
        /// </summary>
        internal static List<string> Chain(CacheService cache, string type, string masked = null)
        {
            var chain = new List<string> { type };
            var info = cache.GetClassInfo(type);
            if (info?.InheritanceChain != null && info.InheritanceChain.Count > 0) { chain.AddRange(info.InheritanceChain); return chain; }
            if (masked == null) return chain;
            var head = Regex.Match(masked, @"\b(?:class|struct)\s+(?:\w+_API\s+)?" + Regex.Escape(type) + @"\b(?:\s+final)?\s*:\s*(?:public|protected|private)?\s*([A-Za-z_]\w*)");
            if (!head.Success) return chain;
            var baseName = head.Groups[1].Value;
            chain.Add(baseName);
            var baseInfo = cache.GetClassInfo(baseName);
            if (baseInfo?.InheritanceChain != null) chain.AddRange(baseInfo.InheritanceChain);
            return chain;
        }

        /// <summary>Members of <paramref name="type"/> and its base classes (nearest first, no constructors).</summary>
        internal static List<UnrealSymbol> MembersWithBases(CacheService cache, string type, string masked = null)
        {
            var result = new List<UnrealSymbol>();
            if (string.IsNullOrEmpty(type)) return result;
            var chain = Chain(cache, type, masked);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var owner in chain.Take(12))
                foreach (var m in cache.GetMembers(owner))
                {
                    if (m.Name == owner || m.Name.StartsWith("~")) continue;
                    if (names.Add(m.Name + "|" + m.Kind)) result.Add(m);
                }
            return result;
        }

        /// <summary>The class owning the member-function definition around <paramref name="caret"/> in a .cpp (AFoo::Bar(…) { | }).</summary>
        internal static string EnclosingMemberOwner(string masked, int caret) => EnclosingDefinition(masked, caret)?.Groups["owner"].Value;

        private static readonly Regex RxMemberDefinition = new Regex(
            @"(?<ret>[A-Za-z_][\w:<>,\*&\s]*?)?\b(?<owner>[A-Za-z_]\w*)\s*::\s*(?<name>~?[A-Za-z_]\w*)\s*\((?<params>[^;{}]*)\)\s*(?:const\s*)?(?:noexcept\s*)?(?::[^;{]*)?\{",
            RegexOptions.Compiled);

        private static Match EnclosingDefinition(string masked, int caret)
        {
            int windowStart = Math.Max(0, caret - 60000);
            Match best = null;
            foreach (Match m in RxMemberDefinition.Matches(masked.Substring(windowStart, caret - windowStart)))
            {
                int open = windowStart + m.Index + m.Length - 1;
                int close = CodeContext.MatchBrace(masked, open);
                if (close < 0 || close >= caret) best = m;
            }
            return best;
        }

        /// <summary>
        /// Declared type of <paramref name="name"/> at <paramref name="position"/>: a local/parameter declaration in
        /// the text, else a member (property or getter) of the current class chain. Pointer/ptr wrappers stripped.
        /// </summary>
        internal static string ResolveType(CacheService cache, string masked, int position, string name, string currentClass, bool isCall)
        {
            if (!isCall)
            {
                int windowStart = Math.Max(0, position - 20000);
                var window = masked.Substring(windowStart, position - windowStart);
                var rx = new Regex(@"(?<type>(?:const\s+)?(?:class\s+)?[A-Za-z_][\w:]*(?:\s*<[^;{}()]*?>)?)\s*[\*&]*\s*\b" + Regex.Escape(name) + @"\b\s*(?:=|;|,|\)|\{|\[|:)");
                var matches = rx.Matches(window);
                for (int i = matches.Count - 1; i >= 0; i--)
                {
                    var type = CleanType(matches[i].Groups["type"].Value);
                    if (type != null && !Keywords.Contains(type) && type != name) return Known(cache, type);
                }
            }
            if (currentClass != null)
            {
                var member = MembersWithBases(cache, currentClass, masked).FirstOrDefault(m => m.Name == name &&
                    (isCall ? m.Kind == UnrealSymbolKind.Function : m.Kind == UnrealSymbolKind.Property || m.Kind == UnrealSymbolKind.Variable));
                if (member != null) return Known(cache, CleanType(member.ReturnType));
            }
            // A free function or global with a known return type
            var global = cache.GetByExactName(name).FirstOrDefault(s => !string.IsNullOrEmpty(s.ReturnType) && string.IsNullOrEmpty(s.OwnerClass));
            return global == null ? null : Known(cache, CleanType(global.ReturnType));
        }

        private static string Known(CacheService cache, string type) =>
            type != null && (cache.GetClassInfo(type) != null || cache.GetMembers(type).Count > 0) ? type : null;

        /// <summary>"const TObjectPtr&lt;UStaticMeshComponent&gt;&amp;" → "UStaticMeshComponent".</summary>
        internal static string CleanType(string type)
        {
            if (string.IsNullOrWhiteSpace(type)) return null;
            var t = Regex.Replace(type, @"\b(const|class|struct|volatile|mutable|static|virtual|inline|FORCEINLINE|UPROPERTY|UFUNCTION)\b", " ");
            var wrapper = Regex.Match(t, @"\b(?:TObjectPtr|TWeakObjectPtr|TSoftObjectPtr|TStrongObjectPtr|TSharedPtr|TSharedRef|TUniquePtr|TWeakPtr)\s*<\s*([\w:]+)\s*>");
            if (wrapper.Success) t = wrapper.Groups[1].Value;
            t = t.Replace("*", " ").Replace("&", " ").Trim();
            if (t.Contains("<")) return null; // containers: their element members are not what '.' lists
            t = t.Split(new[] { "::" }, StringSplitOptions.None).Last().Trim();
            return Regex.IsMatch(t, @"^[A-Za-z_]\w*$") ? t : null;
        }

        /// <summary>Raw declared type (keeps '*' / wrappers so pointer-ness can be judged).</summary>
        private static string RawType(CacheService cache, string masked, int position, string name, string currentClass)
        {
            int windowStart = Math.Max(0, position - 20000);
            var window = masked.Substring(windowStart, position - windowStart);
            var rx = new Regex(@"(?<type>(?:const\s+)?[A-Za-z_][\w:]*(?:\s*<[^;{}()]*?>)?\s*[\*&]*)\s*\b" + Regex.Escape(name) + @"\b\s*(?:=|;|,|\)|\{|\[|:)");
            var matches = rx.Matches(window);
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                var t = matches[i].Groups["type"].Value.Trim();
                if (!Keywords.Contains(t)) return t;
            }
            if (currentClass != null)
                return MembersWithBases(cache, currentClass, masked).FirstOrDefault(m => m.Name == name &&
                    (m.Kind == UnrealSymbolKind.Property || m.Kind == UnrealSymbolKind.Variable))?.ReturnType;
            return null;
        }

        // ── Smart suggestions ───────────────────────────────────────

        private static void AddSmart(PenguinCompletionModel model, CacheService cache, string masked, string text, int spanStart,
            string trimmed, string currentClass)
        {
            string rawType = null, why = null;
            var assign = Regex.Match(trimmed, @"(?<lhs>[A-Za-z_]\w*)\s*(?:\[[^\]]*\])?\s*(?<op>==|!=|=)$");
            if (assign.Success && !trimmed.EndsWith("<=") && !trimmed.EndsWith(">="))
            {
                rawType = RawType(cache, masked, spanStart, assign.Groups["lhs"].Value, currentClass);
                why = assign.Groups["lhs"].Value + " " + assign.Groups["op"].Value;
            }
            else if (Regex.IsMatch(trimmed, @"(^|[;{}:\s])case$"))
            {
                var enumName = SwitchEnum(cache, masked, spanStart, currentClass);
                if (enumName != null) { rawType = enumName; why = "switch on " + enumName; }
            }
            else if (Regex.IsMatch(trimmed, @"(^|[;{}\s])return$"))
            {
                var def = EnclosingDefinition(masked, spanStart);
                var ret = def?.Groups["ret"].Value;
                if (!string.IsNullOrWhiteSpace(ret)) { rawType = ret.Trim(); why = "returns " + rawType; }
            }
            if (rawType == null) return;

            var plain = Regex.Replace(rawType, @"\b(const|static|virtual|inline|FORCEINLINE)\b", " ").Trim();
            void Smart(string insert, string detail) => model.Items.Add(new PenguinItem
            {
                Kind = PenguinItemKind.Smart, Display = insert, Insert = insert, FilterText = insert.Split(new[] { "::" }, StringSplitOptions.None).Last(),
                Detail = detail, Description = "Suggested because " + why + "."
            });

            if (Regex.IsMatch(plain, @"^bool\b")) { Smart("true", "bool"); Smart("false", "bool"); return; }
            if (plain.Contains("*") || Regex.IsMatch(plain, @"\b(TObjectPtr|TWeakObjectPtr|TSharedPtr|TUniquePtr|TSoftObjectPtr)\b"))
            {
                Smart("nullptr", "pointer");
                if (why.StartsWith("returns")) Smart("this", "pointer");
                return;
            }
            var enumType = CleanType(plain);
            if (enumType == null) return;
            var values = cache.GetMembers(enumType).Where(v => v.Kind == UnrealSymbolKind.EnumValue).OrderBy(v => v.LineNumber).ToList();
            if (values.Count == 0) return;
            HashSet<string> handled = null;
            if (why.StartsWith("switch"))
            {
                // Hide values that already have a case in this switch.
                handled = new HashSet<string>(Regex.Matches(masked.Substring(Math.Max(0, spanStart - 8000), Math.Min(8000, spanStart)),
                    @"\bcase\s+(?:[\w:]*::)?(\w+)\s*:").Cast<Match>().Select(m => m.Groups[1].Value));
            }
            foreach (var v in values.Where(v => !v.Name.EndsWith("_MAX") && v.Name != "MAX" && (handled == null || !handled.Contains(v.Name))))
                Smart(enumType + "::" + v.Name, enumType);
        }

        private static string SwitchEnum(CacheService cache, string masked, int position, string currentClass)
        {
            int windowStart = Math.Max(0, position - 20000);
            Match best = null;
            foreach (Match m in Regex.Matches(masked.Substring(windowStart, position - windowStart), @"\bswitch\s*\(\s*([^()]{1,120}?)\s*\)\s*\{"))
            {
                int open = windowStart + m.Index + m.Length - 1;
                int close = CodeContext.MatchBrace(masked, open);
                if (close < 0 || close >= position) best = m;
            }
            if (best == null) return null;
            var expr = best.Groups[1].Value.Trim();
            var last = Regex.Match(expr, @"([A-Za-z_]\w*)\s*(\(\s*\))?\s*$");
            if (!last.Success) return null;
            var type = last.Groups[2].Success
                ? ResolveType(cache, masked, position, last.Groups[1].Value, currentClass, true)
                : CleanType(RawType(cache, masked, windowStart + best.Index, last.Groups[1].Value, currentClass));
            return type != null && cache.GetByExactName(type).Any(s => s.Kind == UnrealSymbolKind.Enum) ? type : null;
        }

        // ── Override suggestions ────────────────────────────────────

        private static bool InClassBodyDirectly(string masked, int position, string currentClass)
        {
            // The innermost '{' before the caret must be the class's own brace (not a function body inside it).
            int depth = 0;
            for (int i = position - 1; i >= 0 && position - i < 60000; i--)
            {
                char c = masked[i];
                if (c == '}') depth++;
                else if (c == '{')
                {
                    if (depth > 0) { depth--; continue; }
                    var head = masked.Substring(Math.Max(0, i - 300), Math.Min(300, i));
                    return Regex.IsMatch(head, @"\b(class|struct)\s+(?:\w+_API\s+)?" + Regex.Escape(currentClass) + @"\b[^;{]*$");
                }
            }
            return false;
        }

        private static void AddOverrides(PenguinCompletionModel model, CacheService cache, string masked, string currentClass, bool afterVirtual)
        {
            var bases = Chain(cache, currentClass, masked).Skip(1).Take(8).ToList();
            if (bases.Count == 0) return;
            var body = masked; // the whole file is fine for "already overridden"
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var baseClass in bases)
                foreach (var m in cache.GetMembers(baseClass))
                {
                    if (m.Kind != UnrealSymbolKind.Function || m.Signature == null || !Regex.IsMatch(m.Signature, @"\bvirtual\b")) continue;
                    if (m.Name.StartsWith("~") || !seen.Add(m.Name)) continue;
                    if (Regex.IsMatch(body, @"\b" + Regex.Escape(m.Name) + @"\s*\([^;{]*\)[^;{]*\boverride\b")) continue; // already overridden
                    var sig = Regex.Replace(m.Signature, @"^\s*UFUNCTION\s*\([^)]*\)\s*", "");
                    sig = Regex.Replace(sig, @"\bvirtual\s+", "");
                    sig = Regex.Replace(sig, @"\s*(=\s*0|override|final|PURE_VIRTUAL\s*\(.*\))\s*", " ").Trim().TrimEnd(';').Trim();
                    var insert = (afterVirtual ? "" : "virtual ") + sig + " override;";
                    model.Items.Add(new PenguinItem
                    {
                        Kind = PenguinItemKind.Smart, Symbol = m, Display = (afterVirtual ? "" : "virtual ") + sig + " override", Insert = insert,
                        FilterText = m.Name, Detail = "override " + baseClass, Description = "Override " + baseClass + "::" + m.Name + "."
                    });
                }
        }

        // ── Small helpers ───────────────────────────────────────────

        private static string IdentBefore(string s, int end)
        {
            int e = end;
            while (e > 0 && char.IsWhiteSpace(s[e - 1])) e--;
            int b = e;
            while (b > 0 && IsIdent(s[b - 1])) b--;
            return b < e && !char.IsDigit(s[b]) ? s.Substring(b, e - b) : null;
        }

        private static int OpenParenBefore(string s)
        {
            int depth = 0;
            for (int i = s.Length - 1; i >= 0; i--)
            {
                if (s[i] == ')') depth++;
                else if (s[i] == '(' && --depth == 0) return i;
            }
            return -1;
        }

        internal static bool IsIdent(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
