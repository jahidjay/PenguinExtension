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
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExistsAndFullyLoaded_string,
                     PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideToolWindow(typeof(UnrealExplorerWindow),
                       Style = VsDockStyle.Linked,
                       Window = ToolWindowGuids.SolutionExplorer,
                       Orientation = ToolWindowOrientation.Left)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideOptionPage(typeof(PenguinOptionsPage),
                       "PenguinExtension", "General", 0, 0, true)]
    public sealed class PenguinExtensionPackage : AsyncPackage
    {
        public const string PackageGuidString = "7D9B3C2E-8A4F-4B1D-9E5C-6F0A2D3B8C7E";

        private SQLiteCache _db;
        private UnrealIndexer _indexer;
        private IncrementalIndexer _incrementalIndexer;
        private CancellationTokenSource _indexCts;

        // ── Package initialization ──────────────────────────────────

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress).ConfigureAwait(false);

            // Register commands on the main thread
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            await RegisterCommandsAsync().ConfigureAwait(true);

            // Continue initialization on a background thread
            await TaskScheduler.Default;
            await InitializeExtensionAsync(cancellationToken).ConfigureAwait(false);
        }

        private async Task InitializeExtensionAsync(CancellationToken ct)
        {
            try
            {
                // Step 1: Detect Unreal project
                SetStatusBar("PenguinExtension: Detecting Unreal project...");

                UnrealProjectDetector.CreateInstance();
                var options = (PenguinOptionsPage)GetDialogPage(typeof(PenguinOptionsPage));
                var engineOverride = options?.EngineRootOverride ?? string.Empty;

                await UnrealProjectDetector.Instance.DetectAsync(this, engineOverride).ConfigureAwait(false);

                if (!UnrealProjectDetector.Instance.IsUnrealProject)
                {
                    SetStatusBar("PenguinExtension: Not an Unreal project. Extension inactive.");
                    return;
                }

                var detector = UnrealProjectDetector.Instance;
                WriteToOutputWindow($"Unreal project detected: {detector.UProjectFilePath}");
                WriteToOutputWindow($"Project source: {detector.ProjectSourceRoot}");
                WriteToOutputWindow($"Engine source: {detector.EngineSourceRoot ?? "(not found)"}");

                // Step 2: Initialize database and load cache
                SetStatusBar("PenguinExtension: Loading symbol cache...");

                _db = new SQLiteCache(detector.SolutionDirectory);
                CacheService.Initialize(_db);

                var loader = new StartupCacheLoader(_db, CacheService.Instance);
                var loadTime = await loader.LoadAsync().ConfigureAwait(false);

                WriteToOutputWindow($"Cache loaded in {loadTime.TotalMilliseconds:F0}ms — {CacheService.Instance.SymbolCount:N0} symbols");
                SetStatusBar($"PenguinExtension: Ready ({CacheService.Instance.SymbolCount:N0} cached symbols)");

                // Step 3: Start background indexing
                _indexCts = new CancellationTokenSource();
                var maxThreads = options?.MaxIndexingThreads ?? (Environment.ProcessorCount / 2);
                var indexEngine = options?.IndexEngineSource ?? true;

                _indexer = new UnrealIndexer(_db, CacheService.Instance, maxThreads);

                // Fire-and-forget background indexing
                _ = Task.Run(async () =>
                {
                    try
                    {
                        SetStatusBar("PenguinExtension: Background indexing...");

                        var indexProgress = new Progress<IndexProgress>(p =>
                        {
                            if (p.IsComplete)
                            {
                                SetStatusBar($"PenguinExtension: Indexing complete — {p.SymbolsFound:N0} symbols");
                                WriteToOutputWindow($"Indexing complete: {p.ProcessedFiles} files processed, {p.SkippedFiles} unchanged, {p.SymbolsFound:N0} total symbols");
                            }
                            else
                            {
                                var pct = p.TotalFiles > 0 ? (p.ProcessedFiles + p.SkippedFiles) * 100 / p.TotalFiles : 0;
                                SetStatusBar($"PenguinExtension: Indexing {pct}% — {p.CurrentFile}");
                            }
                        });

                        await _indexer.IndexAsync(
                            detector.ProjectSourceRoot,
                            detector.EngineSourceRoot,
                            indexEngine,
                            indexProgress,
                            _indexCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        WriteToOutputWindow($"Indexing error: {ex.Message}");
                        SetStatusBar("PenguinExtension: Indexing failed. See Output window.");
                    }
                }, _indexCts.Token);

                // Step 4: Start file watcher for incremental indexing
                _incrementalIndexer = new IncrementalIndexer(_indexer, CacheService.Instance);
                _incrementalIndexer.Start(detector.ProjectSourceRoot);

                WriteToOutputWindow("Incremental file watcher started.");
            }
            catch (Exception ex)
            {
                WriteToOutputWindow($"PenguinExtension initialization error: {ex}");
                SetStatusBar("PenguinExtension: Initialization failed.");
            }
        }

        // ── Command registration ────────────────────────────────────

        private async Task RegisterCommandsAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            if (commandService == null) return;

            // Go To Unreal Definition
            await GoToUnrealDefinitionCommand.InitializeAsync(this).ConfigureAwait(true);

            // Open Unreal Explorer
            var explorerCmdId = new CommandID(
                PenguinExtensionCommandIds.CommandSetGuid,
                PenguinExtensionCommandIds.OpenUnrealExplorerId);

            commandService.AddCommand(new MenuCommand(async (s, e) =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                var window = await ShowToolWindowAsync(
                    typeof(UnrealExplorerWindow), 0, true, DisposalToken);
                if (window?.Frame is IVsWindowFrame frame)
                    frame.Show();
            }, explorerCmdId));
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
                catch { }
            });
        }

        private void WriteToOutputWindow(string message)
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    var outputWindow = GetService(typeof(SVsOutputWindow)) as IVsOutputWindow;
                    if (outputWindow == null) return;

                    var paneGuid = new Guid("A1B2C3D4-0001-0002-0003-000000000001");
                    outputWindow.CreatePane(ref paneGuid, "PenguinExtension", 1, 1);
                    outputWindow.GetPane(ref paneGuid, out IVsOutputWindowPane pane);
                    pane?.OutputStringThreadSafe($"[{DateTime.Now:HH:mm:ss}] {message}\n");
                }
                catch { }
            });
        }

        // ── Cleanup ─────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _indexCts?.Cancel();
                _indexCts?.Dispose();
                _incrementalIndexer?.Dispose();
                _db?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
