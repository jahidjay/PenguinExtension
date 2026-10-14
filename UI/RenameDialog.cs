using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using PenguinExtention.Models;

namespace PenguinExtention.UI
{
    /// <summary>Rename preview (VA: Shift+Alt+R). Uncertain (name-only) matches start unchecked.</summary>
    internal sealed class RenameDialog : DialogWindow
    {
        private readonly TextBox _name = new TextBox { FontSize = 14, Padding = new Thickness(4) };
        private readonly ListBox _list = new ListBox { FontFamily = new FontFamily("Consolas") };
        private readonly CheckBox _redirect = new CheckBox { Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _error = new TextBlock { Foreground = Brushes.IndianRed, Margin = new Thickness(0, 4, 0, 0) };
        private readonly string _oldName;

        public string NewName => _name.Text.Trim();
        public bool AddCoreRedirect => _redirect.IsChecked == true;
        public List<SymbolOccurrence> Chosen { get; } = new List<SymbolOccurrence>();

        private RenameDialog(UnrealSymbol symbol, IReadOnlyList<SymbolOccurrence> occurrences, string redirectLabel, bool redirectDefault)
        {
            _oldName = symbol.Name;
            Title = "Rename " + symbol.DisplayText;
            Width = 820; Height = 560;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            HasMinimizeButton = false; HasMaximizeButton = false;
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            _name.Text = symbol.Name;
            int certain = occurrences.Count(o => o.IsCertain);
            var info = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6),
                Text = $"{occurrences.Count} occurrence(s): {certain} certain, {occurrences.Count - certain} possible. " +
                       "Matches are by name (no compiler semantics) — review unchecked items before renaming. One Undo reverts everything."
            };

            foreach (var group in occurrences.GroupBy(o => o.FilePath, StringComparer.OrdinalIgnoreCase))
            {
                _list.Items.Add(new TextBlock { Text = group.Key, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 6, 0, 0) });
                foreach (var o in group)
                {
                    var box = new CheckBox
                    {
                        IsChecked = o.IsCertain, Tag = o,
                        Content = $"{o.LineNumber,5}: {(o.LineText ?? "").Trim()}" + (o.IsCertain ? "" : "   (possible)")
                    };
                    _list.Items.Add(box);
                }
            }

            _redirect.Visibility = redirectLabel == null ? Visibility.Collapsed : Visibility.Visible;
            _redirect.Content = redirectLabel;
            _redirect.IsChecked = redirectDefault;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            var all = new Button { Content = "Check all", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 0) };
            all.Click += (s, e) => SetAll(true);
            var none = new Button { Content = "Uncheck all", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 18, 0) };
            none.Click += (s, e) => SetAll(false);
            var ok = new Button { Content = "Rename", IsDefault = true, Padding = new Thickness(16, 3, 16, 3), Margin = new Thickness(0, 0, 6, 0) };
            ok.Click += (s, e) => Accept();
            var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 3, 16, 3) };
            buttons.Children.Add(all); buttons.Children.Add(none); buttons.Children.Add(ok); buttons.Children.Add(cancel);

            var top = new StackPanel();
            top.Children.Add(new TextBlock { Text = "New name:" });
            top.Children.Add(_name);
            top.Children.Add(info);
            var bottom = new StackPanel();
            bottom.Children.Add(_redirect);
            bottom.Children.Add(_error);
            bottom.Children.Add(buttons);

            var root = new DockPanel { Margin = new Thickness(10) };
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(top);
            root.Children.Add(bottom);
            root.Children.Add(_list);
            Content = root;
            Loaded += (s, e) => { _name.Focus(); _name.SelectAll(); };
            _name.KeyDown += (s, e) => { if (e.Key == Key.Enter) Accept(); };
        }

        public static RenameDialog Show(UnrealSymbol symbol, IReadOnlyList<SymbolOccurrence> occurrences, string redirectLabel, bool redirectDefault)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var dialog = new RenameDialog(symbol, occurrences, redirectLabel, redirectDefault);
            return dialog.ShowModal() == true ? dialog : null;
        }

        private void SetAll(bool value)
        {
            foreach (var box in _list.Items.OfType<CheckBox>()) box.IsChecked = value;
        }

        private void Accept()
        {
            var name = NewName;
            if (!IsIdentifier(name)) { _error.Text = "Not a valid C++ identifier."; return; }
            if (name == _oldName) { _error.Text = "Enter a different name."; return; }
            Chosen.Clear();
            Chosen.AddRange(_list.Items.OfType<CheckBox>().Where(b => b.IsChecked == true).Select(b => (SymbolOccurrence)b.Tag));
            if (Chosen.Count == 0) { _error.Text = "Nothing selected."; return; }
            DialogResult = true;
        }

        internal static bool IsIdentifier(string s) =>
            !string.IsNullOrEmpty(s) && (char.IsLetter(s[0]) || s[0] == '_') && s.All(c => char.IsLetterOrDigit(c) || c == '_');
    }
}
