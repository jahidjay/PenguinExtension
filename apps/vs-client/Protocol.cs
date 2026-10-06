using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using PenguinExtention.Models;

namespace PenguinExtention.Core
{
    internal static class Protocol
    {
        public static void ValidateVersion(JToken result)
        {
            if ((int?)result?["capabilities"]?["experimental"]?["penguin"]?["protocolVersion"] != 1)
                throw new InvalidOperationException("Unsupported Penguin protocol. Install a matching protocol v1 Core binary.");
        }
        public static string FileUri(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Document path is empty.");
            return new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri;
        }
        public static UnrealSymbol Symbol(JToken token, string engineRoot)
        {
            if (token == null || token.Type == JTokenType.Null) return null;
            UnrealSymbolKind kind;
            if (!Enum.TryParse((string)token["kind"], true, out kind)) return null;
            var file = (string)token["file"];
            var specifiers = token["specifiers"] as JArray;
            return new UnrealSymbol
            {
                CoreId = (string)token["id"], Name = (string)token["name"], Kind = kind,
                FilePath = file, LineNumber = Math.Max(1, (int?)token["line"] ?? 1),
                MacroName = (string)token["macroName"], ReturnType = (string)token["typeName"],
                Signature = (string)token["signature"], Comment = (string)token["documentation"],
                OwnerClass = (string)token["owner"], FullyQualifiedName = (string)token["qualifiedName"],
                Bases = (token["bases"] as JArray)?.Values<string>().ToArray() ?? new string[0],
                Specifiers = specifiers == null ? null : string.Join(", ", specifiers.Select(s =>
                    (string)s["key"] + (s["value"] == null || s["value"].Type == JTokenType.Null ? "" : "=" + (string)s["value"]))),
                IsEngineSymbol = IsWithin(file, engineRoot)
            };
        }
        public static bool IsWithin(string file, string root) => !string.IsNullOrEmpty(file) &&
            !string.IsNullOrEmpty(root) && file.StartsWith(root.TrimEnd((char)92, '/') + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        public static string PlainText(JToken content)
        {
            if (content == null || content.Type == JTokenType.Null) return null;
            if (content.Type == JTokenType.String) return (string)content;
            if (content is JArray parts) return string.Join("\n\n", parts.Select(PlainText));
            return (string)content["value"];
        }
        public static bool Terminal(string state) => state == "succeeded" || state == "failed" || state == "cancelled";
    }
    internal sealed class CoreConfiguration
    {
        public string Executable { get; set; }
        public string ProjectRoot { get; set; }
        public string EngineRoot { get; set; }
        public bool AIEnabled { get; set; }
        public string AIEndpoint { get; set; }
        public string AIModel { get; set; }
    }
}
