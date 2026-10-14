using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using PenguinExtention.Services;
using NameCategory = PenguinExtention.Services.CacheService.NameCategory;

namespace PenguinExtention.Editor
{
    /// <summary>Classification types — user-adjustable in Tools > Options > Fonts and Colors.</summary>
    internal static class UnrealClassificationTypes
    {
        // The first four names predate v1.7; keep them so user color customizations survive.
        public const string Macro = "Penguin Unreal Macro";
        public const string Type = "Penguin Unreal Type";
        public const string Function = "Penguin Function";
        public const string EnumValue = "Penguin Enum Value";
        public const string Struct = "Penguin Unreal Struct";
        public const string Enum = "Penguin Unreal Enum";
        public const string Delegate = "Penguin Unreal Delegate";
        public const string Property = "Penguin Property";
        public const string Specifier = "Penguin Macro Specifier";
        public const string MetaKey = "Penguin Meta Key";

        [Export, Name(Macro), BaseDefinition(PredefinedClassificationTypeNames.Keyword)]
        internal static ClassificationTypeDefinition MacroType = null;

        [Export, Name(Type), BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition TypeType = null;

        [Export, Name(Function), BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition FunctionType = null;

        [Export, Name(EnumValue), BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition EnumValueType = null;

        [Export, Name(Struct), BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition StructType = null;

        [Export, Name(Enum), BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition EnumType = null;

        [Export, Name(Delegate), BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition DelegateType = null;

        [Export, Name(Property), BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition PropertyType = null;

        [Export, Name(Specifier), BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition SpecifierType = null;

        [Export, Name(MetaKey), BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition MetaKeyType = null;
    }

    /// <summary>
    /// Fonts and Colors default of a Penguin classification: the Penguin Dark color of its theme entry. While the
    /// Penguin color theme is on, <see cref="ThemeService"/> overrides these at runtime (and picks dark/light).
    /// </summary>
    internal abstract class PenguinFormat : ClassificationFormatDefinition
    {
        protected PenguinFormat(string displayName, string themeEntryId)
        {
            var entry = ThemeCatalog.Find(themeEntryId);
            DisplayName = displayName;
            ForegroundColor = entry.Dark;
            if (entry.Bold) IsBold = true;
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.Macro)]
    [Name(UnrealClassificationTypes.Macro), UserVisible(true), Order(After = Priority.High)]
    internal sealed class UnrealMacroFormat : PenguinFormat
    {
        public UnrealMacroFormat() : base(UnrealClassificationTypes.Macro, "ueMacro") { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.Type)]
    [Name(UnrealClassificationTypes.Type), UserVisible(true), Order(After = Priority.High)]
    internal sealed class UnrealTypeFormat : PenguinFormat
    {
        public UnrealTypeFormat() : base(UnrealClassificationTypes.Type, "class") { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.Function)]
    [Name(UnrealClassificationTypes.Function), UserVisible(true), Order(After = Priority.High)]
    internal sealed class PenguinFunctionFormat : PenguinFormat
    {
        public PenguinFunctionFormat() : base(UnrealClassificationTypes.Function, "function") { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.EnumValue)]
    [Name(UnrealClassificationTypes.EnumValue), UserVisible(true), Order(After = Priority.High)]
    internal sealed class PenguinEnumValueFormat : PenguinFormat
    {
        public PenguinEnumValueFormat() : base(UnrealClassificationTypes.EnumValue, "enumValue") { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.Struct)]
    [Name(UnrealClassificationTypes.Struct), UserVisible(true), Order(After = Priority.High)]
    internal sealed class PenguinStructFormat : PenguinFormat
    {
        public PenguinStructFormat() : base(UnrealClassificationTypes.Struct, "struct") { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.Enum)]
    [Name(UnrealClassificationTypes.Enum), UserVisible(true), Order(After = Priority.High)]
    internal sealed class PenguinEnumFormat : PenguinFormat
    {
        public PenguinEnumFormat() : base(UnrealClassificationTypes.Enum, "enum") { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.Delegate)]
    [Name(UnrealClassificationTypes.Delegate), UserVisible(true), Order(After = Priority.High)]
    internal sealed class PenguinDelegateFormat : PenguinFormat
    {
        public PenguinDelegateFormat() : base(UnrealClassificationTypes.Delegate, "delegate") { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.Property)]
    [Name(UnrealClassificationTypes.Property), UserVisible(true), Order(After = Priority.High)]
    internal sealed class PenguinPropertyFormat : PenguinFormat
    {
        public PenguinPropertyFormat() : base(UnrealClassificationTypes.Property, "property") { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.Specifier)]
    [Name(UnrealClassificationTypes.Specifier), UserVisible(true), Order(After = Priority.High)]
    internal sealed class PenguinSpecifierFormat : PenguinFormat
    {
        public PenguinSpecifierFormat() : base(UnrealClassificationTypes.Specifier, "specifier") { }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.MetaKey)]
    [Name(UnrealClassificationTypes.MetaKey), UserVisible(true), Order(After = Priority.High)]
    internal sealed class PenguinMetaKeyFormat : PenguinFormat
    {
        public PenguinMetaKeyFormat() : base(UnrealClassificationTypes.MetaKey, "metaKey") { }
    }

    [Export(typeof(IClassifierProvider))]
    [ContentType("C/C++")]
    internal sealed class UnrealClassifierProvider : IClassifierProvider
    {
        [Import] internal IClassificationTypeRegistryService Registry = null;

        public IClassifier GetClassifier(ITextBuffer buffer)
        {
            ThemeService.OnCppBufferOpened();
            return buffer.Properties.GetOrCreateSingletonProperty(() => new UnrealClassifier(buffer, Registry));
        }
    }

    /// <summary>What a classified span is; maps 1:1 onto a classification type.</summary>
    internal enum ColorKind { Macro, Class, Struct, Enum, EnumValue, Delegate, Function, Property, Specifier, MetaKey }

    /// <summary>
    /// Colors Unreal macros, indexed symbols (types, structs, enums, delegates, functions, enum values, properties)
    /// and reflection-macro specifiers / meta keys. One linear, comment- and string-aware scan per line (no regex,
    /// no AST); the name map is an immutable snapshot, never queried per span. Specifier lists that span several
    /// lines are handled by looking back a few lines for the opening macro.
    /// </summary>
    internal sealed class UnrealClassifier : IClassifier
    {
        private const int MaxLineLength = 4000; // minified/generated lines: not worth it
        private const int MacroLookback = 12;

        private static readonly HashSet<string> Macros = new HashSet<string>(StringComparer.Ordinal)
        {
            "UCLASS", "USTRUCT", "UENUM", "UFUNCTION", "UPROPERTY", "UINTERFACE", "UDELEGATE", "UMETA", "UPARAM",
            "GENERATED_BODY", "GENERATED_UCLASS_BODY", "GENERATED_USTRUCT_BODY", "GENERATED_IINTERFACE_BODY", "GENERATED_UINTERFACE_BODY",
            "UE_LOG", "UE_CLOG", "UE_LOGFMT", "TEXT", "LOCTEXT", "NSLOCTEXT", "check", "checkf", "checkSlow", "verify", "verifyf",
            "ensure", "ensureMsgf", "ensureAlways", "ensureAlwaysMsgf", "unimplemented", "checkNoEntry", "checkNoReentry",
            "DECLARE_LOG_CATEGORY_EXTERN", "DEFINE_LOG_CATEGORY", "DEFINE_LOG_CATEGORY_STATIC", "DECLARE_CYCLE_STAT",
            "SCOPE_CYCLE_COUNTER", "QUICK_SCOPE_CYCLE_COUNTER", "TRACE_CPUPROFILER_EVENT_SCOPE", "FORCEINLINE", "FORCENOINLINE",
            "DECLARE_DELEGATE", "DECLARE_MULTICAST_DELEGATE", "DECLARE_DYNAMIC_DELEGATE", "DECLARE_DYNAMIC_MULTICAST_DELEGATE",
            "DECLARE_EVENT", "IMPLEMENT_MODULE", "IMPLEMENT_PRIMARY_GAME_MODULE", "IMPLEMENT_GAME_MODULE", "ENUM_CLASS_FLAGS",
            "GET_MEMBER_NAME_CHECKED", "GET_FUNCTION_NAME_CHECKED", "PURE_VIRTUAL", "UE_DEPRECATED", "CORE_API", "ENGINE_API"
        };

        /// <summary>Reflection macros whose argument list is specifiers (keys) and meta=(…) entries.</summary>
        private static readonly string[] SpecifierMacros =
        {
            "UPROPERTY", "UFUNCTION", "UCLASS", "USTRUCT", "UENUM", "UINTERFACE", "UPARAM", "UMETA", "UDELEGATE"
        };

        private readonly ITextBuffer _buffer;
        private readonly IClassificationType[] _types;
        private Dictionary<string, byte> _names = new Dictionary<string, byte>(StringComparer.Ordinal);
        private int _namesVersion = -1;

        public UnrealClassifier(ITextBuffer buffer, IClassificationTypeRegistryService registry)
        {
            _buffer = buffer;
            // Indexed by ColorKind.
            _types = new[]
            {
                registry.GetClassificationType(UnrealClassificationTypes.Macro),
                registry.GetClassificationType(UnrealClassificationTypes.Type),
                registry.GetClassificationType(UnrealClassificationTypes.Struct),
                registry.GetClassificationType(UnrealClassificationTypes.Enum),
                registry.GetClassificationType(UnrealClassificationTypes.EnumValue),
                registry.GetClassificationType(UnrealClassificationTypes.Delegate),
                registry.GetClassificationType(UnrealClassificationTypes.Function),
                registry.GetClassificationType(UnrealClassificationTypes.Property),
                registry.GetClassificationType(UnrealClassificationTypes.Specifier),
                registry.GetClassificationType(UnrealClassificationTypes.MetaKey),
            };
            // Recolor open files when indexing finishes or the backend changes (no edit needed).
            PenguinExtention.Core.BackendService.Changed += () => _ = Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var snapshot = _buffer.CurrentSnapshot;
                ClassificationChanged?.Invoke(this, new ClassificationChangedEventArgs(new SnapshotSpan(snapshot, 0, snapshot.Length)));
            });
        }

        public event EventHandler<ClassificationChangedEventArgs> ClassificationChanged;

        public IList<ClassificationSpan> GetClassificationSpans(SnapshotSpan span)
        {
            var result = new List<ClassificationSpan>();
            if (PenguinOptionsPage.Current?.EnableUnrealColoring == false) return result;
            RefreshNames();
            var names = _names;

            var snapshot = span.Snapshot;
            int firstLine = snapshot.GetLineNumberFromPosition(span.Start);
            int lastLine = snapshot.GetLineNumberFromPosition(Math.Max(span.Start, span.End - 1));
            var state = StateBefore(snapshot, firstLine);
            for (int ln = firstLine; ln <= lastLine; ln++)
            {
                var line = snapshot.GetLineFromLineNumber(ln);
                if (line.Length > MaxLineLength) { state = default; continue; }
                var start = line.Start;
                Scan(line.GetText(), ref state, names, (index, length, kind) =>
                {
                    var s = new SnapshotSpan(start + index, length);
                    if (s.IntersectsWith(span)) result.Add(new ClassificationSpan(s, _types[(int)kind]));
                });
            }
            return result;
        }

        /// <summary>Macro-argument state at the start of <paramref name="lineNumber"/> (for multi-line UPROPERTY(…)).</summary>
        private static MacroState StateBefore(ITextSnapshot snapshot, int lineNumber)
        {
            var state = default(MacroState);
            int from = -1;
            for (int k = lineNumber - 1; k >= 0 && k >= lineNumber - MacroLookback; k--)
            {
                var text = snapshot.GetLineFromLineNumber(k).GetText();
                if (text.Length > MaxLineLength) break;
                if (ContainsSpecifierMacro(text)) { from = k; break; }
                // Specifier lists never contain these outside strings: the statement ended above us.
                if (text.IndexOf(';') >= 0 || text.IndexOf('{') >= 0 || text.IndexOf('}') >= 0) break;
            }
            if (from < 0) return state;
            for (int k = from; k < lineNumber; k++)
                Scan(snapshot.GetLineFromLineNumber(k).GetText(), ref state, null, null);
            return state;
        }

        private static bool ContainsSpecifierMacro(string text)
        {
            foreach (var macro in SpecifierMacros)
                if (text.IndexOf(macro, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>Where the scanner is inside a reflection macro's argument list.</summary>
        internal struct MacroState
        {
            public int Depth;       // paren depth inside a specifier macro; 0 = outside
            public int MetaDepth;   // depth of the meta=( … ) contents; 0 = not inside one
            public bool Pending;    // saw UPROPERTY etc., waiting for its '('
            public bool ExpectKey;  // the next identifier is an item key (specifier or meta key)
            public bool AfterMeta;  // the last key was "meta", so the next '(' opens meta entries
        }

        /// <summary>
        /// Scans one line, reporting colored identifiers through <paramref name="emit"/>. With null
        /// <paramref name="names"/>/<paramref name="emit"/> it only advances <paramref name="state"/>.
        /// </summary>
        internal static void Scan(string text, ref MacroState state, Dictionary<string, byte> names, Action<int, int, ColorKind> emit)
        {
            int i = 0, n = text.Length;
            while (i < n)
            {
                char c = text[i];
                if (c == '/' && i + 1 < n && text[i + 1] == '/') return;
                if (c == '/' && i + 1 < n && text[i + 1] == '*')
                {
                    int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    if (end < 0) return;
                    i = end + 2;
                    continue;
                }
                if (c == '"' || c == '\'')
                {
                    i = SkipQuoted(text, i);
                    if (state.Depth > 0) { state.ExpectKey = false; state.AfterMeta = false; }
                    state.Pending = false;
                    continue;
                }
                if (char.IsDigit(c))
                {
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '.' || text[i] == '\'' || text[i] == '_')) i++;
                    continue;
                }
                if (char.IsLetter(c) || c == '_')
                {
                    int start = i;
                    while (i < n && (char.IsLetterOrDigit(text[i]) || text[i] == '_')) i++;
                    Identifier(text, start, text.Substring(start, i - start), ref state, names, emit);
                    continue;
                }
                if (state.Depth > 0 || state.Pending) Punctuation(c, ref state);
                i++;
            }
        }

        private static void Identifier(string text, int start, string word, ref MacroState state,
            Dictionary<string, byte> names, Action<int, int, ColorKind> emit)
        {
            state.Pending = false;
            if (state.Depth > 0 && state.ExpectKey)
            {
                bool meta = state.MetaDepth > 0 && state.Depth == state.MetaDepth;
                emit?.Invoke(start, word.Length, meta ? ColorKind.MetaKey : ColorKind.Specifier);
                state.AfterMeta = !meta && state.Depth == 1 && word.Equals("meta", StringComparison.OrdinalIgnoreCase);
                state.ExpectKey = false;
                return;
            }
            state.AfterMeta = false;

            if (state.Depth == 0 && Array.IndexOf(SpecifierMacros, word) >= 0)
            {
                emit?.Invoke(start, word.Length, ColorKind.Macro);
                state.Pending = true;
                return;
            }
            if (emit == null) return;
            if (Macros.Contains(word) || word.StartsWith("DECLARE_", StringComparison.Ordinal) && word.ToUpperInvariant() == word)
            {
                emit(start, word.Length, ColorKind.Macro);
                return;
            }
            if (names == null || !names.TryGetValue(word, out var category)) return;
            ColorKind kind;
            switch (category)
            {
                case NameCategory.Class: kind = ColorKind.Class; break;
                case NameCategory.Struct: kind = ColorKind.Struct; break;
                case NameCategory.Enum: kind = ColorKind.Enum; break;
                case NameCategory.Delegate: kind = ColorKind.Delegate; break;
                case NameCategory.Macro: kind = ColorKind.Macro; break;
                case NameCategory.Function: kind = ColorKind.Function; break;
                case NameCategory.EnumValue: kind = ColorKind.EnumValue; break;
                case NameCategory.Property: kind = ColorKind.Property; break;
                // Engine member names (Value, Index, X…) would color unrelated locals; only trust obj->Name / obj.Name.
                case NameCategory.EngineProperty:
                    if (!AfterMemberAccess(text, start)) return;
                    kind = ColorKind.Property;
                    break;
                default: return;
            }
            emit(start, word.Length, kind);
        }

        private static void Punctuation(char c, ref MacroState state)
        {
            if (state.Pending)
            {
                if (c == '(') state = new MacroState { Depth = 1, ExpectKey = true };
                else if (!char.IsWhiteSpace(c)) state.Pending = false;
                return;
            }
            switch (c)
            {
                case '(':
                    state.Depth++;
                    state.ExpectKey = state.AfterMeta;
                    if (state.AfterMeta) state.MetaDepth = state.Depth;
                    state.AfterMeta = false;
                    break;
                case ')':
                    if (state.Depth == state.MetaDepth) state.MetaDepth = 0;
                    state.Depth--;
                    state.ExpectKey = false;
                    state.AfterMeta = false;
                    if (state.Depth <= 0) state = default;
                    break;
                case ',':
                    state.ExpectKey = state.Depth == 1 || state.Depth == state.MetaDepth;
                    state.AfterMeta = false;
                    break;
                case '=':
                    state.ExpectKey = false; // keep AfterMeta: "meta = ("
                    break;
                default:
                    if (!char.IsWhiteSpace(c)) { state.ExpectKey = false; state.AfterMeta = false; }
                    break;
            }
        }

        private static bool AfterMemberAccess(string text, int start)
        {
            int j = start - 1;
            while (j >= 0 && char.IsWhiteSpace(text[j])) j--;
            if (j < 0) return false;
            return text[j] == '.' || text[j] == '>' && j > 0 && text[j - 1] == '-';
        }

        /// <summary>Index just past the string/char literal starting at <paramref name="i"/> (or end of line).</summary>
        private static int SkipQuoted(string text, int i)
        {
            char quote = text[i++];
            while (i < text.Length)
            {
                char c = text[i++];
                if (c == '\\') i++;
                else if (c == quote) break;
            }
            return Math.Min(i, text.Length);
        }

        private void RefreshNames()
        {
            var cache = CacheService.Instance;
            if (cache == null || !cache.IsLoaded) return;
            var names = cache.GetNameCategories(out int version);
            if (version == _namesVersion) return;
            _names = names;
            _namesVersion = version;
        }
    }
}
