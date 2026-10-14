using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Text.RegularExpressions;
using System.Windows.Media;
using Microsoft.VisualStudio.Language.StandardClassification;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;
using PenguinExtention.Services;

namespace PenguinExtention.Editor
{
    /// <summary>Classification types — user-adjustable in Tools > Options > Fonts and Colors.</summary>
    internal static class UnrealClassificationTypes
    {
        public const string Macro = "Penguin Unreal Macro";
        public const string Type = "Penguin Unreal Type";
        public const string Function = "Penguin Function";
        public const string EnumValue = "Penguin Enum Value";

        [Export, Name(Function), BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition FunctionType = null;

        [Export, Name(EnumValue), BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition EnumValueType = null;

        [Export, Name(Macro), BaseDefinition(PredefinedClassificationTypeNames.Keyword)]
        internal static ClassificationTypeDefinition MacroType = null;

        [Export, Name(Type), BaseDefinition(PredefinedClassificationTypeNames.Identifier)]
        internal static ClassificationTypeDefinition TypeType = null;
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.Macro)]
    [Name(UnrealClassificationTypes.Macro), UserVisible(true), Order(After = Priority.High)]
    internal sealed class UnrealMacroFormat : ClassificationFormatDefinition
    {
        public UnrealMacroFormat()
        {
            DisplayName = "Penguin Unreal Macro";
            ForegroundColor = Color.FromRgb(0xC5, 0x86, 0xC0);
            IsBold = true;
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.Type)]
    [Name(UnrealClassificationTypes.Type), UserVisible(true), Order(After = Priority.High)]
    internal sealed class UnrealTypeFormat : ClassificationFormatDefinition
    {
        public UnrealTypeFormat()
        {
            DisplayName = "Penguin Unreal Type";
            ForegroundColor = Color.FromRgb(0x4E, 0xC9, 0xB0);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.Function)]
    [Name(UnrealClassificationTypes.Function), UserVisible(true), Order(After = Priority.High)]
    internal sealed class PenguinFunctionFormat : ClassificationFormatDefinition
    {
        public PenguinFunctionFormat()
        {
            DisplayName = "Penguin Function";
            ForegroundColor = Color.FromRgb(0xDC, 0xDC, 0xAA);
        }
    }

    [Export(typeof(EditorFormatDefinition))]
    [ClassificationType(ClassificationTypeNames = UnrealClassificationTypes.EnumValue)]
    [Name(UnrealClassificationTypes.EnumValue), UserVisible(true), Order(After = Priority.High)]
    internal sealed class PenguinEnumValueFormat : ClassificationFormatDefinition
    {
        public PenguinEnumValueFormat()
        {
            DisplayName = "Penguin Enum Value";
            ForegroundColor = Color.FromRgb(0xB8, 0xD7, 0xA3);
        }
    }

    [Export(typeof(IClassifierProvider))]
    [ContentType("C/C++")]
    internal sealed class UnrealClassifierProvider : IClassifierProvider
    {
        [Import] internal IClassificationTypeRegistryService Registry = null;

        public IClassifier GetClassifier(ITextBuffer buffer) =>
            buffer.Properties.GetOrCreateSingletonProperty(() => new UnrealClassifier(buffer, Registry));
    }

    /// <summary>
    /// Colors Unreal reflection/logging macros and indexed Unreal types (A/U/F/E/I/T-prefixed names present in the
    /// index). Classifies single lines with one compiled, non-backtracking identifier regex; the name set is an
    /// immutable snapshot, never queried per span. Comments/strings are left to the C++ colorizer (it wins there).
    /// </summary>
    internal sealed class UnrealClassifier : IClassifier
    {
        private static readonly Regex RxIdentifier = new Regex(@"\b[A-Za-z_]\w*\b", RegexOptions.Compiled);

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

        private readonly ITextBuffer _buffer;
        private readonly IClassificationType _macroType;
        private readonly IClassificationType _typeType;
        private Dictionary<string, byte> _names = new Dictionary<string, byte>(StringComparer.Ordinal);
        private readonly IClassificationType _functionType;
        private readonly IClassificationType _enumValueType;
        private int _namesVersion = -1;

        public UnrealClassifier(ITextBuffer buffer, IClassificationTypeRegistryService registry)
        {
            _buffer = buffer;
            _macroType = registry.GetClassificationType(UnrealClassificationTypes.Macro);
            _typeType = registry.GetClassificationType(UnrealClassificationTypes.Type);
            _functionType = registry.GetClassificationType(UnrealClassificationTypes.Function);
            _enumValueType = registry.GetClassificationType(UnrealClassificationTypes.EnumValue);
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
            for (int ln = firstLine; ln <= lastLine; ln++)
            {
                var line = snapshot.GetLineFromLineNumber(ln);
                var text = line.GetText();
                if (text.Length > 4000) continue; // minified/generated lines: not worth it
                int codeEnd = CodeEnd(text);
                foreach (Match m in RxIdentifier.Matches(text, 0))
                {
                    if (m.Index >= codeEnd) break;
                    var word = m.Value;
                    IClassificationType type = null;
                    if (Macros.Contains(word) || word.StartsWith("DECLARE_", StringComparison.Ordinal) && word.ToUpperInvariant() == word) type = _macroType;
                    else if (names.TryGetValue(word, out var cat)) type = cat == 1 ? _typeType : cat == 2 ? _functionType : cat == 3 ? _macroType : _enumValueType;
                    if (type == null) continue;
                    var s = new SnapshotSpan(line.Start + m.Index, m.Length);
                    if (s.IntersectsWith(span)) result.Add(new ClassificationSpan(s, type));
                }
            }
            return result;
        }

        /// <summary>Index where a // comment starts on this line, ignoring "//" inside string literals.</summary>
        private static int CodeEnd(string text)
        {
            bool inString = false;
            for (int i = 0; i < text.Length - 1; i++)
            {
                char c = text[i];
                if (c == '"' && (i == 0 || text[i - 1] != '\\')) inString = !inString;
                else if (!inString && c == '/' && text[i + 1] == '/') return i;
            }
            return text.Length;
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
