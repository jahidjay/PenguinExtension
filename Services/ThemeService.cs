using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Formatting;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Applies the Penguin Color Theme to every C/C++ editor by writing colors into the editor's "text"
    /// classification format map at runtime. Nothing is written to Fonts and Colors: the values VS had before the
    /// first apply are captured and put back when the theme is turned off. All map access is on the UI thread.
    /// </summary>
    internal static class ThemeService
    {
        private static IClassificationFormatMap _map;
        private static IClassificationTypeRegistryService _registry;
        private static readonly Dictionary<string, TextFormattingRunProperties> Original =
            new Dictionary<string, TextFormattingRunProperties>(StringComparer.Ordinal);
        private static ThemeSettings _settings = new ThemeSettings();

        /// <summary>Raised on the UI thread after the theme was applied (settings or VS theme changed).</summary>
        public static event Action Applied;

        public static ThemeSettings Settings => _settings;
        public static bool IsReady => _map != null;
        /// <summary>Whether the editor background is dark (decides Penguin Dark vs Penguin Light colors).</summary>
        public static bool IsDarkEditor { get; private set; } = true;
        public static int AppliedCount { get; private set; }
        /// <summary>Classification names not registered (yet) — VC++ registers its cpp* types when C++ loads.</summary>
        public static IReadOnlyList<string> Missing { get; private set; } = new string[0];
        public static string LoadError { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            var path = ThemeSettings.DefaultPath;
            var loaded = await Task.Run(() =>
            {
                try { return ThemeSettings.Load(path); }
                catch (Exception ex)
                {
                    LoadError = ex.Message;
                    PenguinLog.Error("Theme", "Could not read " + path + " — using Penguin defaults: " + ex.Message);
                    return new ThemeSettings();
                }
            }).ConfigureAwait(false);

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            var componentModel = await package.GetServiceAsync(typeof(SComponentModel)) as IComponentModel;
            if (componentModel == null)
            {
                PenguinLog.Error("Theme", "SComponentModel unavailable — color theme not applied");
                return;
            }
            _registry = componentModel.GetService<IClassificationTypeRegistryService>();
            _map = componentModel.GetService<IClassificationFormatMapService>().GetClassificationFormatMap("text");
            _settings = loaded;
            VSColorTheme.ThemeChanged += _ => QueueReapply();
            // VS pushes its Fonts and Colors values into the map when the first editor opens (and whenever they
            // change), overwriting ours — put the theme back whenever someone else changes the map.
            _map.ClassificationFormatMappingChanged += (s, e) => { if (!_applying) QueueReapply(); };
            Apply();
        }

        private static bool _applying, _reapplyQueued;
        private static int _reapplyCount;
        private static DateTime _reapplyWindowStart = DateTime.UtcNow;
        /// <summary>Foreground we last wrote per classification, to tell when VS replaced it.</summary>
        private static readonly Dictionary<string, Color> LastSet = new Dictionary<string, Color>(StringComparer.Ordinal);

        private static void QueueReapply()
        {
            if (_reapplyQueued) return;
            _reapplyQueued = true;
            ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await Task.Delay(150).ConfigureAwait(true); // let VS finish its own batch
                _reapplyQueued = false;
                if (DateTime.UtcNow - _reapplyWindowStart > TimeSpan.FromMinutes(1)) { _reapplyWindowStart = DateTime.UtcNow; _reapplyCount = 0; }
                if (++_reapplyCount > 30)
                {
                    if (_reapplyCount == 31) PenguinLog.Error("Theme", "Another component keeps changing editor colors — stopped re-applying for a minute");
                    return;
                }
                Apply();
            }).FileAndForget("PenguinExtension/Theme/Reapply");
        }

        /// <summary>Replaces the settings (window edits, import), applies them and saves theme.json in the background.</summary>
        public static void Update(ThemeSettings settings)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            _settings = settings;
            Apply();
            var copy = settings.Clone();
            _ = Task.Run(() =>
            {
                try { copy.Save(ThemeSettings.DefaultPath); }
                catch (Exception ex) { PenguinLog.Error("Theme", "Could not save " + ThemeSettings.DefaultPath + ": " + ex.Message); }
            });
        }

        /// <summary>Called when a C/C++ buffer opens: VC++'s cpp* types may only exist from now on.</summary>
        public static void OnCppBufferOpened()
        {
            if (_map == null || Missing.Count == 0 || !_settings.Enabled) return;
            if (!Missing.Any(name => _registry.GetClassificationType(name) != null)) return;
            QueueReapply();
        }

        public static void Apply()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_map == null) return;
            IsDarkEditor = DetectDarkEditor();
            var missing = new List<string>();
            int applied = 0, replacedByVs = 0;
            _applying = true;
            _map.BeginBatchUpdate();
            try
            {
                if (!_settings.Enabled)
                {
                    RestoreOriginals();
                }
                else
                {
                    foreach (var entry in ThemeCatalog.Entries)
                    {
                        var style = _settings.Resolve(entry, IsDarkEditor);
                        foreach (var name in entry.Classifications)
                        {
                            var type = _registry.GetClassificationType(name);
                            if (type == null) { missing.Add(name); continue; }
                            var current = _map.GetExplicitTextProperties(type);
                            if (!Original.TryGetValue(name, out var original))
                                Original[name] = original = current;
                            else if (LastSet.TryGetValue(name, out var mine) && !HasForeground(current, mine))
                            {
                                Original[name] = original = current; // VS rewrote it: that is VS's value now
                                replacedByVs++;
                            }
                            _map.SetExplicitTextProperties(type, original
                                .SetForeground(style.Color)
                                .SetBold(style.Bold)
                                .SetItalic(style.Italic));
                            LastSet[name] = style.Color;
                            applied++;
                        }
                    }
                }
            }
            finally
            {
                _map.EndBatchUpdate();
                _applying = false;
            }
            AppliedCount = applied;
            Missing = missing;
            PenguinLog.Info("Theme", !_settings.Enabled
                ? "Penguin color theme off — Visual Studio colors restored"
                : "Penguin " + (IsDarkEditor ? "Dark" : "Light") + " applied to " + applied + " classifications" +
                  (replacedByVs == 0 ? "" : " (re-applied " + replacedByVs + " that Visual Studio had reset)") +
                  (missing.Count == 0 ? "" : " (" + missing.Count + " not registered yet: " + string.Join(", ", missing) + ")"));
            Applied?.Invoke();
        }

        private static void RestoreOriginals()
        {
            foreach (var pair in Original)
            {
                var type = _registry.GetClassificationType(pair.Key);
                if (type != null) _map.SetExplicitTextProperties(type, pair.Value);
            }
            Original.Clear();
            LastSet.Clear();
        }

        private static bool HasForeground(TextFormattingRunProperties props, Color color) =>
            !props.ForegroundBrushEmpty && props.ForegroundBrush is SolidColorBrush brush && brush.Color == color;

        private static bool DetectDarkEditor()
        {
            if (_map.DefaultTextProperties.BackgroundBrush is SolidColorBrush editor && !_map.DefaultTextProperties.BackgroundBrushEmpty)
                return ThemeColors.IsDark(editor.Color);
            var window = VSColorTheme.GetThemedColor(EnvironmentColors.ToolWindowBackgroundColorKey);
            return ThemeColors.IsDark(Color.FromRgb(window.R, window.G, window.B));
        }
    }
}
