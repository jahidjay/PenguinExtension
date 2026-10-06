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

        [Category("Backend"), DisplayName("Backend")]
        [Description("Legacy remains the default until experimental-instance acceptance. Core never falls back silently. Changing settings restarts the selected backend.")]
        public PenguinBackend Backend { get; set; } = PenguinBackend.Legacy;

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

        [Category("Indexing")]
        [DisplayName("Index Engine Source")]
        [Description("Index Unreal Engine source headers for navigation and suggestions. Disable to reduce indexing time.")]
        public bool IndexEngineSource { get; set; } = true;
    }
}
