using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace PenguinExtention.Core
{
    internal sealed class SolutionEvents : IVsSolutionEvents, IDisposable
    {
        private readonly IVsSolution solution;
        private readonly Action changed;
        private uint cookie;
        public SolutionEvents(IVsSolution solution, Action changed)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            this.solution = solution; this.changed = changed;
            solution?.AdviseSolutionEvents(this, out cookie);
        }
        public int OnAfterOpenSolution(object reserved, int newSolution) { changed(); return VSConstants.S_OK; }
        public int OnAfterCloseSolution(object reserved) { changed(); return VSConstants.S_OK; }
        public int OnAfterOpenProject(IVsHierarchy h, int added) => VSConstants.S_OK;
        public int OnQueryCloseProject(IVsHierarchy h, int removing, ref int cancel) => VSConstants.S_OK;
        public int OnBeforeCloseProject(IVsHierarchy h, int removed) => VSConstants.S_OK;
        public int OnAfterLoadProject(IVsHierarchy stub, IVsHierarchy real) => VSConstants.S_OK;
        public int OnQueryUnloadProject(IVsHierarchy h, ref int cancel) => VSConstants.S_OK;
        public int OnBeforeUnloadProject(IVsHierarchy real, IVsHierarchy stub) => VSConstants.S_OK;
        public int OnQueryCloseSolution(object reserved, ref int cancel) => VSConstants.S_OK;
        public int OnBeforeCloseSolution(object reserved) => VSConstants.S_OK;
        public void Dispose() { ThreadHelper.ThrowIfNotOnUIThread(); if (cookie != 0) solution?.UnadviseSolutionEvents(cookie); cookie = 0; }
    }
}
