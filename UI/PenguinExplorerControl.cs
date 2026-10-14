using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using PenguinExtention.Commands;
using PenguinExtention.Core;
using PenguinExtention.Models;
using PenguinExtention.Services;

namespace PenguinExtention.UI
{
    /// <summary>
    /// Penguin Explorer: symbol browser (search + kind/scope filters + grouping), live outline of the active file,
    /// and a class hierarchy. Code-built WPF (no XAML) so it themes with VS and has no binding-to-internal pitfalls.
    /// </summary>
    internal sealed class PenguinExplorerControl : UserControl
    {
        private static readonly FontFamily Glyphs = new FontFamily("Segoe MDL2 Assets");
        private static readonly FontFamily Mono = new FontFamily("Consolas");

        // ── Symbols tab ─────────────────────────────────────────────
        private readonly TextBox _search = new TextBox { Padding = new Thickness(4), FontSize = 13 };
        private readonly ComboBox _scope = new ComboBox { Width = 105, Margin = new Thickness(4, 0, 0, 0) };
        private readonly ComboBox _group = new ComboBox { Width = 115, Margin = new Thickness(4, 0, 0, 0) };
        private readonly WrapPanel _kindBar = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        private readonly Dictionary<UnrealSymbolKind[], ToggleButton> _kindToggles = new Dictionary<UnrealSymbolKind[], ToggleButton>();
        private readonly TreeView _symbolTree = new TreeView { BorderThickness = new Thickness(0) };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(2, 4, 2, 2), Opacity = 0.75, TextTrimming = TextTrimming.CharacterEllipsis };

        // ── Details pane ────────────────────────────────────────────
        private readonly TextBlock _detailTitle = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _detailLocation = new TextBlock { Margin = new Thickness(0, 2, 0, 2) };
        private readonly TextBox _detailBody = new TextBox
        {
            IsReadOnly = true, FontFamily = Mono, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0),
            MaxHeight = 160, VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };

        // ── Outline + hierarchy tabs ────────────────────────────────
        private readonly TreeView _outlineTree = new TreeView { BorderThickness = new Thickness(0) };
        private readonly TextBlock _outlineStatus = new TextBlock { Margin = new Thickness(2, 4, 2, 2), Opacity = 0.75 };
        private readonly TreeView _hierarchyTree = new TreeView { BorderThickness = new Thickness(0) };
        private readonly TextBox _hierarchyFilter = new TextBox { Padding = new Thickness(4) };
        private readonly TabControl _tabs = new TabControl();

        private readonly DispatcherTimer _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        private readonly DispatcherTimer _outlineTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        private CancellationTokenSource _query;
        private string _outlineKey;
        private UnrealSymbol _selected;

        public PenguinExplorerControl()
        {
            SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
            // WPF's stock styles hard-code light system colors (black tree text on the dark theme, white tabs/buttons).
            PenguinTheme.Apply(this);
            foreach (var tree in new[] { _symbolTree, _outlineTree, _hierarchyTree })
            {
                tree.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
                tree.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);
                tree.MouseDoubleClick += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); OpenSelected((TreeView)s); };
                tree.KeyDown += (s, e) => { ThreadHelper.ThrowIfNotOnUIThread(); if (e.Key == Key.Enter) { OpenSelected((TreeView)s); e.Handled = true; } };
                tree.SelectedItemChanged += (s, e) => ShowDetails((e.NewValue as TreeViewItem)?.Tag as UnrealSymbol);
                tree.ContextMenu = BuildContextMenu(tree);
            }
            _detailBody.SetResourceReference(BackgroundProperty, EnvironmentColors.ToolWindowBackgroundBrushKey);
            _detailBody.SetResourceReference(ForegroundProperty, EnvironmentColors.ToolWindowTextBrushKey);

            _tabs.Items.Add(new TabItem { Header = "Symbols", Content = BuildSymbolsTab() });
            _tabs.Items.Add(new TabItem { Header = "This File", Content = BuildOutlineTab() });
            _tabs.Items.Add(new TabItem { Header = "Class Hierarchy", Content = BuildHierarchyTab() });
            _tabs.SelectionChanged += (s, e) => { if (e.Source == _tabs) OnTabChanged(); };

            var details = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(6), Child = new StackPanel { Children = { _detailTitle, _detailLocation, _detailBody } } };
            details.SetResourceReference(Border.BorderBrushProperty, EnvironmentColors.ToolWindowBorderBrushKey);
            var root = new DockPanel();
            DockPanel.SetDock(details, Dock.Bottom);
            root.Children.Add(details);
            root.Children.Add(_tabs);
            Content = root;

            _debounce.Tick += (s, e) => { _debounce.Stop(); RefreshSymbols(); };
            _outlineTimer.Tick += (s, e) => RefreshOutline(force: false);
            Loaded += (s, e) => { RefreshSymbols(); _outlineTimer.Start(); BackendService.Changed += OnBackendChanged; };
            Unloaded += (s, e) => { _outlineTimer.Stop(); _query?.Cancel(); BackendService.Changed -= OnBackendChanged; };
            ShowDetails(null);
        }

        private void OnBackendChanged() => _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            Restart();
        });

        private void Restart() { _debounce.Stop(); _debounce.Start(); }

        // ════════════════════════════════════════════════════════════
        // Symbols tab
        // ════════════════════════════════════════════════════════════

        private UIElement BuildSymbolsTab()
        {
            _search.TextChanged += (s, e) => Restart();
            _search.PreviewKeyDown += (s, e) =>
            {
                if (e.Key == Key.Down && _symbolTree.Items.Count > 0) { _symbolTree.Focus(); e.Handled = true; }
                if (e.Key == Key.Escape) { _search.Clear(); e.Handled = true; }
            };
            _search.ToolTip = "Type a name, prefix, substring or acronym (e.g. CMC, PlayerCtrl). Esc clears.";

            foreach (var s in new[] { "All", "Project", "Libraries" }) _scope.Items.Add(s);
            _scope.SelectedIndex = 0;
            _scope.ToolTip = "Project = your code; Libraries = SDL2 / engine / external headers";
            _scope.SelectionChanged += (s, e) => Restart();
            foreach (var g in new[] { "Group: Kind", "Group: File", "Group: Owner", "Group: None" }) _group.Items.Add(g);
            _group.SelectedIndex = 0;
            _group.SelectionChanged += (s, e) => Restart();

            AddKind("Classes", true, UnrealSymbolKind.Class, UnrealSymbolKind.Interface);
            AddKind("Structs", true, UnrealSymbolKind.Struct);
            AddKind("Enums", true, UnrealSymbolKind.Enum, UnrealSymbolKind.EnumValue);
            AddKind("Functions", true, UnrealSymbolKind.Function, UnrealSymbolKind.Delegate);
            AddKind("Fields", true, UnrealSymbolKind.Property);
            AddKind("Variables", true, UnrealSymbolKind.Variable);
            AddKind("Typedefs", true, UnrealSymbolKind.Typedef);
            AddKind("Macros", false, UnrealSymbolKind.Macro);

            var row = new DockPanel();
            DockPanel.SetDock(_group, Dock.Right);
            DockPanel.SetDock(_scope, Dock.Right);
            row.Children.Add(_group);
            row.Children.Add(_scope);
            row.Children.Add(_search);

            var panel = new DockPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(row, Dock.Top);
            DockPanel.SetDock(_kindBar, Dock.Top);
            DockPanel.SetDock(_status, Dock.Bottom);
            panel.Children.Add(row);
            panel.Children.Add(_kindBar);
            panel.Children.Add(_status);
            panel.Children.Add(_symbolTree);
            return panel;
        }

        private void AddKind(string label, bool on, params UnrealSymbolKind[] kinds)
        {
            var t = new ToggleButton { Content = label, IsChecked = on, Margin = new Thickness(0, 0, 4, 2), Padding = new Thickness(6, 1, 6, 1), FontSize = 11 };
            t.Checked += (s, e) => Restart();
            t.Unchecked += (s, e) => Restart();
            _kindToggles[kinds] = t;
            _kindBar.Children.Add(t);
        }

        private void RefreshSymbols() => _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            _query?.Cancel();
            var cts = new CancellationTokenSource();
            _query = cts;
            var token = cts.Token;
            var text = _search.Text.Trim();
            var kinds = new HashSet<UnrealSymbolKind>(_kindToggles.Where(k => k.Value.IsChecked == true).SelectMany(k => k.Key));
            int scope = _scope.SelectedIndex, group = _group.SelectedIndex;

            if (!BackendService.IsReady) { _symbolTree.Items.Clear(); _status.Text = "🐧 " + BackendService.Status; return; }
            try
            {
                await TaskScheduler.Default;
                var results = await BackendService.SearchAsync(text, text.Length == 0 ? 3000 : 1500, token).ConfigureAwait(false);
                var filtered = results.Where(s => kinds.Contains(s.Kind) && (scope == 0 || (scope == 1) != s.IsEngineSymbol)).Take(1000).ToList();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                Populate(filtered, group);
                _status.Text = $"🐧 {filtered.Count}{(filtered.Count == 1000 ? "+" : "")} symbol(s) · {BackendService.Status}" +
                               (text.Length == 0 ? " · type to search everything" : "");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!token.IsCancellationRequested) _status.Text = ex.Message;
            }
        });

        private void Populate(List<UnrealSymbol> symbols, int group)
        {
            _symbolTree.Items.Clear();
            if (group == 3) { foreach (var s in symbols) _symbolTree.Items.Add(SymbolItem(s, showOwner: true)); return; }

            Func<UnrealSymbol, string> key;
            switch (group)
            {
                case 1: key = s => Path.GetFileName(s.FilePath ?? "?"); break;
                case 2: key = s => s.OwnerClass ?? "(global)"; break;
                default: key = s => KindGroup(s.Kind); break;
            }
            bool expand = symbols.Count <= 60;
            foreach (var g in symbols.GroupBy(key).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var header = GroupItem(g.Key, g.Count(), group == 1 ? "" : group == 2 ? "" : g.First().KindGlyph);
                header.ToolTip = group == 1 ? g.First().FilePath : null;
                foreach (var s in g) header.Items.Add(SymbolItem(s, showOwner: group != 2));
                header.IsExpanded = expand;
                _symbolTree.Items.Add(header);
            }
        }

        private static string KindGroup(UnrealSymbolKind k)
        {
            switch (k)
            {
                case UnrealSymbolKind.Class: case UnrealSymbolKind.Interface: return "Classes";
                case UnrealSymbolKind.Struct: return "Structs";
                case UnrealSymbolKind.Enum: return "Enums";
                case UnrealSymbolKind.EnumValue: return "Enum values";
                case UnrealSymbolKind.Function: return "Functions";
                case UnrealSymbolKind.Delegate: return "Delegates";
                case UnrealSymbolKind.Property: return "Fields";
                case UnrealSymbolKind.Variable: return "Variables";
                case UnrealSymbolKind.Typedef: return "Typedefs";
                case UnrealSymbolKind.Macro: return "Macros";
                default: return k.ToString();
            }
        }

        // ════════════════════════════════════════════════════════════
        // This File (outline of the active document, parsed from the live buffer)
        // ════════════════════════════════════════════════════════════

        private UIElement BuildOutlineTab()
        {
            var refresh = new Button { Content = "Refresh", Padding = new Thickness(8, 1, 8, 1), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 4) };
            refresh.Click += (s, e) => RefreshOutline(force: true);
            var panel = new DockPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(refresh, Dock.Top);
            DockPanel.SetDock(_outlineStatus, Dock.Bottom);
            panel.Children.Add(refresh);
            panel.Children.Add(_outlineStatus);
            panel.Children.Add(_outlineTree);
            return panel;
        }

        private void RefreshOutline(bool force)
        {
            if (!(_tabs.SelectedIndex == 1 || force)) return;
            var view = EditorAccess.ActiveView();
            if (view == null) return;
            var file = PenguinEditor.FilePathOf(view) ?? "(unsaved)";
            var snapshot = view.TextSnapshot;
            var key = file + "#" + snapshot.Version.VersionNumber;
            if (!force && key == _outlineKey) return;
            _outlineKey = key;

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await TaskScheduler.Default;
                var symbols = new List<UnrealSymbol>();
                CppSymbolExtractor.Extract(snapshot.GetText(), file, false, symbols, new List<UnrealClassInfo>());
                var outline = symbols.Where(s => s.Kind != UnrealSymbolKind.Macro || s.Signature?.Contains("(") == true || symbols.Count < 200)
                                     .OrderBy(s => s.LineNumber).ToList();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (key != _outlineKey) return;

                _outlineTree.Items.Clear();
                var types = outline.Where(s => s.Kind == UnrealSymbolKind.Class || s.Kind == UnrealSymbolKind.Struct || s.Kind == UnrealSymbolKind.Enum).ToList();
                var typeItems = new Dictionary<string, TreeViewItem>(StringComparer.Ordinal);
                foreach (var s in outline)
                {
                    if (types.Contains(s))
                    {
                        var item = SymbolItem(s, showOwner: false);
                        item.IsExpanded = true;
                        if (!typeItems.ContainsKey(s.Name)) typeItems[s.Name] = item;
                        _outlineTree.Items.Add(item);
                    }
                    else if (s.OwnerClass != null && typeItems.TryGetValue(s.OwnerClass, out var parent)) parent.Items.Add(SymbolItem(s, showOwner: false));
                    else _outlineTree.Items.Add(SymbolItem(s, showOwner: true));
                }
                _outlineStatus.Text = $"🐧 {Path.GetFileName(file)}: {outline.Count} declaration(s) — live from the editor buffer";
            });
        }

        // ════════════════════════════════════════════════════════════
        // Class hierarchy
        // ════════════════════════════════════════════════════════════

        private UIElement BuildHierarchyTab()
        {
            _hierarchyFilter.ToolTip = "Filter classes by name; the tree shows each matching class with its base chain and derived classes.";
            _hierarchyFilter.TextChanged += (s, e) => RefreshHierarchy();
            var panel = new DockPanel { Margin = new Thickness(4) };
            DockPanel.SetDock(_hierarchyFilter, Dock.Top);
            panel.Children.Add(_hierarchyFilter);
            panel.Children.Add(_hierarchyTree);
            return panel;
        }

        private void RefreshHierarchy() => _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            var filter = _hierarchyFilter.Text.Trim();
            if (!BackendService.IsReady) return;
            await TaskScheduler.Default;
            var all = await BackendService.SearchAsync(filter, 4000, CancellationToken.None).ConfigureAwait(false);
            var classes = all.Where(s => s.Kind == UnrealSymbolKind.Class || s.Kind == UnrealSymbolKind.Struct || s.Kind == UnrealSymbolKind.Interface)
                             .GroupBy(s => s.Name).Select(g => g.OrderBy(s => s.IsEngineSymbol ? 1 : 0).First()).ToList();
            var bases = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var c in classes)
            {
                var info = await BackendService.ClassInfoAsync(c, CancellationToken.None).ConfigureAwait(false);
                var b = info?.BaseClass ?? c.Bases?.FirstOrDefault();
                if (!string.IsNullOrEmpty(b)) bases[c.Name] = b;
            }
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _hierarchyTree.Items.Clear();
            var byName = classes.ToDictionary(c => c.Name, StringComparer.Ordinal);
            var children = bases.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).OrderBy(n => n).ToList());

            // Roots: classes whose base isn't in the result set (shown with the base name as context).
            foreach (var c in classes.Where(c => !bases.TryGetValue(c.Name, out var b) || !byName.ContainsKey(b)).OrderBy(c => c.Name).Take(400))
            {
                var item = SymbolItem(c, showOwner: false);
                if (bases.TryGetValue(c.Name, out var outside)) item.Header = HeaderFor(c.KindGlyph, c.Name, "  : " + outside, c.IsEngineSymbol);
                AddDerived(item, c.Name, byName, children, 0);
                item.IsExpanded = filter.Length > 0;
                _hierarchyTree.Items.Add(item);
            }
        });

        private void AddDerived(TreeViewItem parent, string name, Dictionary<string, UnrealSymbol> byName, Dictionary<string, List<string>> children, int depth)
        {
            if (depth > 12 || !children.TryGetValue(name, out var kids)) return;
            foreach (var k in kids)
            {
                if (!byName.TryGetValue(k, out var sym)) continue;
                var item = SymbolItem(sym, showOwner: false);
                AddDerived(item, k, byName, children, depth + 1);
                parent.Items.Add(item);
            }
        }

        private void OnTabChanged()
        {
            if (_tabs.SelectedIndex == 1) RefreshOutline(force: true);
            else if (_tabs.SelectedIndex == 2 && _hierarchyTree.Items.Count == 0) RefreshHierarchy();
        }

        // ════════════════════════════════════════════════════════════
        // Items, details, actions
        // ════════════════════════════════════════════════════════════

        private TreeViewItem SymbolItem(UnrealSymbol s, bool showOwner)
        {
            var label = showOwner ? s.DisplayText : s.Name;
            var item = new TreeViewItem { Tag = s, Header = HeaderFor(s.KindGlyph, label, Suffix(s), s.IsEngineSymbol) };
            item.ToolTip = (s.Signature ?? s.DisplayText) + "\n" + Path.GetFileName(s.FilePath ?? "") + ":" + s.LineNumber;
            return item;
        }

        private static string Suffix(UnrealSymbol s)
        {
            if (s.Kind == UnrealSymbolKind.Function && s.Signature != null)
            {
                int open = s.Signature.IndexOf('(');
                var args = open >= 0 ? s.Signature.Substring(open) : "()";
                return args.Length > 60 ? "(…)" : args;
            }
            if ((s.Kind == UnrealSymbolKind.Property || s.Kind == UnrealSymbolKind.Variable) && !string.IsNullOrEmpty(s.ReturnType)) return " : " + s.ReturnType;
            return "";
        }

        private static StackPanel HeaderFor(string glyph, string name, string suffix, bool external)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = glyph, FontFamily = Glyphs, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85 });
            panel.Children.Add(new TextBlock { Text = name, FontStyle = external ? FontStyles.Italic : FontStyles.Normal });
            if (!string.IsNullOrEmpty(suffix)) panel.Children.Add(new TextBlock { Text = suffix, Opacity = 0.6 });
            return panel;
        }

        private static TreeViewItem GroupItem(string title, int count, string glyph)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal };
            panel.Children.Add(new TextBlock { Text = glyph, FontFamily = Glyphs, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "  " + count, Opacity = 0.6 });
            return new TreeViewItem { Header = panel };
        }

        private void ShowDetails(UnrealSymbol s)
        {
            _selected = s;
            if (s == null)
            {
                _detailTitle.Text = "🐧 Select a symbol — double-click or Enter opens it; right-click for more.";
                _detailLocation.Inlines.Clear();
                _detailBody.Visibility = Visibility.Collapsed;
                return;
            }
            _detailTitle.Text = $"[{KindGroup(s.Kind).TrimEnd('s')}] {s.DisplayText}" + (s.IsEngineSymbol ? "   (library)" : "");
            _detailLocation.Inlines.Clear();
            var link = new Hyperlink(new Run(Path.GetFileName(s.FilePath ?? "?") + ":" + s.LineNumber)) { ToolTip = s.FilePath };
            link.Click += (o, e) => { ThreadHelper.ThrowIfNotOnUIThread(); PenguinEditor.Navigate(_selected); };
            _detailLocation.Inlines.Add(link);
            var body = s.Signature ?? "";
            if (!string.IsNullOrWhiteSpace(s.Comment)) body = s.Comment.Trim() + "\n\n" + body;
            _detailBody.Text = body;
            _detailBody.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OpenSelected(TreeView tree)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if ((tree.SelectedItem as TreeViewItem)?.Tag is UnrealSymbol s) PenguinEditor.Navigate(s);
        }

        private ContextMenu BuildContextMenu(TreeView tree)
        {
            var menu = new ContextMenu();
            UnrealSymbol Current() => (tree.SelectedItem as TreeViewItem)?.Tag as UnrealSymbol;
            void Add(string text, Action<UnrealSymbol> action)
            {
                var mi = new MenuItem { Header = text };
                mi.Click += (s, e) => { var sym = Current(); if (sym != null) action(sym); };
                menu.Items.Add(mi);
            }
            Add("Go to Definition", s => { ThreadHelper.ThrowIfNotOnUIThread(); PenguinEditor.Navigate(s); });
            void AddPremium(string text, Func<Func<UnrealSymbol, Task>> hook) => Add(text, s =>
            {
                var action = hook();
                if (action == null) { ThreadHelper.ThrowIfNotOnUIThread(); PenguinEditor.Status(PremiumHooks.Upsell); return; }
                PenguinEditor.Run(() => action(s));
            });
            AddPremium("Go to Implementation", () => PremiumHooks.GoToImplementation);
            AddPremium("Find References", () => PremiumHooks.FindReferences);
            menu.Items.Add(new Separator());
            Add("Copy Name", s => Clipboard.SetText(s.Name));
            Add("Copy Qualified Name", s => Clipboard.SetText(s.FullyQualifiedName ?? s.DisplayText));
            Add("Copy #include", s =>
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var current = PenguinEditor.FilePathOf(EditorAccess.ActiveView());
                Clipboard.SetText("#include \"" + HeaderSourceResolver.IncludePathFor(s.FilePath, current) + "\"");
                PenguinEditor.Status("Copied #include for " + s.Name + ".");
            });
            Add("Copy Location", s => Clipboard.SetText(s.FilePath + "(" + s.LineNumber + ")"));
            return menu;
        }
    }
}
