using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using PenguinExtention.Models;
using PenguinExtention.Services;

namespace PenguinExtention.UI
{
    /// <summary>
    /// Parameter info box (signature with the current parameter in bold, ▲/▼ overloads, docs) and, beside it, the
    /// definition peek box (the function's .cpp body or declaration). Shown above the line being typed. UI thread only.
    /// </summary>
    internal sealed class PenguinParameterInfoPopup
    {
        private readonly IWpfTextView _view;
        private readonly Popup _popup;
        private readonly TextBlock _overloads, _signature, _doc, _location, _peekTitle, _peekCode;
        private readonly Border _peekPane;
        private List<UnrealSymbol> _symbols = new List<UnrealSymbol>();
        private int _index, _parameter;
        private DefinitionSnippet _peek;

        public PenguinParameterInfoPopup(IWpfTextView view)
        {
            _view = view;
            _overloads = Text(11, 0.7);
            _signature = Text(13, 1);
            _signature.FontFamily = new FontFamily("Consolas");
            _doc = Text(12, 0.9);
            _location = Text(11, 0.6);
            var info = new StackPanel { Margin = new Thickness(8, 5, 8, 6), MaxWidth = 560 };
            info.Children.Add(_overloads);
            info.Children.Add(_signature);
            info.Children.Add(_doc);
            info.Children.Add(_location);

            _peekTitle = Text(11, 0.75);
            _peekCode = Text(12, 1);
            _peekCode.FontFamily = new FontFamily("Consolas");
            _peekCode.TextWrapping = TextWrapping.NoWrap;
            var peek = new StackPanel { Margin = new Thickness(8, 5, 8, 6) };
            peek.Children.Add(_peekTitle);
            peek.Children.Add(new ScrollViewer { Content = _peekCode, MaxHeight = 380, MaxWidth = 640, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            _peekPane = new Border { BorderThickness = new Thickness(1, 0, 0, 0), Child = peek };
            _peekPane.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(info);
            row.Children.Add(_peekPane);
            var frame = new Border { BorderThickness = new Thickness(1), Child = row };
            frame.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            TextOptions.SetTextFormattingMode(frame, TextFormattingMode.Display);
            _popup = new Popup { Child = frame, StaysOpen = true, Focusable = false, Placement = PlacementMode.Relative, PlacementTarget = _view.VisualElement };
        }

        private static TextBlock Text(double size, double opacity)
        {
            var t = new TextBlock { FontSize = size, Opacity = opacity, TextWrapping = TextWrapping.Wrap };
            t.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            return t;
        }

        public bool IsOpen => _popup.IsOpen;
        public int OverloadCount => _symbols.Count;
        public UnrealSymbol Current => _index < _symbols.Count ? _symbols[_index] : null;
        /// <summary>Raised when the shown overload changes, so the caller can load its definition peek.</summary>
        public event Action<UnrealSymbol> OverloadShown;

        public void Show(List<UnrealSymbol> symbols, int parameter, SnapshotPoint anchor, bool showPeek)
        {
            _symbols = symbols;
            _index = Math.Min(_index, Math.Max(0, symbols.Count - 1));
            _peekPane.Visibility = showPeek ? Visibility.Visible : Visibility.Collapsed;
            SetParameter(parameter);
            Place(anchor);
            _popup.IsOpen = true;
        }

        public void ResetOverload() { _index = 0; _peek = null; }

        public void Cycle(int delta)
        {
            if (_symbols.Count < 2) return;
            _index = (_index + delta + _symbols.Count) % _symbols.Count;
            _peek = null;
            Render();
            OverloadShown?.Invoke(Current);
        }

        public void SetParameter(int parameter)
        {
            _parameter = parameter;
            Render();
        }

        public void SetPeek(UnrealSymbol forSymbol, DefinitionSnippet peek)
        {
            if (forSymbol != Current) return;
            _peek = peek;
            RenderPeek();
        }

        public void Close() => _popup.IsOpen = false;

        private void Render()
        {
            var s = Current;
            if (s == null) return;
            _overloads.Text = _symbols.Count > 1 ? "▲ " + (_index + 1) + " of " + _symbols.Count + " ▼   (↑/↓ switch overload)" : "🐧 " + s.KindDisplay;
            _overloads.Visibility = Visibility.Visible;

            _signature.Inlines.Clear();
            var decl = s.Signature ?? s.Name + "()";
            decl = System.Text.RegularExpressions.Regex.Replace(decl, @"^\s*UFUNCTION\s*\([^)]*\)\s*", "");
            int open = decl.IndexOf('('), close = decl.LastIndexOf(')');
            if (open < 0 || close < open)
            {
                _signature.Inlines.Add(new Run(decl));
            }
            else
            {
                _signature.Inlines.Add(new Run(QualifiedHead(s, decl.Substring(0, open + 1))));
                var parameters = CodeContext.SplitTopLevel(decl.Substring(open + 1, close - open - 1)).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
                for (int i = 0; i < parameters.Count; i++)
                {
                    var run = new Run(parameters[i]);
                    if (i == _parameter) { run.FontWeight = FontWeights.Bold; run.TextDecorations = TextDecorations.Underline; }
                    _signature.Inlines.Add(run);
                    if (i < parameters.Count - 1) _signature.Inlines.Add(new Run(", "));
                }
                _signature.Inlines.Add(new Run(decl.Substring(close)));
                if (_parameter >= parameters.Count && parameters.Count > 0)
                    _overloads.Text += "   ⚠ more arguments than parameters";
            }
            _doc.Text = string.IsNullOrEmpty(s.Comment) ? "" : s.Comment.Trim();
            _doc.Visibility = _doc.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _location.Text = "📁 " + System.IO.Path.GetFileName(s.FilePath) + ":" + s.LineNumber + (s.IsEngineSymbol ? "   [Engine]" : "   [Project]");
            RenderPeek();
        }

        private static string QualifiedHead(UnrealSymbol s, string head)
        {
            // "void BeginPlay(" → "void AActor::BeginPlay(" so the owner is visible.
            if (string.IsNullOrEmpty(s.OwnerClass) || head.Contains("::")) return head;
            int name = head.LastIndexOf(s.Name + "(", StringComparison.Ordinal);
            return name < 0 ? head : head.Substring(0, name) + s.OwnerClass + "::" + head.Substring(name);
        }

        private void RenderPeek()
        {
            if (_peekPane.Visibility != Visibility.Visible) return;
            if (_peek == null) { _peekTitle.Text = "🐧 loading definition…"; _peekCode.Text = ""; return; }
            _peekTitle.Text = "🐧 " + (Current?.OwnerClass == null ? "" : Current.OwnerClass + "::") + Current?.Name + " — " + _peek.Title + "   (F12 / Alt+G to open)";
            _peekCode.Text = _peek.Text;
        }

        private void Place(SnapshotPoint anchor)
        {
            ITextViewLine line = null;
            try { line = _view.TextViewLines?.GetTextViewLineContainingBufferPosition(anchor); } catch (InvalidOperationException) { }
            double zoom = _view.ZoomLevel / 100.0;
            if (line == null) return;
            var bounds = line.GetCharacterBounds(anchor);
            _popup.HorizontalOffset = (bounds.Left - _view.ViewportLeft) * zoom;
            // Above the line so the completion list (below) and the code being typed stay visible.
            _popup.Child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var height = _popup.Child.DesiredSize.Height;
            var top = (bounds.Top - _view.ViewportTop) * zoom - height - 4;
            _popup.VerticalOffset = top < 0 ? (bounds.TextBottom - _view.ViewportTop) * zoom + 4 : top;
        }
    }
}
