using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using StreamJsonRpc;

namespace PenguinExtention.Core
{
    // VS-independent transport. Document events and requests use the same FIFO.
    internal sealed class CoreClientService
    {
        private sealed class SessionRpc : JsonRpc
        {
            private static long nextId;
            public SessionRpc(IJsonRpcMessageHandler handler) : base(handler) { }
            protected override RequestId CreateNewRequestId() => new RequestId(Interlocked.Increment(ref nextId));
        }
        private readonly object gate = new object();
        private Task tail = Task.CompletedTask;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly Dictionary<string, long> documents = new Dictionary<string, long>(StringComparer.Ordinal);
        private Process process;
        private JsonRpc rpc;
        private Task stderrTask;
        private Task stopTask;
        private volatile bool stopping;
        private volatile bool ready;
        public bool IsReady => ready && !stopping;
        public event Action<string> Message;
        public event Action<JObject> Diagnostics;
        public event Action Failed;

        public async Task StartAsync(CoreConfiguration config, CancellationToken token)
        {
            await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();
                if (!Path.IsPathRooted(config.Executable) || !File.Exists(config.Executable))
                    throw new FileNotFoundException("Core executable missing. Package Core or set an absolute Core Executable Override in Tools > Options > PenguinExtension.", config.Executable);
                process = new Process { StartInfo = new ProcessStartInfo(config.Executable)
                {
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(config.Executable)
                }};
                if (!process.Start()) throw new IOException("Could not start Penguin Core.");
                stderrTask = DrainStderrAsync();
                var formatter = new JsonMessageFormatter();
                formatter.JsonSerializer.ContractResolver = new CamelCasePropertyNamesContractResolver();
                rpc = new SessionRpc(new HeaderDelimitedMessageHandler(process.StandardInput.BaseStream, process.StandardOutput.BaseStream, formatter));
                foreach (var method in GetType().GetMethods())
                {
                    var attribute = (JsonRpcMethodAttribute)Attribute.GetCustomAttribute(method, typeof(JsonRpcMethodAttribute));
                    if (attribute != null) rpc.AddLocalRpcMethod(method, this, attribute);
                }
                rpc.Disconnected += (s, e) =>
                {
                    ready = false;
                    if (!stopping)
                    {
                        Message?.Invoke("Core disconnected: " + e.Description + ". Use Restart Backend. No Legacy fallback.");
                        Failed?.Invoke();
                    }
                };
                rpc.StartListening();
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    var uri = Protocol.FileUri(config.ProjectRoot);
                    var result = await WithCancellationAsync(rpc.InvokeWithParameterObjectAsync<JToken>("initialize", new
                    {
                        processId = Process.GetCurrentProcess().Id, rootUri = uri,
                        workspaceFolders = new[] { new { uri, name = Path.GetFileName(config.ProjectRoot) } },
                        capabilities = new { general = new { positionEncodings = new[] { "utf-16" } }, textDocument = new { synchronization = new { didSave = true } } },
                        initializationOptions = new { penguin = new
                        {
                            adapter = "vs", engineRoots = string.IsNullOrEmpty(config.EngineRoot) ? new string[0] : new[] { config.EngineRoot },
                            ai = new { enabled = config.AIEnabled, endpoint = config.AIEndpoint, model = config.AIModel }
                        }}
                    }, timeout.Token), timeout.Token).ConfigureAwait(false);
                    Protocol.ValidateVersion(result);
                    await rpc.NotifyWithParameterObjectAsync("initialized", new { }).ConfigureAwait(false);
                    ready = true;
                }
            }, token).ConfigureAwait(false);
        }

        [JsonRpcMethod("textDocument/publishDiagnostics", UseSingleObjectParameterDeserialization = true)]
        public void PublishDiagnostics(JObject parameters) { if (!stopping) Diagnostics?.Invoke(parameters); }
        [JsonRpcMethod("window/logMessage", UseSingleObjectParameterDeserialization = true)]
        public void LogMessage(JObject parameters) => Message?.Invoke((string)parameters["message"]);
        [JsonRpcMethod("window/showMessage", UseSingleObjectParameterDeserialization = true)]
        public void ShowMessage(JObject parameters) => Message?.Invoke((string)parameters["message"]);
        [JsonRpcMethod("$/logTrace", UseSingleObjectParameterDeserialization = true)]
        public void LogTrace(JObject parameters) { }

        private async Task DrainStderrAsync()
        {
            try
            {
                var buffer = new char[2048];
                int count;
                while ((count = await process.StandardError.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    Message?.Invoke(new string(buffer, 0, count));
            }
            catch (ObjectDisposedException) { }
            catch (IOException) { }
        }
        // StreamJsonRpc signals $/cancelRequest but may wait for an uncooperative peer.
        // Stop awaiting locally too; observe the eventual response without blocking the FIFO.
        private static async Task<T> WithCancellationAsync<T>(Task<T> operation, CancellationToken token)
        {
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (token.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(operation, cancelled.Task).ConfigureAwait(false) != operation)
                {
                    _ = ObserveLateAsync(operation);
                    token.ThrowIfCancellationRequested();
                }
                return await operation.ConfigureAwait(false);
            }
        }
        private static async Task ObserveLateAsync(Task task) { try { await task.ConfigureAwait(false); } catch { } }

        private Task<T> EnqueueAsync<T>(Func<CancellationToken, Task<T>> operation, CancellationToken token)
        {
            lock (gate)
            {
                if (stopping) return Task.FromCanceled<T>(new CancellationToken(true));
                var previous = tail;
                var task = Task.Run(async () =>
                {
                    try { await previous.ConfigureAwait(false); } catch { }
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        if (!IsReady) throw new IOException("Core unavailable. Restart Backend; no Legacy fallback is active.");
                        return await operation(linked.Token).ConfigureAwait(false);
                    }
                });
                tail = task;
                return task;
            }
        }
        public Task<JToken> RequestAsync(string method, object parameters, CancellationToken token) => EnqueueAsync(async ct =>
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                return await WithCancellationAsync(rpc.InvokeWithParameterObjectAsync<JToken>(method, parameters, timeout.Token), timeout.Token).ConfigureAwait(false);
            }
        }, token);

        public Task SynchronizeAsync(string uri, long version, Func<string> text, bool save = false) => EnqueueAsync(async ct =>
        {
            long previous;
            var opened = documents.TryGetValue(uri, out previous);
            if (!opened || version > previous)
            {
                var source = text();
                if (source.Length > 8 * 1024 * 1024) throw new IOException("Core document exceeds the 8M UTF-16 character client limit.");
                if (!opened)
                    await rpc.NotifyWithParameterObjectAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = "cpp", version, text = source } }).ConfigureAwait(false);
                else
                    await rpc.NotifyWithParameterObjectAsync("textDocument/didChange", new { textDocument = new { uri, version }, contentChanges = new[] { new { text = source } } }).ConfigureAwait(false);
                documents[uri] = version;
            }
            if (save && (!opened || version >= previous))
                await rpc.NotifyWithParameterObjectAsync("textDocument/didSave", new { textDocument = new { uri } }).ConfigureAwait(false);
            return true;
        }, CancellationToken.None);
        public Task CloseDocumentAsync(string uri) => EnqueueAsync(async ct =>
        {
            if (documents.Remove(uri)) await rpc.NotifyWithParameterObjectAsync("textDocument/didClose", new { textDocument = new { uri } }).ConfigureAwait(false);
            return true;
        }, CancellationToken.None);
        public Task FilesChangedAsync(object[] changes) => EnqueueAsync(async ct =>
        {
            await rpc.NotifyWithParameterObjectAsync("workspace/didChangeWatchedFiles", new { changes }).ConfigureAwait(false);
            return true;
        }, CancellationToken.None);

        public Task StopAsync()
        {
            lock (gate)
            {
                if (stopTask != null) return stopTask;
                stopping = true; ready = false; lifetime.Cancel();
                return stopTask = Task.Run(StopWorkerAsync);
            }
        }
        private async Task StopWorkerAsync()
        {
            try
            {
                var drained = await Task.WhenAny(tail, Task.Delay(2000)).ConfigureAwait(false);
                if (drained != tail) rpc?.Dispose();
                if (rpc != null)
                {
                    using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)))
                    {
                        try { await WithCancellationAsync(rpc.InvokeWithParameterObjectAsync<JToken>("shutdown", null, timeout.Token), timeout.Token).ConfigureAwait(false); } catch { }
                    }
                    try { await Task.WhenAny(rpc.NotifyWithParameterObjectAsync("exit", null), Task.Delay(500)).ConfigureAwait(false); } catch { }
                }
                if (process != null)
                {
                    for (var i = 0; i < 20 && !process.HasExited; i++) await Task.Delay(100).ConfigureAwait(false);
                    if (!process.HasExited) process.Kill();
                }
            }
            catch (InvalidOperationException) { }
            finally
            {
                rpc?.Dispose();
                process?.Dispose();
            }
        }
    }
}
