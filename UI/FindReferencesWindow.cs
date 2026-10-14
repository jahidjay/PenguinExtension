using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using PenguinExtention.Models;

namespace PenguinExtention.UI
{
    /// <summary>Find References results (VA: Shift+Alt+F). Name-based candidates grouped by file.</summary>
    [Guid("6C1E2F7A-3B9D-4E5A-A1C8-2D7F0B4E9A63")]
    public class FindReferencesWindow : ToolWindowPane
    {
        internal readonly ReferencesControl Control = new ReferencesControl();

        public FindReferencesWindow() : base(null)
        {
            Caption = "🐧 Penguin — Find References";
            Content = Control;
        }

        /// <summary>Shows the window with <paramref name="occurrences"/> for <paramref name="title"/>.</summary>
        internal static async Task ShowAsync(string title, IReadOnlyList<SymbolOccurrence> occurrences)
        {
            var package = PenguinExtensionPackage.Instance;
            var pane = await package.ShowToolWindowAsync(typeof(FindReferencesWindow), 0, true, package.DisposalToken) as FindReferencesWindow;
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            pane?.Control.SetResults(title, occurrences);
        }
    }

    internal sealed class ReferencesControl : UserControl
    {
        private readonly TextBlock _header = new TextBlock { FontWeight = FontWeights.SemiBold, Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap, Text = "Run Find References (Alt+U, F) on a symbol." };
        private readonly CheckBox _onlyCertain = new CheckBox { Content = "Certain matches only", Margin = new Thickness(6, 0, 6, 4) };
        private readonly ListBox _list = new ListBox { BorderThickness = new Thickness(0), FontFamily = new FontFamily("Consolas") };
        private IReadOnlyList<SymbolOccurrence> _all = new SymbolOccurrence[0];
        private string _title;

        public ReferencesControl()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _list.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _list.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            _list.MouseDoubleClick += (s, e) => NavigateSelected();
            _list.KeyDown += (s, e) => { if (e.Key == Key.Enter) { NavigateSelected(); e.Handled = true; } };
            _onlyCertain.Checked += (s, e) => Render();
            _onlyCertain.Unchecked += (s, e) => Render();

            var root = new DockPanel();
            DockPanel.SetDock(_header, Dock.Top);
            DockPanel.SetDock(_onlyCertain, Dock.Top);
            root.Children.Add(_header);
            root.Children.Add(_onlyCertain);
            root.Children.Add(_list);
            Content = root;
        }

        public void SetResults(string title, IReadOnlyList<SymbolOccurrence> occurrences)
        {
            _title = title;
            _all = occurrences ?? new SymbolOccurrence[0];
            Render();
        }

        private void Render()
        {
            var shown = _onlyCertain.IsChecked == true ? _all.Where(o => o.IsCertain).ToList() : _all.ToList();
            int certain = _all.Count(o => o.IsCertain);
            _header.Text = $"{_title}: {_all.Count} reference(s), {certain} certain, {_all.Count - certain} possible (name match). Double-click or Enter to open.";
            _list.Items.Clear();
            foreach (var group in shown.GroupBy(o => o.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                _list.Items.Add(new ListBoxItem
                {
                    Content = $"{Path.GetFileName(group.Key)}  ({group.Count()})  —  {group.Key}",
                    FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 0)
                });
                foreach (var o in group)
                {
                    var tag = o.Context == OccurrenceContext.Declaration ? "decl" : o.IsCertain ? "    " : "  ? ";
                    _list.Items.Add(new ListBoxItem
                    {
                        Content = $"  {tag} {o.LineNumber,5}: {(o.LineText ?? "").Trim()}",
                        Tag = o, Opacity = o.IsCertain ? 1.0 : 0.75
                    });
                }
            }
        }

        private void NavigateSelected()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if ((_list.SelectedItem as ListBoxItem)?.Tag is SymbolOccurrence o)
                Commands.PenguinEditor.Open(o.FilePath, o.LineNumber, o.ColumnNumber);
        }
    }
}
