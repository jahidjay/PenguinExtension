using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Copies packs into a pack location (user-global or the solution's .penguin\packs): a pack folder, a pack .zip,
    /// or a plain library folder that gets wrapped in a new pack (its headers become the pack's includeRoots).
    /// File I/O only: call off the UI thread.
    /// </summary>
    internal static class PackImporter
    {
        private static readonly Regex IdPattern = new Regex(@"^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

        internal sealed class PackExistsException : Exception
        {
            public string Id { get; }
            public PackExistsException(string id, string dir) : base("A pack \"" + id + "\" already exists in " + dir) { Id = id; }
        }

        /// <summary>Imports a folder holding pack.json (at its root or in its only subfolder). Returns the pack id.</summary>
        public static string ImportFolder(string source, string targetRoot, bool overwrite)
        {
            var packDir = FindPackDir(source) ?? throw new InvalidDataException("No pack.json found in " + source + " (or its subfolders).");
            var id = ReadId(Path.Combine(packDir, "pack.json"));
            var dest = Path.Combine(targetRoot, id);
            CheckNotNested(packDir, dest);
            PrepareDestination(dest, id, overwrite);
            CopyDirectory(packDir, dest);
            return id;
        }

        /// <summary>Imports a .zip containing a pack (pack.json at the root or in one top folder). Returns the pack id.</summary>
        public static string ImportZip(string zipPath, string targetRoot, bool overwrite)
        {
            var temp = Path.Combine(Path.GetTempPath(), "PenguinPackImport", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                var tempFull = Path.GetFullPath(temp) + Path.DirectorySeparatorChar;
                using (var zip = ZipFile.OpenRead(zipPath))
                {
                    foreach (var entry in zip.Entries)
                    {
                        var target = Path.GetFullPath(Path.Combine(temp, entry.FullName));
                        if (!target.StartsWith(tempFull, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Zip entry escapes the pack folder: " + entry.FullName);
                        if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\")) { Directory.CreateDirectory(target); continue; }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        entry.ExtractToFile(target, true);
                    }
                }
                return ImportFolder(temp, targetRoot, overwrite);
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException) { }
            }
        }

        /// <summary>
        /// Wraps a library folder (e.g. a downloaded SDL3_image) in a new pack: copies it to &lt;target&gt;\&lt;id&gt;\lib and
        /// writes pack.json whose includeRoots point at its include folder. Returns the pack id.
        /// </summary>
        public static string ImportLibrary(string source, string targetRoot, string name, bool overwrite)
        {
            if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
            name = string.IsNullOrWhiteSpace(name) ? Path.GetFileName(source.TrimEnd('\\', '/')) : name.Trim();
            var id = Slug(name);
            var dest = Path.Combine(targetRoot, id);
            CheckNotNested(source, dest);
            PrepareDestination(dest, id, overwrite);
            var lib = Path.Combine(dest, "lib");
            CopyDirectory(source, lib);
            var include = Directory.Exists(Path.Combine(lib, "include")) ? "lib/include" : "lib";
            var manifest = new JObject
            {
                ["id"] = id,
                ["name"] = name,
                ["version"] = "1.0.0",
                ["description"] = "Library imported from " + source,
                ["languages"] = new JArray("cpp"),
                ["includeRoots"] = new JArray(include)
            };
            File.WriteAllText(Path.Combine(dest, "pack.json"), manifest.ToString(Formatting.Indented));
            return id;
        }

        /// <summary>Deletes a user or solution pack folder.</summary>
        public static void Remove(PackInfo pack)
        {
            if (pack.Location == PackLocation.BuiltIn) throw new InvalidOperationException("Built-in packs ship with the extension and cannot be removed; uncheck it instead.");
            Directory.Delete(pack.Directory, true);
        }

        internal static string Slug(string name)
        {
            var slug = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
            return slug.Length == 0 ? "library" : slug;
        }

        private static string FindPackDir(string source)
        {
            if (File.Exists(Path.Combine(source, "pack.json"))) return source;
            var subs = Directory.GetDirectories(source).Where(d => File.Exists(Path.Combine(d, "pack.json"))).ToList();
            return subs.Count == 1 ? subs[0] : null;
        }

        private static string ReadId(string manifestPath)
        {
            string id;
            try { id = (string)JObject.Parse(File.ReadAllText(manifestPath))["id"]; }
            catch (JsonException ex) { throw new InvalidDataException("pack.json is not valid JSON: " + ex.Message); }
            if (string.IsNullOrEmpty(id) || !IdPattern.IsMatch(id))
                throw new InvalidDataException("pack.json \"id\" is missing or invalid (lowercase letters, digits, dashes): " + (id ?? "(none)"));
            return id;
        }

        /// <summary>Refuses to import a folder into itself or into one of its own subfolders.</summary>
        private static void CheckNotNested(string source, string dest)
        {
            string Full(string p) => Path.GetFullPath(p).TrimEnd('\\', '/') + "\\";
            var s = Full(source); var d = Full(dest);
            if (d.StartsWith(s, StringComparison.OrdinalIgnoreCase) || s.StartsWith(d, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Cannot import " + source + " into " + dest + ": one folder contains the other.");
        }

        private static void PrepareDestination(string dest, string id, bool overwrite)
        {
            if (Directory.Exists(dest))
            {
                if (!overwrite) throw new PackExistsException(id, Path.GetDirectoryName(dest));
                Directory.Delete(dest, true);
            }
            Directory.CreateDirectory(dest);
        }

        private static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (var file in Directory.GetFiles(from))
                File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
            foreach (var dir in Directory.GetDirectories(from))
            {
                var name = Path.GetFileName(dir);
                if (name == ".git" || name == ".vs") continue;
                CopyDirectory(dir, Path.Combine(to, name));
            }
        }
    }
}
