using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using PenguinExtention.Completion;

namespace PenguinExtention.UI
{
    /// <summary>
    /// The 🐧 Penguin suggestion box: a list (left) with a description pane (right), placed under the caret.
    /// UI thread only. Keyboard handling lives in <see cref="Editor.PenguinKeyFilter"/>; this class only renders.
    /// </summary>
    internal sealed class PenguinCompletionPopup
    {
        private readonly IWpfTextView _view;
        private readonly Popup _popup;
        private readonly ListBox _list;
        private readonly TextBlock _header;
        private readonly TextBlock _title;
        private readonly TextBlock _details;
        private readonly Border _detailPane;
        private List<PenguinItem> _items = new List<PenguinItem>();

        public event Action<PenguinItem> ItemChosen;

        public PenguinCompletionPopup(IWpfTextView view)
        {
            _view = view;
            _list = new ListBox
            {
                MaxHeight = 300, MinWidth = 260, MaxWidth = 520, BorderThickness = new Thickness(0),
                Background = Brushes.Transparent, FocusVisualStyle = null, Focusable = false
            };
            _list.SetResourceReference(Control.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            _list.SelectionChanged += (s, e) => ShowDetails();
            _list.MouseDoubleClick += (s, e) => { if (Selected != null) ItemChosen?.Invoke(Selected); };

            _header = new TextBlock { Margin = new Thickness(6, 3, 6, 3), FontSize = 11, Opacity = 0.75 };
            _header.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var left = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            left.Children.Add(_header);
            left.Children.Add(_list);

            _title = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) };
            _title.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _details = new TextBlock { TextWrapping = TextWrapping.Wrap };
            _details.SetResourceReference(TextBlock.ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            var detailStack = new StackPanel { Margin = new Thickness(8) };
            detailStack.Children.Add(_title);
            detailStack.Children.Add(_details);
            _detailPane = new Border { Width = 380, BorderThickness = new Thickness(1, 0, 0, 0), Child = detailStack };
            _detailPane.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(_detailPane, 1);
            grid.Children.Add(left);
            grid.Children.Add(_detailPane);

            var frame = new Border { BorderThickness = new Thickness(1), Child = grid, SnapsToDevicePixels = true };
            frame.SetResourceReference(Border.BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            frame.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            TextOptions.SetTextFormattingMode(frame, TextFormattingMode.Display);

            _popup = new Popup
            {
                Child = frame, AllowsTransparency = false, StaysOpen = true, Focusable = false,
                Placement = PlacementMode.Relative, PlacementTarget = _view.VisualElement
            };
        }

        public bool IsOpen => _popup.IsOpen;
        public PenguinItem Selected => _list.SelectedIndex >= 0 && _list.SelectedIndex < _items.Count ? _items[_list.SelectedIndex] : null;

        /// <summary>Shows (or refreshes) the list for <paramref name="model"/>, anchored at <paramref name="anchor"/>.</summary>
        public void Show(PenguinCompletionModel model, SnapshotPoint anchor)
        {
            var previous = Selected;
            _items = model.Items;
            _header.Text = "🐧 Penguin" + (string.IsNullOrEmpty(model.Context) ? "" : " — " + model.Context) + "   (" + _items.Count + ")";
            _list.Items.Clear();
            foreach (var item in _items) _list.Items.Add(Row(item));
            // Keep the user's choice when it is still there; otherwise the best match (first).
            int index = previous == null ? -1 : _items.FindIndex(i => i.Insert == previous.Insert && i.Kind == previous.Kind);
            _list.SelectedIndex = index >= 0 && model.Typed.Length > 0 && _items[index].Score <= _items[0].Score ? index : 0;
            if (_list.SelectedItem != null) _list.ScrollIntoView(_list.SelectedItem);
            Place(anchor);
            _popup.IsOpen = true;
            ShowDetails();
        }

        public void Close() => _popup.IsOpen = false;

        public void Move(int delta)
        {
            if (_items.Count == 0) return;
            int i = _list.SelectedIndex + delta;
            if (delta == 1 && i >= _items.Count) i = 0;               // wrap like Visual Assist
            else if (delta == -1 && i < 0) i = _items.Count - 1;
            _list.SelectedIndex = Math.Max(0, Math.Min(_items.Count - 1, i));
            _list.ScrollIntoView(_list.SelectedItem);
        }

        private void Place(SnapshotPoint anchor)
        {
            ITextViewLine line = null;
            try { line = _view.TextViewLines?.GetTextViewLineContainingBufferPosition(anchor); } catch (InvalidOperationException) { }
            double zoom = _view.ZoomLevel / 100.0;
            if (line == null) { _popup.HorizontalOffset = 40; _popup.VerticalOffset = 40; return; }
            var bounds = line.GetCharacterBounds(anchor);
            _popup.HorizontalOffset = (bounds.Left - _view.ViewportLeft) * zoom - 22;
            _popup.VerticalOffset = (bounds.TextBottom - _view.ViewportTop) * zoom + 2;
        }

        private static ListBoxItem Row(PenguinItem item)
        {
            var glyph = new TextBlock { Text = item.Glyph, Width = 18, FontFamily = new FontFamily("Segoe UI Symbol"), TextAlignment = TextAlignment.Center, Opacity = 0.85 };
            var name = new TextBlock { Text = item.Display, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 380 };
            if (item.Kind == PenguinItemKind.Smart) name.FontWeight = FontWeights.SemiBold;
            var detail = new TextBlock { Text = item.Detail, Opacity = 0.55, Margin = new Thickness(10, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160 };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(glyph);
            row.Children.Add(name);
            row.Children.Add(detail);
            return new ListBoxItem { Content = row, Padding = new Thickness(2, 1, 6, 1), Focusable = false };
        }

        private void ShowDetails()
        {
            var item = Selected;
            if (item == null) { _detailPane.Visibility = Visibility.Collapsed; return; }
            _detailPane.Visibility = Visibility.Visible;
            if (item.Symbol != null)
            {
                _title.Text = SymbolTooltip.Header(item.Symbol);
                var body = string.Join("\n", SymbolTooltip.Body(item.Symbol));
                _details.Text = item.Description == null ? body : item.Description + "\n\n" + body;
            }
            else
            {
                _title.Text = item.Display;
                _details.Text = item.Description ?? item.Detail ?? "";
            }
        }
    }
}
