using System.ComponentModel;
using Microsoft.VisualStudio.Shell;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Tools → Options → PenguinExtension → General.
    /// Provides user-configurable settings persisted by the VS settings store.
    /// </summary>
    public class PenguinOptionsPage : DialogPage
    {
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
