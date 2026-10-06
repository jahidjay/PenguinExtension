using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using PenguinExtention.Core;

internal static class Program
{
    private static int assertions;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
        assertions++; Console.WriteLine("ok " + assertions + " " + message);
    }
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { FakeServer(); return 0; }
        try
        {
            await TestAsync(args[0]);
            if (args.Length > 1) await RealServerAsync(args[0], args[1]);
            Console.WriteLine("PASS " + assertions + " bridge/DTO assertions");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    private static async Task TestAsync(string scratch)
    {
        var minimal = Protocol.Symbol(JObject.Parse("{ 'id':'revision:opaque', 'name':'AThing', 'kind':'class', 'file':'C:/x.h', 'line':2, 'bases':[], 'specifiers':[] }"), null);
        Check(minimal.CoreId == "revision:opaque" && minimal.Id == 0, "Opaque IDs must not become SQLite IDs");
        Check(minimal.Signature == null && minimal.OwnerClass == null && minimal.Comment == null, "Absent metadata stays absent");
        var rich = Protocol.Symbol(JObject.Parse("{ 'id':'2', 'name':'Health', 'kind':'property', 'file':'C:/x.h', 'line':3, 'owner':'AThing', 'documentation':'Doc', 'signature':'int Health;', 'bases':[], 'specifiers':[{'key':'Category','value':'Stats'}] }"), null);
        Check(rich.OwnerClass == "AThing" && rich.Specifiers == "Category=Stats", "Rich metadata maps conservatively");
        Check(Protocol.Symbol(JObject.Parse("{'kind':'futureKind'}"), null) == null, "Unknown kinds do not fabricate a type");
        var config = new CoreConfiguration { Executable = Assembly.GetExecutingAssembly().Location, ProjectRoot = scratch,
            AIEnabled = false, AIEndpoint = "http://127.0.0.1:11434", AIModel = "not-installed" };
        var uri = Protocol.FileUri(Path.Combine(scratch, "Space # emoji.h"));
        Check(uri.Contains("%23") && uri.Contains("%20"), "File URIs escape names");
        var client = new CoreClientService();
        try
        {
            await client.StartAsync(config, CancellationToken.None);
            Check(client.IsReady, "Initialization negotiates v1");
            var a = client.SynchronizeAsync(uri, 100, () => "first");
            var b = client.SynchronizeAsync(uri, 101, () => "unicode " + char.ConvertFromUtf32(0x1f427));
            var c = client.SynchronizeAsync(uri, 102, () => "", true);
            var d = client.SynchronizeAsync(uri, 99, () => "STALE");
            await Task.WhenAll(a,b,c,d);
            var state = await client.RequestAsync("test/state", new { }, CancellationToken.None);
            Check((string)state["text"] == "", "FULL empty buffer is delivered; stale versions ignored");
            Check(string.Join(",", state["events"].Values<string>()) == "initialize,initialized,textDocument/didOpen,textDocument/didChange,textDocument/didChange,textDocument/didSave,test/state", "Wire ordering is FIFO");
            Check((string)state["adapter"] == "vs", "Supported adapter initialization option");
            var lastId = (long)state["requestId"];
            using (var cancel = new CancellationTokenSource(150))
            {
                try { await client.RequestAsync("test/wait", new { }, cancel.Token); throw new Exception("Cancellation did not fire"); }
                catch (OperationCanceledException) { assertions++; }
            }
            // Cancellation notifications and completion continuations may run on different pool threads.
            await Task.Delay(100);
            state = await client.RequestAsync("test/state", new { }, CancellationToken.None);
            Check(state["events"].Values<string>().Contains("$/cancelRequest"), "Cancellation uses LSP notification");
            await client.CloseDocumentAsync(uri);
            state = await client.RequestAsync("test/state", new { }, CancellationToken.None);
            Check(state["events"].Values<string>().Contains("textDocument/didClose"), "Close delivered");
            await client.StopAsync();
            Check(!client.IsReady, "Shutdown disables requests");
            client = new CoreClientService();
            await client.StartAsync(config, CancellationToken.None);
            state = await client.RequestAsync("test/state", new { }, CancellationToken.None);
            Check((long)state["requestId"] > lastId, "Request IDs do not repeat after restart");
            bool failed = false;
            client.Failed += () => failed = true;
            try { await client.RequestAsync("test/crash", new { }, CancellationToken.None); } catch { }
            await Task.Delay(100);
            Check(failed && !client.IsReady, "Process failure is explicit");
        }
        finally { await client.StopAsync(); }
        client = new CoreClientService();
        try
        {
            config.ProjectRoot = Path.Combine(scratch, "unsupported");
            try { await client.StartAsync(config, CancellationToken.None); throw new Exception("Future protocol accepted"); }
            catch (InvalidOperationException) { assertions++; }
        }
        finally { await client.StopAsync(); }
    }
    private static async Task RealServerAsync(string scratch, string executable)
    {
        var root = Path.Combine(scratch, "vs-core-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "Example.h");
        var source = "UCLASS(Blueprintable)" + Environment.NewLine + "class ABridgeTest : public AActor { GENERATED_BODY() };";
        File.WriteAllText(file, source);
        var core = new CoreClientService();
        try
        {
            await core.StartAsync(new CoreConfiguration { Executable = executable, ProjectRoot = root,
                AIEndpoint = "http://127.0.0.1:11434", AIModel = "not-installed" }, CancellationToken.None);
            var uri = Protocol.FileUri(file);
            await core.SynchronizeAsync(uri, 300, () => source);
            var symbols = await core.RequestAsync("penguin/symbols", new { query = "ABridgeTest", limit = 20 }, CancellationToken.None);
            Check(symbols is JArray array && array.Count == 1, "Real Core sees unsaved overlay through C# bridge");
            var symbol = Protocol.Symbol(symbols[0], null);
            Check(symbol.Name == "ABridgeTest" && symbol.CoreId != null, "Real Core symbol DTO maps");
            var hover = await core.RequestAsync("textDocument/hover", new { textDocument = new { uri }, position = new { line = 1, character = 10 } }, CancellationToken.None);
            Check(hover != null && hover.Type != JTokenType.Null, "Real Core hover responds");
            await core.SynchronizeAsync(uri, 301, () => "");
            symbols = await core.RequestAsync("penguin/symbols", new { query = "ABridgeTest", limit = 20 }, CancellationToken.None);
            Check(((JArray)symbols).Count == 0, "Real Core empty overlay suppresses disk symbols");
            await core.CloseDocumentAsync(uri);
            var status = await core.RequestAsync("penguin/status", new { }, CancellationToken.None);
            Check((int?)status["protocolVersion"] == 1, "Real Core status contract v1");
        }
        finally { await core.StopAsync(); }
    }

    private static void FakeServer()
    {
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var events = new List<string>();
        string text = null, adapter = null;
        while (true)
        {
            var header = new List<byte>();
            while (true)
            {
                int value = input.ReadByte();
                if (value < 0) return;
                header.Add((byte)value);
                int n = header.Count;
                if (n >= 4 && header[n-4] == 13 && header[n-3] == 10 && header[n-2] == 13 && header[n-1] == 10) break;
            }
            var lengthLine = Encoding.ASCII.GetString(header.ToArray()).Split((char)10).First(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            var length = int.Parse(lengthLine.Substring(lengthLine.IndexOf(':') + 1).Trim());
            var bytes = new byte[length];
            int offset = 0;
            while (offset < length) { var read = input.Read(bytes, offset, length-offset); if (read == 0) return; offset += read; }
            var message = JObject.Parse(Encoding.UTF8.GetString(bytes));
            var method = (string)message["method"];
            events.Add(method);
            if (method == "exit" || method == "test/crash") return;
            if (method == "textDocument/didOpen") text = (string)message["params"]["textDocument"]["text"];
            if (method == "textDocument/didChange") text = (string)message["params"]["contentChanges"][0]["text"];
            if (method == "test/wait") continue;
            if (message["id"] == null) continue;
            object result = null;
            if (method == "initialize")
            {
                adapter = (string)message["params"]["initializationOptions"]["penguin"]["adapter"];
                var version = ((string)message["params"]["rootUri"]).Contains("unsupported") ? 9 : 1;
                result = new { capabilities = new { experimental = new { penguin = new { protocolVersion = version } } } };
            }
            if (method == "test/state") result = new { text, adapter, events = events.ToArray(), requestId = (long)message["id"] };
            var response = new JObject { ["jsonrpc"] = "2.0", ["id"] = message["id"], ["result"] = result == null ? JValue.CreateNull() : JToken.FromObject(result) };
            var body = Encoding.UTF8.GetBytes(response.ToString(Newtonsoft.Json.Formatting.None));
            var prefix = Encoding.ASCII.GetBytes("Content-Length: " + body.Length + (char)13 + (char)10 + (char)13 + (char)10);
            output.Write(prefix, 0, prefix.Length); output.Write(body, 0, body.Length); output.Flush();
        }
    }
}
