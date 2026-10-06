using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json.Linq;

namespace PenguinExtention.Core
{
    // Error List is deliberately used rather than competing with the C++ language service tagger.
    internal sealed class CoreDiagnostics : IDisposable
    {
        private readonly ErrorListProvider errors;
        private readonly Dictionary<string, List<ErrorTask>> byUri = new Dictionary<string, List<ErrorTask>>();
        private bool disposed;
        public CoreDiagnostics(IServiceProvider provider)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            errors = new ErrorListProvider(provider) { ProviderName = "Penguin Core Style", ProviderGuid = new Guid("130FE8A6-EB87-4F0E-893D-76ABBAECF83B") };
            DocumentSync.DocumentInvalidated += Invalidate;
        }
        public void Publish(CoreClientService source, JObject data)
        {
            var uri = (string)data["uri"];
            var version = (long?)data["version"];
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (disposed || source != BackendService.Core || !DocumentSync.IsCurrent(uri, version)) return;
                Remove(uri);
                Uri parsed;
                if (!Uri.TryCreate(uri, UriKind.Absolute, out parsed) || !parsed.IsFile) return;
                var tasks = new List<ErrorTask>();
                foreach (var diagnostic in (data["diagnostics"] as JArray ?? new JArray()).Take(200))
                {
                    var task = new ErrorTask
                    {
                        Category = TaskCategory.CodeSense,
                        ErrorCategory = (int?)diagnostic["severity"] == 1 ? TaskErrorCategory.Error : TaskErrorCategory.Warning,
                        Text = "Penguin " + (string)diagnostic["code"] + ": " + (string)diagnostic["message"],
                        Document = parsed.LocalPath,
                        Line = Math.Max(0, (int?)diagnostic["range"]?["start"]?["line"] ?? 0),
                        Column = Math.Max(0, (int?)diagnostic["range"]?["start"]?["character"] ?? 0)
                    };
                    task.Navigate += (s,e) => errors.Navigate(task, new Guid("{7651A700-06E5-11D1-8EBD-00A0C90F26EA}"));
                    tasks.Add(task); errors.Tasks.Add(task);
                }
                byUri[uri] = tasks;
            });
        }
        private void Invalidate(string uri)
        {
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (!disposed) Remove(uri);
            });
        }
        private void Remove(string uri)
        {
            List<ErrorTask> tasks;
            if (uri != null && byUri.TryGetValue(uri, out tasks))
            { foreach (var task in tasks) errors.Tasks.Remove(task); byUri.Remove(uri); }
        }
        public void Clear() { ThreadHelper.ThrowIfNotOnUIThread(); if (!disposed) { errors.Tasks.Clear(); byUri.Clear(); } }
        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            DocumentSync.DocumentInvalidated -= Invalidate;
            Clear(); disposed = true; errors.Dispose();
        }
    }
}
