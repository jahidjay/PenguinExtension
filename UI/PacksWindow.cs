using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using PenguinExtention.Services;
using Task = System.Threading.Tasks.Task;

namespace PenguinExtention.UI
{
    /// <summary>Penguin Packs window (Alt+U, Shift+P): check packs on/off for this solution, import packs and libraries.</summary>
    [Guid("5E1A7C3B-9D24-4F6E-8B0A-2C7D4E9F1A63")]
    public sealed class PacksWindow : ToolWindowPane
    {
        public PacksWindow() : base(null)
        {
            Caption = "🐧 Penguin Packs";
            Content = new PacksControl();
        }
    }

    /// <summary>
    /// Code-built WPF. One row per pack: check mark (this solution), name, id/version/location/state, description, errors,
    /// and Auto / Folder / Remove buttons. A check mark change is saved to .vs\PenguinExtension\packs.json and re-indexes.
    /// </summary>
    internal sealed class PacksControl : UserControl
    {
        private readonly StackPanel _list = new StackPanel { Margin = new Thickness(10, 0, 10, 10) };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(10, 4, 10, 6), TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        private readonly TextBlock _title = new TextBlock { FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap };
        private readonly ComboBox _target = new ComboBox { Margin = new Thickness(0, 0, 6, 4), MinWidth = 180, VerticalAlignment = VerticalAlignment.Center };

        public PacksControl()
        {
            PenguinTheme.Apply(this);
            SetResourceReference(BackgroundProperty, "P.Bg");
            SetResourceReference(ForegroundProperty, "P.Text");

            _target.Items.Add(new ComboBoxItem { Content = "Import for me (all solutions)", Tag = "user" });
            _target.Items.Add(new ComboBoxItem { Content = "Import into this solution (.penguin\\packs)", Tag = "solution" });
            _target.SelectedIndex = 0;

            var header = new StackPanel { Margin = new Thickness(10, 10, 10, 0) };
            header.Children.Add(_title);
            header.Children.Add(new TextBlock
            {
                Text = "A check mark turns a pack on or off for this solution. Packs without a choice follow detection (Unity, Unreal, SDL3… are found automatically).",
                TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 6)
            });
            var tools = new WrapPanel();
            tools.Children.Add(_target);
            tools.Children.Add(Button("Import pack folder…", () => Import(ImportKind.Folder), "A folder with pack.json"));
            tools.Children.Add(Button("Import pack .zip…", () => Import(ImportKind.Zip), "A .zip with pack.json at the root or in one folder"));
            tools.Children.Add(Button("Import library folder…", () => Import(ImportKind.Library),
                "Any library folder (headers, include/, lib/): copied into a new pack whose headers get indexed"));
            tools.Children.Add(Button("Open packs folder", OpenTargetFolder, "Open the folder chosen on the left"));
            tools.Children.Add(Button("Reload", () => { SetStatus("Reloading packs…"); PackService.Reload(); }, "Re-read every pack folder"));
            header.Children.Add(tools);

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(_status, Dock.Bottom);
            root.Children.Add(header);
            root.Children.Add(_status);
            root.Children.Add(new ScrollViewer { Content = _list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;

            void OnLoaded() => Dispatcher.BeginInvoke(new Action(() => { Refresh(); SetStatus("Packs reloaded."); }));
            Loaded += (s, e) => { PackService.Loaded -= OnLoaded; PackService.Loaded += OnLoaded; Refresh(); };
            Unloaded += (s, e) => PackService.Loaded -= OnLoaded;
            Refresh();
        }

        private enum ImportKind { Folder, Zip, Library }

        private void Refresh()
        {
            var solution = PackService.SolutionDirectory;
            _title.Text = solution == null ? "No solution open — check marks need a solution." : "Packs for " + solution;
            ((ComboBoxItem)_target.Items[1]).IsEnabled = solution != null;
            if (solution == null && _target.SelectedIndex == 1) _target.SelectedIndex = 0;
            _list.Children.Clear();
            if (!PackService.IsLoaded)
            {
                _list.Children.Add(new TextBlock { Text = "Packs load when a solution opens.", Opacity = 0.75 });
                return;
            }
            foreach (var p in PackService.Packs) _list.Children.Add(Row(p, solution != null));
        }

        private UIElement Row(PackInfo p, bool hasSolution)
        {
            var name = p.Manifest?.Name ?? p.Id;
            var check = new CheckBox
            {
                Content = new TextBlock { Text = name, FontWeight = FontWeights.SemiBold },
                IsChecked = p.Enabled,
                IsEnabled = hasSolution && p.Error == null,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = hasSolution ? "On/off for this solution" : "Open a solution to change check marks"
            };
            check.Click += (s, e) => SetChoice(p.Id, check.IsChecked == true);

            var state = p.Error != null ? "error" : p.Choice == null ? (p.Detected ? "auto: detected" : "auto: not detected") : "set by you";
            var meta = string.Join(" · ", new[] { p.Id, p.Manifest?.Version, p.Location.ToString().ToLowerInvariant(), state, Languages(p) }.Where(x => !string.IsNullOrEmpty(x)));
            var info = new StackPanel { Margin = new Thickness(22, 0, 0, 0) };
            info.Children.Add(new TextBlock { Text = meta, Opacity = 0.7 });
            if (!string.IsNullOrEmpty(p.Manifest?.Description))
                info.Children.Add(new TextBlock { Text = p.Manifest.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            if (p.ClaimedRoots.Count > 0)
                info.Children.Add(new TextBlock { Text = "Include folders: " + string.Join("; ", p.ClaimedRoots), TextWrapping = TextWrapping.Wrap, Opacity = 0.7 });
            if (p.Error != null)
                info.Children.Add(new TextBlock { Text = "❌ " + p.Error, Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap });

            var buttons = new WrapPanel { Margin = new Thickness(22, 4, 0, 0) };
            if (p.Choice != null && hasSolution)
                buttons.Children.Add(Button("Auto", () => SetChoice(p.Id, null), "Forget your choice and follow detection again"));
            buttons.Children.Add(Button("Folder", () => OpenFolder(p.Directory), p.Directory));
            if (p.Location != PackLocation.BuiltIn)
                buttons.Children.Add(Button("Remove", () => RemovePack(p), "Delete this pack folder"));

            var panel = new StackPanel();
            panel.Children.Add(check);
            panel.Children.Add(info);
            panel.Children.Add(buttons);
            var border = new Border { Child = panel, Padding = new Thickness(0, 8, 0, 8), BorderThickness = new Thickness(0, 0, 0, 1) };
            border.SetResourceReference(Border.BorderBrushProperty, "P.Border");
            return border;
        }

        private static string Languages(PackInfo p) =>
            p.Manifest?.Languages == null || p.Manifest.Languages.Length == 0 ? null : string.Join("/", p.Manifest.Languages);

        private void SetChoice(string id, bool? on)
        {
            SetStatus("Saving " + id + " → " + (on == null ? "auto" : on.Value ? "on" : "off") + " and reloading…");
            Run(() => PackService.SetEnabledAsync(id, on));
        }

        private string TargetDirectory()
        {
            var solution = PackService.SolutionDirectory;
            return (string)((ComboBoxItem)_target.SelectedItem).Tag == "solution" && solution != null
                ? PackService.SolutionPacksDirectory(solution) : PackService.UserDirectory;
        }

        private void Import(ImportKind kind)
        {
            string source;
            if (kind == ImportKind.Zip)
            {
                var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Pack zip (*.zip)|*.zip", Title = "Import Penguin pack" };
                if (dialog.ShowDialog() != true) return;
                source = dialog.FileName;
            }
            else
            {
                using (var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = kind == ImportKind.Library ? "Library folder to import (headers are indexed)" : "Pack folder (contains pack.json)",
                    ShowNewFolderButton = false
                })
                {
                    if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                    source = dialog.SelectedPath;
                }
            }
            var target = TargetDirectory();
            SetStatus("Importing " + source + "…");
            Run(async () =>
            {
                string id;
                Func<bool, string> work = overwrite =>
                    kind == ImportKind.Zip ? PackImporter.ImportZip(source, target, overwrite)
                    : kind == ImportKind.Library ? PackImporter.ImportLibrary(source, target, null, overwrite)
                    : PackImporter.ImportFolder(source, target, overwrite);
                try { id = await Task.Run(() => work(false)).ConfigureAwait(true); }
                catch (PackImporter.PackExistsException ex)
                {
                    if (MessageBox.Show(ex.Message + "\n\nReplace it?", "Penguin Packs", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    {
                        SetStatus("Import cancelled.");
                        return;
                    }
                    id = await Task.Run(() => work(true)).ConfigureAwait(true);
                }
                PenguinLog.Info("Packs", "imported " + id + " into " + target);
                if (PackService.SolutionDirectory != null &&
                    MessageBox.Show("Imported pack \"" + id + "\".\n\nTurn it on for this solution now?", "Penguin Packs", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                    await PackService.SetEnabledAsync(id, true).ConfigureAwait(true);
                else
                    PackService.Reload();
                SetStatus("Imported " + id + " into " + target);
            });
        }

        private void RemovePack(PackInfo p)
        {
            if (MessageBox.Show("Delete the pack folder?\n\n" + p.Directory, "Penguin Packs", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            SetStatus("Removing " + p.Id + "…");
            Run(async () =>
            {
                await Task.Run(() => PackImporter.Remove(p)).ConfigureAwait(true);
                PackService.Reload();
                SetStatus("Removed " + p.Id + ".");
            });
        }

        private void OpenTargetFolder()
        {
            var dir = TargetDirectory();
            Run(async () =>
            {
                await Task.Run(() => Directory.CreateDirectory(dir)).ConfigureAwait(true);
                OpenFolder(dir);
            });
        }

        private void OpenFolder(string dir)
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true }); }
            catch (Exception ex) { SetStatus("Cannot open " + dir + ": " + ex.Message); }
        }

        /// <summary>Runs UI-started async work; failures go to the status line and the Output pane.</summary>
        private void Run(Func<Task> work)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try { await work().ConfigureAwait(true); }
                catch (Exception ex)
                {
                    PenguinLog.Error("Packs", ex);
                    SetStatus("❌ " + ex.Message);
                }
            });
        }

        private void SetStatus(string text) => _status.Text = text;

        private static Button Button(string text, Action click, string tip)
        {
            var b = new Button { Content = text, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 2, 8, 2), ToolTip = tip };
            b.Click += (s, e) => click();
            return b;
        }
    }
}
