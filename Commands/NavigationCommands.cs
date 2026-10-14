using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Shell;
using PenguinExtention.Core;
using PenguinExtention.Models;
using PenguinExtention.Services;
using PenguinExtention.UI;

namespace PenguinExtention.Commands
{
    /// <summary>Visual Assist–style navigation: Find Symbol, Open File, Toggle Header/Source, GoTo Related/Implementation, List Methods.</summary>
    internal static class NavigationCommands
    {
        private static CancellationToken Token => PenguinExtensionPackage.Instance.DisposalToken;

        // ── Find Symbol (VA: Shift+Alt+S) ───────────────────────────

        public static async Task FindSymbolAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (!BackendService.IsReady) { PenguinEditor.Status(BackendService.Status); return; }
            var initial = PenguinEditor.WordAtCaret(EditorAccess.ActiveView(), out _);
            var pick = QuickPickDialog.Show("Find Unreal Symbol", initial, async (text, ct) =>
            {
                if (string.IsNullOrWhiteSpace(text)) return new List<PickItem>();
                var symbols = await BackendService.SearchAsync(text.Trim(), 200, ct).ConfigureAwait(false);
                return symbols.Select(ToPick).ToList();
            });
            if (pick?.Tag is UnrealSymbol symbol) PenguinEditor.Navigate(symbol);
        }

        // ── Open File in Solution (VA: Shift+Alt+O) ─────────────────

        public static async Task OpenFileAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var root = UnrealProjectDetector.Instance?.ProjectRoot;
            var pick = QuickPickDialog.Show("Open File in Unreal Project", "", async (text, ct) =>
            {
                var files = await SolutionFileIndex.GetAsync(ct).ConfigureAwait(false);
                return files
                    .Select(f => new { f, score = SolutionFileIndex.Score(text, Path.GetFileName(f)) })
                    .Where(x => x.score > 0)
                    .OrderByDescending(x => x.score).ThenBy(x => x.f.Length)
                    .Take(200)
                    .Select(x => new PickItem
                    {
                        Title = Path.GetFileName(x.f),
                        Detail = root != null && x.f.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? x.f.Substring(root.Length).TrimStart('\\') : x.f,
                        Glyph = "",
                        Tag = x.f
                    }).ToList<PickItem>();
            });
            if (pick?.Tag is string path) PenguinEditor.Open(path);
        }

        // ── Toggle Header/Source (VA: Alt+O) ────────────────────────

        public static async Task ToggleHeaderSourceAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var file = PenguinEditor.FilePathOf(EditorAccess.ActiveView());
            if (file == null) { PenguinEditor.Status("No active file."); return; }
            await SolutionFileIndex.GetAsync(Token);
            await TaskScheduler.Default;
            var partner = HeaderSourceResolver.Partner(file);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (partner == null) PenguinEditor.Status("No matching header/source for " + Path.GetFileName(file) + ".");
            else PenguinEditor.Open(partner);
        }

        // ── GoTo Implementation (VA: Alt+G) ─────────────────────────

        public static async Task GoToImplementationAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (!BackendService.IsReady || view == null) { PenguinEditor.Status(BackendService.Status); return; }
            var symbol = await PenguinEditor.SymbolAtCaretAsync(view, Token);
            if (symbol == null) { PenguinEditor.Status("No indexed symbol under the caret."); return; }
            var impl = symbol.Kind == UnrealSymbolKind.Function ? await FindImplementationAsync(symbol, Token) : null;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (impl != null) PenguinEditor.Open(impl.Item1, impl.Item2, impl.Item3);
            else PenguinEditor.Navigate(symbol); // no body found: fall back to the declaration
        }

        /// <summary>Finds <c>Owner::Name(</c> in the declaration's partner source (or any project .cpp). Background thread.</summary>
        public static async Task<Tuple<string, int, int>> FindImplementationAsync(UnrealSymbol symbol, CancellationToken token)
        {
            if (symbol == null || string.IsNullOrEmpty(symbol.OwnerClass)) return null;
            await TaskScheduler.Default;
            var rx = new Regex(@"\b" + Regex.Escape(symbol.OwnerClass) + @"\s*::\s*(" + Regex.Escape(symbol.Name) + @")\s*\(", RegexOptions.Compiled);
            var files = new List<string>();
            var partner = HeaderSourceResolver.Partner(symbol.FilePath);
            if (partner != null) files.Add(partner);
            var all = await SolutionFileIndex.GetAsync(token).ConfigureAwait(false);
            files.AddRange(all.Where(f => HeaderSourceResolver.IsSource(f) && !PenguinEditor.SameFile(f, partner)));
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                string[] lines;
                try { lines = File.ReadAllLines(file); } catch { continue; }
                for (int i = 0; i < lines.Length; i++)
                {
                    var m = rx.Match(lines[i]);
                    // Skip calls: a definition line doesn't end with ';'
                    if (m.Success && !lines[i].TrimEnd().EndsWith(";")) return Tuple.Create(file, i + 1, m.Groups[1].Index);
                }
            }
            return null;
        }

        // ── GoTo Related (VA: Shift+Alt+G) ──────────────────────────

        public static async Task GoToRelatedAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (!BackendService.IsReady || view == null) { PenguinEditor.Status(BackendService.Status); return; }
            var file = PenguinEditor.FilePathOf(view);
            var symbol = await PenguinEditor.SymbolAtCaretAsync(view, Token);
            var items = await Task.Run(async () =>
            {
                var list = new List<PickItem>();
                if (symbol != null)
                {
                    list.Add(new PickItem { Title = "Declaration: " + symbol.DisplayText, Detail = Where(symbol), Glyph = "", Tag = symbol });
                    if (symbol.Kind == UnrealSymbolKind.Function)
                    {
                        var impl = await FindImplementationAsync(symbol, Token).ConfigureAwait(false);
                        if (impl != null) list.Add(new PickItem { Title = "Implementation: " + symbol.DisplayText, Detail = impl.Item1 + ":" + impl.Item2, Glyph = "", Tag = impl });
                    }
                    var typeName = IsType(symbol) ? symbol.Name : symbol.OwnerClass;
                    if (!string.IsNullOrEmpty(typeName))
                    {
                        var typeSymbol = IsType(symbol) ? symbol : (await BackendService.ExactAsync(typeName, Token).ConfigureAwait(false)).FirstOrDefault(IsType);
                        if (typeSymbol != null)
                        {
                            if (!ReferenceEquals(typeSymbol, symbol))
                                list.Add(new PickItem { Title = "Owner: " + typeSymbol.Name, Detail = Where(typeSymbol), Glyph = "", Tag = typeSymbol });
                            var inheritance = await BackendService.InheritanceAsync(typeSymbol, Token).ConfigureAwait(false);
                            foreach (var b in inheritance.Bases)
                            {
                                var baseSymbol = (await BackendService.ExactAsync(b, Token).ConfigureAwait(false)).FirstOrDefault(IsType);
                                list.Add(new PickItem { Title = "Base: " + b, Detail = baseSymbol == null ? "(not indexed)" : Where(baseSymbol), Glyph = "", Tag = baseSymbol });
                            }
                            foreach (var d in inheritance.Derived.Take(100))
                                list.Add(new PickItem { Title = "Derived: " + d.Name, Detail = Where(d), Glyph = "", Tag = d });
                        }
                    }
                }
                var partner = file == null ? null : HeaderSourceResolver.Partner(file);
                if (partner != null) list.Add(new PickItem { Title = "Corresponding file: " + Path.GetFileName(partner), Detail = partner, Glyph = "", Tag = partner });
                return list;
            });
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (items.Count == 0) { PenguinEditor.Status("Nothing related found."); return; }
            var pick = QuickPickDialog.Show("GoTo Related" + (symbol == null ? "" : " — " + symbol.DisplayText), "", (text, ct) =>
                Task.FromResult<IList<PickItem>>(items.Where(i => string.IsNullOrEmpty(text) || i.Title.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0).ToList()));
            Open(pick?.Tag);
        }

        // ── List Methods in File (VA: Alt+M) ────────────────────────

        // [qualifiers] ReturnType Name( — bounded quantifiers, evaluated one line at a time.
        private static readonly Regex RxFunctionLine = new Regex(
            @"^\s*(?:(?:virtual|static|inline|FORCEINLINE|FORCENOINLINE|explicit|constexpr|friend)\s+){0,4}(?<type>[A-Za-z_][\w<>,:\*&\s]{0,160}?[\s\*&])?(~?[A-Za-z_]\w*(?:\s*::\s*~?[A-Za-z_]\w*){0,4})\s*\(",
            RegexOptions.Compiled);
        private static readonly HashSet<string> NotFunctions = new HashSet<string>(StringComparer.Ordinal)
        {
            "if", "for", "while", "switch", "return", "catch", "sizeof", "decltype", "static_assert", "alignof", "new", "delete",
            "UPROPERTY", "UFUNCTION", "UCLASS", "USTRUCT", "UENUM", "UINTERFACE", "UMETA", "GENERATED_BODY", "GENERATED_UCLASS_BODY",
            "check", "checkf", "ensure", "ensureMsgf", "verify", "UE_LOG", "TEXT", "DECLARE_LOG_CATEGORY_EXTERN", "DEFINE_LOG_CATEGORY"
        };

        public static async Task ListMethodsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var view = EditorAccess.ActiveView();
            if (view == null) return;
            var snapshot = view.TextSnapshot;
            var file = PenguinEditor.FilePathOf(view);
            var methods = await Task.Run(() => ScanMethods(snapshot.GetText()));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var pick = QuickPickDialog.Show("Methods in " + Path.GetFileName(file ?? "file"), "", (text, ct) =>
                Task.FromResult<IList<PickItem>>(methods.Where(m => string.IsNullOrEmpty(text) || m.Title.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    AcronymIndex.Matches(text, m.Title.Split(':').Last())).ToList()));
            if (pick?.Tag is int line && line < view.TextSnapshot.LineCount)
            {
                var target = view.TextSnapshot.GetLineFromLineNumber(line);
                view.Caret.MoveTo(target.Start);
                view.ViewScroller.EnsureSpanVisible(target.Extent, Microsoft.VisualStudio.Text.Editor.EnsureSpanVisibleOptions.AlwaysCenter);
                (view as Microsoft.VisualStudio.Text.Editor.IWpfTextView)?.VisualElement.Focus();
            }
        }

        internal static List<PickItem> ScanMethods(string text)
        {
            var result = new List<PickItem>();
            var lines = text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//") || trimmed.StartsWith("#") || trimmed.StartsWith("*") || trimmed.Length == 0) continue;
                var m = RxFunctionLine.Match(line);
                if (!m.Success) continue;
                var name = Regex.Replace(m.Groups[1].Value, @"\s+", "");
                var last = name.Split(':').Last().TrimStart('~');
                if (NotFunctions.Contains(last) || last.All(c => char.IsUpper(c) || c == '_') && last.Length > 3) continue; // macro calls
                // A plain call statement inside a body has no return type before the name; constructors and
                // destructors are recognised by Owner:: qualification or a leading ~ instead.
                var type = m.Groups["type"];
                bool hasType = type.Success && type.Value.Trim().Length > 0 && !type.Value.TrimStart().StartsWith("return") &&
                               !type.Value.TrimStart().StartsWith("else") && !type.Value.TrimStart().StartsWith("new ");
                if (!hasType && (!name.Contains("::") && !name.StartsWith("~") || trimmed.TrimEnd().EndsWith(";"))) continue; // e.g. Super::BeginPlay();
                result.Add(new PickItem
                {
                    Title = name,
                    Detail = "Line " + (i + 1) + ":  " + trimmed.Trim().TrimEnd('{', ';').Trim(),
                    Glyph = "",
                    Tag = i
                });
            }
            return result;
        }

        // ── Shared ──────────────────────────────────────────────────

        internal static void Open(object tag)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            switch (tag)
            {
                case UnrealSymbol s: PenguinEditor.Navigate(s); break;
                case Tuple<string, int, int> loc: PenguinEditor.Open(loc.Item1, loc.Item2, loc.Item3); break;
                case SymbolOccurrence o: PenguinEditor.Open(o.FilePath, o.LineNumber, o.ColumnNumber); break;
                case string path: PenguinEditor.Open(path); break;
            }
        }

        internal static PickItem ToPick(UnrealSymbol s) => new PickItem
        {
            Title = s.DisplayText + "  [" + s.KindDisplay + "]" + (s.IsEngineSymbol ? "  (engine)" : ""),
            Detail = Where(s),
            Glyph = s.KindGlyph,
            Tag = s
        };

        internal static string Where(UnrealSymbol s) => s.FilePath + ":" + s.LineNumber;

        private static bool IsType(UnrealSymbol s) => s.Kind == UnrealSymbolKind.Class || s.Kind == UnrealSymbolKind.Struct || s.Kind == UnrealSymbolKind.Interface;
    }
}
