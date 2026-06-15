using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace PenguinExtention.Completion
{
    /// <summary>
    /// MEF-exported factory that creates <see cref="UnrealCompletionSource"/> instances
    /// for C/C++ text views in the VS editor.
    /// </summary>
    [Export(typeof(IAsyncCompletionSourceProvider))]
    [Name("PenguinExtension.UnrealCompletionSource")]
    [ContentType("C/C++")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class UnrealCompletionSourceProvider : IAsyncCompletionSourceProvider
    {
        public IAsyncCompletionSource GetOrCreate(ITextView textView)
        {
            return textView.Properties.GetOrCreateSingletonProperty(
                typeof(UnrealCompletionSource),
                () => new UnrealCompletionSource());
        }
    }
}
