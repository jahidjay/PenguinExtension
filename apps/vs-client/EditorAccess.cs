using System;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.TextManager.Interop;

namespace PenguinExtention.Core
{
    internal static class EditorAccess
    {
        public static ITextView ActiveView()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var manager = Package.GetGlobalService(typeof(SVsTextManager)) as IVsTextManager;
            IVsTextView view;
            if (manager == null || manager.GetActiveView(1, null, out view) < 0 || view == null) return null;
            return Adapt(view);
        }
        public static ITextView Adapt(IVsTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var component = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            return component?.GetService<IVsEditorAdaptersFactoryService>().GetWpfTextView(view);
        }
        public static void Insert(ITextView view, int position, string text, string description)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var component = Package.GetGlobalService(typeof(SComponentModel)) as IComponentModel;
            var registry = component.GetService<ITextUndoHistoryRegistry>();
            ITextUndoHistory history;
            if (!registry.TryGetHistory(view.TextBuffer, out history)) history = registry.RegisterHistory(view.TextBuffer);
            using (var transaction = history.CreateTransaction(description))
            using (var edit = view.TextBuffer.CreateEdit())
            {
                if (!edit.Insert(position, text)) throw new InvalidOperationException("Editor is read-only.");
                edit.Apply();
                if (edit.Canceled) throw new InvalidOperationException("Editor rejected the edit.");
                transaction.Complete();
            }
        }
    }
}
