using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Tools → Options → PenguinExtension → General.
    /// Provides user-configurable settings persisted by the VS settings store.
    /// </summary>
    public enum PenguinBackend { Legacy, Core }

    public class PenguinOptionsPage : DialogPage
    {
        public static event System.EventHandler SettingsApplied;

        // The Rust Core backend is frozen: the extension always runs the Legacy (C#/SQLite) backend.
        // The setter ignores persisted "Core" values from older versions.
        [Browsable(false)]
        public PenguinBackend Backend { get => PenguinBackend.Legacy; set { } }

        [Browsable(false)]
        [Category("Backend"), DisplayName("Core Executable Override")]
        [Description("Absolute path to penguin-lsp.exe. Blank uses the bundled Core/penguin-lsp.exe; no Cargo or PATH lookup.")]
        public string CoreExecutableOverride { get; set; } = string.Empty;

        [Category("Local AI"), DisplayName("Enable AI Previews")]
        public bool EnableLocalAI { get; set; }
        [Category("Local AI"), DisplayName("Endpoint (loopback only)")]
        public string AIEndpoint { get; set; } = "http://127.0.0.1:11434";
        [Category("Local AI"), DisplayName("Model")]
        public string AIModel { get; set; } = "qwen2.5-coder:3b";

        protected override void OnApply(PageApplyEventArgs e)
        {
            base.OnApply(e);
            if (e.ApplyBehavior == ApplyKind.Apply) SettingsApplied?.Invoke(this, System.EventArgs.Empty);
        }

        [Category("Engine")]
        [DisplayName("Engine Root Override")]
        [Description("Absolute path to the Unreal Engine root directory. Leave blank for auto-detection via .uproject and registry.")]
        public string EngineRootOverride { get; set; } = string.Empty;

        [Category("Indexing")]
        [DisplayName("Max Indexing Threads")]
        [Description("Maximum number of parallel threads used for background indexing. Default is half of CPU cores.")]
        public int MaxIndexingThreads { get; set; } = System.Environment.ProcessorCount / 2;

        [Category("Editor")]
        [DisplayName("Enable Hover Info")]
        [Description("Show Unreal metadata tooltip when hovering over symbols.")]
        public bool EnableHoverInfo { get; set; } = true;

        [Category("Editor")]
        [DisplayName("Enable Completion Suggestions")]
        [Description("Show Unreal symbol suggestions in the IntelliSense completion list.")]
        public bool EnableCompletionSuggestions { get; set; } = true;

        /// <summary>The loaded options instance, or null before the package has read settings (use defaults).</summary>
        public static PenguinOptionsPage Current { get; private set; }

        public override void LoadSettingsFromStorage()
        {
            base.LoadSettingsFromStorage();
            Current = this;
        }

        [Category("Editor")]
        [DisplayName("Enable Unreal Syntax Coloring")]
        [Description("Color Unreal reflection macros and indexed Unreal types (Fonts and Colors: 'Penguin Unreal ...').")]
        public bool EnableUnrealColoring { get; set; } = true;

        [Category("Editor")]
        [DisplayName("Highlight References Under Caret")]
        [Description("Highlight other occurrences of the identifier under the caret in the current file (comment/string aware). Off by default because Visual Studio's own C++ reference highlighting is usually on.")]
        public bool EnableReferenceHighlight { get; set; }

        [Category("Suggestion Box")]
        [DisplayName("Use Penguin Suggestion Box")]
        [Description("Visual Assist-style 🐧 list that opens while typing (Unreal symbols, members, specifiers, snippets, smart suggestions) and replaces Visual Studio's member list while it is open. Tab/Enter accept, Esc closes, Ctrl+Space opens it.")]
        public bool UsePenguinSuggestionBox { get; set; } = true;

        [Category("Suggestion Box")]
        [DisplayName("Open After Characters")]
        [Description("Number of identifier characters typed before the 🐧 list opens by itself (1 = like Visual Assist).")]
        public int SuggestionBoxMinChars { get; set; } = 1;

        [Category("Suggestion Box")]
        [DisplayName("Auto-Add #include")]
        [Description("When a type or free function is picked from the 🐧 list, add its #include if the file doesn't have it yet.")]
        public bool AutoAddIncludes { get; set; } = true;

        [Category("Suggestion Box")]
        [DisplayName("Parameter Info Box")]
        [Description("While typing a call's arguments, show the 🐧 signature box (current parameter in bold, ↑/↓ for overloads). Replaces Visual Studio's parameter help for indexed functions.")]
        public bool ShowParameterInfoBox { get; set; } = true;

        [Category("Suggestion Box")]
        [DisplayName("Definition Peek Box")]
        [Description("Next to the parameter info box, show the called function's code (its .cpp body, or the declaration).")]
        public bool ShowDefinitionPeek { get; set; } = true;

        [Category("Editor")]
        [DisplayName("Signature Help While Typing")]
        [Description("Show the Unreal function declaration and documentation when typing '(' after a function name.")]
        public bool EnableSignatureHelp { get; set; } = true;

        [Category("Refactoring")]
        [DisplayName("Add CoreRedirects On Rename")]
        [Description("Pre-check 'Add Unreal CoreRedirect' when renaming reflected properties, functions, classes, structs and enums.")]
        public bool AddCoreRedirectsOnRename { get; set; }

        [Category("Indexing")]
        [DisplayName("Extra Include Directories (C/C++ mode)")]
        [Description("Semicolon-separated library include folders to index in plain C/C++ solutions, e.g. C:\\libs\\SDL2\\include;C:\\libs\\SDL2_image\\include. Folders from each .vcxproj's Additional Include Directories are added automatically. %ENV% variables are expanded.")]
        public string ExtraIncludeDirectories { get; set; } = string.Empty;

        [Category("Indexing")]
        [DisplayName("Index Engine Source")]
        [Description("Unreal: index engine source headers. Plain C/C++: index library include folders (SDL2 etc.). Disable to reduce indexing time.")]
        public bool IndexEngineSource { get; set; } = true;
    }
}
