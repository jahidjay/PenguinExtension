using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;
using PenguinExtention.Commands;

namespace PenguinExtention.Core
{
    internal static class CoreCommands
    {
        private static CancellationTokenSource activeJob;
        public static void Register(PenguinExtensionPackage package, OleMenuCommandService service)
        {
            Add(service, PenguinExtensionCommandIds.CoreStatusId, () => StatusAsync());
            Add(service, PenguinExtensionCommandIds.CoreReindexId, () => JobAsync("reindex"));
            Add(service, PenguinExtensionCommandIds.RestartBackendId, () => { package.RequestRestart(); return Task.CompletedTask; });
            Add(service, PenguinExtensionCommandIds.AIExplainId, () => JobAsync("explain"));
            Add(service, PenguinExtensionCommandIds.AIGenerateId, () => JobAsync("generate"));
            Add(service, PenguinExtensionCommandIds.CancelCoreJobId, () => { activeJob?.Cancel(); return Task.CompletedTask; });
        }
        private static void Add(OleMenuCommandService service, int id, Func<Task> action)
        {
            service.AddCommand(new MenuCommand((s,e) =>
            {
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
                {
                    try { await action(); }
                    catch (OperationCanceledException) { BackendService.Report("Core operation cancelled."); }
                    catch (Exception ex) { BackendService.Report("Core: " + ex.Message); }
                });
            }, new CommandID(PenguinExtensionCommandIds.CommandSetGuid, id)));
        }
        private static CoreClientService RequireCore()
        {
            if (!BackendService.IsCore) throw new InvalidOperationException("Select Core in Tools > Options > PenguinExtension to use this command.");
            var core = BackendService.Core;
            if (core?.IsReady != true) throw new InvalidOperationException(BackendService.Status);
            return core;
        }
        private static async Task StatusAsync()
        {
            if (!BackendService.IsCore)
            {
                await ShowTextAsync("Penguin Backend Status", BackendService.Status + "\nSelect Core in Tools > Options > PenguinExtension for Core indexing, style diagnostics and AI previews.");
                return;
            }
            var response = await RequireCore().RequestAsync("penguin/status", new { }, CancellationToken.None).ConfigureAwait(false);
            await ShowTextAsync("Penguin Core Status", response.ToString()).ConfigureAwait(false);
        }
        private static async Task JobAsync(string kind)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var core = RequireCore();
            if (activeJob != null) throw new InvalidOperationException("A Core action is already running. Cancel it before starting another.");
            bool ai = kind != "reindex";
            if (ai && !BackendService.AIEnabled) throw new InvalidOperationException("Enable local AI in Tools > Options > PenguinExtension first. No model is installed automatically.");
            var view = ai ? EditorAccess.ActiveView() : null;
            var snapshot = view?.TextSnapshot;
            var span = ai && view != null ? (view.Selection.IsEmpty
                ? view.Caret.Position.BufferPosition.GetContainingLine().Extent
                : view.Selection.StreamSelectionSpan.SnapshotSpan) : default(Microsoft.VisualStudio.Text.SnapshotSpan);
            if (ai && (view == null || span.Length == 0)) throw new InvalidOperationException("Select source text, or place the caret on a nonempty declaration.");
            if (ai && span.Length > 32000) throw new InvalidOperationException("Select at most 32,000 UTF-16 characters for local AI.");
            var generation = BackendService.Generation;
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            activeJob = cts;
            string jobId = null;
            string sessionId = null;
            try
            {
                var source = ai ? await Task.Run(() => span.GetText()).ConfigureAwait(false) : null;
                var job = await core.RequestAsync(ai ? "penguin/ai" : "penguin/reindex",
                    ai ? (object)new { task = kind, source, instruction = "" } : new { }, cts.Token).ConfigureAwait(false);
                jobId = (string)(job as JObject)?["id"];
                sessionId = (string)(job as JObject)?["sessionId"];
                if (jobId == null || sessionId == null) throw new InvalidOperationException("Core returned no job/session ID.");
                while (job is JObject && !Protocol.Terminal((string)job["state"]))
                {
                    BackendService.Report("Core " + kind + ": " + (string)job["state"] + ". Cancel via Penguin Extension menu.");
                    await Task.Delay(500, cts.Token).ConfigureAwait(false);
                    if (generation != BackendService.Generation) throw new OperationCanceledException();
                    job = await core.RequestAsync("penguin/job", new { id = jobId }, cts.Token).ConfigureAwait(false);
                }
                cts.Token.ThrowIfCancellationRequested();
                if (generation != BackendService.Generation) throw new OperationCanceledException();
                if (!(job is JObject)) throw new InvalidOperationException("Job expired. Run the action again.");
                if ((string)job["sessionId"] != sessionId) throw new OperationCanceledException();
                if ((string)job["state"] != "succeeded") throw new InvalidOperationException((string)job["error"] ?? "Job " + (string)job["state"]);
                var result = job["result"] as JObject;
                var preview = ai
                    ? "Preview only - untrusted model output. No changes applied.\nModel: " + (string)result?["model"] + "\n\n" + (string)result?["text"]
                    : job["result"]?.ToString() ?? "Index complete.";
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cts.Token);
                if (ai && (view.IsClosed || view.TextSnapshot != snapshot))
                    throw new InvalidOperationException("Source changed while AI was working. Discarded stale preview; run again.");
                await ShowTextAsync(ai ? "Penguin AI Preview" : "Penguin Reindex Result", preview);
                BackendService.Report("Core " + kind + " complete.");
                jobId = null;
            }
            finally
            {
                if (jobId != null && core.IsReady)
                {
                    try { await core.RequestAsync("penguin/cancelJob", new { id = jobId }, CancellationToken.None).ConfigureAwait(false); } catch { }
                }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                activeJob = null; cts.Dispose();
            }
        }
        private static async Task ShowTextAsync(string title, string text)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(12) };
            var window = new Window { Title = title, Width = 780, Height = 540, Content = box };
            window.Show();
        }
    }
}
