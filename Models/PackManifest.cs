using Newtonsoft.Json;

namespace PenguinExtention.Models
{
    /// <summary>Contents of a pack's pack.json. See docs/PACKS_PLAN.md for the format.</summary>
    public sealed class PackManifest
    {
        [JsonProperty("id")] public string Id { get; set; }
        [JsonProperty("name")] public string Name { get; set; }
        [JsonProperty("version")] public string Version { get; set; }
        [JsonProperty("description")] public string Description { get; set; }
        [JsonProperty("languages")] public string[] Languages { get; set; } = new string[0];
        [JsonProperty("detect")] public PackDetect Detect { get; set; } = new PackDetect();
        /// <summary>Folders inside the pack whose headers are indexed (imported libraries).</summary>
        [JsonProperty("includeRoots")] public string[] IncludeRoots { get; set; } = new string[0];
        /// <summary>File name of the specifiers JSON, relative to the pack folder.</summary>
        [JsonProperty("specifiers")] public string Specifiers { get; set; }
        /// <summary>File name of the snippets JSON, relative to the pack folder.</summary>
        [JsonProperty("snippets")] public string Snippets { get; set; }
        /// <summary>File name of the callbacks JSON (engine-called methods such as Unity's Update), relative to the pack folder.</summary>
        [JsonProperty("callbacks")] public string Callbacks { get; set; }
        /// <summary>Online docs page template; {version} = engine docs version, {page} = callback docPage.</summary>
        [JsonProperty("docsUrl")] public string DocsUrl { get; set; }
        /// <summary>Offline docs template tried first; {editor} = installed editor folder, {page} = docPage.</summary>
        [JsonProperty("localDocs")] public string LocalDocs { get; set; }
        /// <summary>MCP servers this pack suggests (shown in the MCP window while the pack is on).</summary>
        [JsonProperty("mcp")] public McpServerConfig[] Mcp { get; set; } = new McpServerConfig[0];
    }

    /// <summary>One MCP server: stdio (command/args/env) or HTTP (url/headers). Same keys as .mcp.json / VS Code mcp.json.</summary>
    public sealed class McpServerConfig
    {
        [JsonProperty("name")] public string Name { get; set; }
        /// <summary>"stdio", "http" (streamable HTTP) or "sse" (legacy HTTP+SSE). Empty: stdio if command is set, else http.</summary>
        [JsonProperty("type")] public string Type { get; set; }
        [JsonProperty("command")] public string Command { get; set; }
        [JsonProperty("args")] public string[] Args { get; set; } = new string[0];
        [JsonProperty("env")] public System.Collections.Generic.Dictionary<string, string> Env { get; set; }
        [JsonProperty("cwd")] public string Cwd { get; set; }
        [JsonProperty("url")] public string Url { get; set; }
        [JsonProperty("headers")] public System.Collections.Generic.Dictionary<string, string> Headers { get; set; }
        /// <summary>Shown when connecting fails, e.g. how to start the server inside the editor.</summary>
        [JsonProperty("hint")] public string Hint { get; set; }
        /// <summary>Where the entry came from (config file path or "pack unity"); not read from JSON.</summary>
        [JsonIgnore] public string Source { get; set; }

        [JsonIgnore] public bool IsStdio => string.Equals(Type, "stdio", System.StringComparison.OrdinalIgnoreCase) || (string.IsNullOrEmpty(Type) && !string.IsNullOrEmpty(Command));

        /// <summary>One line for the UI: the command line or the URL.</summary>
        [JsonIgnore] public string Endpoint => IsStdio ? Command + (Args == null || Args.Length == 0 ? "" : " " + string.Join(" ", Args)) : Url;
    }

    public sealed class PackDetect
    {
        /// <summary>File patterns looked up in the solution directory and up to 4 parents (e.g. "*.uproject").</summary>
        [JsonProperty("files")] public string[] Files { get; set; } = new string[0];
        /// <summary>Headers (relative to an include directory) that mark that directory as this pack's (e.g. "SDL3/SDL.h").</summary>
        [JsonProperty("includeHeaders")] public string[] IncludeHeaders { get; set; } = new string[0];
    }
}
