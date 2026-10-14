using System;
using System.Collections.Concurrent;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Records which editor hooks Visual Studio actually called this session (and how often), so the Self-Test
    /// can prove which completion / hover / signature pipeline the running VS uses.
    /// </summary>
    internal static class FeatureProbe
    {
        private static readonly ConcurrentDictionary<string, (int count, DateTime last, string detail)> Hits =
            new ConcurrentDictionary<string, (int, DateTime, string)>();

        public static void Mark(string feature, string detail = null) =>
            Hits.AddOrUpdate(feature, _ => (1, DateTime.Now, detail), (_, old) => (old.count + 1, DateTime.Now, detail ?? old.detail));

        public static bool Seen(string feature) => Hits.ContainsKey(feature);

        public static string Describe(string feature) =>
            Hits.TryGetValue(feature, out var h)
                ? "called " + h.count + "×, last " + h.last.ToString("HH:mm:ss") + (string.IsNullOrEmpty(h.detail) ? "" : " — " + h.detail)
                : "not called yet";
    }

    /// <summary>
    /// The "PenguinExtension" Output pane. Callable from any thread: lines are queued and flushed on the UI thread.
    /// Every feature logs what it did (or why it did nothing) here, so behaviour is verifiable without a debugger.
    /// </summary>
    internal static class PenguinLog
    {
        private static readonly Guid PaneGuid = new Guid("A1B2C3D4-0001-0002-0003-000000000001");
        private static readonly ConcurrentQueue<string> Pending = new ConcurrentQueue<string>();
        private static int _flushScheduled;
        private static IVsOutputWindowPane _pane;

        public static void Info(string area, string message) => Write("[" + area + "] " + message);

        public static void Error(string area, string message) => Write("[" + area + "] ❌ " + message);

        public static void Error(string area, Exception ex) =>
            Write("[" + area + "] ❌ " + ex.GetType().Name + ": " + ex.Message + Environment.NewLine + ex.StackTrace);

        public static void Write(string message)
        {
            Pending.Enqueue("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + message + Environment.NewLine);
            if (System.Threading.Interlocked.Exchange(ref _flushScheduled, 1) == 0)
                _ = ThreadHelper.JoinableTaskFactory.RunAsync(FlushAsync);
        }

        /// <summary>Brings the PenguinExtension pane to the front (used when a command fails).</summary>
        public static void Show() => _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            try
            {
                GetPane()?.Activate();
                var shell = Package.GetGlobalService(typeof(SVsUIShell)) as IVsUIShell;
                var outputWindowGuid = new Guid(ToolWindowGuids80.Outputwindow);
                if (shell != null && shell.FindToolWindow((uint)__VSFINDTOOLWIN.FTW_fForceCreate, ref outputWindowGuid, out var frame) == 0)
                    frame?.ShowNoActivate();
            }
            catch (Exception) { /* never let logging take down a caller */ }
        });

        private static async System.Threading.Tasks.Task FlushAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            System.Threading.Interlocked.Exchange(ref _flushScheduled, 0);
            var pane = GetPane();
            while (Pending.TryDequeue(out var line))
                pane?.OutputStringThreadSafe(line);
        }

        private static IVsOutputWindowPane GetPane()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_pane != null) return _pane;
            var output = Package.GetGlobalService(typeof(SVsOutputWindow)) as IVsOutputWindow;
            if (output == null) return null;
            var guid = PaneGuid;
            output.CreatePane(ref guid, "PenguinExtension", 1, 0); // keep history across solution close
            output.GetPane(ref guid, out _pane);
            return _pane;
        }
    }
}
