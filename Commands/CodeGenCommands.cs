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
using Microsoft.VisualStudio.Threading;
using Newtonsoft.Json;
using PenguinExtention.Core;
using PenguinExtention.Models;
using PenguinExtention.Services;
using PenguinExtention.UI;

namespace PenguinExtention.Commands
{
    /// <summary>
    /// Visual Assist–style code generation: Implement Virtual Methods, Add Missing Case Statements, Introduce Variable,
    /// Add Forward Declaration, and Snippets / Surround With. Text-based, undoable, never saved automatically.
    /// </summary>
    internal static class CodeGenCommands
    {
        private static CancellationToken Token => PenguinExtensionPackage.Instance.DisposalToken;

        // ── Implement Virtual Methods ───────────────────────────────

        public static async Task ImplementVirtualMethodsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (!BackendService.IsReady || view == null) { PenguinEditor.Status(BackendService.Status); return; }
            var text = view.TextSnapshot.GetText();
            int caret = view.Caret.Position.BufferPosition.Position;
            var owner = RefactorCommands.EnclosingClass(text, caret);
            if (owner == null) { PenguinEditor.Status("Place the caret inside a class body."); return; }
            var body = RefactorCommands.FindClassBody(text, owner);
            if (body == null) { PenguinEditor.Status("Could not find the body of " + owner + "."); return; }
            var classText = text.Substring(body.Item1, body.Item2 - body.Item1);

            // Walk the base chain via the index and collect virtual functions not yet overridden.
            var candidates = await Task.Run(async () =>
            {
                var found = new List<UnrealSymbol>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var classSym = (await BackendService.ExactAsync(owner, Token).ConfigureAwait(false)).FirstOrDefault(IsType);
                var current = classSym;
                for (int depth = 0; current != null && depth < 8; depth++)
                {
                    var inheritance = await BackendService.InheritanceAsync(current, Token).ConfigureAwait(false);
                    var baseName = inheritance.Bases.FirstOrDefault();
                    if (baseName == null) break;
                    var all = await AllMembersOf(baseName).ConfigureAwait(false);
                    foreach (var m in all.Where(m => m.Kind == UnrealSymbolKind.Function && m.Signature != null &&
                                                     Regex.IsMatch(m.Signature, @"\bvirtual\b") && !m.Name.StartsWith("~")))
                        if (seen.Add(m.Name)) found.Add(m);
                    current = (await BackendService.ExactAsync(baseName, Token).ConfigureAwait(false)).FirstOrDefault(IsType);
                }
                return found.Where(m => !Regex.IsMatch(classText, @"\b" + Regex.Escape(m.Name) + @"\s*\(")).ToList();
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (candidates.Count == 0) { PenguinEditor.Status("No un-overridden virtual methods found in the base classes of " + owner + "."); return; }

            var pick = PickMany("Implement Virtual Methods — " + owner, candidates.Select(c => c.OwnerClass + "::" + c.Name + "  " + c.Signature).ToList());
            if (pick == null || pick.Count == 0) return;
            var indent = Indent(classText);
            var sb = new StringBuilder("public:" + Environment.NewLine);
            foreach (var i in pick)
            {
                var sig = Regex.Replace(candidates[i].Signature, @"\bvirtual\s+", "");
                sig = Regex.Replace(sig, @"\s*=\s*0\s*$", "").Replace(" override", "").TrimEnd(';', ' ');
                sb.Append(indent).Append(sig).Append(" override;").Append(Environment.NewLine);
            }
            int closeLineStart = text.LastIndexOf('\n', body.Item2 - 1) + 1;
            EditorAccess.Insert(view, closeLineStart, sb.ToString(), "Implement virtual methods");
            PenguinEditor.Status($"Declared {pick.Count} override(s). Use Generate Implementation (Alt+U, I) on each to create the bodies.");
        }

        private static async Task<List<UnrealSymbol>> AllMembersOf(string owner)
        {
            var cache = CacheService.Instance;
            if (!BackendService.IsCore && cache != null)
                return cache.GetAllSymbols(UnrealSymbolKind.Function, null, int.MaxValue).Where(s => s.OwnerClass == owner).ToList();
            var results = await BackendService.SearchAsync(owner + "::", 500, Token).ConfigureAwait(false);
            return results.Where(s => s.OwnerClass == owner).ToList();
        }

        // ── Add Missing Case Statements ─────────────────────────────

        private static readonly Regex RxSwitch = new Regex(@"\bswitch\s*\(\s*([^()]{1,120}?)\s*\)\s*\{?", RegexOptions.Compiled);

        public static async Task AddMissingCasesAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (!BackendService.IsReady || view == null) { PenguinEditor.Status(BackendService.Status); return; }
            var snapshot = view.TextSnapshot;
            var text = snapshot.GetText();
            int caret = view.Caret.Position.BufferPosition.Position;

            // The switch whose header is on the caret line or whose body contains the caret.
            Match sw = null; int open = -1, close = -1;
            foreach (Match m in RxSwitch.Matches(text))
            {
                if (m.Index > caret) break;
                int o = text.IndexOf('{', m.Index + m.Length - 1);
                if (o < 0) continue;
                int c = RefactorCommands.MatchBrace(text, o);
                if (c > 0 && (caret <= c || snapshot.GetLineNumberFromPosition(m.Index) == snapshot.GetLineNumberFromPosition(caret))) { sw = m; open = o; close = c; }
            }
            if (sw == null) { PenguinEditor.Status("Place the caret on or inside a switch statement."); return; }

            var expr = sw.Groups[1].Value.Trim();
            var variable = Regex.Match(expr, @"([A-Za-z_]\w*)\s*(?:\(\s*\))?\s*$").Groups[1].Value; // last member/variable/getter in "a.b->State" or "GetState()"
            var bodyText = text.Substring(open, close - open);
            var enumerators = await Task.Run(async () =>
            {
                // The enum type: the declared type of the switched variable/field/getter, or an enum whose values already appear in a case.
                string enumName = null;
                foreach (var s in await BackendService.ExactAsync(variable, Token).ConfigureAwait(false))
                {
                    var type = (s.ReturnType ?? "").Replace("const", "").Replace("&", "").Trim();
                    type = Regex.Replace(type, @"^(?:enum\s+(?:class\s+)?)", "").Split(new[] { "::" }, StringSplitOptions.None).Last().Trim();
                    if (type.Length > 0 && (await BackendService.ExactAsync(type, Token).ConfigureAwait(false)).Any(t => t.Kind == UnrealSymbolKind.Enum || t.Kind == UnrealSymbolKind.Typedef))
                    { enumName = type; break; }
                }
                if (enumName == null)
                {
                    var firstCase = Regex.Match(bodyText, @"\bcase\s+(?:([A-Za-z_]\w*)::)?([A-Za-z_]\w*)\s*:");
                    if (firstCase.Success)
                    {
                        if (firstCase.Groups[1].Success) enumName = firstCase.Groups[1].Value;
                        else enumName = (await BackendService.ExactAsync(firstCase.Groups[2].Value, Token).ConfigureAwait(false))
                            .FirstOrDefault(v => v.Kind == UnrealSymbolKind.EnumValue)?.OwnerClass;
                    }
                }
                if (enumName == null) return null;
                List<UnrealSymbol> members;
                var cache = CacheService.Instance;
                if (!BackendService.IsCore && cache != null)
                    members = cache.GetAllSymbols(UnrealSymbolKind.EnumValue, null, int.MaxValue).Where(v => v.OwnerClass == enumName).OrderBy(v => v.LineNumber).ToList();
                else
                    members = (await BackendService.SearchAsync("", 3000, Token).ConfigureAwait(false)).Where(v => v.Kind == UnrealSymbolKind.EnumValue && v.OwnerClass == enumName).ToList();
                return Tuple.Create(enumName, members);
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (enumerators == null || enumerators.Item2.Count == 0) { PenguinEditor.Status("Could not determine the enum type of '" + expr + "' (is it indexed?)."); return; }

            var enumName2 = enumerators.Item1;
            bool scoped = IsScopedEnum(enumName2) || Regex.IsMatch(bodyText, @"\bcase\s+" + Regex.Escape(enumName2) + "::");
            var missing = enumerators.Item2.Where(v => !Regex.IsMatch(bodyText, @"\bcase\s+(?:[\w:]*::)?" + Regex.Escape(v.Name) + @"\s*:")).ToList();
            if (missing.Count == 0) { PenguinEditor.Status("All " + enumerators.Item2.Count + " cases of " + enumName2 + " are handled."); return; }

            // Match existing "case" lines; otherwise one level deeper than the switch.
            var existingCase = Regex.Match(bodyText, @"^([ \t]*)case\b", RegexOptions.Multiline);
            var switchLine = snapshot.GetLineFromPosition(sw.Index).GetText();
            var caseIndent = existingCase.Success ? existingCase.Groups[1].Value
                           : switchLine.Substring(0, switchLine.Length - switchLine.TrimStart().Length) + "\t";
            var nl = Environment.NewLine;
            var sb = new StringBuilder();
            foreach (var v in missing)
                sb.Append(caseIndent).Append("case ").Append(scoped ? enumName2 + "::" : "").Append(v.Name).Append(':').Append(nl)
                  .Append(caseIndent).Append('\t').Append("break;").Append(nl);
            // Insert before "default:" if present, else before the closing brace line.
            var def = Regex.Match(bodyText, @"^[ \t]*default\s*:", RegexOptions.Multiline);
            int insertAt = def.Success ? open + def.Index : text.LastIndexOf('\n', close - 1) + 1;
            EditorAccess.Insert(view, insertAt, sb.ToString(), "Add missing case statements");
            PenguinEditor.Status($"Added {missing.Count} case(s) for {enumName2}.");
        }

        private static bool IsScopedEnum(string enumName)
        {
            var cache = CacheService.Instance;
            var sym = cache?.GetByExactName(enumName).FirstOrDefault(s => s.Kind == UnrealSymbolKind.Enum);
            return sym?.Signature != null && Regex.IsMatch(sym.Signature, @"\benum\s+(?:class|struct)\b");
        }

        // ── Introduce Variable ──────────────────────────────────────

        public static async Task IntroduceVariableAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (view == null) return;
            if (view.Selection.IsEmpty || view.Selection.SelectedSpans.Count != 1) { PenguinEditor.Status("Select an expression first."); return; }
            var span = view.Selection.SelectedSpans[0];
            var expr = span.GetText().Trim();
            if (expr.Length == 0 || expr.Contains(";") || expr.Contains("\n")) { PenguinEditor.Status("Select a single expression (no ';' or line breaks)."); return; }

            var name = Prompt("Introduce Variable", "Variable name:", SuggestName(expr));
            if (name == null) return;
            if (!RenameDialog.IsIdentifier(name)) { PenguinEditor.Status("'" + name + "' is not a valid identifier."); return; }

            var line = span.Start.GetContainingLine();
            var lineText = line.GetText();
            var indent = lineText.Substring(0, lineText.Length - lineText.TrimStart().Length);
            var snapshot = view.TextSnapshot;
            using (var edit = view.TextBuffer.CreateEdit())
            {
                edit.Replace(span.Span, name);
                edit.Insert(line.Start.Position, indent + "auto " + name + " = " + expr + ";" + Environment.NewLine);
                edit.Apply();
            }
            PenguinEditor.Status("Introduced '" + name + "'.");
        }

        private static string SuggestName(string expr)
        {
            var m = Regex.Match(expr, @"(?:Get|get_?)?([A-Za-z_]\w*)\s*\([^()]*\)\s*$");
            var baseName = m.Success ? m.Groups[1].Value : Regex.Match(expr, @"([A-Za-z_]\w*)\s*$").Groups[1].Value;
            if (string.IsNullOrEmpty(baseName)) return "Value";
            return char.ToLowerInvariant(baseName[0]) + baseName.Substring(1);
        }

        // ── Add Forward Declaration ─────────────────────────────────

        public static async Task AddForwardDeclarationAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (!BackendService.IsReady || view == null) { PenguinEditor.Status(BackendService.Status); return; }
            var symbol = await PenguinEditor.SymbolAtCaretAsync(view, Token);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (symbol == null || !IsType(symbol)) { PenguinEditor.Status("Place the caret on an indexed class or struct name."); return; }
            var keyword = symbol.Kind == UnrealSymbolKind.Struct ? "struct" : "class";
            var decl = keyword + " " + symbol.Name + ";";
            var snapshot = view.TextSnapshot;
            if (snapshot.GetText().Contains(decl)) { PenguinEditor.Status(decl + " already exists."); return; }

            // After the last #include (but before *.generated.h), else after #pragma once.
            int target = 0;
            foreach (var l in snapshot.Lines)
            {
                var t = l.GetText().TrimStart();
                if (t.StartsWith("#pragma once") || t.StartsWith("#include") && !t.Contains(".generated.h")) target = l.LineNumber + 1;
                if (t.StartsWith("#include") && t.Contains(".generated.h")) break;
            }
            var pos = target >= snapshot.LineCount ? snapshot.Length : snapshot.GetLineFromLineNumber(target).Start.Position;
            EditorAccess.Insert(view, pos, Environment.NewLine + decl + Environment.NewLine, "Add forward declaration");
            PenguinEditor.Status("Added " + decl);
        }

        // ── Snippets / Surround With ────────────────────────────────

        internal sealed class Snippet
        {
            public string Name { get; set; }
            public string Shortcut { get; set; }
            public string Description { get; set; }
            public string Body { get; set; }
        }

        /// <summary>User snippets live here (created on first use with examples); merged over the built-ins.</summary>
        internal static string UserSnippetPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PenguinExtension", "snippets.json");

        private static readonly Snippet[] BuiltIn =
        {
            new Snippet { Name = "if", Shortcut = "if", Description = "if block", Body = "if ($condition$)\n{\n\t$selected$$end$\n}" },
            new Snippet { Name = "if / else", Shortcut = "ife", Description = "if-else block", Body = "if ($condition$)\n{\n\t$selected$$end$\n}\nelse\n{\n}" },
            new Snippet { Name = "for (index)", Shortcut = "for", Description = "indexed for loop", Body = "for (int $i$ = 0; $i$ < $count$; ++$i$)\n{\n\t$selected$$end$\n}" },
            new Snippet { Name = "for (range)", Shortcut = "forr", Description = "range-based for", Body = "for (auto& $item$ : $container$)\n{\n\t$selected$$end$\n}" },
            new Snippet { Name = "while", Shortcut = "while", Description = "while loop", Body = "while ($condition$)\n{\n\t$selected$$end$\n}" },
            new Snippet { Name = "braces { }", Shortcut = "{", Description = "wrap in a block", Body = "{\n\t$selected$$end$\n}" },
            new Snippet { Name = "#if 0 … #endif", Shortcut = "#if0", Description = "disable code", Body = "#if 0\n$selected$$end$\n#endif" },
            new Snippet { Name = "#ifdef … #endif", Shortcut = "#ifdef", Description = "conditional compilation", Body = "#ifdef $SYMBOL$\n$selected$$end$\n#endif" },
            new Snippet { Name = "#pragma region", Shortcut = "region", Description = "collapsible region", Body = "#pragma region $Name$\n$selected$$end$\n#pragma endregion" },
            new Snippet { Name = "try / catch", Shortcut = "try", Description = "exception guard", Body = "try\n{\n\t$selected$$end$\n}\ncatch (const std::exception& e)\n{\n}" },
            new Snippet { Name = "class", Shortcut = "class", Description = "class with ctor/dtor", Body = "class $Name$\n{\npublic:\n\t$Name$();\n\t~$Name$();\n\nprivate:\n\t$end$\n};" },
            new Snippet { Name = "include guard", Shortcut = "guard", Description = "#pragma once", Body = "#pragma once\n$end$" },
            new Snippet { Name = "SDL: init + window + renderer", Shortcut = "sdlinit", Description = "SDL2 boilerplate", Body =
                "if (SDL_Init(SDL_INIT_VIDEO | SDL_INIT_AUDIO | SDL_INIT_EVENTS) != 0)\n{\n\tSDL_Log(\"SDL_Init failed: %s\", SDL_GetError());\n\treturn 1;\n}\n" +
                "SDL_Window* window = SDL_CreateWindow(\"$Title$\", SDL_WINDOWPOS_CENTERED, SDL_WINDOWPOS_CENTERED, $1280$, $720$, SDL_WINDOW_SHOWN);\n" +
                "SDL_Renderer* renderer = SDL_CreateRenderer(window, -1, SDL_RENDERER_ACCELERATED | SDL_RENDERER_PRESENTVSYNC);\n$end$" },
            new Snippet { Name = "SDL: main loop", Shortcut = "sdlloop", Description = "event poll + clear/present", Body =
                "bool running = true;\nUint64 last = SDL_GetPerformanceCounter();\nwhile (running)\n{\n\tSDL_Event event;\n\twhile (SDL_PollEvent(&event))\n\t{\n\t\tif (event.type == SDL_QUIT) running = false;\n\t}\n" +
                "\tconst Uint64 now = SDL_GetPerformanceCounter();\n\tconst float dt = (float)(now - last) / (float)SDL_GetPerformanceFrequency();\n\tlast = now;\n\n\t$end$\n\n" +
                "\tSDL_SetRenderDrawColor(renderer, 0, 0, 0, 255);\n\tSDL_RenderClear(renderer);\n\tSDL_RenderPresent(renderer);\n}" },
            new Snippet { Name = "SDL: event switch", Shortcut = "sdlevent", Description = "switch on SDL_Event type", Body =
                "switch (event.type)\n{\ncase SDL_QUIT:\n\tbreak;\ncase SDL_KEYDOWN:\n\tswitch (event.key.keysym.sym)\n\t{\n\tcase SDLK_ESCAPE:\n\t\t$end$break;\n\t}\n\tbreak;\ncase SDL_MOUSEBUTTONDOWN:\n\tbreak;\n}" },
            new Snippet { Name = "SDL: cleanup", Shortcut = "sdlquit", Description = "destroy + quit", Body = "SDL_DestroyRenderer(renderer);\nSDL_DestroyWindow(window);\nSDL_Quit();$end$" },
            new Snippet { Name = "SDL: load texture (SDL_image)", Shortcut = "sdltex", Description = "IMG_LoadTexture with check", Body =
                "SDL_Texture* $texture$ = IMG_LoadTexture(renderer, \"$path.png$\");\nif (!$texture$)\n{\n\tSDL_Log(\"IMG_LoadTexture failed: %s\", IMG_GetError());\n}$end$" },
            new Snippet { Name = "SDL: log", Shortcut = "sdllog", Description = "SDL_Log with error", Body = "SDL_Log(\"$message$: %s\", SDL_GetError());$end$" },
        };

        public static List<Snippet> LoadSnippets()
        {
            var all = BuiltIn.ToList();
            try
            {
                var path = UserSnippetPath;
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(path, JsonConvert.SerializeObject(new[]
                    {
                        new Snippet { Name = "Example: TODO comment", Shortcut = "todo", Description = "edit this file to add your own snippets", Body = "// TODO($user$): $end$" }
                    }, Formatting.Indented));
                }
                var user = JsonConvert.DeserializeObject<List<Snippet>>(File.ReadAllText(path)) ?? new List<Snippet>();
                foreach (var s in user.Where(s => !string.IsNullOrEmpty(s.Body)))
                {
                    all.RemoveAll(b => b.Shortcut == s.Shortcut && !string.IsNullOrEmpty(s.Shortcut));
                    all.Add(s);
                }
            }
            catch (Exception ex) { System.Diagnostics.Trace.WriteLine("Penguin snippets: " + ex.Message); }
            return all;
        }

        /// <summary>Insert Snippet (no selection) or Surround With (selection → $selected$).</summary>
        public static async Task InsertSnippetAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (view == null) return;
            var snippets = await Task.Run(() => LoadSnippets());
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            bool surround = !view.Selection.IsEmpty;
            var choices = surround ? snippets.Where(s => s.Body.Contains("$selected$")).ToList() : snippets;
            var pick = QuickPickDialog.Show(surround ? "Surround With…" : "Insert Snippet  (edit " + UserSnippetPath + " to add your own)", "", (text, ct) =>
                Task.FromResult<IList<PickItem>>(choices
                    .Where(s => string.IsNullOrEmpty(text) || (s.Name + " " + s.Shortcut + " " + s.Description).IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                    .Select(s => new PickItem { Title = s.Name + (string.IsNullOrEmpty(s.Shortcut) ? "" : "   [" + s.Shortcut + "]"), Detail = s.Description, Glyph = "", Tag = s })
                    .ToList()));
            if (!(pick?.Tag is Snippet snippet)) return;
            Apply(view, snippet);
        }

        private static void Apply(ITextView view, Snippet snippet)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var snapshot = view.TextSnapshot;
            var span = view.Selection.IsEmpty ? new SnapshotSpan(view.Caret.Position.BufferPosition, 0) : view.Selection.StreamSelectionSpan.SnapshotSpan;
            var startLine = span.Start.GetContainingLine();
            var lineText = startLine.GetText();
            var indent = lineText.Substring(0, lineText.Length - lineText.TrimStart().Length);

            // Ask for each distinct $placeholder$ (except the reserved ones) once.
            var body = snippet.Body.Replace("\r\n", "\n");
            foreach (var ph in Regex.Matches(body, @"\$([A-Za-z_][\w. ]*)\$").Cast<Match>().Select(m => m.Groups[1].Value).Distinct().ToList())
            {
                if (ph == "selected" || ph == "end") continue;
                var value = ph == "user" ? Environment.UserName : Prompt(snippet.Name, ph + ":", ph);
                if (value == null) return;
                body = body.Replace("$" + ph + "$", value);
            }

            var selected = span.Length == 0 ? "" : span.GetText();
            if (selected.Length > 0)
            {
                // Re-indent the selection relative to its first line, then into the snippet's indentation.
                var selLines = selected.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
                int common = selLines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
                selected = string.Join("\n", selLines.Select(l => l.Length >= common ? l.Substring(common) : l.TrimStart()));
            }

            var lines = body.Split('\n').ToList();
            var outLines = new List<string>();
            foreach (var l in lines)
            {
                if (l.Contains("$selected$"))
                {
                    var prefix = l.Substring(0, l.IndexOf("$selected$", StringComparison.Ordinal));
                    var selLines = selected.Split('\n');
                    for (int i = 0; i < selLines.Length; i++)
                        outLines.Add((i == 0 ? l.Replace("$selected$", selLines[0]) : prefix + selLines[i]));
                    continue;
                }
                outLines.Add(l);
            }
            var nl = Environment.NewLine;
            bool atLineStart = span.Start.Position == startLine.Start.Position + indent.Length || span.Start.Position == startLine.Start.Position;
            var text = string.Join(nl, outLines.Select((l, i) => i == 0 ? l : indent + l));
            if (span.Length > 0 && span.GetText().EndsWith("\n")) text += nl;

            int endMarker = text.IndexOf("$end$", StringComparison.Ordinal);
            text = text.Replace("$end$", "");
            int start = span.Start.Position;
            if (span.Length > 0 && atLineStart) { start = startLine.Start.Position; span = new SnapshotSpan(snapshot, start, span.End.Position - start); text = indent + text; if (endMarker >= 0) endMarker += indent.Length; }
            using (var edit = view.TextBuffer.CreateEdit())
            {
                edit.Replace(span.Span, text);
                edit.Apply();
            }
            if (endMarker >= 0) view.Caret.MoveTo(new SnapshotPoint(view.TextSnapshot, Math.Min(view.TextSnapshot.Length, start + endMarker)));
            PenguinEditor.Status("Inserted snippet '" + snippet.Name + "'.");
        }

        // ── shared ──────────────────────────────────────────────────

        private static bool IsType(UnrealSymbol s) => s.Kind == UnrealSymbolKind.Class || s.Kind == UnrealSymbolKind.Struct || s.Kind == UnrealSymbolKind.Interface;

        private static string Indent(string classText)
        {
            foreach (var raw in classText.Split('\n'))
            {
                var l = raw.TrimEnd('\r');
                if (l.Trim().Length == 0 || l.TrimEnd().EndsWith(":") && !l.Contains("::")) continue;
                return l.Substring(0, l.Length - l.TrimStart().Length);
            }
            return "\t";
        }

        /// <summary>Small modal text prompt (UI thread). Returns null on cancel.</summary>
        internal static string Prompt(string title, string label, string initial)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var value = QuickPickDialog.PromptText(title + " — " + label + " (type, then Enter)", initial, label);
            if (value == null) return null;
            return value.Trim().Length == 0 ? initial : value.Trim();
        }

        /// <summary>Multi-select picker built on the quick-pick dialog: pick items one at a time; "✔ Done" finishes.</summary>
        private static List<int> PickMany(string title, List<string> items)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var chosen = new SortedSet<int>();
            while (true)
            {
                var pick = QuickPickDialog.Show(title + $"  ({chosen.Count} selected)", "", (text, ct) =>
                {
                    var list = new List<PickItem> { new PickItem { Title = chosen.Count == 0 ? "✔ All of them" : "✔ Done — insert " + chosen.Count, Glyph = "", Tag = -1 } };
                    for (int i = 0; i < items.Count; i++)
                        if (string.IsNullOrEmpty(text) || items[i].IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0)
                            list.Add(new PickItem { Title = (chosen.Contains(i) ? "☑ " : "☐ ") + items[i], Glyph = "", Tag = i });
                    return Task.FromResult<IList<PickItem>>(list);
                });
                if (pick == null) return null;
                int idx = (int)pick.Tag;
                if (idx == -1) return chosen.Count == 0 ? Enumerable.Range(0, items.Count).ToList() : chosen.ToList();
                if (!chosen.Add(idx)) chosen.Remove(idx);
            }
        }
    }
}
