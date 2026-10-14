using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;
using PenguinExtention.Completion;
using PenguinExtention.Core;
using PenguinExtention.Editor;
using PenguinExtention.QuickInfo;
using PenguinExtention.Services;

namespace PenguinExtention.Commands
{
    /// <summary>
    /// "Penguin Self-Test" (Alt+U, U): writes a pass/fail checklist of every moving part to the Output pane —
    /// project detection, index contents, lookups, which editor hooks VS has called, and key bindings.
    /// </summary>
    internal static class SelfTestCommand
    {
        public static async Task RunAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var report = new StringBuilder();
            void Line(bool? ok, string text) =>
                report.AppendLine((ok == null ? "   ℹ " : ok.Value ? "   ✅ " : "   ❌ ") + text);

            report.AppendLine();
            report.AppendLine("════════ 🐧 PenguinExtension Self-Test ════════");
            Line(null, "Extension " + PenguinExtensionPackage.Version + " in Visual Studio " + PenguinExtensionPackage.VsVersion());

            // ── Project ──
            var detector = UnrealProjectDetector.Instance;
            Line(detector?.IsSupportedProject == true, "Project: " + (detector == null ? "(detection has not run — open a solution)"
                : detector.IsUnrealProject ? "Unreal — " + detector.UProjectFilePath
                : detector.IsPlainCppProject ? "plain C/C++ — " + detector.ProjectRoot : "none found"));
            if (detector?.IsUnrealProject == true)
            {
                Line(!string.IsNullOrEmpty(detector.ProjectSourceRoot), "Project source: " + (detector.ProjectSourceRoot ?? "(missing)"));
                Line(!string.IsNullOrEmpty(detector.EngineSourceRoot), "Engine source: " + (detector.EngineSourceRoot ??
                    "(not found — set Tools → Options → PenguinExtension → Engine Root Override)"));
            }

            // ── Backend / index ──
            Line(BackendService.IsReady, "Backend: " + BackendService.Status);
            Line(BackendService.IsIndexComplete, BackendService.IsIndexComplete ? "Indexing finished" : "Indexing still running (status bar shows progress) — results may be partial");
            var cache = CacheService.Instance;
            await TaskScheduler.Default;
            if (cache != null && cache.IsLoaded)
            {
                long files = -1, occurrences = -1;
                try
                {
                    files = await cache.Database.GetIndexedFileCountAsync().ConfigureAwait(false);
                    occurrences = await cache.Database.GetOccurrenceCountAsync().ConfigureAwait(false);
                }
                catch (Exception ex) { PenguinLog.Error("SelfTest", ex.Message); }
                Line(cache.SymbolCount > 0, "Index: " + cache.SymbolCount + " symbols, " + files + " files, " + occurrences + " reference occurrences");

                var watch = Stopwatch.StartNew();
                var actor = await BackendService.ExactAsync("AActor", default).ConfigureAwait(false);
                Line(actor.Count > 0, "Exact lookup AActor → " + (actor.Count == 0 ? "not found (is engine indexing on?)"
                    : actor[0].FilePath + ":" + actor[0].LineNumber) + " (" + watch.ElapsedMilliseconds + " ms)");

                watch.Restart();
                var search = cache.Search("UCharacterMov", null, 5);
                Line(search.Any(s => s.Name == "UCharacterMovementComponent"),
                    "Completion query \"UCharacterMov\" → " + (search.Count == 0 ? "nothing" : string.Join(", ", search.Take(3).Select(s => s.Name))) +
                    " (" + watch.ElapsedMilliseconds + " ms)");

                var members = cache.GetMembers("AActor");
                Line(members.Count > 0, "Members of AActor (for AActor:: completion) → " + members.Count);
            }
            else
            {
                Line(false, "Index not loaded" + (cache == null ? " (no project detected yet)" : " yet"));
            }

            // ── Syntax coloring: name categories + specifier scanner on a fixed sample ──
            if (cache != null && cache.IsLoaded)
            {
                var categories = cache.GetNameCategories(out _);
                var counts = categories.Values.GroupBy(c => c).OrderBy(g => g.Key)
                    .Select(g => CategoryName(g.Key) + " " + g.Count());
                Line(categories.Count > 0, "Coloring names: " + string.Join(", ", counts));
            }
            Line(ThemeService.IsReady && ThemeService.LoadError == null, !ThemeService.IsReady
                ? "Color theme: not initialized"
                : "Color theme: " + (ThemeService.Settings.Enabled
                    ? "on, Penguin " + (ThemeService.IsDarkEditor ? "Dark" : "Light") + ", " + ThemeService.AppliedCount + " classifications colored, " +
                      ThemeService.Settings.Overrides.Count + " customized entries"
                    : "off (Visual Studio colors)") +
                  (ThemeService.LoadError == null ? "" : " — theme.json error: " + ThemeService.LoadError) +
                  " — Alt+U, Shift+T opens the theme window");
            if (ThemeService.Missing.Count > 0)
                Line(null, "Not registered (yet) — open a C++ file, then re-run: " + string.Join(", ", ThemeService.Missing));
            var colored = new List<string>();
            var macroState = default(UnrealClassifier.MacroState);
            const string sample = "UPROPERTY(EditAnywhere, Category=\"Stats\", meta=(ClampMin=\"0\")) float Health; // Hidden";
            UnrealClassifier.Scan(sample, ref macroState, null, (index, length, kind) =>
                colored.Add(kind + ":" + sample.Substring(index, length)));
            var coloredText = string.Join(" ", colored);
            Line(coloredText == "Macro:UPROPERTY Specifier:EditAnywhere Specifier:Category Specifier:meta MetaKey:ClampMin",
                "Specifier coloring → " + coloredText);

            // ── Editor hooks: which pipelines has VS actually called? ──
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            report.AppendLine("   Editor hooks (type in a .h/.cpp file first, then run Self-Test again):");
            ProbeLine(report, PenguinKeyFilter.ProbeList);
            ProbeLine(report, PenguinKeyFilter.ProbeParams);
            ProbeLine(report, "Auto #include");
            ProbeLine(report, UnrealCompletionSource.ProbeAsync);
            ProbeLine(report, UnrealLegacyCompletionSource.ProbeLegacy);
            ProbeLine(report, UnrealQuickInfoSource.Probe);
            ProbeLine(report, UnrealSignatureHelpController.ProbeLookup);
            ProbeLine(report, UnrealSignatureHelpController.ProbeSource);
            if (FeatureProbe.Seen(UnrealCompletionSource.ProbeAsync) || FeatureProbe.Seen(UnrealLegacyCompletionSource.ProbeLegacy))
                Line(true, "Completion is wired into this Visual Studio's " +
                    (FeatureProbe.Seen(UnrealCompletionSource.ProbeAsync) ? "async (modern)" : "legacy") + " pipeline");
            else
                Line(null, "No completion call yet — type a few letters in a C++ file, then rerun");

            Line(null, PremiumHooks.IsInstalled ? "Penguin Premium: installed" : "Penguin Premium: not installed (free edition)");

            // ── Key bindings ──
            foreach (var line in KeyBindings()) report.AppendLine(line);

            report.AppendLine("════════ end of Self-Test (copy this block when reporting a problem) ════════");
            PenguinLog.Write(report.ToString());
            PenguinLog.Show();
            PenguinEditor.Status("Self-Test written to Output → PenguinExtension");
        }

        private static void ProbeLine(StringBuilder report, string feature) =>
            report.AppendLine((FeatureProbe.Seen(feature) ? "   ✅ " : "   ▫ ") + feature + ": " + FeatureProbe.Describe(feature));

        private static string CategoryName(byte category)
        {
            switch (category)
            {
                case CacheService.NameCategory.Class: return "classes";
                case CacheService.NameCategory.Struct: return "structs";
                case CacheService.NameCategory.Enum: return "enums";
                case CacheService.NameCategory.Delegate: return "delegates";
                case CacheService.NameCategory.Macro: return "macros";
                case CacheService.NameCategory.Function: return "functions";
                case CacheService.NameCategory.EnumValue: return "enum values";
                case CacheService.NameCategory.Property: return "project properties";
                case CacheService.NameCategory.EngineProperty: return "engine properties";
                default: return "category " + category;
            }
        }

        /// <summary>Key bindings VS actually registered for our commands (proves the .vsct shortcuts loaded).</summary>
        private static IEnumerable<string> KeyBindings()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var lines = new List<string>();
            try
            {
                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE.DTE;
                if (dte == null) { lines.Add("   ▫ Key bindings: DTE unavailable"); return lines; }
                var guid = PenguinExtensionCommandIds.CommandSetGuid.ToString("B").ToUpperInvariant();
                var wanted = new Dictionary<int, string>
                {
                    { PenguinExtensionCommandIds.GoToUnrealDefinitionId, "Go To Definition" },
                    { PenguinExtensionCommandIds.OpenUnrealExplorerId, "Unreal Explorer" },
                    { PenguinExtensionCommandIds.GenerateImplementationId, "Generate Implementation" },
                    { PenguinExtensionCommandIds.OpenColorThemeId, "Penguin Color Theme" },
                };
                foreach (EnvDTE.Command command in dte.Commands)
                {
                    if (!string.Equals(command.Guid, guid, StringComparison.OrdinalIgnoreCase) || !wanted.TryGetValue(command.ID, out var label)) continue;
                    var bindings = (command.Bindings as object[] ?? Array.Empty<object>()).Select(b => b.ToString()).ToList();
                    lines.Add((bindings.Count > 0 ? "   ✅ " : "   ❌ ") + label + ": " + (bindings.Count > 0 ? string.Join(" | ", bindings) : "no key binding"));
                }
                if (lines.Count == 0) lines.Add("   ❌ Key bindings: our commands are not registered with Visual Studio");
            }
            catch (Exception ex) { lines.Add("   ▫ Key bindings: " + ex.Message); }
            return lines;
        }
    }
}
