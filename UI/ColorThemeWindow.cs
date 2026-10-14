using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using PenguinExtention.Services;

namespace PenguinExtention.UI
{
    /// <summary>Penguin Color Theme window (Alt+U, Shift+T) — pick the color of every kind of C++/Unreal code.</summary>
    [Guid("7C2B9E14-3F6A-4D8B-A1E5-5B0C9D2F6E31")]
    public class ColorThemeWindow : ToolWindowPane
    {
        public ColorThemeWindow() : base(null)
        {
            Caption = "🐧 Penguin Color Theme";
            Content = new ColorThemeControl();
        }
    }

    /// <summary>
    /// Code-built WPF: on/off switch, dark/light set selector, live Unreal preview, one row per theme entry
    /// (swatch → color picker, hex, bold, italic, reset), Reset all / Import / Export. Every edit applies to open
    /// editors immediately and is saved to theme.json.
    /// </summary>
    internal sealed class ColorThemeControl : UserControl
    {
        // «entry¦text» marks a colored token; everything else is plain text.
        private const string Sample =
            "«comment¦// 🐧 Penguin Color Theme preview»\n" +
            "«preprocessor¦#include» «string¦\"GameFramework/Actor.h\"»\n" +
            "«preprocessor¦#define» «macro¦MAX_BIRDS» «number¦8»\n" +
            "«keyword¦using namespace» «namespace¦UE»::«namespace¦Math»;\n\n" +
            "«ueMacro¦UENUM»(«specifier¦BlueprintType»)\n" +
            "«keyword¦enum class» «enum¦EMood» : «keyword¦uint8» { «enumValue¦Happy» «ueMacro¦UMETA»(«specifier¦DisplayName»=«string¦\"Happy\"»), «enumValue¦Sleepy» };\n\n" +
            "«ueMacro¦DECLARE_DYNAMIC_MULTICAST_DELEGATE»(«delegate¦FOnLanded»);\n\n" +
            "«ueMacro¦UCLASS»()\n" +
            "«keyword¦class» «class¦ABird» : «keyword¦public» «class¦AActor»\n" +
            "{\n" +
            "    «ueMacro¦GENERATED_BODY»()\n" +
            "«keyword¦public»:\n" +
            "    «ueMacro¦UPROPERTY»(«specifier¦EditAnywhere», «specifier¦meta»=(«metaKey¦ClampMin»=«string¦\"0\"»))\n" +
            "    «keyword¦float» «property¦Speed» «operator¦=» «number¦600.f»;\n" +
            "    «struct¦FVector» «property¦Home»;\n" +
            "    «template¦TArray»<«class¦AActor»*> «property¦Friends»;\n" +
            "    «enum¦EMood» «property¦Mood» «operator¦=» «enum¦EMood»::«enumValue¦Happy»;\n" +
            "    «delegate¦FOnLanded» «property¦OnLanded»;\n" +
            "    «keyword¦void» «method¦Fly»(«keyword¦float» «parameter¦Height»);\n" +
            "};\n\n" +
            "«keyword¦void» «class¦ABird»::«method¦Fly»(«keyword¦float» «parameter¦Height»)\n" +
            "{\n" +
            "    «keyword¦const float» «local¦Limit» «operator¦=» «property¦Speed» «operator¦*» «macro¦MAX_BIRDS»;\n" +
            "    «control¦if» («parameter¦Height» «operator¦>» «local¦Limit» «operator¦||» «operator¦!»«function¦IsValid»(«global¦GEngine»))\n" +
            "    {\n" +
            "        «ueMacro¦UE_LOG»(LogTemp, Warning, «ueMacro¦TEXT»(«string¦\"Too high»«escape¦\\n»«string¦\"»));\n" +
            "        «control¦goto» «label¦Land»;\n" +
            "    }\n" +
            "    «property¦Home» «operator¦=» «method¦GetActorLocation»();\n" +
            "«label¦Land»:\n" +
            "    «property¦OnLanded».«method¦Broadcast»();\n" +
            "}";

        private static readonly Color DarkBackground = Color.FromRgb(0x1E, 0x1E, 0x1E);
        private static readonly Color DarkText = Color.FromRgb(0xDC, 0xDC, 0xDC);
        private static readonly Color LightBackground = Colors.White;
        private static readonly Color LightText = Colors.Black;

        private readonly CheckBox _enabled = new CheckBox { Content = "Penguin color mode — color every C++ file with this theme", Margin = new Thickness(0, 0, 0, 6) };
        private readonly RadioButton _editDark = new RadioButton { Content = "Dark colors", GroupName = "PenguinThemeSet", Margin = new Thickness(0, 0, 12, 0) };
        private readonly RadioButton _editLight = new RadioButton { Content = "Light colors", GroupName = "PenguinThemeSet" };
        private readonly TextBlock _status = new TextBlock { Margin = new Thickness(0, 4, 0, 0), Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _preview = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 12, Padding = new Thickness(10) };
        private readonly Border _previewBorder = new Border { BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 8) };
        private readonly Dictionary<string, Row> _rows = new Dictionary<string, Row>();
        private readonly List<KeyValuePair<Run, ThemeEntry>> _previewRuns = new List<KeyValuePair<Run, ThemeEntry>>();
        private ThemeSettings _working;
        private bool _syncing;

        private sealed class Row
        {
            public Border Swatch;
            public TextBox Hex;
            public CheckBox Bold, Italic;
            public Button Reset;
            public TextBlock Name;
        }

        public ColorThemeControl()
        {
            PenguinTheme.Apply(this);
            SetResourceReference(BackgroundProperty, "P.Bg");
            SetResourceReference(ForegroundProperty, "P.Text");
            _previewBorder.SetResourceReference(Border.BorderBrushProperty, "P.Border");

            _working = ThemeService.Settings.Clone();
            (ThemeService.IsDarkEditor ? _editDark : _editLight).IsChecked = true;

            var header = new StackPanel { Margin = new Thickness(10, 10, 10, 0) };
            header.Children.Add(_enabled);
            var sets = new StackPanel { Orientation = Orientation.Horizontal };
            sets.Children.Add(new TextBlock { Text = "Editing: ", Margin = new Thickness(0, 0, 6, 0) });
            sets.Children.Add(_editDark);
            sets.Children.Add(_editLight);
            header.Children.Add(sets);
            header.Children.Add(_status);
            _previewBorder.Child = new ScrollViewer
            {
                Content = _preview,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 330,
            };
            header.Children.Add(_previewBorder);

            var buttons = new WrapPanel { Margin = new Thickness(10, 6, 10, 10) };
            buttons.Children.Add(MakeButton("Reset all to Penguin defaults", ResetAll));
            buttons.Children.Add(MakeButton("Import…", Import));
            buttons.Children.Add(MakeButton("Export…", Export));

            var list = new StackPanel { Margin = new Thickness(10, 0, 10, 0) };
            foreach (var group in ThemeCatalog.Groups)
            {
                list.Children.Add(new TextBlock { Text = group, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 4) });
                foreach (var entry in ThemeCatalog.Entries.Where(e => e.Group == group))
                    list.Children.Add(BuildRow(entry));
            }

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            root.Children.Add(header);
            root.Children.Add(buttons);
            root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            Content = root;

            BuildPreview();
            _enabled.Checked += (s, e) => Edit(w => w.Enabled = true);
            _enabled.Unchecked += (s, e) => Edit(w => w.Enabled = false);
            _editDark.Checked += (s, e) => Refresh();
            _editLight.Checked += (s, e) => Refresh();
            void OnApplied() => Dispatcher.BeginInvoke(new Action(() => { _working = ThemeService.Settings.Clone(); Refresh(); }));
            Loaded += (s, e) => { ThemeService.Applied -= OnApplied; ThemeService.Applied += OnApplied; Refresh(); };
            Unloaded += (s, e) => ThemeService.Applied -= OnApplied;
            Refresh();
        }

        private bool EditingDark => _editDark.IsChecked == true;

        private UIElement BuildRow(ThemeEntry entry)
        {
            var row = new Row
            {
                Swatch = new Border { Width = 22, Height = 16, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Cursor = Cursors.Hand, ToolTip = "Pick a color" },
                Name = new TextBlock { Text = entry.DisplayName, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis },
                Hex = new TextBox { Width = 68, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center, ToolTip = "#RRGGBB" },
                Bold = new CheckBox { Content = "B", FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), ToolTip = "Bold" },
                Italic = new CheckBox { Content = "I", FontStyle = FontStyles.Italic, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0), ToolTip = "Italic" },
                Reset = MakeButton("↺", () => Edit(w => w.Reset(entry))),
            };
            row.Swatch.BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, 0x80, 0x80, 0x80));
            row.Reset.ToolTip = "Reset to the Penguin default";
            row.Reset.Margin = new Thickness(6, 0, 0, 0);
            row.Reset.Padding = new Thickness(4, 0, 4, 0);
            row.Swatch.MouseLeftButtonUp += (s, e) => PickColor(entry);
            row.Hex.LostFocus += (s, e) => CommitHex(entry, row.Hex);
            row.Hex.KeyDown += (s, e) => { if (e.Key == Key.Enter) CommitHex(entry, row.Hex); };
            row.Bold.Click += (s, e) => Edit(w => w.Set(entry, EditingDark, bold: row.Bold.IsChecked == true));
            row.Italic.Click += (s, e) => Edit(w => w.Set(entry, EditingDark, italic: row.Italic.IsChecked == true));
            _rows[entry.Id] = row;

            var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 80 });
            for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var cells = new UIElement[] { row.Swatch, row.Name, row.Hex, row.Bold, row.Italic, row.Reset };
            for (int i = 0; i < cells.Length; i++)
            {
                Grid.SetColumn(cells[i], i);
                grid.Children.Add(cells[i]);
            }
            grid.ToolTip = "Colors: " + string.Join(", ", entry.Classifications);
            return grid;
        }

        private void BuildPreview()
        {
            int i = 0;
            while (i < Sample.Length)
            {
                int open = Sample.IndexOf('«', i);
                if (open < 0) { _preview.Inlines.Add(new Run(Sample.Substring(i))); break; }
                if (open > i) _preview.Inlines.Add(new Run(Sample.Substring(i, open - i)));
                int bar = Sample.IndexOf('¦', open);
                int close = Sample.IndexOf('»', bar);
                var run = new Run(Sample.Substring(bar + 1, close - bar - 1));
                _previewRuns.Add(new KeyValuePair<Run, ThemeEntry>(run, ThemeCatalog.Find(Sample.Substring(open + 1, bar - open - 1))));
                _preview.Inlines.Add(run);
                i = close + 1;
            }
        }

        /// <summary>Applies one change to the working copy, pushes it to every editor and saves theme.json.</summary>
        private void Edit(Action<ThemeSettings> change)
        {
            if (_syncing) return;
            try
            {
                change(_working);
                ThemeService.Update(_working.Clone());
            }
            catch (Exception ex)
            {
                PenguinLog.Error("Theme", ex);
            }
            Refresh();
        }

        private void Refresh()
        {
            _syncing = true;
            try
            {
                bool dark = EditingDark;
                _enabled.IsChecked = _working.Enabled;
                foreach (var entry in ThemeCatalog.Entries)
                {
                    var row = _rows[entry.Id];
                    var style = _working.Resolve(entry, dark);
                    row.Swatch.Background = new SolidColorBrush(style.Color);
                    if (!row.Hex.IsKeyboardFocused) row.Hex.Text = ThemeColors.ToHex(style.Color);
                    row.Bold.IsChecked = style.Bold;
                    row.Italic.IsChecked = style.Italic;
                    row.Reset.IsEnabled = _working.IsCustomized(entry);
                    row.Name.FontWeight = _working.IsCustomized(entry) ? FontWeights.SemiBold : FontWeights.Normal;
                }
                _preview.Background = new SolidColorBrush(dark ? DarkBackground : LightBackground);
                _preview.Foreground = new SolidColorBrush(dark ? DarkText : LightText);
                foreach (var pair in _previewRuns)
                {
                    var style = _working.Resolve(pair.Value, dark);
                    pair.Key.Foreground = new SolidColorBrush(style.Color);
                    pair.Key.FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal;
                    pair.Key.FontStyle = style.Italic ? FontStyles.Italic : FontStyles.Normal;
                }
                var customized = ThemeCatalog.Entries.Count(_working.IsCustomized);
                _status.Text = !_working.Enabled
                    ? "Color mode is off — Visual Studio's own colors are used."
                    : "Visual Studio is using the " + (ThemeService.IsDarkEditor ? "dark" : "light") + " set. " +
                      (customized == 0 ? "All entries use Penguin defaults." : customized + " entr" + (customized == 1 ? "y" : "ies") + " customized (bold names).") +
                      (ThemeService.Missing.Count == 0 ? "" : " Some VS C++ colors apply once a C++ file is open.");
            }
            finally
            {
                _syncing = false;
            }
        }

        private void PickColor(ThemeEntry entry)
        {
            bool dark = EditingDark;
            var current = _working.Resolve(entry, dark).Color;
            using (var dialog = new System.Windows.Forms.ColorDialog { FullOpen = true, AnyColor = true })
            {
                dialog.Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B);
                // The theme's colors as custom colors (COLORREF = 0x00BBGGRR), so they are one click away.
                dialog.CustomColors = ThemeCatalog.Entries.Select(e => dark ? e.Dark : e.Light).Distinct().Take(16)
                    .Select(c => c.R | c.G << 8 | c.B << 16).ToArray();
                if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                var picked = Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B);
                Edit(w => w.Set(entry, dark, color: picked));
            }
        }

        private void CommitHex(ThemeEntry entry, TextBox box)
        {
            if (_syncing) return;
            bool dark = EditingDark;
            if (ThemeColors.TryParse(box.Text, out var color))
            {
                if (color != _working.Resolve(entry, dark).Color) Edit(w => w.Set(entry, dark, color: color));
            }
            else
            {
                box.Text = ThemeColors.ToHex(_working.Resolve(entry, dark).Color);
            }
        }

        private void ResetAll()
        {
            var answer = MessageBox.Show("Reset every color to the Penguin defaults?", "Penguin Color Theme",
                MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.OK) Edit(w => w.Overrides.Clear());
        }

        private void Import()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Penguin theme (*.json)|*.json", Title = "Import Penguin Color Theme" };
            if (dialog.ShowDialog() != true) return;
            try
            {
                var imported = ThemeSettings.Load(dialog.FileName);
                imported.Enabled = _working.Enabled;
                _working = imported;
                ThemeService.Update(_working.Clone());
                PenguinLog.Info("Theme", "Imported " + dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not import the theme:\n" + ex.Message, "Penguin Color Theme", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Refresh();
        }

        private void Export()
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Penguin theme (*.json)|*.json", FileName = "PenguinTheme.json", Title = "Export Penguin Color Theme" };
            if (dialog.ShowDialog() != true) return;
            try
            {
                _working.Save(dialog.FileName);
                PenguinLog.Info("Theme", "Exported " + dialog.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Could not export the theme:\n" + ex.Message, "Penguin Color Theme", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static Button MakeButton(string text, Action onClick)
        {
            var button = new Button { Content = text, Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(0, 0, 6, 0) };
            button.Click += (s, e) => onClick();
            return button;
        }
    }
}
