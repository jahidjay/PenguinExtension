using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;

namespace PenguinExtention.UI
{
    // Public: WPF bindings ignore properties of non-public types.
    public sealed class PickItem
    {
        public string Title { get; set; }
        public string Detail { get; set; }
        public string Glyph { get; set; } = "";
        public object Tag { get; set; }
    }

    /// <summary>
    /// Filter-as-you-type picker (Find Symbol, Open File, GoTo Related, List Methods).
    /// Queries run off the UI thread with a 120 ms debounce; stale results are discarded.
    /// </summary>
    internal sealed class QuickPickDialog : DialogWindow
    {
        private readonly Func<string, CancellationToken, Task<IList<PickItem>>> _provider;
        private readonly TextBox _filter = new TextBox { FontSize = 14, Padding = new Thickness(4), Margin = new Thickness(0, 0, 0, 6) };
        private readonly ListBox _list = new ListBox { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(0, 6, 0, 0), Opacity = 0.7 };
        private readonly DispatcherTimer _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        private CancellationTokenSource _query;

        public PickItem Selected { get; private set; }

        public QuickPickDialog(string title, string initialFilter, Func<string, CancellationToken, Task<IList<PickItem>>> provider)
        {
            _provider = provider;
            Title = title;
            Width = 720; Height = 480;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            HasMinimizeButton = false; HasMaximizeButton = false;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            _list.ItemTemplate = BuildTemplate();
            _list.MouseDoubleClick += (s, e) => Accept();
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);

            var root = new DockPanel { Margin = new Thickness(10) };
            DockPanel.SetDock(_filter, Dock.Top);
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(_filter);
            root.Children.Add(_status);
            root.Children.Add(_list);
            Content = root;

            _filter.Text = initialFilter ?? "";
            _filter.TextChanged += (s, e) => { _debounce.Stop(); _debounce.Start(); };
            _filter.PreviewKeyDown += FilterKeyDown;
            _debounce.Tick += (s, e) => { _debounce.Stop(); Refresh(); };
            Loaded += (s, e) => { _filter.Focus(); _filter.SelectAll(); Refresh(); };
            Closed += (s, e) => _query?.Cancel();
        }

        /// <summary>Shows the dialog modally; returns the chosen item or null.</summary>
        public static PickItem Show(string title, string initialFilter, Func<string, CancellationToken, Task<IList<PickItem>>> provider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dialog = new QuickPickDialog(title, initialFilter, provider);
            return dialog.ShowModal() == true ? dialog.Selected : null;
        }

        /// <summary>Single-line text prompt: returns exactly what was typed (Enter), or null (Esc).</summary>
        public static string PromptText(string title, string initial, string hint)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dialog = new QuickPickDialog(title, initial, (text, ct) =>
                Task.FromResult<IList<PickItem>>(new List<PickItem> { new PickItem { Title = text, Detail = hint + " — press Enter to accept", Glyph = "" } }));
            dialog._promptMode = true;
            dialog.Height = 200;
            return dialog.ShowModal() == true ? dialog._filter.Text : null;
        }

        private bool _promptMode;

        private void FilterKeyDown(object sender, KeyEventArgs e)
        {
            if (_promptMode && e.Key == Key.Enter) { DialogResult = true; e.Handled = true; return; }
            switch (e.Key)
            {
                case Key.Down: Move(1); e.Handled = true; break;
                case Key.Up: Move(-1); e.Handled = true; break;
                case Key.PageDown: Move(10); e.Handled = true; break;
                case Key.PageUp: Move(-10); e.Handled = true; break;
                case Key.Enter: Accept(); e.Handled = true; break;
                case Key.Escape: DialogResult = false; e.Handled = true; break;
            }
        }

        private void Move(int delta)
        {
            if (_list.Items.Count == 0) return;
            _list.SelectedIndex = Math.Max(0, Math.Min(_list.Items.Count - 1, _list.SelectedIndex + delta));
            _list.ScrollIntoView(_list.SelectedItem);
        }

        private void Accept()
        {
            Selected = _list.SelectedItem as PickItem;
            if (Selected != null) DialogResult = true;
        }

        private void Refresh() => _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            _query?.Cancel();
            _query = new CancellationTokenSource();
            var token = _query.Token;
            var text = _filter.Text;
            _status.Text = "Searching…";
            try
            {
                await TaskScheduler.Default;
                var items = await _provider(text, token).ConfigureAwait(false);
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (token.IsCancellationRequested) return;
                _list.ItemsSource = items;
                if (items.Count > 0) _list.SelectedIndex = 0;
                _status.Text = items.Count == 0 ? "No matches" : items.Count + " result(s) — Enter to open, Esc to close";
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _status.Text = ex.Message;
            }
        });

        private static DataTemplate BuildTemplate()
        {
            var panel = new FrameworkElementFactory(typeof(DockPanel));
            var glyph = new FrameworkElementFactory(typeof(TextBlock));
            glyph.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PickItem.Glyph)));
            glyph.SetValue(TextBlock.FontFamilyProperty, new FontFamily("Segoe MDL2 Assets"));
            glyph.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
            glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            glyph.SetValue(DockPanel.DockProperty, Dock.Left);
            panel.AppendChild(glyph);

            var stack = new FrameworkElementFactory(typeof(StackPanel));
            var title = new FrameworkElementFactory(typeof(TextBlock));
            title.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PickItem.Title)));
            title.SetValue(TextBlock.FontWeightProperty, FontWeights.SemiBold);
            stack.AppendChild(title);
            var detail = new FrameworkElementFactory(typeof(TextBlock));
            detail.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding(nameof(PickItem.Detail)));
            detail.SetValue(UIElement.OpacityProperty, 0.65);
            detail.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            stack.AppendChild(detail);
            panel.AppendChild(stack);

            return new DataTemplate { VisualTree = panel };
        }
    }
}
