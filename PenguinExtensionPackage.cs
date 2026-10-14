using System;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;
using PenguinExtention.Commands;
using PenguinExtention.Core;
using System.IO;
using PenguinExtention.Database;
using PenguinExtention.Services;
using PenguinExtention.UI;
using Task = System.Threading.Tasks.Task;

namespace PenguinExtention
{
    /// <summary>
    /// PenguinExtension main package.
    /// Orchestrates the startup sequence: detect UE project → load cache → start indexing → register commands.
    /// Uses <see cref="AsyncPackage"/> with background loading to never block the VS UI thread.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideBindingPath]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string,
                     PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideToolWindow(typeof(UnrealExplorerWindow),
                       Style = VsDockStyle.Linked,
                       Window = ToolWindowGuids.SolutionExplorer,
                       Orientation = ToolWindowOrientation.Left)]
    [ProvideToolWindow(typeof(SymbolInspectorWindow),
                       Style = VsDockStyle.Linked,
                       Window = ToolWindowGuids.SolutionExplorer,
                       Orientation = ToolWindowOrientation.Right)]
    [ProvideToolWindow(typeof(ShortcutsReferenceWindow),
                       Style = VsDockStyle.Float)]
    [ProvideToolWindow(typeof(ColorThemeWindow),
                       Style = VsDockStyle.Linked,
                       Window = ToolWindowGuids.SolutionExplorer,
                       Orientation = ToolWindowOrientation.Right)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(PenguinOptionsPage),
                       "PenguinExtension", "General", 0, 0, true)]
    [ProvideOptionPage(typeof(ColorThemeOptionsPage),
                       "PenguinExtension", "Color Theme", 0, 0, true)]
    public sealed class PenguinExtensionPackage : AsyncPackage
    {
        public const string PackageGuidString = "7D9B3C2E-8A4F-4B1D-9E5C-6F0A2D3B8C7E";

        /// <summary>Keep in step with Identity/@Version in source.extension.vsixmanifest.</summary>
        internal const string Version = "1.11";

        private SQLiteCache _db;
        private UnrealIndexer _indexer;
        private IncrementalIndexer _incrementalIndexer;
        private CancellationTokenSource _indexCts;
        private Task _indexTask;
        private CoreClientService _core;
        private CoreFileWatcher _coreWatcher;
        private CoreDiagnostics _diagnostics;
        private SolutionEvents _solutionEvents;
        private readonly SemaphoreSlim _lifecycle = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _workspaceCts;
        internal static PenguinExtensionPackage Instance { get; private set; }

        // ── Package initialization ──────────────────────────────────

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress).ConfigureAwait(false);

            // Register commands on the main thread
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Instance = this;
            await RegisterCommandsAsync().ConfigureAwait(true);
            await ThemeService.InitializeAsync(this).ConfigureAwait(true);
            // Visible proof the extension loaded: Output window (PenguinExtension pane) + status bar.
            WriteToOutputWindow("🐧 PenguinExtension " + Version + " loaded in Visual Studio " + VsVersion() +
                " — Alt+U, K lists shortcuts; Tools → Penguin Extension 🐧 → Self-Test checks every feature.");
            SetStatusBar("🐧 PenguinExtension " + Version + " loaded");

            _diagnostics = new CoreDiagnostics(this);
            var solution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
            _solutionEvents = new SolutionEvents(solution, () => RequestRestart());
            PenguinOptionsPage.SettingsApplied += OnSettingsApplied;
            BackendService.Changed += OnBackendChanged;
            await RestartBackendAsync(cancellationToken).ConfigureAwait(false);
        }

        private void OnSettingsApplied(object sender, EventArgs e) => RequestRestart();
        private void OnBackendChanged()
        {
            PenguinLog.Info("Backend", BackendService.Status);
            SetStatusBar("PenguinExtension: " + BackendService.Status);
        }
        internal void RequestRestart() => _ = BackendService.ObserveAsync(RestartBackendAsync(DisposalToken));

        private async Task RestartBackendAsync(CancellationToken token)
        {
            await JoinableTaskFactory.SwitchToMainThreadAsync(token);
            _workspaceCts?.Cancel();
            var requested = new CancellationTokenSource();
            _workspaceCts = requested;
            var options = (PenguinOptionsPage)GetDialogPage(typeof(PenguinOptionsPage));
            BackendService.Reset(options);
            var mode = options.Backend;
            var engineOverride = options.EngineRootOverride;
            var maxThreads = Math.Max(1, options.MaxIndexingThreads);
            var indexEngine = options.IndexEngineSource;
            var extraIncludes = options.ExtraIncludeDirectories;
            var config = new CoreConfiguration
            {
                Executable = string.IsNullOrWhiteSpace(options.CoreExecutableOverride)
                    ? Path.Combine(Path.GetDirectoryName(typeof(PenguinExtensionPackage).Assembly.Location), "Core", "penguin-lsp.exe")
                    : options.CoreExecutableOverride,
                AIEnabled = options.EnableLocalAI, AIEndpoint = options.AIEndpoint, AIModel = options.AIModel
            };
            _diagnostics?.Clear();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, requested.Token))
            {
                var ct = linked.Token;
                await TaskScheduler.Default;
                await _lifecycle.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    await StopBackendAsync().ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    UnrealProjectDetector.CreateInstance();
                    var detector = UnrealProjectDetector.Instance;
                    await detector.DetectAsync(this, engineOverride, extraIncludes).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (!detector.IsSupportedProject)
                    {
                        BackendService.Report("No C/C++ project found. Backend inactive.");
                        return;
                    }
                    if (detector.IsPlainCppProject && mode == PenguinBackend.Core)
                    {
                        BackendService.Report("Core backend is Unreal-only. Set Backend = Legacy for plain C/C++ (SDL2) projects.");
                        return;
                    }
                    if (detector.IsPlainCppProject)
                        WriteToOutputWindow("🐧 Plain C/C++ mode. Library include roots: " +
                            (detector.ExternalIncludeRoots.Count == 0 ? "(none found)" : string.Join("; ", detector.ExternalIncludeRoots)));
                    config.ProjectRoot = detector.ProjectRoot;
                    config.EngineRoot = indexEngine ? detector.EngineSourceRoot : null;
                    if (mode == PenguinBackend.Core)
                    {
                        var core = new CoreClientService();
                        _core = core;
                        core.Message += WriteToOutputWindow;
                        core.Failed += () =>
                        {
                            if (BackendService.Core != core) return;
                            BackendService.Report("Core failed. Use Restart Backend; no Legacy fallback.");
                            _ = JoinableTaskFactory.RunAsync(async () =>
                            {
                                await JoinableTaskFactory.SwitchToMainThreadAsync();
                                _diagnostics?.Clear();
                            });
                        };
                        core.Diagnostics += data => _diagnostics?.Publish(core, data);
                        await core.StartAsync(config, ct).ConfigureAwait(false);
                        await JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                        ct.ThrowIfCancellationRequested();
                        BackendService.Activate(core, config.EngineRoot);
                        DocumentSync.Replay();
                        await TaskScheduler.Default;
                        ct.ThrowIfCancellationRequested();
                        _coreWatcher = new CoreFileWatcher(core, detector.ProjectSourceRoot);
                        return; // No legacy DB/cache/indexer/watchers in Core mode.
                    }
                    _db = new SQLiteCache(detector.SolutionDirectory);
                    CacheService.Initialize(_db);
                    var cache = CacheService.Instance;
                    await new StartupCacheLoader(_db, cache).LoadAsync().ConfigureAwait(false);
                    await JoinableTaskFactory.SwitchToMainThreadAsync(ct);
                    ct.ThrowIfCancellationRequested();
                    BackendService.Activate(cache);
                    await TaskScheduler.Default;
                    ct.ThrowIfCancellationRequested();
                    _indexCts = CancellationTokenSource.CreateLinkedTokenSource(requested.Token, token);
                    _indexer = new UnrealIndexer(_db, cache, maxThreads)
                    {
                        IndexExternalAsCpp = detector.IsPlainCppProject,
                        ExternalRoots = detector.IsPlainCppProject && indexEngine ? detector.ExternalIncludeRoots : null
                    };
                    var indexer = _indexer;
                    var indexToken = _indexCts.Token;
                    PenguinLog.Info("Startup", "project: " + (detector.UProjectFilePath ?? detector.ProjectRoot) +
                        " | engine source: " + (detector.EngineSourceRoot ?? "(not found)") +
                        " | cached symbols: " + cache.SymbolCount + " | index engine: " + indexEngine);
                    _indexTask = Task.Run(async () =>
                    {
                        var watch = Stopwatch.StartNew();
                        try
                        {
                            var progress = new IndexStatusProgress(this);
                            await indexer.IndexAsync(detector.ProjectSourceRoot, detector.EngineSourceRoot,
                                indexEngine, progress, indexToken).ConfigureAwait(false);
                            progress.Finish();
                            PenguinLog.Info("Index", "symbols done: " + progress.Summary + " in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s");
                            await indexer.ScanOccurrencesAsync(detector.ProjectSourceRoot, indexToken).ConfigureAwait(false);
                            if (!indexToken.IsCancellationRequested)
                            {
                                BackendService.IndexComplete();
                                var done = "🐧 indexing complete — " + cache.SymbolCount + " symbols" + (detector.IsPlainCppProject ? " (C/C++ mode)" : " (Unreal mode)");
                                PenguinLog.Info("Index", done + " (" + watch.Elapsed.TotalSeconds.ToString("0.0") + " s total)");
                                BackendService.Report(done);
                            }
                        }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { PenguinLog.Error("Index", ex); BackendService.Report("Indexing failed: " + ex.Message + " (see Output → PenguinExtension)"); }
                    });
                    _incrementalIndexer = new IncrementalIndexer(indexer, cache);
                    _incrementalIndexer.Start(detector.ProjectSourceRoot);
                }
                catch (OperationCanceledException) { await StopBackendAsync().ConfigureAwait(false); }
                catch (Exception ex)
                {
                    await StopBackendAsync().ConfigureAwait(false);
                    WriteToOutputWindow(ex.ToString());
                    BackendService.Report(mode + " failed: " + ex.Message + " No automatic fallback. Use Restart Backend.");
                }
                finally { _lifecycle.Release(); }
            }
        }

        private async Task StopBackendAsync()
        {
            _coreWatcher?.Dispose(); _coreWatcher = null;
            if (_core != null) { await _core.StopAsync().ConfigureAwait(false); _core = null; }
            _indexCts?.Cancel();
            if (_incrementalIndexer != null)
            {
                await _incrementalIndexer.StopAsync().ConfigureAwait(false);
                _incrementalIndexer = null;
            }
            if (_indexTask != null)
            {
                try { await _indexTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PenguinLog.Error("Index", ex.Message); }
                _indexTask = null;
            }
            _indexCts?.Dispose(); _indexCts = null;
            _db?.Dispose(); _db = null;
        }

        private async Task RegisterCommandsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null) return;
            CoreCommands.Register(this, commandService);
            commandService.AddCommand(new MenuCommand((s, e) => PenguinEditor.Run(SelfTestCommand.RunAsync),
                new CommandID(PenguinExtensionCommandIds.CommandSetGuid, PenguinExtensionCommandIds.SelfTestId)));

            // Go To Unreal Definition
            await GoToUnrealDefinitionCommand.InitializeAsync(this).ConfigureAwait(true);

            // Generate Implementation
            await GenerateImplementationCommand.InitializeAsync(this).ConfigureAwait(true);

            // Generate Getter/Setter
            await GenerateGetterSetterCommand.InitializeAsync(this).ConfigureAwait(true);

            // Open Unreal Explorer
            RegisterToolWindowCommand(commandService,
                PenguinExtensionCommandIds.OpenUnrealExplorerId, typeof(UnrealExplorerWindow));

            // Open Symbol Inspector
            RegisterToolWindowCommand(commandService,
                PenguinExtensionCommandIds.OpenSymbolInspectorId, typeof(SymbolInspectorWindow));

            // Open Shortcuts Reference
            RegisterToolWindowCommand(commandService,
                PenguinExtensionCommandIds.OpenShortcutsReferenceId, typeof(ShortcutsReferenceWindow));

            // Open Penguin Color Theme
            RegisterToolWindowCommand(commandService,
                PenguinExtensionCommandIds.OpenColorThemeId, typeof(ColorThemeWindow));
        }

        // ponytail: one helper instead of repeating tool window opening boilerplate
        private void RegisterToolWindowCommand(OleMenuCommandService svc, int cmdId, Type windowType)
        {
            var id = new CommandID(PenguinExtensionCommandIds.CommandSetGuid, cmdId);
            svc.AddCommand(new MenuCommand(async (s, e) =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var window = await ShowToolWindowAsync(windowType, 0, true, DisposalToken);
                if (window?.Frame is IVsWindowFrame frame)
                    frame.Show();
            }, id));
        }

        // ── Helpers ─────────────────────────────────────────────────

        private void SetStatusBar(string text)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var statusBar = GetService(typeof(SVsStatusbar)) as IVsStatusbar;
                    statusBar?.SetText(text);
                }
                catch (Exception ex) { PenguinLog.Error("Status", ex.Message); }
            });
        }

        private static void WriteToOutputWindow(string message) => PenguinLog.Write(message);

        /// <summary>Running devenv.exe file version, e.g. "18.0.11010.0" (VS 2026) or "17.14.…" (VS 2022).</summary>
        internal static string VsVersion()
        {
            try { return FileVersionInfo.GetVersionInfo(Process.GetCurrentProcess().MainModule.FileName).FileVersion; }
            catch (Exception) { return "(unknown)"; }
        }

        /// <summary>Shows "🐧 Indexing n/N" with the status-bar progress meter, throttled to ~4 updates/s.</summary>
        private sealed class IndexStatusProgress : IProgress<IndexProgress>
        {
            private readonly PenguinExtensionPackage _package;
            private long _lastTicks;
            private uint _cookie;
            private int _total, _done, _skipped;

            public IndexStatusProgress(PenguinExtensionPackage package) { _package = package; }

            public string Summary => _total + " files (" + (_done) + " re-indexed, " + _skipped + " unchanged)";

            public void Report(IndexProgress value)
            {
                int total, processed, skipped;
                lock (value) { total = value.TotalFiles; processed = value.ProcessedFiles; skipped = value.SkippedFiles; }
                _total = total; _done = processed; _skipped = skipped;
                var now = DateTime.UtcNow.Ticks;
                if (now - Interlocked.Read(ref _lastTicks) < TimeSpan.TicksPerMillisecond * 250) return;
                Interlocked.Exchange(ref _lastTicks, now);
                Update(processed + skipped, total, false);
            }

            public void Finish() => Update(_total, _total, true);

            private void Update(int current, int total, bool clear) => _ = _package.JoinableTaskFactory.RunAsync(async () =>
            {
                await _package.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var bar = _package.GetService(typeof(SVsStatusbar)) as IVsStatusbar;
                    if (bar == null) return;
                    if (clear) { bar.Progress(ref _cookie, 0, "", 0, 0); return; }
                    bar.Progress(ref _cookie, 1, "🐧 Indexing " + current + "/" + total + " files", (uint)current, (uint)Math.Max(1, total));
                }
                catch (Exception ex) { PenguinLog.Error("Index", "status bar: " + ex.Message); }
            });
        }

        // ── Cleanup ─────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                PenguinOptionsPage.SettingsApplied -= OnSettingsApplied;
                BackendService.Changed -= OnBackendChanged;
                var solutionEvents = _solutionEvents; _solutionEvents = null;
                var diagnostics = _diagnostics; _diagnostics = null;
                _ = JoinableTaskFactory.RunAsync(async () =>
                {
                    await JoinableTaskFactory.SwitchToMainThreadAsync();
                    solutionEvents?.Dispose();
                    diagnostics?.Dispose();
                });
                _workspaceCts?.Cancel();
                _ = Task.Run(async () =>
                {
                    await _lifecycle.WaitAsync().ConfigureAwait(false);
                    try { await StopBackendAsync().ConfigureAwait(false); }
                    finally { _lifecycle.Release(); }
                });
            }
            base.Dispose(disposing);
        }
    }
}
