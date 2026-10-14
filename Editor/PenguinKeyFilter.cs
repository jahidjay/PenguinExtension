using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion;
using Microsoft.VisualStudio.Language.Intellisense.AsyncCompletion.Data;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using PenguinExtention.Commands;
using PenguinExtention.Completion;
using PenguinExtention.Core;
using PenguinExtention.Models;
using PenguinExtention.Services;
using PenguinExtention.UI;

namespace PenguinExtention.Editor
{
    /// <summary>Installs <see cref="PenguinKeyFilter"/> on every C/C++ editor view.</summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("C/C++")]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    internal sealed class PenguinKeyFilterProvider : IVsTextViewCreationListener
    {
        [Import] internal IVsEditorAdaptersFactoryService Adapters = null;
        [Import] internal IAsyncCompletionBroker AsyncBroker = null;
        [Import] internal ICompletionBroker LegacyBroker = null;
        [Import] internal ISignatureHelpBroker SignatureBroker = null;

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            var view = Adapters.GetWpfTextView(textViewAdapter);
            if (view == null) return;
            var filter = new PenguinKeyFilter(view, this);
            if (ErrorHandler.Succeeded(textViewAdapter.AddCommandFilter(filter, out var next))) filter.Next = next;
            view.Closed += (s, e) => { filter.Dispose(); textViewAdapter.RemoveCommandFilter(filter); };
        }
    }

    /// <summary>
    /// Visual Assist-style typing assistance, first in the view's command chain:
    ///  • the 🐧 suggestion box (opens while typing; Tab/Enter accept, Esc closes, ↑/↓/PgUp/PgDn move),
    ///    with auto #include for picked symbols;
    ///  • the parameter info box + definition peek while typing a call's arguments.
    /// Everything expensive runs on a background thread; the UI thread only renders.
    /// </summary>
    internal sealed class PenguinKeyFilter : IOleCommandTarget, IDisposable
    {
        internal const string ProbeList = "Penguin suggestion box";
        internal const string ProbeParams = "Penguin parameter info box";

        private readonly IWpfTextView _view;
        private readonly PenguinKeyFilterProvider _services;
        private PenguinCompletionPopup _list;
        private PenguinParameterInfoPopup _params;
        private CancellationTokenSource _listQuery, _paramQuery;
        private int _listGeneration;
        /// <summary>Open position of the call whose parameter info is shown (-1: none).</summary>
        private int _callOpen = -1;
        private bool _committing;

        public IOleCommandTarget Next { get; set; }

        public PenguinKeyFilter(IWpfTextView view, PenguinKeyFilterProvider services)
        {
            _view = view;
            _services = services;
            _view.Caret.PositionChanged += OnCaretMoved;
            _view.LostAggregateFocus += OnLostFocus;
            _services.AsyncBroker.CompletionTriggered += OnVsCompletionTriggered;
        }

        private static PenguinOptionsPage Options => PenguinOptionsPage.Current;
        private static bool ListEnabled => Options?.UsePenguinSuggestionBox != false && BackendService.CompletionEnabled;
        private static bool ParamsEnabled => Options?.ShowParameterInfoBox != false;
        private bool ListOpen => _list != null && _list.IsOpen;
        private bool ParamsOpen => _params != null && _params.IsOpen;

        // ── Command routing ─────────────────────────────────────────

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) =>
            Next.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);

        public int Exec(ref Guid group, uint id, uint options, IntPtr pvaIn, IntPtr pvaOut)
        {
            if (VsShellUtilities.IsInAutomationFunction(ServiceProvider.GlobalProvider))
                return Next.Exec(ref group, id, options, pvaIn, pvaOut);
            try
            {
                if (group == VSConstants.VSStd2K)
                {
                    var cmd = (VSConstants.VSStd2KCmdID)id;
                    char typed = cmd == VSConstants.VSStd2KCmdID.TYPECHAR ? (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn) : '\0';
                    if (HandleBefore(cmd, typed)) return VSConstants.S_OK;
                    int result = Next.Exec(ref group, id, options, pvaIn, pvaOut);
                    if (ErrorHandler.Succeeded(result)) HandleAfter(cmd, typed);
                    return result;
                }
                if (group == VSConstants.GUID_VSStandardCommandSet97 && ListOpen)
                {
                    var cmd97 = (VSConstants.VSStd97CmdID)id;
                    if (cmd97 == VSConstants.VSStd97CmdID.Delete) { int r = Next.Exec(ref group, id, options, pvaIn, pvaOut); RefreshList(false); return r; }
                }
            }
            catch (Exception ex) { PenguinLog.Error("Typing", ex); CloseList(); }
            return Next.Exec(ref group, id, options, pvaIn, pvaOut);
        }

        /// <summary>Keys we consume before Visual Studio sees them. Returns true when handled.</summary>
        private bool HandleBefore(VSConstants.VSStd2KCmdID cmd, char typed)
        {
            if (ListOpen)
            {
                switch (cmd)
                {
                    case VSConstants.VSStd2KCmdID.TAB:
                    case VSConstants.VSStd2KCmdID.RETURN:
                        return Commit(_list.Selected, '\0');
                    case VSConstants.VSStd2KCmdID.CANCEL: CloseList(); return true;
                    case VSConstants.VSStd2KCmdID.UP: _list.Move(-1); return true;
                    case VSConstants.VSStd2KCmdID.DOWN: _list.Move(1); return true;
                    case VSConstants.VSStd2KCmdID.PAGEUP: _list.Move(-8); return true;
                    case VSConstants.VSStd2KCmdID.PAGEDN: _list.Move(8); return true;
                    case VSConstants.VSStd2KCmdID.TYPECHAR:
                        // '(' after a function name commits it (then parameter info opens); other punctuation just closes.
                        if (typed == '(' && _list.Selected?.Symbol?.Kind == UnrealSymbolKind.Function && TypedSoFar().Length > 0)
                        {
                            Commit(_list.Selected, '\0');
                            return false; // let '(' through
                        }
                        if (!PenguinCompletionEngine.IsIdent(typed)) CloseList();
                        return false;
                }
            }
            if (ParamsOpen)
            {
                switch (cmd)
                {
                    case VSConstants.VSStd2KCmdID.CANCEL: CloseParams(); return true;
                    case VSConstants.VSStd2KCmdID.UP when _params.OverloadCount > 1: _params.Cycle(-1); return true;
                    case VSConstants.VSStd2KCmdID.DOWN when _params.OverloadCount > 1: _params.Cycle(1); return true;
                }
            }
            if ((cmd == VSConstants.VSStd2KCmdID.COMPLETEWORD || cmd == VSConstants.VSStd2KCmdID.SHOWMEMBERLIST) && ListEnabled)
            {
                OpenList(explicitInvoke: true);
                return true;
            }
            return false;
        }

        /// <summary>After Visual Studio inserted the character: open/refresh/close our boxes.</summary>
        private void HandleAfter(VSConstants.VSStd2KCmdID cmd, char typed)
        {
            if (_committing) return;
            if (cmd == VSConstants.VSStd2KCmdID.BACKSPACE || cmd == VSConstants.VSStd2KCmdID.DELETE)
            {
                if (ListOpen) RefreshList(false);
                if (ParamsOpen) UpdateParams();
                return;
            }
            if (cmd != VSConstants.VSStd2KCmdID.TYPECHAR) return;

            if (ParamsEnabled)
            {
                if (typed == '(') OpenParams();
                else if (ParamsOpen && (typed == ',' || typed == ')' || typed == ';')) UpdateParams();
            }
            if (!ListEnabled) return;
            if (ListOpen) { RefreshList(false); return; }
            if (ShouldAutoOpen(typed)) OpenList(explicitInvoke: false);
        }

        private bool ShouldAutoOpen(char typed)
        {
            var caret = _view.Caret.Position.BufferPosition;
            var snapshot = caret.Snapshot;
            if (caret.Position < 1) return false;
            if (PenguinCompletionEngine.IsIdent(typed))
            {
                // First letter(s) of a new identifier.
                int start = caret.Position;
                while (start > 0 && PenguinCompletionEngine.IsIdent(snapshot[start - 1])) start--;
                int length = caret.Position - start;
                return !char.IsDigit(snapshot[start]) && length == Math.Max(1, Options?.SuggestionBoxMinChars ?? 1);
            }
            if (typed == ':' && caret.Position >= 2 && snapshot[caret.Position - 2] == ':') return true;
            if (typed == '>' && caret.Position >= 2 && snapshot[caret.Position - 2] == '-') return true;
            if (typed == '.' ) return caret.Position >= 2 && PenguinCompletionEngine.IsIdent(snapshot[caret.Position - 2]) || caret.Position >= 2 && snapshot[caret.Position - 2] == ')';
            if (typed == '(' || typed == ',') return true;  // inside UPROPERTY( … ) → specifiers (engine returns null elsewhere)
            if (typed == ' ')
            {
                // smart contexts: "x = ", "return ", "case "
                var line = caret.GetContainingLine();
                var before = snapshot.GetText(line.Start, caret.Position - line.Start).TrimEnd();
                return before.EndsWith("=") || before.EndsWith("return") || before.EndsWith("case") || before.EndsWith("virtual");
            }
            return false;
        }

        // ── Suggestion box ──────────────────────────────────────────

        private void OpenList(bool explicitInvoke) => RefreshList(explicitInvoke);

        private string TypedSoFar()
        {
            var caret = _view.Caret.Position.BufferPosition;
            int start = caret.Position;
            while (start > 0 && PenguinCompletionEngine.IsIdent(caret.Snapshot[start - 1])) start--;
            return caret.Snapshot.GetText(start, caret.Position - start);
        }

        private void RefreshList(bool explicitInvoke)
        {
            _listQuery?.Cancel();
            _listQuery = new CancellationTokenSource();
            var token = _listQuery.Token;
            var snapshot = _view.TextSnapshot;
            int caret = _view.Caret.Position.BufferPosition.Position;
            int generation = ++_listGeneration;
            var watch = System.Diagnostics.Stopwatch.StartNew();

            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                PenguinCompletionModel model = null;
                try
                {
                    await TaskScheduler.Default;
                    var text = snapshot.GetText();
                    model = PenguinCompletionEngine.Compute(text, caret, explicitInvoke);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { PenguinLog.Error("Suggestion box", ex); }
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                if (generation != _listGeneration || _view.IsClosed || _view.TextSnapshot != snapshot) return;
                if (model == null || model.Items.Count == 0) { CloseList(); return; }
                if (_list == null)
                {
                    _list = new PenguinCompletionPopup(_view);
                    _list.ItemChosen += item => Commit(item, '\0');
                }
                _list.Show(model, new SnapshotPoint(snapshot, model.SpanStart));
                DismissVisualStudioLists();
                FeatureProbe.Mark(ProbeList, model.Context + " \"" + model.Typed + "\" → " + model.Items.Count + " in " + watch.ElapsedMilliseconds + " ms");
            });
        }

        private bool Commit(PenguinItem item, char _)
        {
            if (item == null) { CloseList(); return false; }
            var caret = _view.Caret.Position.BufferPosition;
            var snapshot = caret.Snapshot;
            int start = caret.Position;
            while (start > 0 && PenguinCompletionEngine.IsIdent(snapshot[start - 1])) start--;
            // Also replace the rest of an identifier the caret is in the middle of (VA behaviour).
            int end = caret.Position;
            while (end < snapshot.Length && PenguinCompletionEngine.IsIdent(snapshot[end])) end++;
            CloseList();
            _committing = true;
            try
            {
                using (var edit = _view.TextBuffer.CreateEdit())
                {
                    edit.Replace(Span.FromBounds(start, end), item.Insert);
                    edit.Apply();
                }
                int target = start + item.Insert.Length - item.CaretBack;
                _view.Caret.MoveTo(new SnapshotPoint(_view.TextSnapshot, Math.Max(start, target)));
            }
            finally { _committing = false; }
            PenguinLog.Info("Suggestion box", "inserted " + item.Insert + (item.Symbol == null ? "" : " (" + item.Symbol.FilePath + ")"));
            if (item.Symbol != null) BackendService.RecordUsage(item.Symbol);
            if (item.Symbol != null && Options?.AutoAddIncludes != false) AddIncludeFor(item.Symbol);
            if (item.Kind == PenguinItemKind.Specifier && item.Insert.EndsWith("\"\"")) { /* caret inside quotes */ }
            return true;
        }

        private void AddIncludeFor(UnrealSymbol symbol)
        {
            var snapshot = _view.TextSnapshot;
            var file = PenguinEditor.FilePathOf(_view);
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await TaskScheduler.Default;
                    var plan = IncludeInserter.For(symbol, file, snapshot.GetText());
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    if (!plan.Needed) { PenguinLog.Info("Include", symbol.Name + ": " + plan.Reason); return; }
                    if (_view.IsClosed) return;
                    PenguinEditor.InsertInclude(_view, plan);
                    FeatureProbe.Mark("Auto #include", plan.IncludePath);
                }
                catch (Exception ex) { PenguinLog.Error("Include", ex); }
            });
        }

        private void CloseList()
        {
            _listQuery?.Cancel();
            _listGeneration++;
            _list?.Close();
        }

        /// <summary>While our box is open, Visual Studio's own member list would cover it: dismiss it.</summary>
        private void DismissVisualStudioLists()
        {
            try
            {
                _services.AsyncBroker.GetSession(_view)?.Dismiss();
                _services.LegacyBroker.DismissAllSessions(_view);
            }
            catch (Exception ex) { PenguinLog.Error("Suggestion box", "dismissing VS list: " + ex.Message); }
        }

        private void OnVsCompletionTriggered(object sender, CompletionTriggeredEventArgs e)
        {
            if (e.TextView == _view && ListOpen)
                _view.VisualElement.Dispatcher.BeginInvoke(new Action(() => { if (ListOpen) e.CompletionSession.Dismiss(); }), DispatcherPriority.Background);
        }

        // ── Parameter info + definition peek ────────────────────────

        private void OpenParams()
        {
            var caret = _view.Caret.Position.BufferPosition;
            var snapshot = caret.Snapshot;
            int open = caret.Position - 1;
            if (open < 0 || snapshot[open] != '(') return;
            var line = snapshot.GetLineFromPosition(open);
            var lineText = line.GetText();
            int nameEnd = open - line.Start.Position;
            while (nameEnd > 0 && char.IsWhiteSpace(lineText[nameEnd - 1])) nameEnd--;
            int nameStart = nameEnd;
            while (nameStart > 0 && PenguinCompletionEngine.IsIdent(lineText[nameStart - 1])) nameStart--;
            if (nameStart == nameEnd) return;
            var name = lineText.Substring(nameStart, nameEnd - nameStart);
            if (UnrealMacroSpecifiers.MacroNames.Contains(name) || name == "if" || name == "for" || name == "while" || name == "switch" || name == "return") return;
            var nameSpan = new SnapshotSpan(line.Start + nameStart, nameEnd - nameStart);
            var qualifier = PenguinEditor.QualifierBefore(nameSpan);
            var file = PenguinEditor.FilePathOf(_view);
            var text = snapshot.GetText();

            _paramQuery?.Cancel();
            _paramQuery = new CancellationTokenSource();
            var token = _paramQuery.Token;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await TaskScheduler.Default;
                    var candidates = (await BackendService.ExactAsync(name, token).ConfigureAwait(false))
                        .Where(s => s.Kind == UnrealSymbolKind.Function || s.Kind == UnrealSymbolKind.Delegate || s.Kind == UnrealSymbolKind.Macro).ToList();
                    if (candidates.Count == 0) return;
                    // Receiver-aware: "Owner::Name(" or "obj->Name(" narrows to that class (and its bases).
                    var masked = CodeContext.Mask(text);
                    var cache = CacheService.Instance;
                    var owner = qualifier ?? ReceiverType(cache, masked, text, line.Start.Position + nameStart);
                    if (owner != null && cache != null)
                    {
                        var chain = new HashSet<string>(new[] { owner }.Concat(cache.GetClassInfo(owner)?.InheritanceChain ?? new List<string>()));
                        var narrowed = candidates.Where(s => s.OwnerClass != null && chain.Contains(s.OwnerClass)).ToList();
                        if (narrowed.Count > 0) candidates = narrowed;
                    }
                    var partner = file == null ? null : HeaderSourceResolver.Partner(file);
                    var ordered = candidates
                        .OrderBy(s => PenguinEditor.SameFile(s.FilePath, file) || PenguinEditor.SameFile(s.FilePath, partner) ? 0 : 1)
                        .ThenBy(s => s.IsEngineSymbol ? 1 : 0)
                        .ThenByDescending(s => s.AccessCount).Take(12).ToList();

                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(token);
                    if (_view.IsClosed) return;
                    if (_params == null)
                    {
                        _params = new PenguinParameterInfoPopup(_view);
                        _params.OverloadShown += LoadPeek;
                    }
                    _callOpen = open;
                    _params.ResetOverload();
                    _params.Show(ordered, 0, new SnapshotPoint(_view.TextSnapshot, Math.Min(open, _view.TextSnapshot.Length)), Options?.ShowDefinitionPeek != false);
                    DismissVisualStudioSignatureHelp();
                    FeatureProbe.Mark(ProbeParams, name + " → " + ordered.Count + " overload(s)");
                    LoadPeek(_params.Current);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { PenguinLog.Error("Parameter info", ex); }
            });
        }

        private static string ReceiverType(CacheService cache, string masked, string text, int namePosition)
        {
            if (cache == null) return null;
            int i = namePosition;
            while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
            bool arrow = i >= 2 && text[i - 1] == '>' && text[i - 2] == '-';
            bool dot = i >= 1 && text[i - 1] == '.';
            if (!arrow && !dot) return CodeContext.EnclosingClass(masked, namePosition) ?? PenguinCompletionEngine.EnclosingMemberOwner(masked, namePosition);
            int end = i - (arrow ? 2 : 1);
            int start = end;
            while (start > 0 && PenguinCompletionEngine.IsIdent(text[start - 1])) start--;
            if (start == end) return null;
            var receiver = text.Substring(start, end - start);
            var currentClass = CodeContext.EnclosingClass(masked, namePosition) ?? PenguinCompletionEngine.EnclosingMemberOwner(masked, namePosition);
            return receiver == "this" ? currentClass : PenguinCompletionEngine.ResolveType(cache, masked, start, receiver, currentClass, false);
        }

        private void LoadPeek(UnrealSymbol symbol)
        {
            if (symbol == null || Options?.ShowDefinitionPeek == false) return;
            _ = ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await TaskScheduler.Default;
                    var peek = DefinitionLookup.For(symbol);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _params?.SetPeek(symbol, peek ?? new DefinitionSnippet { FilePath = symbol.FilePath, Line = symbol.LineNumber, Text = "(source not readable)" });
                }
                catch (Exception ex) { PenguinLog.Error("Definition peek", ex); }
            });
        }

        /// <summary>Recomputes the current argument index, or closes when the caret left the call.</summary>
        private void UpdateParams()
        {
            if (!ParamsOpen || _callOpen < 0) return;
            var caret = _view.Caret.Position.BufferPosition;
            var snapshot = caret.Snapshot;
            if (_callOpen >= snapshot.Length || caret.Position <= _callOpen || snapshot[_callOpen] != '(') { CloseParams(); return; }
            int depth = 0, argument = 0;
            for (int i = _callOpen + 1; i < caret.Position; i++)
            {
                char c = snapshot[i];
                if (c == '(' || c == '<' || c == '{' || c == '[') depth++;
                else if (c == ')' || c == '>' || c == '}' || c == ']')
                {
                    if (c == ')' && depth == 0) { CloseParams(); return; } // call closed
                    depth = Math.Max(0, depth - 1);
                }
                else if (c == ',' && depth == 0) argument++;
                else if (c == ';' && depth == 0) { CloseParams(); return; }
            }
            _params.SetParameter(argument);
        }

        private void CloseParams()
        {
            _paramQuery?.Cancel();
            _callOpen = -1;
            _params?.Close();
        }

        private void DismissVisualStudioSignatureHelp()
        {
            try { _services.SignatureBroker.DismissAllSessions(_view); }
            catch (Exception ex) { PenguinLog.Error("Parameter info", "dismissing VS signature help: " + ex.Message); }
        }

        // ── View events ─────────────────────────────────────────────

        private void OnCaretMoved(object sender, CaretPositionChangedEventArgs e)
        {
            if (_committing) return;
            if (ListOpen)
            {
                // Caret left the identifier being completed (click / arrow keys outside our handling).
                var pos = e.NewPosition.BufferPosition.Position;
                var old = e.OldPosition.BufferPosition.Position;
                if (Math.Abs(pos - old) > 1 || e.NewPosition.BufferPosition.GetContainingLine().LineNumber != e.OldPosition.BufferPosition.GetContainingLine().LineNumber)
                    CloseList();
            }
            if (ParamsOpen) UpdateParams();
        }

        private void OnLostFocus(object sender, EventArgs e)
        {
            CloseList();
            CloseParams();
        }

        public void Dispose()
        {
            CloseList();
            CloseParams();
            _view.Caret.PositionChanged -= OnCaretMoved;
            _view.LostAggregateFocus -= OnLostFocus;
            _services.AsyncBroker.CompletionTriggered -= OnVsCompletionTriggered;
        }
    }
}
