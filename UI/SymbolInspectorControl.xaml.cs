using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using PenguinExtention.Models;
using PenguinExtention.Services;
using PenguinExtention.Core;
using System.Threading;
using Microsoft.VisualStudio.Shell;

namespace PenguinExtention.UI
{
    /// <summary>
    /// Code-behind for SymbolInspectorControl.
    /// Polls the active caret position on a timer and updates the display.
    /// ponytail: no MVVM overhead for a simple display-only panel.
    /// </summary>
    public partial class SymbolInspectorControl : UserControl
    {
        private readonly DispatcherTimer _timer;
        private string _lastWord;
        private int _generation = -1;
        private CancellationTokenSource _query;
        private UnrealSymbol _currentSymbol;

        public SymbolInspectorControl()
        {
            InitializeComponent();

            // ponytail: poll every 500ms instead of hooking caret events (much simpler, no MEF import needed)
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _timer.Tick += Timer_Tick;
            Loaded += (s, e) => _timer.Start();
            Unloaded += (s, e) => { _timer.Stop(); _query?.Cancel(); _lastWord = null; };
        }

        private async void Timer_Tick(object sender, EventArgs e)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!BackendService.IsReady) { ClearDisplay(); SymbolHeader.Text = BackendService.Status; _lastWord = null; return; }
                var word = GetWordUnderCaret();
                if (word == _lastWord && _generation == BackendService.Generation) return;
                _lastWord = word; _generation = BackendService.Generation;
                _query?.Cancel();
                _query = new CancellationTokenSource();
                var token = _query.Token;
                if (string.IsNullOrWhiteSpace(word) || word.Length < 2) { ClearDisplay(); return; }
                var symbols = await BackendService.ExactAsync(word, token).ConfigureAwait(false);
                var sym = symbols.OrderBy(s => s.IsEngineSymbol ? 1 : 0).ThenByDescending(s => s.AccessCount).FirstOrDefault();
                var info = sym == null ? null : await BackendService.ClassInfoAsync(sym, token).ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                token.ThrowIfCancellationRequested();
                if (sym == null) ClearDisplay(); else UpdateDisplay(sym, info);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ClearDisplay(); SymbolHeader.Text = ex.Message;
            }
        }

        private void UpdateDisplay(UnrealSymbol sym, UnrealClassInfo info)
        {
            _currentSymbol = sym;

            SymbolHeader.Text = $"[{sym.KindDisplay}] {sym.DisplayText}";

            // Signature
            if (!string.IsNullOrEmpty(sym.Signature))
            {
                SignatureText.Text = sym.Signature;
                SignatureSection.Visibility = Visibility.Visible;
            }
            else
            {
                SignatureSection.Visibility = Visibility.Collapsed;
            }

            // Inheritance
            if (sym.Kind == UnrealSymbolKind.Class || sym.Kind == UnrealSymbolKind.Struct ||
                sym.Kind == UnrealSymbolKind.Interface)
            {
                if (info?.InheritanceChain?.Count > 0)
                {
                    InheritanceText.Text = sym.Name + " → " + string.Join(" → ", info.InheritanceChain);
                    InheritanceSection.Visibility = Visibility.Visible;
                }
                else if (info != null && !string.IsNullOrEmpty(info.BaseClass))
                {
                    InheritanceText.Text = $"{sym.Name} → {info.BaseClass}";
                    InheritanceSection.Visibility = Visibility.Visible;
                }
                else
                {
                    InheritanceSection.Visibility = Visibility.Collapsed;
                }

                // Specifiers
                if (info != null && !string.IsNullOrEmpty(info.MetaSpecifiers))
                {
                    SpecifiersText.Text = info.MetaSpecifiers;
                    SpecifiersSection.Visibility = Visibility.Visible;
                }
                else
                {
                    SpecifiersSection.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                InheritanceSection.Visibility = Visibility.Collapsed;
                SpecifiersSection.Visibility = Visibility.Collapsed;
            }

            // Owner
            if (!string.IsNullOrEmpty(sym.OwnerClass))
            {
                OwnerText.Text = sym.OwnerClass;
                OwnerSection.Visibility = Visibility.Visible;
            }
            else
            {
                OwnerSection.Visibility = Visibility.Collapsed;
            }

            // Documentation
            if (!string.IsNullOrEmpty(sym.Comment))
            {
                DocText.Text = sym.Comment;
                DocSection.Visibility = Visibility.Visible;
            }
            else
            {
                DocSection.Visibility = Visibility.Collapsed;
            }

            // Location
            var fileName = Path.GetFileName(sym.FilePath);
            LocationText.Text = $"{fileName}:{sym.LineNumber}";
            LocationSection.Visibility = Visibility.Visible;

            // Source badge
            SourceText.Text = sym.IsEngineSymbol ? "ENGINE" : "PROJECT";
            SourceBadge.Background = sym.IsEngineSymbol
                ? System.Windows.Media.Brushes.DarkSlateBlue
                : System.Windows.Media.Brushes.DarkGreen;
            SourceText.Foreground = System.Windows.Media.Brushes.White;
            SourceBadge.Visibility = Visibility.Visible;
        }

        private void ClearDisplay()
        {
            _currentSymbol = null;
            SymbolHeader.Text = "No symbol selected";
            SignatureSection.Visibility = Visibility.Collapsed;
            InheritanceSection.Visibility = Visibility.Collapsed;
            SpecifiersSection.Visibility = Visibility.Collapsed;
            OwnerSection.Visibility = Visibility.Collapsed;
            DocSection.Visibility = Visibility.Collapsed;
            LocationSection.Visibility = Visibility.Collapsed;
            SourceBadge.Visibility = Visibility.Collapsed;
        }

        private void LocationText_Click(object sender, MouseButtonEventArgs e)
        {
            if (_currentSymbol == null) return;

            // ponytail: fire-and-forget navigation
            Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    Microsoft.VisualStudio.Shell.VsShellUtilities.OpenDocument(
                        Microsoft.VisualStudio.Shell.ServiceProvider.GlobalProvider,
                        _currentSymbol.FilePath,
                        Guid.Empty,
                        out _, out _, out Microsoft.VisualStudio.Shell.Interop.IVsWindowFrame frame);
                    frame?.Show();

                    var view = Microsoft.VisualStudio.Shell.VsShellUtilities.GetTextView(frame);
                    if (view != null)
                    {
                        view.SetCaretPos(_currentSymbol.LineNumber - 1, 0);
                        view.CenterLines(_currentSymbol.LineNumber - 1, 1);
                    }
                }
                catch { }
            });
        }

        /// <summary>
        /// Gets the word under the caret in the active text view.
        /// ponytail: reuses the same pattern from GoToUnrealDefinitionCommand.
        /// </summary>
        private static string GetWordUnderCaret()
        {
            Microsoft.VisualStudio.Shell.ThreadHelper.ThrowIfNotOnUIThread();

            var view = EditorAccess.ActiveView();
            if (view == null) return null;
            var caret = view.Caret.Position.BufferPosition;
            var line = caret.GetContainingLine();
            var lineText = line.GetText();
            var col = caret.Position - line.Start.Position;

            if (string.IsNullOrEmpty(lineText) || col > lineText.Length) return null;

            int start = col;
            int end = col;
            while (start > 0 && IsIdChar(lineText[start - 1])) start--;
            while (end < lineText.Length && IsIdChar(lineText[end])) end++;

            return start >= end ? null : lineText.Substring(start, end - start);
        }

        private static bool IsIdChar(char c) => char.IsLetterOrDigit(c) || c == '_';
    }
}
