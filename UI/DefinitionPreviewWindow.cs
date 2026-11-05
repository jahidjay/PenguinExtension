using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using PenguinExtention.Commands;
using PenguinExtention.Core;
using PenguinExtention.Models;
using PenguinExtention.Services;

namespace PenguinExtention.UI
{
    /// <summary>
    /// Live definition preview (like VA's context/definition view): shows the source of the declaration — and, for
    /// functions, the implementation — of the symbol under the caret as you type or move.
    /// </summary>
    [Guid("9F4D2C81-7A3E-4B6F-8D15-0E2A6C9B3F47")]
    public class DefinitionPreviewWindow : ToolWindowPane
    {
        public DefinitionPreviewWindow() : base(null)
        {
            Caption = "🐧 Penguin — Definition Preview";
            Content = new DefinitionPreviewControl();
        }
    }

    internal sealed class DefinitionPreviewControl : UserControl
    {
        private const int MaxLines = 80;

        private readonly TextBlock _title = new TextBlock { FontWeight = FontWeights.SemiBold, FontSize = 13, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _location = new TextBlock { Margin = new Thickness(0, 2, 0, 6) };
        private readonly TextBox _declaration = SourceBox();
        private readonly TextBlock _implHeader = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 2), Visibility = Visibility.Collapsed };
        private readonly TextBox _implementation = SourceBox();
        private readonly DispatcherTimer _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        private string _lastKey;
        private int _generation = -1;
        private CancellationTokenSource _query;
        private UnrealSymbol _current;
        private Tuple<string, int, int> _currentImpl;

        public DefinitionPreviewControl()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _implementation.Visibility = Visibility.Collapsed;

            var panel = new StackPanel { Margin = new Thickness(8) };
            panel.Children.Add(_title);
            panel.Children.Add(_location);
            panel.Children.Add(_declaration);
            panel.Children.Add(_implHeader);
            panel.Children.Add(_implementation);
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            _title.Text = "Move the caret onto an Unreal symbol (or type a call / Owner::Function) to preview its definition.";
            _timer.Tick += (s, e) => Refresh();
            Loaded += (s, e) => _timer.Start();
            Unloaded += (s, e) => { _timer.Stop(); _query?.Cancel(); _lastKey = null; };
        }

        private static TextBox SourceBox()
        {
            var box = new TextBox
            {
                IsReadOnly = true, FontFamily = new FontFamily("Consolas"), FontSize = 12, BorderThickness = new Thickness(1),
                TextWrapping = TextWrapping.NoWrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(4), Visibility = Visibility.Collapsed
            };
            box.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            box.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return box;
        }

        private void Refresh() => _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            try
            {
                if (!BackendService.IsReady) { ShowMessage(BackendService.Status); _lastKey = null; return; }
                var view = EditorAccess.ActiveView();
                if (view == null) return; // keep showing the last symbol while focus is in this window
                var word = PenguinEditor.WordAtCaret(view, out var span);
                var qualifier = word == null ? null : PenguinEditor.QualifierBefore(span);

                // The function whose argument list the caret is in: previewed when the word itself isn't indexed.
                string callee = null, calleeQualifier = null;
                var caret = view.Caret.Position.BufferPosition;
                int open = Editor.UnrealSignatureHelpController.FindOpenParen(caret.Snapshot, Math.Max(0, caret.Position - 1));
                if (open > 0)
                {
                    var line = caret.Snapshot.GetLineFromPosition(open);
                    callee = OccurrenceScanner.IdentifierAt(line.GetText(), open - line.Start.Position, out int start);
                    if (callee != null) calleeQualifier = PenguinEditor.QualifierBefore(new Microsoft.VisualStudio.Text.SnapshotSpan(line.Start + start, callee.Length));
                }

                if ((string.IsNullOrEmpty(word) || word.Length < 2) && callee == null) return;
                var key = qualifier + "::" + word + "|" + calleeQualifier + "::" + callee;
                if (key == _lastKey && _generation == BackendService.Generation) return;
                _lastKey = key; _generation = BackendService.Generation;

                _query?.Cancel();
                _query = new CancellationTokenSource();
                var token = _query.Token;
                var file = PenguinEditor.FilePathOf(view);

                await TaskScheduler.Default;
                UnrealSymbol symbol = null;
                if (!string.IsNullOrEmpty(word) && word.Length >= 2)
                    symbol = PenguinEditor.Best(await BackendService.ExactAsync(word, token).ConfigureAwait(false), qualifier, file);
                if (symbol == null && callee != null)
                    symbol = PenguinEditor.Best(await BackendService.ExactAsync(callee, token).ConfigureAwait(false), calleeQualifier, file);
                if (symbol == null) return; // keep the previous preview for unindexed words (locals, keywords)
                var declaration = ReadDeclaration(symbol);
                Tuple<string, int, int> impl = null;
                string implText = null;
                if (symbol.Kind == UnrealSymbolKind.Function)
                {
                    impl = await NavigationCommands.FindImplementationAsync(symbol, token).ConfigureAwait(false);
                    if (impl != null && !(PenguinEditor.SameFile(impl.Item1, file) && IsLineNear(view, impl.Item2)))
                        implText = ReadBlock(impl.Item1, impl.Item2, includeLeadingComments: false);
                    else impl = null;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                Show(symbol, declaration, impl, implText);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ShowMessage(ex.Message);
            }
        });

        private static bool IsLineNear(Microsoft.VisualStudio.Text.Editor.ITextView view, int line1) =>
            Math.Abs(view.Caret.Position.BufferPosition.GetContainingLine().LineNumber + 1 - line1) <= 2;

        private void Show(UnrealSymbol symbol, string declaration, Tuple<string, int, int> impl, string implText)
        {
            _current = symbol;
            _currentImpl = impl;
            _title.Text = $"[{symbol.KindDisplay}] {symbol.DisplayText}" + (symbol.IsEngineSymbol ? "   (engine)" : "");
            _location.Inlines.Clear();
            var link = new Hyperlink(new Run(Path.GetFileName(symbol.FilePath) + ":" + symbol.LineNumber)) { ToolTip = symbol.FilePath };
            link.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); PenguinEditor.Navigate(_current); };
            _location.Inlines.Add(new Run("Declared in "));
            _location.Inlines.Add(link);

            _declaration.Text = declaration ?? symbol.Signature ?? "(source not readable)";
            _declaration.Visibility = Visibility.Visible;

            bool hasImpl = impl != null && !string.IsNullOrEmpty(implText);
            _implHeader.Visibility = _implementation.Visibility = hasImpl ? Visibility.Visible : Visibility.Collapsed;
            if (hasImpl)
            {
                _implHeader.Inlines.Clear();
                var implLink = new Hyperlink(new Run(Path.GetFileName(impl.Item1) + ":" + impl.Item2)) { ToolTip = impl.Item1 };
                implLink.Click += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); PenguinEditor.Open(_currentImpl.Item1, _currentImpl.Item2, _currentImpl.Item3); };
                _implHeader.Inlines.Add(new Run("Implementation — "));
                _implHeader.Inlines.Add(implLink);
                _implementation.Text = implText;
            }
        }

        private void ShowMessage(string message)
        {
            _current = null;
            _title.Text = message;
            _location.Inlines.Clear();
            _declaration.Visibility = _implHeader.Visibility = _implementation.Visibility = Visibility.Collapsed;
        }

        /// <summary>Doc comment + macro + declaration (and body for inline functions / types, capped). Background thread.</summary>
        private static string ReadDeclaration(UnrealSymbol symbol) => ReadBlock(symbol.FilePath, symbol.LineNumber, includeLeadingComments: true);

        internal static string ReadBlock(string path, int line1, bool includeLeadingComments)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); } catch { return null; }
            if (line1 < 1 || line1 > lines.Length) return null;
            int first = line1 - 1;

            if (includeLeadingComments)
            {
                // Pull in the reflection macro and the doc comment directly above.
                int i = first - 1;
                bool inBlock = false;
                while (i >= 0 && first - i < 40)
                {
                    var t = lines[i].Trim();
                    if (inBlock) { first = i; if (t.StartsWith("/*")) inBlock = false; i--; continue; }
                    if (t.EndsWith("*/")) { inBlock = !t.StartsWith("/*") || t == "*/"; first = i; i--; continue; }
                    if (t.StartsWith("//") || t.StartsWith("UFUNCTION") || t.StartsWith("UPROPERTY") || t.StartsWith("UCLASS") ||
                        t.StartsWith("USTRUCT") || t.StartsWith("UENUM") || t.StartsWith("UINTERFACE") || t.StartsWith("UDELEGATE") || t.StartsWith("template"))
                    { first = i; i--; continue; }
                    break;
                }
            }

            // Extend through the end of the declaration: first ';' at depth 0, or the matching '}' of a body.
            int depth = 0, last = line1 - 1;
            bool sawBrace = false, done = false;
            for (int i = line1 - 1; i < lines.Length && i - first < MaxLines && !done; i++)
            {
                last = i;
                var code = StripLineComment(lines[i]);
                foreach (var c in code)
                {
                    if (c == '{') { depth++; sawBrace = true; }
                    else if (c == '}') { depth--; if (sawBrace && depth <= 0) { done = true; break; } }
                    else if (c == ';' && depth == 0 && !sawBrace && !IsMacroOnly(code)) { done = true; break; }
                }
            }

            var sb = new StringBuilder();
            int indent = Enumerable.Range(first, last - first + 1).Where(i => lines[i].Trim().Length > 0)
                .Select(i => lines[i].Length - lines[i].TrimStart().Length).DefaultIfEmpty(0).Min();
            for (int i = first; i <= last; i++)
                sb.AppendLine(lines[i].Length >= indent ? lines[i].Substring(indent) : lines[i].TrimStart());
            if (last - first + 1 >= MaxLines) sb.AppendLine("…");
            return sb.ToString().TrimEnd();
        }

        private static bool IsMacroOnly(string code)
        {
            var t = code.Trim();
            return t.StartsWith("UFUNCTION") || t.StartsWith("UPROPERTY") || t.StartsWith("UCLASS") || t.StartsWith("USTRUCT") || t.StartsWith("UENUM");
        }

        private static string StripLineComment(string line)
        {
            int idx = line.IndexOf("//", StringComparison.Ordinal);
            return idx >= 0 ? line.Substring(0, idx) : line;
        }
    }
}
