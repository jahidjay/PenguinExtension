using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using PenguinExtention.Core;
using PenguinExtention.Models;
using PenguinExtention.Services;
using PenguinExtention.UI;

namespace PenguinExtention.Commands
{
    /// <summary>
    /// Visual Assist–style refactorings. All are text-based (no AST): every edit goes through the editor,
    /// is left unsaved, and is undoable (multi-file edits share one linked undo).
    /// </summary>
    internal static class RefactorCommands
    {
        private static CancellationToken Token => PenguinExtensionPackage.Instance.DisposalToken;

        // class|struct [X_API] Name [final] [: bases] {
        private static readonly Regex RxClassHead = new Regex(
            @"\b(?:class|struct)\s+(?:\w+_API\s+)?(\w+)\b[^;{()]*\{", RegexOptions.Compiled);

        // [ret] Owner::Name(params) [const] — a definition line in a .cpp
        private static readonly Regex RxDefinition = new Regex(
            @"^\s*(?<ret>[A-Za-z_][\w:<>,\*&\s]{0,160}?[\s\*&])?(?<owner>\w+)\s*::\s*(?<name>~?\w+)\s*\((?<params>[^()]*(?:\([^()]*\)[^()]*){0,4})\)\s*(?<q>const)?",
            RegexOptions.Compiled);

        // [specifiers] ret Name(params) [const] [override] ... on one header line
        private static readonly Regex RxDeclaration = new Regex(
            @"^\s*(?<spec>(?:(?:virtual|static|inline|FORCEINLINE|FORCENOINLINE|explicit|constexpr)\s+){0,4})(?<ret>[A-Za-z_][\w:<>,\*&\s]{0,160}?[\s\*&])(?<name>~?\w+)\s*\((?<params>[^()]*(?:\([^()]*\)[^()]*){0,4})\)(?<tail>[^{;]*)",
            RegexOptions.Compiled);

        // Type Name [= init | {init}] ;   (member field)
        private static readonly Regex RxField = new Regex(
            @"^\s*(?<type>[A-Za-z_][\w:<>,\*&\s]{0,160}?[\s\*&])(?<name>[A-Za-z_]\w*)\s*(?:=[^;]*|\{[^;]*\})?;\s*(?://.*)?$",
            RegexOptions.Compiled);

        private static readonly Regex RxAccess = new Regex(@"^\s*(public|protected|private)\s*:", RegexOptions.Compiled | RegexOptions.Multiline);

        // ── Find References (VA: Shift+Alt+F) ───────────────────────

        public static async Task FindReferencesAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (!BackendService.IsReady || view == null) { PenguinEditor.Status(BackendService.Status); return; }
            PenguinEditor.Status("Finding references…");
            var result = await CollectAsync(view);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (result.Item1 == null) { PenguinEditor.Status("No indexed symbol under the caret."); return; }
            await FindReferencesWindow.ShowAsync(result.Item1.DisplayText, result.Item2);
            PenguinEditor.Status(result.Item2.Count + " reference(s) to " + result.Item1.DisplayText + ".");
        }

        private static async Task<Tuple<UnrealSymbol, List<SymbolOccurrence>>> CollectAsync(ITextView view)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(Token);
            var file = PenguinEditor.FilePathOf(view);
            var text = view.TextSnapshot.GetText();
            var symbol = await PenguinEditor.SymbolAtCaretAsync(view, Token);
            if (symbol == null) return Tuple.Create<UnrealSymbol, List<SymbolOccurrence>>(null, null);
            var live = file == null ? null : new Dictionary<string, string> { { file, text } };
            var refs = await BackendService.ReferencesAsync(symbol, Token, live).ConfigureAwait(false);
            return Tuple.Create(symbol, refs);
        }

        // ── Rename (VA: Shift+Alt+R) ────────────────────────────────

        public static async Task RenameAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (!BackendService.IsReady || view == null) { PenguinEditor.Status(BackendService.Status); return; }
            PenguinEditor.Status("Collecting references…");
            var result = await CollectAsync(view);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var symbol = result.Item1;
            if (symbol == null) { PenguinEditor.Status("No indexed symbol under the caret."); return; }
            if (result.Item2.Count == 0) { PenguinEditor.Status("No occurrences of " + symbol.Name + " found."); return; }

            var redirectLabel = SupportsRedirect(symbol)
                ? "Add Unreal CoreRedirect to Config/DefaultEngine.ini (keeps Blueprints and saved assets working)" : null;
            var dialog = RenameDialog.Show(symbol, result.Item2, redirectLabel, PenguinOptionsPage.Current?.AddCoreRedirectsOnRename == true);
            if (dialog == null) return;

            var changes = dialog.Chosen.Select(o => new TextChange
            {
                FilePath = o.FilePath, LineNumber = o.LineNumber, ColumnNumber = o.ColumnNumber,
                Length = symbol.Name.Length, NewText = dialog.NewName, ExpectedText = symbol.Name
            }).ToList();

            string note = "";
            if (dialog.AddCoreRedirect)
            {
                var newName = dialog.NewName;
                var redirect = await Task.Run(() => RedirectChange(symbol, newName));
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (redirect != null) { changes.Add(redirect); note = " + CoreRedirect"; }
                else note = " (no Config/DefaultEngine.ini found — CoreRedirect skipped)";
            }

            int applied = PenguinEditor.ApplyChanges(changes, "Rename " + symbol.Name + " → " + dialog.NewName);
            PenguinEditor.Status($"Renamed {applied} of {changes.Count} edit(s){note}. Files are unsaved; Edit > Undo reverts all.");
        }

        private static bool SupportsRedirect(UnrealSymbol s) =>
            s.Kind == UnrealSymbolKind.Property || s.Kind == UnrealSymbolKind.Function || s.Kind == UnrealSymbolKind.Class ||
            s.Kind == UnrealSymbolKind.Struct || s.Kind == UnrealSymbolKind.Enum || s.Kind == UnrealSymbolKind.Interface;

        /// <summary>Module that owns <paramref name="file"/>: the folder directly under the nearest "Source" folder.</summary>
        internal static string ModuleOf(string file)
        {
            var parts = Path.GetFullPath(file).Split(Path.DirectorySeparatorChar);
            for (int i = parts.Length - 2; i >= 0; i--)
                if (parts[i].Equals("Source", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length - 1) return parts[i + 1];
            return null;
        }

        /// <summary>Script object name without the C++ type prefix (AMyActor → MyActor, FMyStruct → MyStruct; enums keep E).</summary>
        private static string ScriptName(string cppName) =>
            AcronymIndex.HasUnrealPrefix(cppName) && cppName[0] != 'E' ? cppName.Substring(1) : cppName;

        /// <summary>Builds the ini insertion for a CoreRedirect. Background thread (file I/O).</summary>
        internal static TextChange RedirectChange(UnrealSymbol symbol, string newName)
        {
            var root = UnrealProjectDetector.Instance?.ProjectRoot;
            var module = ModuleOf(symbol.FilePath);
            if (root == null || module == null) return null;
            var ini = Path.Combine(root, "Config", "DefaultEngine.ini");
            if (!File.Exists(ini)) return null;

            var script = "/Script/" + module + ".";
            string entry;
            switch (symbol.Kind)
            {
                case UnrealSymbolKind.Property:
                    entry = $"+PropertyRedirects=(OldName=\"{script}{ScriptName(symbol.OwnerClass ?? "")}.{symbol.Name}\",NewName=\"{newName}\")"; break;
                case UnrealSymbolKind.Function:
                    entry = $"+FunctionRedirects=(OldName=\"{script}{ScriptName(symbol.OwnerClass ?? "")}.{symbol.Name}\",NewName=\"{newName}\")"; break;
                case UnrealSymbolKind.Struct:
                    entry = $"+StructRedirects=(OldName=\"{script}{ScriptName(symbol.Name)}\",NewName=\"{script}{ScriptName(newName)}\")"; break;
                case UnrealSymbolKind.Enum:
                    entry = $"+EnumRedirects=(OldName=\"{script}{symbol.Name}\",NewName=\"{script}{newName}\")"; break;
                default:
                    entry = $"+ClassRedirects=(OldName=\"{script}{ScriptName(symbol.Name)}\",NewName=\"{script}{ScriptName(newName)}\")"; break;
            }

            var lines = File.ReadAllLines(ini);
            int section = Array.FindIndex(lines, l => l.Trim().Equals("[CoreRedirects]", StringComparison.OrdinalIgnoreCase));
            if (section < 0)
            {
                int last = Math.Max(0, lines.Length - 1);
                int col = lines.Length == 0 ? 0 : lines[last].Length;
                return new TextChange { FilePath = ini, LineNumber = last + 1, ColumnNumber = col, Length = 0,
                    NewText = Environment.NewLine + Environment.NewLine + "[CoreRedirects]" + Environment.NewLine + entry + Environment.NewLine };
            }
            // Append after the last non-blank line of the section.
            int end = section;
            for (int i = section + 1; i < lines.Length && !lines[i].TrimStart().StartsWith("["); i++)
                if (lines[i].Trim().Length > 0) end = i;
            return new TextChange { FilePath = ini, LineNumber = end + 1, ColumnNumber = lines[end].Length, Length = 0,
                NewText = Environment.NewLine + entry };
        }

        // ── Add Include ─────────────────────────────────────────────

        public static async Task AddIncludeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (!BackendService.IsReady || view == null) { PenguinEditor.Status(BackendService.Status); return; }
            var file = PenguinEditor.FilePathOf(view);
            var snapshot = view.TextSnapshot;
            var symbol = await PenguinEditor.SymbolAtCaretAsync(view, Token);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (symbol == null || string.IsNullOrEmpty(symbol.FilePath)) { PenguinEditor.Status("No indexed symbol under the caret."); return; }
            if (PenguinEditor.SameFile(symbol.FilePath, file)) { PenguinEditor.Status(symbol.Name + " is declared in this file."); return; }

            var text = snapshot.GetText();
            var plan = await Task.Run(() => IncludeInserter.For(symbol, file, text));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!plan.Needed) { PenguinEditor.Status("No include needed for " + symbol.Name + ": " + plan.Reason + "."); return; }
            if (view.TextSnapshot != snapshot) { PenguinEditor.Status("The file changed; run Add Include again."); return; }
            PenguinEditor.InsertInclude(view, plan);
        }

        // ── Create Declaration (from a .cpp definition) ─────────────

        public static async Task CreateDeclarationAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (view == null) return;
            var file = PenguinEditor.FilePathOf(view);
            var lineText = view.Caret.Position.BufferPosition.GetContainingLine().GetText();
            var m = RxDefinition.Match(lineText);
            if (!m.Success) { PenguinEditor.Status("Place the caret on a definition line like 'void AMyActor::Foo(...)'."); return; }
            var owner = m.Groups["owner"].Value;
            var name = m.Groups["name"].Value;
            var ret = m.Groups["ret"].Success ? m.Groups["ret"].Value.Trim() : "";
            var decl = (ret.Length > 0 ? ret + " " : "") + name + "(" + m.Groups["params"].Value.Trim() + ")" + (m.Groups["q"].Success ? " const" : "") + ";";

            await SolutionFileIndex.GetAsync(Token);
            var header = await Task.Run(() =>
            {
                var partner = HeaderSourceResolver.Partner(file);
                if (partner != null && File.ReadAllText(partner).Contains(owner)) return partner;
                return SolutionFileIndex.Files.FirstOrDefault(f => HeaderSourceResolver.IsHeader(f) &&
                    RxClassHead.Matches(SafeRead(f)).Cast<Match>().Any(c => c.Groups[1].Value == owner));
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (header == null) { PenguinEditor.Status("No header declaring " + owner + " was found."); return; }

            var headerView = PenguinEditor.Open(header);
            if (headerView == null) return;
            var headerText = headerView.TextSnapshot.GetText();
            var body = FindClassBody(headerText, owner);
            if (body == null) { PenguinEditor.Status("Could not find the body of " + owner + " in " + Path.GetFileName(header) + "."); return; }
            var classText = headerText.Substring(body.Item1, body.Item2 - body.Item1);
            if (Regex.IsMatch(classText, @"[\s\*&~]" + Regex.Escape(name.TrimStart('~')) + @"\s*\("))
            { PenguinEditor.Status(owner + "::" + name + " already has a declaration (check overloads manually)."); return; }

            // Insert before the closing brace, switching to public: unless the class already ends in a public section.
            var accesses = RxAccess.Matches(classText);
            bool endsPublic = accesses.Count > 0 ? accesses[accesses.Count - 1].Groups[1].Value == "public"
                                                 : headerText.Substring(Math.Max(0, body.Item1 - 200), Math.Min(200, body.Item1)).Contains("struct ");
            var indent = IndentOfClassMembers(classText);
            var insert = (endsPublic ? "" : "public:" + Environment.NewLine) + indent + decl + Environment.NewLine;
            int closeLineStart = headerText.LastIndexOf('\n', body.Item2 - 1) + 1;
            EditorAccess.Insert(headerView, closeLineStart, insert, "Create declaration " + name);
            PenguinEditor.Status("Declared " + owner + "::" + name + " in " + Path.GetFileName(header) + " (unsaved, undoable).");
        }

        // ── Move Implementation to Source ───────────────────────────

        public static async Task MoveImplementationAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (view == null) return;
            var header = PenguinEditor.FilePathOf(view);
            if (!HeaderSourceResolver.IsHeader(header)) { PenguinEditor.Status("Move Implementation works on an inline body in a header."); return; }
            var text = view.TextSnapshot.GetText();
            int caret = view.Caret.Position.BufferPosition.Position;

            var fn = FindEnclosingInlineFunction(text, caret);
            if (fn == null) { PenguinEditor.Status("Place the caret on or inside an inline member function body."); return; }
            var owner = EnclosingClass(text, fn.SignatureStart);
            if (owner == null) { PenguinEditor.Status("The function is not inside a class/struct body."); return; }

            var signature = text.Substring(fn.SignatureStart, fn.BodyStart - fn.SignatureStart).Trim();
            var bodyText = text.Substring(fn.BodyStart, fn.BodyEnd - fn.BodyStart);
            var definition = DefinitionSignature(signature, owner);
            if (definition == null) { PenguinEditor.Status("Could not parse the function signature."); return; }

            await SolutionFileIndex.GetAsync(Token);
            var cpp = await Task.Run(() => HeaderSourceResolver.Partner(header));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (cpp == null) { PenguinEditor.Status("Create the matching .cpp first (beside the header or under Private)."); return; }

            int indent = fn.SignatureStart - (text.LastIndexOf('\n', Math.Max(0, fn.SignatureStart - 1)) + 1);
            var movedBody = Dedent(bodyText, indent);
            var cppText = File.ReadAllText(cpp);
            var cppLines = cppText.Split('\n');
            int lastLine = cppLines.Length - 1;
            var block = Environment.NewLine + definition + Environment.NewLine + movedBody.TrimEnd() + Environment.NewLine;

            var changes = new List<TextChange>
            {
                // Header: replace "signature {body}" with "signature;"
                Change(header, text, fn.SignatureStart, fn.BodyEnd - fn.SignatureStart, signature + ";",
                       text.Substring(fn.SignatureStart, fn.BodyEnd - fn.SignatureStart)),
                new TextChange { FilePath = cpp, LineNumber = lastLine + 1, ColumnNumber = cppLines[lastLine].TrimEnd('\r').Length, Length = 0, NewText = block }
            };
            int applied = PenguinEditor.ApplyChanges(changes, "Move implementation of " + owner);
            PenguinEditor.Status(applied == 2 ? "Moved implementation to " + Path.GetFileName(cpp) + " (unsaved; one Undo reverts both files)."
                                              : "Move failed: the header changed. Run the command again.");
        }

        private sealed class InlineFunction { public int SignatureStart, BodyStart, BodyEnd; }

        /// <summary>The inline function whose signature line or body contains <paramref name="caret"/>.</summary>
        private static InlineFunction FindEnclosingInlineFunction(string text, int caret)
        {
            // Walk back over lines (max 400) looking for a signature whose body spans the caret.
            int lineStart = text.LastIndexOf('\n', Math.Max(0, Math.Min(caret, text.Length) - 1)) + 1;
            for (int tries = 0; tries < 400 && lineStart >= 0; tries++)
            {
                int lineEnd = text.IndexOf('\n', lineStart);
                if (lineEnd < 0) lineEnd = text.Length;
                var line = text.Substring(lineStart, lineEnd - lineStart);
                var m = RxDeclaration.Match(line);
                if (m.Success && !line.TrimEnd().EndsWith(";") && !ControlKeywords.Contains(m.Groups["name"].Value) &&
                    !ControlKeywords.Contains(m.Groups["ret"].Value.Trim().Split(' ')[0]))
                {
                    int open = text.IndexOf('{', lineStart + m.Index + m.Length);
                    int semi = text.IndexOf(';', lineStart + m.Index + m.Length);
                    if (open >= 0 && (semi < 0 || open < semi) && OnlyQualifiersBetween(text, lineStart + m.Index + m.Length, open))
                    {
                        int close = MatchBrace(text, open);
                        if (close > 0 && caret <= close + 1)
                        {
                            int bodyEnd = close + 1;
                            if (bodyEnd < text.Length && text[bodyEnd] == ';') bodyEnd++; // "{ ... };"
                            return new InlineFunction { SignatureStart = lineStart + (line.Length - line.TrimStart().Length), BodyStart = open, BodyEnd = bodyEnd };
                        }
                        return null;
                    }
                }
                if (lineStart == 0) break;
                lineStart = lineStart >= 2 ? text.LastIndexOf('\n', lineStart - 2) + 1 : 0;
            }
            return null;
        }

        private static readonly HashSet<string> ControlKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "else", "for", "while", "do", "switch", "case", "return", "catch", "new", "delete", "throw", "sizeof", "co_return"
        };

        private static bool OnlyQualifiersBetween(string text, int from, int to)
        {
            var between = text.Substring(from, to - from);
            return Regex.IsMatch(between, @"^[\s\w]*$"); // const / override / final / noexcept / whitespace
        }

        /// <summary>Converts an in-class signature into an out-of-class definition signature.</summary>
        internal static string DefinitionSignature(string signature, string owner)
        {
            var m = RxDeclaration.Match(signature.Replace("\r", "").Replace("\n", " "));
            if (!m.Success) return null;
            var ret = m.Groups["ret"].Value.Trim();
            var name = m.Groups["name"].Value;
            var parameters = StripDefaultArguments(m.Groups["params"].Value.Trim());
            var tail = m.Groups["tail"].Value;
            var quals = new List<string>();
            if (Regex.IsMatch(tail, @"\bconst\b")) quals.Add("const");
            if (Regex.IsMatch(tail, @"\bnoexcept\b")) quals.Add("noexcept");
            return (ret.Length > 0 ? ret + " " : "") + owner + "::" + name + "(" + parameters + ")" + (quals.Count > 0 ? " " + string.Join(" ", quals) : "");
        }

        internal static string StripDefaultArguments(string parameters)
        {
            var parts = SplitTopLevel(parameters);
            return string.Join(", ", parts.Select(p =>
            {
                int eq = TopLevelIndexOf(p, '=');
                return (eq >= 0 ? p.Substring(0, eq) : p).Trim();
            }).Where(p => p.Length > 0));
        }

        // ── Encapsulate Field ───────────────────────────────────────

        public static async Task EncapsulateFieldAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (view == null) return;
            var snapshot = view.TextSnapshot;
            var line = view.Caret.Position.BufferPosition.GetContainingLine();
            var m = RxField.Match(line.GetText());
            if (!m.Success || line.GetText().Contains("(")) { PenguinEditor.Status("Place the caret on a member field declaration (Type Name;)."); return; }
            var type = m.Groups["type"].Value.Trim();
            var name = m.Groups["name"].Value;
            var text = snapshot.GetText();
            var owner = EnclosingClass(text, line.Start.Position);
            if (owner == null) { PenguinEditor.Status("The field is not inside a class/struct body."); return; }
            var body = FindClassBody(text, owner);
            if (body == null) { PenguinEditor.Status("Could not find the body of " + owner + "."); return; }

            var clean = name.Length > 1 && name[0] == 'b' && char.IsUpper(name[1]) ? name.Substring(1) : char.ToUpperInvariant(name[0]) + name.Substring(1);
            bool cheap = type.EndsWith("*") || new[] { "bool", "int32", "uint8", "int", "float", "double", "int64", "uint32", "uint16", "int16", "int8", "uint64" }.Contains(type) || type.StartsWith("E");
            var getterType = cheap ? type : "const " + type + "&";
            var getterName = (type == "bool" ? (name.StartsWith("b") ? "Is" : "Get") : "Get") + clean;
            if (type == "bool" && clean.StartsWith("Is")) getterName = clean;
            var indent = IndentOfClassMembers(text.Substring(body.Item1, body.Item2 - body.Item1));
            var nl = Environment.NewLine;
            var accessors = $"{nl}public:{nl}{indent}{getterType} {getterName}() const {{ return {name}; }}{nl}{indent}void Set{clean}({getterType} New{clean}) {{ {name} = New{clean}; }}{nl}";

            // Make the field private (unless it already is). UPROPERTY with Blueprint access needs AllowPrivateAccess.
            var changes = new List<TextChange>();
            var path = PenguinEditor.FilePathOf(view);
            var access = CurrentAccess(text, body.Item1, line.Start.Position, owner);
            var prevLine = line.LineNumber > 0 ? snapshot.GetLineFromLineNumber(line.LineNumber - 1) : null;
            bool hasUProperty = prevLine != null && prevLine.GetText().TrimStart().StartsWith("UPROPERTY(");
            if (access != "private")
            {
                var declStart = hasUProperty ? prevLine.Start.Position : line.Start.Position;
                var declText = text.Substring(declStart, line.EndIncludingLineBreak.Position - declStart);
                if (hasUProperty && Regex.IsMatch(declText, @"Blueprint(?:ReadWrite|ReadOnly)") && !declText.Contains("AllowPrivateAccess"))
                    declText = AddAllowPrivateAccess(declText);
                changes.Add(Change(path, text, declStart, line.EndIncludingLineBreak.Position - declStart, "", text.Substring(declStart, line.EndIncludingLineBreak.Position - declStart)));
                accessors += $"{nl}private:{nl}{declText.TrimEnd('\r', '\n')}{nl}";
            }
            int closeLineStart = text.LastIndexOf('\n', body.Item2 - 1) + 1;
            changes.Add(Change(path, text, closeLineStart, 0, accessors.TrimStart('\r', '\n'), ""));
            int applied = PenguinEditor.ApplyChanges(changes, "Encapsulate " + name);
            PenguinEditor.Status($"Encapsulated {name}: {getterName}/Set{clean}. Use Find References (Alt+U, F) to update outside uses.");
        }

        private static string AddAllowPrivateAccess(string declText)
        {
            int metaIdx = declText.IndexOf("meta", StringComparison.Ordinal);
            if (metaIdx >= 0)
            {
                int open = declText.IndexOf('(', metaIdx);
                if (open > 0) return declText.Insert(open + 1, "AllowPrivateAccess = \"true\", ");
            }
            int close = declText.IndexOf(')');
            return close > 0 ? declText.Insert(close, ", meta = (AllowPrivateAccess = \"true\")") : declText;
        }

        // ── Document Method ─────────────────────────────────────────

        public static async Task DocumentMethodAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (view == null) return;
            var line = view.Caret.Position.BufferPosition.GetContainingLine();
            var lineText = line.GetText();
            var m = RxDeclaration.Match(lineText);
            var def = m.Success ? null : RxDefinition.Match(lineText);
            if (!m.Success && (def == null || !def.Success)) { PenguinEditor.Status("Place the caret on a function declaration or definition line."); return; }
            var ret = (m.Success ? m.Groups["ret"].Value : def.Groups["ret"].Value).Trim();
            var parameters = m.Success ? m.Groups["params"].Value : def.Groups["params"].Value;

            var indent = lineText.Substring(0, lineText.Length - lineText.TrimStart().Length);
            var nl = Environment.NewLine;
            var sb = new StringBuilder();
            sb.Append(indent).Append("/**").Append(nl);
            sb.Append(indent).Append(" * ").Append(nl);
            foreach (var p in SplitTopLevel(parameters))
            {
                var pname = ParameterName(p);
                if (pname != null) sb.Append(indent).Append(" * @param ").Append(pname).Append(' ').Append(nl);
            }
            var plainRet = Regex.Replace(ret, @"\b(?:virtual|static|inline|FORCEINLINE|explicit|constexpr)\b", "").Trim();
            if (plainRet.Length > 0 && plainRet != "void") sb.Append(indent).Append(" * @return ").Append(nl);
            sb.Append(indent).Append(" */").Append(nl);

            // Place above a directly preceding UFUNCTION(...) line.
            var target = line;
            if (line.LineNumber > 0)
            {
                var prev = line.Snapshot.GetLineFromLineNumber(line.LineNumber - 1);
                if (prev.GetText().TrimStart().StartsWith("UFUNCTION")) target = prev;
            }
            EditorAccess.Insert(view, target.Start.Position, sb.ToString(), "Document method");
            var summary = view.TextSnapshot.GetLineFromLineNumber(target.LineNumber + 1);
            view.Caret.MoveTo(summary.End);
            PenguinEditor.Status("Inserted documentation comment.");
        }

        private static string ParameterName(string parameter)
        {
            var p = parameter;
            int eq = TopLevelIndexOf(p, '=');
            if (eq >= 0) p = p.Substring(0, eq);
            p = p.Trim();
            if (p.Length == 0 || p == "void" || p == "...") return null;
            int end = p.Length;
            while (end > 0 && (p[end - 1] == ']' || p[end - 1] == '[' || char.IsDigit(p[end - 1]) && p.IndexOf('[') >= 0)) end--;
            int start = end;
            while (start > 0 && OccurrenceScanner.IsIdentPart(p[start - 1])) start--;
            if (start == 0) return null; // type only, unnamed parameter
            return p.Substring(start, end - start);
        }

        // ── Text helpers ────────────────────────────────────────────

        private static string SafeRead(string path)
        {
            try { return File.ReadAllText(path); } catch { return ""; }
        }

        private static TextChange Change(string file, string text, int index, int length, string newText, string expected)
        {
            int line = 1, lineStart = 0;
            for (int i = 0; i < index && i < text.Length; i++)
                if (text[i] == '\n') { line++; lineStart = i + 1; }
            return new TextChange { FilePath = file, LineNumber = line, ColumnNumber = index - lineStart, Length = length, NewText = newText, ExpectedText = expected };
        }

        /// <summary>(index after '{', index of matching '}') of <paramref name="owner"/>'s body, or null.</summary>
        internal static Tuple<int, int> FindClassBody(string text, string owner)
        {
            foreach (Match m in RxClassHead.Matches(text))
            {
                if (m.Groups[1].Value != owner) continue;
                int open = m.Index + m.Length - 1;
                int close = MatchBrace(text, open);
                if (close > open) return Tuple.Create(open + 1, close);
            }
            return null;
        }

        /// <summary>The innermost class/struct whose body contains <paramref name="position"/>.</summary>
        internal static string EnclosingClass(string text, int position)
        {
            string best = null; int bestOpen = -1;
            foreach (Match m in RxClassHead.Matches(text))
            {
                int open = m.Index + m.Length - 1;
                if (open >= position) break;
                int close = MatchBrace(text, open);
                if (close > position && open > bestOpen) { best = m.Groups[1].Value; bestOpen = open; }
            }
            return best;
        }

        private static string CurrentAccess(string text, int bodyStart, int position, string owner)
        {
            var segment = text.Substring(bodyStart, Math.Max(0, position - bodyStart));
            var matches = RxAccess.Matches(segment);
            if (matches.Count > 0) return matches[matches.Count - 1].Groups[1].Value;
            var head = text.Substring(Math.Max(0, bodyStart - 300), Math.Min(300, bodyStart));
            return Regex.IsMatch(head, @"\bstruct\s+(?:\w+_API\s+)?" + Regex.Escape(owner) + @"\b") ? "public" : "private";
        }

        private static string IndentOfClassMembers(string classText)
        {
            foreach (var raw in classText.Split('\n'))
            {
                var l = raw.TrimEnd('\r');
                if (l.Trim().Length == 0 || l.TrimEnd().EndsWith(":") || l.TrimStart().StartsWith("GENERATED")) continue;
                return l.Substring(0, l.Length - l.TrimStart().Length);
            }
            return "\t";
        }

        /// <summary>Index of the brace matching the '{' at <paramref name="open"/>, skipping comments and literals; -1 if none.</summary>
        internal static int MatchBrace(string s, int open)
        {
            int depth = 0;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
                if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { i = s.IndexOf("*/", i + 2, StringComparison.Ordinal); if (i < 0) return -1; i++; continue; }
                if (c == '"' || c == '\'')
                {
                    for (i++; i < s.Length && s[i] != c && s[i] != '\n'; i++) if (s[i] == '\\') i++;
                    continue;
                }
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i;
            }
            return -1;
        }

        private static string Dedent(string block, int columns)
        {
            var lines = block.Replace("\r\n", "\n").Split('\n');
            for (int i = 1; i < lines.Length; i++)
            {
                int strip = 0;
                while (strip < lines[i].Length && strip < columns && (lines[i][strip] == ' ' || lines[i][strip] == '\t')) strip++;
                lines[i] = lines[i].Substring(strip);
            }
            return string.Join(Environment.NewLine, lines);
        }

        internal static List<string> SplitTopLevel(string parameters)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < parameters.Length; i++)
            {
                char c = parameters[i];
                if (c == '(' || c == '<' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == '>' || c == ']' || c == '}') depth--;
                else if (c == ',' && depth == 0) { parts.Add(parameters.Substring(start, i - start)); start = i + 1; }
            }
            if (parameters.Trim().Length > 0) parts.Add(parameters.Substring(start));
            return parts;
        }

        private static int TopLevelIndexOf(string s, char target)
        {
            int depth = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '<' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == '>' || c == ']' || c == '}') depth--;
                else if (c == target && depth == 0) return i;
            }
            return -1;
        }
    }
}
