using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PenguinExtention.Models;

namespace PenguinExtention.Services
{
    /// <summary>A tool an MCP server offers (tools/list).</summary>
    internal sealed class McpTool
    {
        public string Name, Title, Description;
        public JObject InputSchema;
        public override string ToString() => string.IsNullOrEmpty(Title) || Title == Name ? Name : Name + " — " + Title;
    }

    /// <summary>
    /// Minimal Model Context Protocol client (JSON-RPC 2.0): initialize, tools/list, tools/call, ping.
    /// Transports: stdio (newline-delimited JSON), streamable HTTP (POST, JSON or SSE replies, Mcp-Session-Id) and
    /// legacy HTTP+SSE (GET event stream + "endpoint" POST URL). Everything runs on background threads.
    /// </summary>
    internal sealed class McpClient : IDisposable
    {
        public const string ProtocolVersion = "2025-06-18";
        private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan CallTimeout = TimeSpan.FromMinutes(5);

        private readonly ConcurrentDictionary<long, TaskCompletionSource<JObject>> _pending = new ConcurrentDictionary<long, TaskCompletionSource<JObject>>();
        private readonly McpServerConfig _config;
        private readonly string _rootFolder;
        private Transport _transport;
        private long _nextId;
        private bool _disposed;

        public McpServerConfig Config => _config;
        public JObject ServerInfo { get; private set; }
        public string NegotiatedVersion { get; private set; }
        public string Instructions { get; private set; }
        public bool IsConnected => _transport != null && !_disposed && !_transport.IsClosed;

        /// <summary>Server stderr, notifications/message and transport events.</summary>
        public event Action<string> Log;
        /// <summary>The connection ended (process exited, stream closed).</summary>
        public event Action Closed;

        private McpClient(McpServerConfig config, string rootFolder) { _config = config; _rootFolder = rootFolder; }

        /// <summary>Starts/opens the server and runs the initialize handshake. <paramref name="log"/> sees stderr from the first line on.</summary>
        public static async Task<McpClient> ConnectAsync(McpServerConfig config, string rootFolder, Action<string> log, CancellationToken ct)
        {
            var client = new McpClient(config, rootFolder);
            if (log != null) client.Log += log;
            try
            {
                await client.StartAsync(ct).ConfigureAwait(false);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }

        private async Task StartAsync(CancellationToken ct)
        {
            var type = (_config.Type ?? "").ToLowerInvariant();
            if (_config.IsStdio) _transport = new StdioTransport(this);
            else if (string.IsNullOrEmpty(_config.Url)) throw new InvalidOperationException("Server \"" + _config.Name + "\" has neither a command nor a url.");
            else if (type == "sse") _transport = new SseTransport(this);
            else _transport = new StreamableHttpTransport(this);
            await _transport.OpenAsync(ct).ConfigureAwait(false);
            JObject init;
            try { init = await InitializeAsync(ct).ConfigureAwait(false); }
            catch (McpHttpException ex) when (string.IsNullOrEmpty(type) && (ex.Status == 404 || ex.Status == 405) && _transport is StreamableHttpTransport)
            {
                // No "type" given and the server does not speak streamable HTTP: try the older HTTP+SSE transport.
                RaiseLog("streamable HTTP refused (" + ex.Status + "), trying HTTP+SSE");
                _transport.Dispose();
                _transport = new SseTransport(this);
                await _transport.OpenAsync(ct).ConfigureAwait(false);
                init = await InitializeAsync(ct).ConfigureAwait(false);
            }
            NegotiatedVersion = (string)init["protocolVersion"];
            ServerInfo = init["serverInfo"] as JObject;
            Instructions = (string)init["instructions"];
            _transport.ProtocolVersion = NegotiatedVersion;
            await NotifyAsync("notifications/initialized", null, ct).ConfigureAwait(false);
        }

        private Task<JObject> InitializeAsync(CancellationToken ct) =>
            RequestAsync("initialize", new JObject
            {
                ["protocolVersion"] = ProtocolVersion,
                ["capabilities"] = new JObject { ["roots"] = new JObject { ["listChanged"] = false } },
                ["clientInfo"] = new JObject { ["name"] = "PenguinExtension", ["version"] = PenguinVersion() }
            }, InitTimeout, ct);

        public async Task<List<McpTool>> ListToolsAsync(CancellationToken ct)
        {
            var tools = new List<McpTool>();
            string cursor = null;
            for (int page = 0; page < 50; page++)
            {
                var p = new JObject();
                if (cursor != null) p["cursor"] = cursor;
                var result = await RequestAsync("tools/list", p, InitTimeout, ct).ConfigureAwait(false);
                foreach (var t in (result["tools"] as JArray) ?? new JArray())
                    tools.Add(new McpTool
                    {
                        Name = (string)t["name"], Title = (string)t["title"] ?? (string)t["annotations"]?["title"],
                        Description = (string)t["description"] ?? "", InputSchema = t["inputSchema"] as JObject ?? new JObject()
                    });
                cursor = (string)result["nextCursor"];
                if (string.IsNullOrEmpty(cursor)) break;
            }
            return tools;
        }

        public Task<JObject> CallToolAsync(string name, JObject arguments, CancellationToken ct) =>
            RequestAsync("tools/call", new JObject { ["name"] = name, ["arguments"] = arguments ?? new JObject() }, CallTimeout, ct);

        public Task<JObject> PingAsync(CancellationToken ct) => RequestAsync("ping", null, InitTimeout, ct);

        /// <summary>Readable text of a tools/call result: text items, image/audio/resource summaries, structured content.</summary>
        public static string FormatResult(JObject result)
        {
            var sb = new StringBuilder();
            if ((bool?)result["isError"] == true) sb.AppendLine("⚠ The tool reported an error:");
            foreach (var c in (result["content"] as JArray) ?? new JArray())
            {
                var type = (string)c["type"];
                switch (type)
                {
                    case "text": sb.AppendLine((string)c["text"]); break;
                    case "image":
                    case "audio":
                        sb.AppendLine("[" + type + " " + (string)c["mimeType"] + ", " + (((string)c["data"])?.Length * 3 / 4 ?? 0) + " bytes]"); break;
                    case "resource":
                        sb.AppendLine((string)c["resource"]?["text"] ?? "[resource " + (string)c["resource"]?["uri"] + "]"); break;
                    case "resource_link":
                        sb.AppendLine("[link " + (string)c["uri"] + "]"); break;
                    default: sb.AppendLine(c.ToString(Formatting.Indented)); break;
                }
            }
            if (result["structuredContent"] != null && result["structuredContent"].Type != JTokenType.Null)
                sb.AppendLine("structuredContent:").AppendLine(result["structuredContent"].ToString(Formatting.Indented));
            return sb.Length == 0 ? result.ToString(Formatting.Indented) : sb.ToString().TrimEnd();
        }

        /// <summary>Argument skeleton from a tool's JSON schema: defaults, first enum values, or empty values by type (required first).</summary>
        public static JObject ArgumentTemplate(JObject schema)
        {
            var result = new JObject();
            var props = schema?["properties"] as JObject;
            if (props == null) return result;
            var required = new HashSet<string>(((schema["required"] as JArray) ?? new JArray()).Select(t => (string)t));
            foreach (var prop in props.Properties().OrderBy(p => required.Contains(p.Name) ? 0 : 1))
            {
                var s = prop.Value as JObject ?? new JObject();
                JToken value;
                if (s["default"] != null) value = s["default"].DeepClone();
                else if (s["enum"] is JArray e && e.Count > 0) value = e[0].DeepClone();
                else
                {
                    var t = s["type"] is JArray types ? (string)types.FirstOrDefault(x => (string)x != "null") : (string)s["type"];
                    switch (t)
                    {
                        case "integer": case "number": value = 0; break;
                        case "boolean": value = false; break;
                        case "array": value = new JArray(); break;
                        case "object": value = ArgumentTemplate(s); break;
                        default: value = ""; break;
                    }
                }
                result[prop.Name] = value;
            }
            return result;
        }

        // ── JSON-RPC ────────────────────────────────────────────────

        private async Task<JObject> RequestAsync(string method, JObject parameters, TimeSpan timeout, CancellationToken ct)
        {
            if (_disposed || _transport == null) throw new ObjectDisposedException("MCP connection");
            long id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            var message = new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
            if (parameters != null) message["params"] = parameters;
            using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeoutCts.CancelAfter(timeout);
                using (timeoutCts.Token.Register(() => tcs.TrySetCanceled()))
                {
                    try
                    {
                        await _transport.SendAsync(message, timeoutCts.Token).ConfigureAwait(false);
                        var response = await tcs.Task.ConfigureAwait(false);
                        if (response["error"] is JObject error)
                            throw new McpException((string)error["message"] ?? error.ToString(Formatting.None), (int?)error["code"] ?? 0);
                        return response["result"] as JObject ?? new JObject();
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_disposed)
                    {
                        _ = NotifyAsync("notifications/cancelled", new JObject { ["requestId"] = id, ["reason"] = "timeout" }, CancellationToken.None);
                        throw new TimeoutException(method + " got no answer within " + timeout.TotalSeconds + " s");
                    }
                    finally { _pending.TryRemove(id, out _); }
                }
            }
        }

        private async Task NotifyAsync(string method, JObject parameters, CancellationToken ct)
        {
            if (_transport == null || _disposed) return;
            var message = new JObject { ["jsonrpc"] = "2.0", ["method"] = method };
            if (parameters != null) message["params"] = parameters;
            try { await _transport.SendAsync(message, ct).ConfigureAwait(false); }
            catch (Exception ex) when (method == "notifications/cancelled") { RaiseLog("cancel notice failed: " + ex.Message); }
        }

        /// <summary>Handles one message from the server: a response, a server→client request, or a notification.</summary>
        private void OnMessage(JToken token)
        {
            if (token is JArray batch) { foreach (var t in batch) OnMessage(t); return; }
            if (!(token is JObject msg)) return;
            var method = (string)msg["method"];
            var id = msg["id"];
            if (method == null)
            {
                if (id != null && long.TryParse(id.ToString(), out var key) && _pending.TryGetValue(key, out var tcs)) tcs.TrySetResult(msg);
                return;
            }
            if (id != null && id.Type != JTokenType.Null)
            {
                JObject reply = new JObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone() };
                if (method == "ping") reply["result"] = new JObject();
                else if (method == "roots/list")
                    reply["result"] = new JObject
                    {
                        ["roots"] = string.IsNullOrEmpty(_rootFolder) ? new JArray()
                            : new JArray(new JObject { ["uri"] = new Uri(_rootFolder.TrimEnd('\\') + "\\").AbsoluteUri, ["name"] = Path.GetFileName(_rootFolder.TrimEnd('\\')) })
                    };
                else reply["error"] = new JObject { ["code"] = -32601, ["message"] = "PenguinExtension does not support " + method };
                _ = Task.Run(async () =>
                {
                    try { await _transport.SendAsync(reply, CancellationToken.None).ConfigureAwait(false); }
                    catch (Exception ex) { RaiseLog("reply to " + method + " failed: " + ex.Message); }
                });
                return;
            }
            if (method == "notifications/message") RaiseLog("[" + (string)msg["params"]?["level"] + "] " + msg["params"]?["data"]);
        }

        private void OnClosed(string reason)
        {
            if (!string.IsNullOrEmpty(reason)) RaiseLog(reason);
            foreach (var kv in _pending) kv.Value.TrySetException(new IOException("MCP server disconnected" + (string.IsNullOrEmpty(reason) ? "" : ": " + reason)));
            try { Closed?.Invoke(); } catch (Exception ex) { PenguinLog.Error("MCP", ex); }
        }

        private void RaiseLog(string line)
        {
            try { Log?.Invoke(line); } catch (Exception ex) { PenguinLog.Error("MCP", ex); }
        }

        private static string PenguinVersion() => typeof(McpClient).Assembly.GetName().Version.ToString();

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _transport?.Dispose();
            foreach (var kv in _pending) kv.Value.TrySetCanceled();
        }

        // ── Transports ──────────────────────────────────────────────

        private abstract class Transport : IDisposable
        {
            protected readonly McpClient Owner;
            protected Transport(McpClient owner) { Owner = owner; }
            public string ProtocolVersion;
            public volatile bool IsClosed;
            public abstract Task OpenAsync(CancellationToken ct);
            public abstract Task SendAsync(JObject message, CancellationToken ct);
            public abstract void Dispose();

            protected void Deliver(string json)
            {
                if (string.IsNullOrWhiteSpace(json)) return;
                JToken token;
                try { token = JToken.Parse(json); }
                catch (JsonException) { Owner.RaiseLog("not JSON: " + (json.Length > 200 ? json.Substring(0, 200) + "…" : json)); return; }
                Owner.OnMessage(token);
            }

            protected void Close(string reason)
            {
                if (IsClosed) return;
                IsClosed = true;
                Owner.OnClosed(reason);
            }

            /// <summary>Reads Server-Sent Events, calling <paramref name="onEvent"/>(event, data) for each one.</summary>
            protected static async Task ReadSseAsync(Stream stream, Action<string, string> onEvent)
            {
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    string evt = null;
                    var data = new StringBuilder();
                    string line;
                    while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        if (line.Length == 0)
                        {
                            if (data.Length > 0) onEvent(evt ?? "message", data.ToString());
                            evt = null; data.Clear();
                            continue;
                        }
                        if (line[0] == ':') continue;
                        int colon = line.IndexOf(':');
                        var field = colon < 0 ? line : line.Substring(0, colon);
                        var value = colon < 0 ? "" : line.Substring(colon + 1).TrimStart(' ');
                        if (field == "event") evt = value;
                        else if (field == "data") { if (data.Length > 0) data.Append('\n'); data.Append(value); }
                    }
                    if (data.Length > 0) onEvent(evt ?? "message", data.ToString());
                }
            }
        }

        private sealed class StdioTransport : Transport
        {
            private Process _process;
            private StreamWriter _stdin;
            private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);

            public StdioTransport(McpClient owner) : base(owner) { }

            public override Task OpenAsync(CancellationToken ct)
            {
                var cfg = Owner._config;
                var psi = BuildStartInfo(cfg.Command, cfg.Args ?? new string[0]);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardInput = psi.RedirectStandardOutput = psi.RedirectStandardError = true;
                psi.StandardOutputEncoding = new UTF8Encoding(false);
                psi.StandardErrorEncoding = new UTF8Encoding(false);
                var cwd = !string.IsNullOrEmpty(cfg.Cwd) ? cfg.Cwd : Owner._rootFolder;
                if (!string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)) psi.WorkingDirectory = cwd;
                if (cfg.Env != null) foreach (var kv in cfg.Env) psi.EnvironmentVariables[kv.Key] = kv.Value ?? "";
                Owner.RaiseLog("starting: " + psi.FileName + " " + psi.Arguments);
                _process = StartWithoutBom(psi) ?? throw new InvalidOperationException("Could not start " + cfg.Command);
                _stdin = new StreamWriter(_process.StandardInput.BaseStream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
                var stdout = _process.StandardOutput;
                var stderr = _process.StandardError;
                Task.Factory.StartNew(() =>
                {
                    try { string line; while ((line = stdout.ReadLine()) != null) Deliver(line); }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
                    Close("server process ended" + ExitCodeText());
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Task.Factory.StartNew(() =>
                {
                    try { string line; while ((line = stderr.ReadLine()) != null) Owner.RaiseLog(line); }
                    catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                return Task.CompletedTask;
            }

            private static readonly object ConsoleEncodingLock = new object();

            /// <summary>
            /// .NET Framework wraps a redirected stdin in a writer using Console.InputEncoding and writes its preamble; when
            /// that is UTF-8 with BOM, the server's first line starts with EF BB BF and JSON parsing fails (Python MCP servers exit).
            /// Swap in BOM-less UTF-8 for the duration of Process.Start.
            /// </summary>
            private static Process StartWithoutBom(ProcessStartInfo psi)
            {
                lock (ConsoleEncodingLock)
                {
                    Encoding previous = null;
                    try
                    {
                        var current = Console.InputEncoding;
                        if (current.GetPreamble().Length > 0) { previous = current; Console.InputEncoding = new UTF8Encoding(false); }
                    }
                    catch (IOException) { previous = null; } // no console (normal inside Visual Studio): nothing to swap
                    try { return Process.Start(psi); }
                    finally
                    {
                        if (previous != null) try { Console.InputEncoding = previous; } catch (IOException) { }
                    }
                }
            }

            private string ExitCodeText()
            {
                try { return _process.WaitForExit(200) ? " (exit code " + _process.ExitCode + ")" : ""; }
                catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { return ""; }
            }

            public override async Task SendAsync(JObject message, CancellationToken ct)
            {
                if (IsClosed) throw new IOException("MCP server process has exited");
                var line = message.ToString(Formatting.None);
                await _writeLock.WaitAsync(ct).ConfigureAwait(false);
                try { await _stdin.WriteLineAsync(line).ConfigureAwait(false); }
                finally { _writeLock.Release(); }
            }

            public override void Dispose()
            {
                IsClosed = true;
                try { _stdin?.Dispose(); } catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException) { }
                var p = _process;
                if (p == null) return;
                Task.Run(() =>
                {
                    try
                    {
                        if (p.WaitForExit(1500)) return;
                        // Kill the whole tree: npx/uvx/cmd wrappers start the real server as a child process.
                        using (var kill = Process.Start(new ProcessStartInfo("taskkill", "/PID " + p.Id + " /T /F") { CreateNoWindow = true, UseShellExecute = false }))
                            kill?.WaitForExit(5000);
                    }
                    catch (Exception ex) when (ex is InvalidOperationException || ex is System.ComponentModel.Win32Exception) { }
                    finally { p.Dispose(); }
                });
            }

            /// <summary>Resolves the command on PATH (with PATHEXT); .cmd/.bat (npx, uvx shims) run through cmd.exe.</summary>
            internal static ProcessStartInfo BuildStartInfo(string command, string[] args)
            {
                if (string.IsNullOrWhiteSpace(command)) throw new InvalidOperationException("The server has an empty \"command\".");
                var resolved = Resolve(command) ?? throw new FileNotFoundException(
                    "Command not found: \"" + command + "\" (not a file and not on PATH). Install it, or use its full path in .mcp.json.");
                var argLine = string.Join(" ", args.Select(Quote));
                var ext = Path.GetExtension(resolved).ToLowerInvariant();
                if (ext == ".cmd" || ext == ".bat")
                    return new ProcessStartInfo(Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe", "/d /s /c \"" + Quote(resolved) + (argLine.Length > 0 ? " " + argLine : "") + "\"");
                return new ProcessStartInfo(resolved, argLine);
            }

            private static string Resolve(string command)
            {
                if (string.IsNullOrEmpty(command)) return null;
                var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';').Where(e => e.Length > 0).ToList();
                IEnumerable<string> Candidates(string basePath) =>
                    Path.HasExtension(basePath) ? new[] { basePath }.Concat(exts.Select(e => basePath + e)) : exts.Select(e => basePath + e);
                try
                {
                    if (Path.IsPathRooted(command) || command.Contains('\\') || command.Contains('/'))
                        return Candidates(Path.GetFullPath(command)).FirstOrDefault(File.Exists);
                    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';').Where(d => d.Trim().Length > 0))
                    {
                        var hit = Candidates(Path.Combine(dir.Trim().Trim('"'), command)).FirstOrDefault(File.Exists);
                        if (hit != null) return hit;
                    }
                }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { }
                return null;
            }

            /// <summary>Windows command-line quoting (CommandLineToArgvW rules).</summary>
            internal static string Quote(string arg)
            {
                if (string.IsNullOrEmpty(arg)) return "\"\"";
                if (arg.IndexOfAny(new[] { ' ', '\t', '"', '&', '|', '<', '>', '^' }) < 0) return arg;
                var sb = new StringBuilder("\"");
                int backslashes = 0;
                foreach (var c in arg)
                {
                    if (c == '\\') { backslashes++; continue; }
                    if (c == '"') { sb.Append('\\', backslashes * 2 + 1); backslashes = 0; sb.Append('"'); continue; }
                    sb.Append('\\', backslashes); backslashes = 0; sb.Append(c);
                }
                sb.Append('\\', backslashes * 2).Append('"');
                return sb.ToString();
            }
        }

        private abstract class HttpTransportBase : Transport
        {
            protected readonly HttpClient Http;
            protected readonly CancellationTokenSource Life = new CancellationTokenSource();

            protected HttpTransportBase(McpClient owner) : base(owner)
            {
                Http = new HttpClient(new HttpClientHandler { UseProxy = !IsLocal(owner._config.Url) }) { Timeout = Timeout.InfiniteTimeSpan };
            }

            private static bool IsLocal(string url)
            {
                try { var host = new Uri(url).Host; return host == "localhost" || host == "127.0.0.1" || host == "::1" || host == "[::1]"; }
                catch (UriFormatException) { return false; }
            }

            protected void AddHeaders(HttpRequestMessage request)
            {
                if (Owner._config.Headers != null)
                    foreach (var kv in Owner._config.Headers) request.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
                if (!string.IsNullOrEmpty(ProtocolVersion)) request.Headers.TryAddWithoutValidation("MCP-Protocol-Version", ProtocolVersion);
            }

            public override void Dispose()
            {
                IsClosed = true;
                Life.Cancel();
                Http.Dispose();
            }
        }

        private sealed class StreamableHttpTransport : HttpTransportBase
        {
            private string _sessionId;
            public StreamableHttpTransport(McpClient owner) : base(owner) { }

            public override Task OpenAsync(CancellationToken ct) => Task.CompletedTask;

            public override async Task SendAsync(JObject message, CancellationToken ct)
            {
                var request = new HttpRequestMessage(HttpMethod.Post, Owner._config.Url)
                {
                    Content = new StringContent(message.ToString(Formatting.None), new UTF8Encoding(false), "application/json")
                };
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (_sessionId != null) request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
                AddHeaders(request);
                HttpResponseMessage response;
                try { response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
                catch (HttpRequestException ex) { throw new McpHttpException(0, "cannot reach " + Owner._config.Url + ": " + (ex.InnerException?.Message ?? ex.Message)); }
                if (response.Headers.TryGetValues("Mcp-Session-Id", out var ids)) _sessionId = ids.FirstOrDefault() ?? _sessionId;
                if (!response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    response.Dispose();
                    throw new McpHttpException((int)response.StatusCode, "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase +
                        (string.IsNullOrWhiteSpace(body) ? "" : ": " + (body.Length > 300 ? body.Substring(0, 300) + "…" : body)));
                }
                if (response.StatusCode == System.Net.HttpStatusCode.Accepted || response.Content.Headers.ContentLength == 0) { response.Dispose(); return; }
                var mediaType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (mediaType == "text/event-stream")
                {
                    // Replies stream back as events; read them in the background so this request can finish.
                    var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    _ = Task.Run(async () =>
                    {
                        using (response)
                        using (Life.Token.Register(() => stream.Dispose()))
                        {
                            try { await ReadSseAsync(stream, (evt, data) => { if (evt == "message") Deliver(data); }).ConfigureAwait(false); }
                            catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is HttpRequestException) { }
                        }
                    });
                    return;
                }
                using (response) Deliver(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
            }

            public override void Dispose()
            {
                if (_sessionId != null && !IsClosed)
                {
                    // Tell the server the session is over (best effort, do not wait).
                    var request = new HttpRequestMessage(HttpMethod.Delete, Owner._config.Url);
                    request.Headers.TryAddWithoutValidation("Mcp-Session-Id", _sessionId);
                    AddHeaders(request);
                    var http = Http;
                    _ = http.SendAsync(request).ContinueWith(t => { var _ = t.Exception; http.Dispose(); }, TaskScheduler.Default);
                    IsClosed = true;
                    Life.Cancel();
                    return;
                }
                base.Dispose();
            }
        }

        private sealed class SseTransport : HttpTransportBase
        {
            private readonly TaskCompletionSource<Uri> _endpoint = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            public SseTransport(McpClient owner) : base(owner) { }

            public override async Task OpenAsync(CancellationToken ct)
            {
                var request = new HttpRequestMessage(HttpMethod.Get, Owner._config.Url);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                AddHeaders(request);
                HttpResponseMessage response;
                try { response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
                catch (HttpRequestException ex) { throw new McpHttpException(0, "cannot reach " + Owner._config.Url + ": " + (ex.InnerException?.Message ?? ex.Message)); }
                if (!response.IsSuccessStatusCode)
                {
                    response.Dispose();
                    throw new McpHttpException((int)response.StatusCode, "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase + " (SSE)");
                }
                var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                _ = Task.Run(async () =>
                {
                    using (response)
                    using (Life.Token.Register(() => stream.Dispose()))
                    {
                        try
                        {
                            await ReadSseAsync(stream, (evt, data) =>
                            {
                                if (evt == "endpoint") _endpoint.TrySetResult(new Uri(new Uri(Owner._config.Url), data.Trim()));
                                else if (evt == "message") Deliver(data);
                            }).ConfigureAwait(false);
                        }
                        catch (Exception ex) when (ex is IOException || ex is ObjectDisposedException || ex is HttpRequestException) { }
                        _endpoint.TrySetException(new IOException("SSE stream closed before the server sent its endpoint"));
                        Close("SSE stream closed");
                    }
                });
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    timeout.CancelAfter(InitTimeout);
                    using (timeout.Token.Register(() => _endpoint.TrySetCanceled()))
                        await _endpoint.Task.ConfigureAwait(false);
                }
            }

            public override async Task SendAsync(JObject message, CancellationToken ct)
            {
                var url = await _endpoint.Task.ConfigureAwait(false);
                var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(message.ToString(Formatting.None), new UTF8Encoding(false), "application/json")
                };
                AddHeaders(request);
                using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    if (!response.IsSuccessStatusCode)
                        throw new McpHttpException((int)response.StatusCode, "HTTP " + (int)response.StatusCode + " " + response.ReasonPhrase);
            }
        }
    }

    internal class McpException : Exception
    {
        public int Code { get; }
        public McpException(string message, int code = 0) : base(message) { Code = code; }
    }

    internal sealed class McpHttpException : McpException
    {
        public int Status { get; }
        public McpHttpException(int status, string message) : base(message) { Status = status; }
    }
}
