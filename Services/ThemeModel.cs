using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Media;
using Newtonsoft.Json;

namespace PenguinExtention.Services
{
    /// <summary>One row of the Penguin Color Theme window: a kind of code and the classification types it colors.</summary>
    internal sealed class ThemeEntry
    {
        public string Id { get; }
        public string DisplayName { get; }
        public string Group { get; }
        /// <summary>VS built-in, VC++ (cpp*) and Penguin classification type names this entry drives.</summary>
        public string[] Classifications { get; }
        public Color Dark { get; }
        public Color Light { get; }
        public bool Bold { get; }
        public bool Italic { get; }

        public ThemeEntry(string id, string displayName, string group, uint dark, uint light, string[] classifications,
            bool bold = false, bool italic = false)
        {
            Id = id;
            DisplayName = displayName;
            Group = group;
            Dark = ThemeColors.FromRgb(dark);
            Light = ThemeColors.FromRgb(light);
            Classifications = classifications;
            Bold = bold;
            Italic = italic;
        }
    }

    /// <summary>
    /// The Penguin Dark / Penguin Light default theme (SYNTAX_COLOR_PLAN.md). cpp* names were verified in
    /// Microsoft.VisualStudio.VC.dll; Penguin names come from Editor/UnrealClassifier.cs.
    /// </summary>
    internal static class ThemeCatalog
    {
        public const string Language = "Language", Types = "Types", Members = "Members", Unreal = "Unreal";

        public static readonly ThemeEntry[] Entries =
        {
            new ThemeEntry("keyword", "Keyword (void, class, const)", Language, 0x569CD6, 0x0000FF, new[] { "keyword" }),
            new ThemeEntry("control", "Control keyword (if, for, return)", Language, 0xD8A0DF, 0x8F08C4, new[] { "cppControlKeyword" }),
            new ThemeEntry("preprocessor", "Preprocessor (#include)", Language, 0x9B9B9B, 0x808080, new[] { "preprocessor keyword" }),
            new ThemeEntry("string", "String", Language, 0xCE9178, 0xA31515, new[] { "string", "cppStringDelimiterCharacter" }),
            new ThemeEntry("escape", "Escape character (\\n)", Language, 0xFFD68F, 0xEE0000, new[] { "cppStringEscapeCharacter" }),
            new ThemeEntry("number", "Number", Language, 0xB5CEA8, 0x098658, new[] { "number", "cppUserDefinedLiteralNumber" }),
            new ThemeEntry("comment", "Comment", Language, 0x6A9955, 0x008000, new[] { "comment" }, italic: true),
            new ThemeEntry("operator", "Operator", Language, 0xD4D4D4, 0x000000, new[] { "operator", "cppOperator", "cppMemberOperator" }),
            new ThemeEntry("namespace", "Namespace", Language, 0xC8C8C8, 0x333333, new[] { "cppNamespace" }),

            new ThemeEntry("class", "Class", Types, 0x4EC9B0, 0x267F99, new[] { "cppRefType", "cppType", "Penguin Unreal Type" }),
            new ThemeEntry("struct", "Struct", Types, 0x86C691, 0x2E7D32, new[] { "cppValueType", "Penguin Unreal Struct" }),
            new ThemeEntry("enum", "Enum", Types, 0xEE9D5B, 0xB35A00, new[] { "Penguin Unreal Enum" }),
            new ThemeEntry("template", "Template", Types, 0x4EC9B0, 0x267F99, new[] { "cppClassTemplate", "cppGenericType" }),
            new ThemeEntry("delegate", "Delegate / event", Types, 0xD16D9E, 0xA4286A, new[] { "cppEvent", "Penguin Unreal Delegate" }),

            new ThemeEntry("function", "Function", Members, 0xDCDCAA, 0x795E26, new[] { "cppFunction", "cppFunctionTemplate", "Penguin Function" }),
            new ThemeEntry("method", "Member function", Members, 0xDCDCAA, 0x795E26, new[] { "cppMemberFunction", "cppStaticMemberFunction" }),
            new ThemeEntry("property", "Property / field", Members, 0x9CDCFE, 0x001080, new[] { "cppMemberField", "cppStaticMemberField", "cppProperty", "Penguin Property" }),
            new ThemeEntry("local", "Local variable", Members, 0xD4D4D4, 0x1F1F1F, new[] { "cppLocalVariable" }),
            new ThemeEntry("parameter", "Parameter", Members, 0x9A9A9A, 0x808080, new[] { "cppParameter" }),
            new ThemeEntry("global", "Global variable", Members, 0x9CDCFE, 0x001080, new[] { "cppGlobalVariable" }),
            new ThemeEntry("enumValue", "Enum value", Members, 0xB8D7A3, 0x4F7A28, new[] { "cppEnumerator", "Penguin Enum Value" }),
            new ThemeEntry("label", "Label", Members, 0xC8C8C8, 0x000000, new[] { "cppLabel" }),

            new ThemeEntry("ueMacro", "UE macro (UPROPERTY, UE_LOG)", Unreal, 0xC586C0, 0xAF00DB, new[] { "Penguin Unreal Macro" }, bold: true),
            new ThemeEntry("macro", "Other macro", Unreal, 0xBEB7FF, 0x8A1BFF, new[] { "cppMacro" }),
            new ThemeEntry("specifier", "Specifier (EditAnywhere)", Unreal, 0x569CD6, 0x0451A5, new[] { "Penguin Macro Specifier" }),
            new ThemeEntry("metaKey", "Meta key (meta=(ClampMin))", Unreal, 0xD7BA7D, 0x8B6914, new[] { "Penguin Meta Key" }),
        };

        public static readonly string[] Groups = { Language, Types, Members, Unreal };

        public static ThemeEntry Find(string id) => Entries.First(e => e.Id == id);
    }

    /// <summary>A user override for one entry; null fields fall back to the Penguin default.</summary>
    internal sealed class ThemeStyle
    {
        public string Dark { get; set; }
        public string Light { get; set; }
        public bool? Bold { get; set; }
        public bool? Italic { get; set; }

        [JsonIgnore]
        public bool IsEmpty => Dark == null && Light == null && Bold == null && Italic == null;
    }

    /// <summary>The effective look of an entry for one VS theme (dark or light).</summary>
    internal struct ResolvedStyle
    {
        public Color Color;
        public bool Bold;
        public bool Italic;
    }

    /// <summary>
    /// Per-user theme settings in %LOCALAPPDATA%\PenguinExtension\theme.json. Only overrides are stored, so improved
    /// defaults reach every entry the user has not changed. The same JSON is used for Export/Import.
    /// </summary>
    internal sealed class ThemeSettings
    {
        public int Version { get; set; } = 1;
        /// <summary>Penguin color mode: on = the whole C++ editor uses the theme; off = stock VS colors.</summary>
        public bool Enabled { get; set; } = true;
        public Dictionary<string, ThemeStyle> Overrides { get; set; } = new Dictionary<string, ThemeStyle>();

        public static string DefaultPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PenguinExtension", "theme.json");

        public ResolvedStyle Resolve(ThemeEntry entry, bool dark)
        {
            Overrides.TryGetValue(entry.Id, out var style);
            var hex = dark ? style?.Dark : style?.Light;
            return new ResolvedStyle
            {
                Color = ThemeColors.TryParse(hex, out var color) ? color : dark ? entry.Dark : entry.Light,
                Bold = style?.Bold ?? entry.Bold,
                Italic = style?.Italic ?? entry.Italic,
            };
        }

        public bool IsCustomized(ThemeEntry entry) =>
            Overrides.TryGetValue(entry.Id, out var style) && !style.IsEmpty;

        /// <summary>Changes one entry; values equal to the default are dropped so the entry keeps following defaults.</summary>
        public void Set(ThemeEntry entry, bool dark, Color? color = null, bool? bold = null, bool? italic = null)
        {
            if (!Overrides.TryGetValue(entry.Id, out var style)) Overrides[entry.Id] = style = new ThemeStyle();
            if (color.HasValue)
            {
                var value = color.Value == (dark ? entry.Dark : entry.Light) ? null : ThemeColors.ToHex(color.Value);
                if (dark) style.Dark = value; else style.Light = value;
            }
            if (bold.HasValue) style.Bold = bold.Value == entry.Bold ? (bool?)null : bold.Value;
            if (italic.HasValue) style.Italic = italic.Value == entry.Italic ? (bool?)null : italic.Value;
            if (style.IsEmpty) Overrides.Remove(entry.Id);
        }

        public void Reset(ThemeEntry entry) => Overrides.Remove(entry.Id);

        public static ThemeSettings Load(string path)
        {
            if (!File.Exists(path)) return new ThemeSettings();
            var settings = JsonConvert.DeserializeObject<ThemeSettings>(File.ReadAllText(path)) ?? new ThemeSettings();
            if (settings.Overrides == null) settings.Overrides = new Dictionary<string, ThemeStyle>();
            // Drop entries this version does not know (e.g. a theme exported by a newer Penguin).
            foreach (var id in settings.Overrides.Keys.ToList())
                if (!ThemeCatalog.Entries.Any(e => e.Id == id)) settings.Overrides.Remove(id);
            return settings;
        }

        public void Save(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(this, Formatting.Indented));
            if (File.Exists(path)) File.Replace(temp, path, null);
            else File.Move(temp, path);
        }

        public ThemeSettings Clone() => JsonConvert.DeserializeObject<ThemeSettings>(JsonConvert.SerializeObject(this));
    }

    internal static class ThemeColors
    {
        public static Color FromRgb(uint rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

        public static string ToHex(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

        /// <summary>Parses #RRGGBB or RRGGBB.</summary>
        public static bool TryParse(string hex, out Color color)
        {
            color = default;
            if (string.IsNullOrWhiteSpace(hex)) return false;
            hex = hex.Trim().TrimStart('#');
            if (hex.Length != 6 || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return false;
            color = FromRgb(rgb);
            return true;
        }

        /// <summary>True for a dark background (relative luminance below 0.5).</summary>
        public static bool IsDark(Color background) =>
            (0.2126 * background.R + 0.7152 * background.G + 0.0722 * background.B) / 255.0 < 0.5;
    }
}
