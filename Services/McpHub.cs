using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using PenguinExtention.Models;

namespace PenguinExtention.Services
{
    /// <summary>Open MCP connections by server name. They outlive the MCP window and close with the solution/package.</summary>
    internal static class McpHub
    {
        private static readonly ConcurrentDictionary<string, McpClient> Clients = new ConcurrentDictionary<string, McpClient>(StringComparer.OrdinalIgnoreCase);

        /// <summary>(server name, line) for server logs and connection changes; also written to Output → PenguinExtension.</summary>
        public static event Action<string, string> Log;
        /// <summary>A connection opened or closed.</summary>
        public static event Action Changed;

        public static McpClient Get(string name) =>
            name != null && Clients.TryGetValue(name, out var c) && c.IsConnected ? c : null;

        public static async Task<McpClient> ConnectAsync(McpServerConfig config, string rootFolder, CancellationToken ct)
        {
            Disconnect(config.Name);
            void OnLog(string line) => Write(config.Name, line);
            Write(config.Name, "connecting to " + config.Endpoint + " (" + (config.IsStdio ? "stdio" : config.Type ?? "http") + ")");
            var client = await McpClient.ConnectAsync(config, rootFolder, OnLog, ct).ConfigureAwait(false);
            client.Closed += () =>
            {
                if (Clients.TryGetValue(config.Name, out var current) && current == client) Clients.TryRemove(config.Name, out _);
                Write(config.Name, "disconnected");
                Changed?.Invoke();
            };
            Clients[config.Name] = client;
            Write(config.Name, "connected: " + ((string)client.ServerInfo?["name"] ?? "server") + " " + (string)client.ServerInfo?["version"] +
                ", protocol " + client.NegotiatedVersion);
            Changed?.Invoke();
            return client;
        }

        public static void Disconnect(string name)
        {
            if (name != null && Clients.TryRemove(name, out var old))
            {
                old.Dispose();
                Changed?.Invoke();
            }
        }

        public static void DisposeAll()
        {
            foreach (var name in Clients.Keys) Disconnect(name);
        }

        private static void Write(string server, string line)
        {
            PenguinLog.Info("MCP " + server, line);
            try { Log?.Invoke(server, line); } catch (Exception ex) { PenguinLog.Error("MCP", ex); }
        }
    }
}
