using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;

namespace PenguinExtention.QuickInfo
{
    /// <summary>
    /// MEF-exported factory that creates <see cref="UnrealQuickInfoSource"/> instances
    /// for C/C++ text buffers.  Provides hover tooltips with Unreal metadata.
    /// </summary>
    [Export(typeof(IAsyncQuickInfoSourceProvider))]
    [Name("PenguinExtension.UnrealQuickInfoSource")]
    [ContentType("C/C++")]
    [Order]
    internal sealed class UnrealQuickInfoSourceProvider : IAsyncQuickInfoSourceProvider
    {
        [Import]
        internal ITextStructureNavigatorSelectorService NavigatorService { get; set; }

        public IAsyncQuickInfoSource TryCreateQuickInfoSource(ITextBuffer textBuffer)
        {
            return textBuffer.Properties.GetOrCreateSingletonProperty(
                typeof(UnrealQuickInfoSource),
                () => new UnrealQuickInfoSource(textBuffer, NavigatorService));
        }
    }
}
