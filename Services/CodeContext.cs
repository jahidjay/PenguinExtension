using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace PenguinExtention.Services
{
    /// <summary>A function declaration parsed from header text (no AST — a bounded, comment-aware scanner).</summary>
    internal sealed class FunctionDeclaration
    {
        public string Name;
        /// <summary>Return type without storage specifiers (virtual/static/inline/FORCEINLINE …); empty for constructors/destructors.</summary>
        public string ReturnType = "";
        /// <summary>Parameters as written, default arguments and UPARAM(…) removed.</summary>
        public List<string> Parameters = new List<string>();
        /// <summary>Qualifiers that belong on the definition too: const, noexcept, &, &&.</summary>
        public string DefinitionQualifiers = "";
        public string Owner;
        /// <summary>Contents of a preceding UFUNCTION(…), or null.</summary>
        public string UFunctionSpecifiers;
        public bool IsPureVirtual, IsDefaultedOrDeleted, HasInlineBody, IsTemplate, IsFriend;
        /// <summary>Character range of the whole statement (including the UFUNCTION line) in the scanned text.</summary>
        public int Start, End;

        /// <summary>Normalized parameter types, for overload comparison ("const FString&|int32").</summary>
        public string ParameterTypeKey => string.Join("|", Parameters.Select(CodeContext.ParameterType));

        /// <summary>The name of the C++ body to write: BlueprintNativeEvent and RPCs are implemented as Name_Implementation.</summary>
        public string ImplementationName =>
            UFunctionSpecifiers != null && Regex.IsMatch(UFunctionSpecifiers, @"\b(BlueprintNativeEvent|Server|Client|NetMulticast)\b")
                ? Name + "_Implementation" : Name;

        public bool NeedsValidate => UFunctionSpecifiers != null && Regex.IsMatch(UFunctionSpecifiers, @"\bWithValidation\b");
    }

    /// <summary>
    /// Text helpers shared by commands, completion and lightbulbs. Pure functions over strings — no editor
    /// types — so they are unit-testable and safe on any thread.
    /// </summary>
    internal static class CodeContext
    {
        private static readonly string[] StorageWords =
            { "virtual", "static", "inline", "FORCEINLINE", "FORCENOINLINE", "explicit", "constexpr", "consteval", "UE_NODISCARD", "[[nodiscard]]", "extern" };

        private static readonly Regex RxLeadingMacro = new Regex(
            @"^\s*(UFUNCTION|UE_DEPRECATED|UE_DEPRECATED_FORGAME|DEPRECATED|UE_NODISCARD_CTOR)\s*\(", RegexOptions.Compiled);

        private static readonly Regex RxAccessLabel = new Regex(@"^\s*(public|protected|private)\s*:(?!:)", RegexOptions.Compiled);

        /// <summary>
        /// <paramref name="text"/> with comments, string and char literals replaced by spaces (same length, newlines
        /// kept), so structural scans never see braces or parentheses inside them.
        /// </summary>
        public static string Mask(string text)
        {
            var sb = new StringBuilder(text);
            for (int i = 0; i < sb.Length; i++)
            {
                char c = sb[i];
                if (c == '/' && i + 1 < sb.Length && sb[i + 1] == '/')
                {
                    while (i < sb.Length && sb[i] != '\n') sb[i++] = ' ';
                }
                else if (c == '/' && i + 1 < sb.Length && sb[i + 1] == '*')
                {
                    sb[i++] = ' '; sb[i++] = ' ';
                    while (i < sb.Length && !(sb[i] == '*' && i + 1 < sb.Length && sb[i + 1] == '/')) { if (sb[i] != '\n') sb[i] = ' '; i++; }
                    if (i < sb.Length) { sb[i++] = ' '; if (i < sb.Length) sb[i] = ' '; }
                }
                else if (c == '"' || c == '\'')
                {
                    // Keep the quotes, blank the contents.
                    for (i++; i < sb.Length && sb[i] != c && sb[i] != '\n'; i++)
                    {
                        if (sb[i] == '\\' && i + 1 < sb.Length) { sb[i] = ' '; i++; }
                        sb[i] = ' ';
                    }
                }
            }
            return sb.ToString();
        }

        /// <summary>
        /// The function declaration that contains <paramref name="caret"/>, or that starts on the caret line
        /// (caret on the UFUNCTION line above, on any line of a multi-line declaration, or on its name).
        /// Returns null when the caret is not on a function declaration.
        /// </summary>
        public static FunctionDeclaration DeclarationAtCaret(string text, int caret)
        {
            if (string.IsNullOrEmpty(text)) return null;
            caret = Math.Max(0, Math.Min(caret, text.Length));
            var masked = Mask(text);
            int lineStart = caret > 0 ? masked.LastIndexOf('\n', caret - 1) + 1 : 0;

            // Caret after the terminating ';' on the same line ("void Foo();|") belongs to that declaration.
            int back = caret;
            while (back > lineStart && char.IsWhiteSpace(masked[back - 1])) back--;
            if (back > lineStart && masked[back - 1] == ';') caret = back - 1;

            // Caret on a reflection-macro line: the declaration starts at that line.
            int start = RxLeadingMacro.IsMatch(LineAt(masked, lineStart)) ? lineStart : StatementStart(masked, caret);
            if (start < 0) return null;
            int end = StatementEnd(masked, start, out bool hasBody);
            if (end < 0 || end < caret - 1 && !hasBody) return null;
            if (end - start > 4000) return null;

            var decl = Parse(masked.Substring(start, end - start));
            if (decl == null) return null;
            decl.HasInlineBody = hasBody;
            decl.Start = start;
            decl.End = end + 1;
            decl.Owner = EnclosingClass(masked, start);
            return decl;
        }

        private static string LineAt(string s, int lineStart)
        {
            int nl = s.IndexOf('\n', lineStart);
            return nl < 0 ? s.Substring(lineStart) : s.Substring(lineStart, nl - lineStart);
        }

        /// <summary>Index just after the previous ';', '{', '}' or access label at parenthesis depth 0.</summary>
        private static int StatementStart(string s, int caret)
        {
            int depth = 0;
            for (int i = Math.Min(caret, s.Length) - 1; i >= 0 && caret - i < 4000; i--)
            {
                char c = s[i];
                if (c == ')') depth++;
                else if (c == '(') depth = Math.Max(0, depth - 1);
                else if (depth == 0 && (c == ';' || c == '{' || c == '}')) return SkipAccessLabel(s, i + 1);
            }
            return caret < 4000 ? SkipAccessLabel(s, 0) : -1;
        }

        private static int SkipAccessLabel(string s, int from)
        {
            int nl = from;
            while (true)
            {
                int lineEnd = s.IndexOf('\n', nl);
                var line = lineEnd < 0 ? s.Substring(nl) : s.Substring(nl, lineEnd - nl);
                var m = RxAccessLabel.Match(line);
                if (m.Success) { from = nl + m.Length; nl = from; continue; }
                if (line.Trim().Length == 0 && lineEnd >= 0) { nl = lineEnd + 1; from = nl; continue; }
                // GENERATED_BODY() and similar bare macro statements end without ';'
                var gm = Regex.Match(line, @"^\s*(GENERATED_BODY|GENERATED_UCLASS_BODY|GENERATED_USTRUCT_BODY|GENERATED_IINTERFACE_BODY)\s*\(\s*\)");
                if (gm.Success && lineEnd >= 0) { nl = lineEnd + 1; from = nl; continue; }
                return from;
            }
        }

        /// <summary>Index of the terminating ';' or body '{' at depth 0 after <paramref name="start"/>; -1 if none.</summary>
        private static int StatementEnd(string s, int start, out bool hasBody)
        {
            hasBody = false;
            int depth = 0;
            for (int i = start; i < s.Length && i - start < 4000; i++)
            {
                char c = s[i];
                if (c == '(') depth++;
                else if (c == ')') depth--;
                else if (depth == 0 && c == ';') return i;
                else if (depth == 0 && c == '{')
                {
                    // "= {…}" / "Type Name{…}" initializers are not bodies; a body follows ')' or a qualifier.
                    var before = s.Substring(start, i - start).TrimEnd();
                    if (before.EndsWith("=")) continue;
                    hasBody = true;
                    return i;
                }
                else if (depth == 0 && c == '}') return -1;
            }
            return -1;
        }

        /// <summary>Parses one masked statement ("UFUNCTION(…) virtual void Foo(int32 A = 0) const override").</summary>
        public static FunctionDeclaration Parse(string statement)
        {
            var decl = new FunctionDeclaration();
            var s = statement.Trim();

            // Leading reflection / deprecation macros
            while (true)
            {
                var m = RxLeadingMacro.Match(s);
                if (!m.Success) break;
                int open = m.Index + m.Length - 1;
                int close = MatchParen(s, open);
                if (close < 0) return null;
                if (m.Groups[1].Value == "UFUNCTION") decl.UFunctionSpecifiers = Collapse(s.Substring(open + 1, close - open - 1));
                s = s.Substring(close + 1).TrimStart();
            }
            if (s.StartsWith("template", StringComparison.Ordinal)) { decl.IsTemplate = true; return null; }
            if (Regex.IsMatch(s, @"^(class|struct|enum|union|typedef|using|namespace|return|if|for|while|switch|UPROPERTY|DECLARE_)\b")) return null;
            if (s.StartsWith("friend ", StringComparison.Ordinal)) { decl.IsFriend = true; s = s.Substring(7).TrimStart(); }

            // Parameter list: first '(' at angle/paren depth 0 that follows an identifier (or operator token).
            int paren = -1, angle = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '<') angle++;
                else if (c == '>' && angle > 0) angle--;
                else if (c == '(' && angle == 0) { paren = i; break; }
                else if (c == '=' && angle == 0 && !IsOperatorContext(s, i)) return null; // "int X = Foo(…)" is a field/initializer
            }
            if (paren <= 0) return null;
            int closeParen = MatchParen(s, paren);
            if (closeParen < 0) return null;

            var head = s.Substring(0, paren).TrimEnd();
            int nameStart = head.Length;
            while (nameStart > 0 && (char.IsLetterOrDigit(head[nameStart - 1]) || head[nameStart - 1] == '_' || head[nameStart - 1] == '~')) nameStart--;
            var name = head.Substring(nameStart);
            var opIndex = head.LastIndexOf("operator", StringComparison.Ordinal);
            if (opIndex >= 0 && (opIndex == 0 || !char.IsLetterOrDigit(head[opIndex - 1])))
            {
                name = Collapse(head.Substring(opIndex));
                nameStart = opIndex;
            }
            if (name.Length == 0 || char.IsDigit(name[0])) return null;
            // Strip an explicit qualification ("void AFoo::Bar(") — that is a definition, not a declaration.
            var ret = head.Substring(0, nameStart).TrimEnd();
            if (ret.EndsWith("::")) return null;

            // Storage words out of the return type
            foreach (var w in StorageWords)
                ret = Regex.Replace(ret, @"(^|\s)" + Regex.Escape(w) + @"(?=\s|$)", " ");
            ret = Regex.Replace(ret, @"\b\w+_API\b", " "); // MYGAME_API on free functions
            decl.ReturnType = Collapse(ret);
            if (decl.ReturnType.Length == 0 && !(name.StartsWith("~") || char.IsUpper(name[0]))) return null;
            // Something like "if (x)" or a macro call statement: reject when the "return type" isn't type-like.
            if (decl.ReturnType.Length > 0 && !Regex.IsMatch(decl.ReturnType, @"^[\w:<>,\s\*&\[\]]+$")) return null;
            decl.Name = name;

            decl.Parameters = SplitTopLevel(s.Substring(paren + 1, closeParen - paren - 1))
                .Select(CleanParameter).Where(p => p.Length > 0 && p != "void").ToList();

            var tail = Collapse(s.Substring(closeParen + 1));
            decl.IsPureVirtual = Regex.IsMatch(tail, @"=\s*0\b") || tail.Contains("PURE_VIRTUAL");
            decl.IsDefaultedOrDeleted = Regex.IsMatch(tail, @"=\s*(default|delete)\b");
            var quals = new List<string>();
            if (Regex.IsMatch(tail, @"(^|\s)const\b")) quals.Add("const");
            if (Regex.IsMatch(tail, @"(^|\s)&&")) quals.Add("&&");
            else if (Regex.IsMatch(tail, @"(^|\s)&(?!&)")) quals.Add("&");
            var noexcept = Regex.Match(tail, @"\bnoexcept(\s*\([^)]*\))?");
            if (noexcept.Success) quals.Add(noexcept.Value);
            decl.DefinitionQualifiers = string.Join(" ", quals);
            // Anything else left in the tail must be a known qualifier, or this wasn't a declaration.
            var leftover = Regex.Replace(tail, @"\b(const|override|final|noexcept(\s*\([^)]*\))?|PURE_VIRTUAL\s*\(.*\)|throw\s*\(\s*\))|=\s*(0|default|delete)\b|&&|&", " ").Trim();
            if (leftover.Length > 0 && !Regex.IsMatch(leftover, @"^(\w+_API|UE_\w+(\s*\(.*\))?)$")) return null;
            return decl;
        }

        private static bool IsOperatorContext(string s, int i)
        {
            int k = s.LastIndexOf("operator", i, StringComparison.Ordinal);
            return k >= 0 && i - k <= 10;
        }

        /// <summary>"const FString& Name = TEXT(\"\")" → "const FString& Name"; UPARAM(ref) removed.</summary>
        public static string CleanParameter(string p)
        {
            var t = Regex.Replace(p, @"\bUPARAM\s*\([^)]*\)", " ");
            int depth = 0;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == '(' || c == '<' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == '>' || c == ']' || c == '}') depth--;
                else if (c == '=' && depth == 0) { t = t.Substring(0, i); break; }
            }
            return Collapse(t);
        }

        /// <summary>The type part of a parameter ("const FString& Name" → "const FString&").</summary>
        public static string ParameterType(string p)
        {
            var t = p.Trim();
            var m = Regex.Match(t, @"^(?<type>.*?[\s\*&>])(?<name>[A-Za-z_]\w*)(\s*\[[^\]]*\])?$");
            var type = m.Success && !IsBareTypeWord(t) ? m.Groups["type"].Value : t;
            return Regex.Replace(type, @"\s+", "");
        }

        private static bool IsBareTypeWord(string t) => Regex.IsMatch(t, @"^(const\s+)?(unsigned\s+|signed\s+)?\w+$");

        /// <summary>Splits on commas at depth 0 of (), &lt;&gt;, [], {}.</summary>
        public static List<string> SplitTopLevel(string s)
        {
            var parts = new List<string>();
            int depth = 0, last = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '<' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == '>' || c == ']' || c == '}') depth = Math.Max(0, depth - 1);
                else if (c == ',' && depth == 0) { parts.Add(s.Substring(last, i - last)); last = i + 1; }
            }
            parts.Add(s.Substring(last));
            return parts;
        }

        /// <summary>Index of the ')' matching the '(' at <paramref name="open"/> in masked text; -1 if none.</summary>
        public static int MatchParen(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')' && --depth == 0) return i;
            }
            return -1;
        }

        private static string Collapse(string s) => Regex.Replace(s ?? "", @"\s+", " ").Trim();

        /// <summary>The innermost class/struct whose body contains <paramref name="position"/>, from masked text.</summary>
        public static string EnclosingClass(string masked, int position)
        {
            string best = null; int bestOpen = -1;
            foreach (Match m in RxClassHead.Matches(masked))
            {
                int open = m.Index + m.Length - 1;
                if (open >= position) break;
                int close = MatchBrace(masked, open);
                if ((close > position || close < 0) && open > bestOpen) { best = m.Groups[1].Value; bestOpen = open; }
            }
            return best;
        }

        // class|struct [alignas(n)] [X_API] Name [final] [: bases] {   — not "class" inside template args (TSubclassOf<class AActor>)
        private static readonly Regex RxClassHead = new Regex(
            @"(?<![<,]\s*)\b(?:class|struct)\s+(?:alignas\s*\([^)]*\)\s*)?(?:\w+_API\s+)?(\w+)\b(?:\s+final)?\s*(?::[^;{]*)?\{",
            RegexOptions.Compiled);

        /// <summary>Index of the '}' matching the '{' at <paramref name="open"/> in masked text; -1 if none.</summary>
        public static int MatchBrace(string masked, int open)
        {
            int depth = 0;
            for (int i = open; i < masked.Length; i++)
            {
                if (masked[i] == '{') depth++;
                else if (masked[i] == '}' && --depth == 0) return i;
            }
            return -1;
        }

        /// <summary>The out-of-class definition skeleton(s) for <paramref name="d"/> (VA "Create Implementation").</summary>
        public static string DefinitionText(FunctionDeclaration d, string newline, string indent, out int caretOffset)
        {
            var sb = new StringBuilder();
            var qualified = (string.IsNullOrEmpty(d.Owner) ? "" : d.Owner + "::");
            var parameters = string.Join(", ", d.Parameters);
            var quals = d.DefinitionQualifiers.Length > 0 ? " " + d.DefinitionQualifiers : "";
            var ret = d.ReturnType.Length > 0 ? d.ReturnType + " " : "";

            sb.Append(newline);
            if (d.NeedsValidate)
            {
                sb.Append("bool ").Append(qualified).Append(d.Name).Append("_Validate(").Append(parameters).Append(")").Append(quals).Append(newline)
                  .Append('{').Append(newline).Append(indent).Append("return true;").Append(newline).Append('}').Append(newline).Append(newline);
            }
            sb.Append(ret).Append(qualified).Append(d.ImplementationName).Append('(').Append(parameters).Append(')').Append(quals).Append(newline);
            sb.Append('{').Append(newline).Append(indent);
            var body = DefaultBody(d);
            sb.Append(body);
            caretOffset = sb.Length; // end of the first body line
            sb.Append(newline).Append('}').Append(newline);
            return sb.ToString();
        }

        /// <summary>Super:: call for UE lifecycle overrides; otherwise empty.</summary>
        private static string DefaultBody(FunctionDeclaration d)
        {
            var superCalls = new HashSet<string> { "BeginPlay", "EndPlay", "Tick", "PostInitializeComponents", "OnConstruction",
                "SetupPlayerInputComponent", "PossessedBy", "UnPossessed", "NativeConstruct", "NativeDestruct", "NativeTick",
                "BeginDestroy", "PostLoad", "PostEditChangeProperty", "GetLifetimeReplicatedProps", "InitializeComponent",
                "TickComponent", "UninitializeComponent", "NativeOnInitialized", "Destroyed", "NotifyHit", "NotifyActorBeginOverlap",
                "NotifyActorEndOverlap", "Landed", "OnRep_PlayerState", "OnRep_Controller" };
            if (string.IsNullOrEmpty(d.Owner) || !superCalls.Contains(d.Name) || d.ImplementationName != d.Name) return "";
            var args = string.Join(", ", d.Parameters.Select(ParameterName).Where(n => n != null));
            return (d.ReturnType == "void" ? "" : "return ") + "Super::" + d.Name + "(" + args + ");";
        }

        public static string ParameterName(string p)
        {
            var m = Regex.Match(p.Trim(), @"([A-Za-z_]\w*)(\s*\[[^\]]*\])?$");
            return m.Success && !IsBareTypeWord(p.Trim()) ? m.Groups[1].Value : null;
        }

        /// <summary>
        /// Finds an existing out-of-class definition of <paramref name="d"/> in source text: index of the name, or -1.
        /// Overloads are told apart by parameter types.
        /// </summary>
        public static int FindDefinition(string sourceText, FunctionDeclaration d)
        {
            var masked = Mask(sourceText);
            var qualified = (string.IsNullOrEmpty(d.Owner) ? "" : Regex.Escape(d.Owner) + @"\s*::\s*") + Regex.Escape(d.ImplementationName);
            foreach (Match m in Regex.Matches(masked, @"(?<![\w:])" + qualified + @"\s*\("))
            {
                int open = m.Index + m.Length - 1;
                int close = MatchParen(masked, open);
                if (close < 0) continue;
                // A definition is followed by qualifiers and '{' (or ':' for constructor init lists), not ';'.
                int k = close + 1;
                while (k < masked.Length && (char.IsWhiteSpace(masked[k]) || char.IsLetter(masked[k]) || masked[k] == '&')) k++;
                if (k >= masked.Length || (masked[k] != '{' && masked[k] != ':')) continue;
                var parameters = SplitTopLevel(masked.Substring(open + 1, close - open - 1)).Select(CleanParameter).Where(p => p.Length > 0 && p != "void");
                if (string.Join("|", parameters.Select(ParameterType)) == d.ParameterTypeKey) return m.Index;
            }
            return -1;
        }
    }
}
