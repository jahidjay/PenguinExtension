using System;
using System.Threading.Tasks;
using PenguinExtention.Models;

namespace PenguinExtention.Services
{
    /// <summary>
    /// Entry points the separate Penguin Premium extension fills in when it loads. Free features that offer a
    /// premium action (e.g. Unreal Explorer's context menu) call through here and show an upsell when it's absent.
    /// </summary>
    internal static class PremiumHooks
    {
        public const string Upsell = "This is a Penguin Premium feature — install Penguin Premium to use it.";

        public static Func<UnrealSymbol, Task> GoToImplementation;
        public static Func<UnrealSymbol, Task> FindReferences;

        public static bool IsInstalled => GoToImplementation != null;
    }
}
