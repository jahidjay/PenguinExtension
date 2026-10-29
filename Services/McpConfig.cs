using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PenguinExtention.Models;

namespace PenguinExtention.Services
{
    /// <summary>
    /// MCP server list for the open solution, read from the files Visual Studio, VS Code and Claude use:
    /// &lt;solution&gt;\.mcp.json, &lt;solution&gt;\.vs\mcp.json, &lt;solution&gt;\.vscode\mcp.json, %USERPROFILE%\.mcp.json —
    /// "servers" or "mcpServers" — plus servers suggested by enabled packs. First entry of a name wins.
    /// </summary>
    internal static class McpConfig
    {
        public static string SolutionConfigPath(string solutionDir) => Path.Combine(solutionDir, ".mcp.json");

        public static IEnumerable<string> ConfigFiles(string solutionDir)
        {
            if (!string.IsNullOrEmpty(solutionDir))
            {
                yield return SolutionConfigPath(solutionDir);
                yield return Path.Combine(solutionDir, ".vs", "mcp.json");
                yield return Path.Combine(solutionDir, ".vscode", "mcp.json");
            }
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mcp.json");
        }

        /// <summary>All servers; <paramref name="errors"/> collects unreadable files. File I/O: call off the UI thread.</summary>
        public static List<McpServerConfig> Load(string solutionDir, IEnumerable<McpServerConfig> packSuggestions, List<string> errors)
        {
            var result = new List<McpServerConfig>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in ConfigFiles(solutionDir).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(file)) continue;
                try
                {
                    var root = JObject.Parse(File.ReadAllText(file));
                    var servers = (root["servers"] ?? root["mcpServers"]) as JObject;
                    if (servers == null) continue;
                    foreach (var prop in servers.Properties())
                    {
                        if (!(prop.Value is JObject obj) || !names.Add(prop.Name)) continue;
                        var cfg = obj.ToObject<McpServerConfig>(JsonSerializer.CreateDefault());
                        cfg.Name = prop.Name;
                        cfg.Source = file;
                        Expand(cfg, solutionDir);
                        result.Add(cfg);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException)
                {
                    errors?.Add(file + ": " + ex.Message);
                }
            }
            foreach (var s in packSuggestions ?? Enumerable.Empty<McpServerConfig>())
                if (names.Add(s.Name)) { Expand(s, solutionDir); result.Add(s); }
            return result;
        }

        /// <summary>Creates a starter .mcp.json in the solution folder (when missing) and returns its path.</summary>
        public static string EnsureSolutionConfig(string solutionDir)
        {
            var path = SolutionConfigPath(solutionDir);
            if (File.Exists(path)) return path;
            const string template =
@"{
  // MCP servers for this solution — read by Penguin (Alt+U, Shift+M), Visual Studio and Claude Code.
  // stdio: { ""type"": ""stdio"", ""command"": ""uvx"", ""args"": [""some-mcp-server""], ""env"": { } }
  // HTTP:  { ""type"": ""http"", ""url"": ""http://localhost:8080/mcp"" }
  // ${workspaceFolder} is replaced with the solution folder.
  ""servers"": {
  }
}
";
            File.WriteAllText(path, template);
            return path;
        }

        private static void Expand(McpServerConfig cfg, string solutionDir)
        {
            string X(string v)
            {
                if (string.IsNullOrEmpty(v)) return v;
                v = v.Replace("${workspaceFolder}", solutionDir ?? "").Replace("${userHome}", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
                v = Regex.Replace(v, @"\$\{env:(\w+)\}", m => Environment.GetEnvironmentVariable(m.Groups[1].Value) ?? "");
                return Environment.ExpandEnvironmentVariables(v);
            }
            cfg.Command = X(cfg.Command);
            cfg.Url = X(cfg.Url);
            cfg.Cwd = X(cfg.Cwd);
            cfg.Args = (cfg.Args ?? new string[0]).Select(X).ToArray();
            if (cfg.Env != null) cfg.Env = cfg.Env.ToDictionary(k => k.Key, k => X(k.Value));
            if (cfg.Headers != null) cfg.Headers = cfg.Headers.ToDictionary(k => k.Key, k => X(k.Value));
        }
    }
}
