using System.Collections.Generic;
using PenguinExtention.Models;
using PenguinExtention.Services;

namespace PenguinExtention.Completion
{
    /// <summary>
    /// The one tooltip layout shared by both completion pipelines and hover:
    ///   Class UCLASS() class X : public Y
    ///   📁 File.h:13
    ///   📦 Member of AOwner
    ///   🔗 X → Y → UObject
    ///   📝 doc comment
    ///   [Project] / [Engine]
    /// </summary>
    internal static class SymbolTooltip
    {
        /// <summary>First line (kind + signature); kept separate so rich hosts can color it.</summary>
        public static string Header(UnrealSymbol s) =>
            s.KindDisplay + " " + (string.IsNullOrEmpty(s.Signature) ? s.DisplayText : s.Signature);

        /// <summary>Lines after the header. Reads only the in-memory cache (cheap, safe on any thread).</summary>
        public static List<string> Body(UnrealSymbol s)
        {
            var lines = new List<string>();
            lines.Add("📁 " + System.IO.Path.GetFileName(s.FilePath) + ":" + s.LineNumber);
            if (!string.IsNullOrEmpty(s.OwnerClass)) lines.Add("📦 Member of " + s.OwnerClass);
            if (s.Kind == UnrealSymbolKind.Class || s.Kind == UnrealSymbolKind.Struct || s.Kind == UnrealSymbolKind.Interface)
            {
                var info = CacheService.Instance?.GetClassInfo(s.Name);
                if (info?.InheritanceChain != null && info.InheritanceChain.Count > 0)
                    lines.Add("🔗 " + s.Name + " → " + string.Join(" → ", info.InheritanceChain));
                else if (!string.IsNullOrEmpty(info?.BaseClass))
                    lines.Add("🔗 " + s.Name + " → " + info.BaseClass);
            }
            if (!string.IsNullOrEmpty(s.Comment)) lines.Add("📝 " + s.Comment.Trim());
            lines.Add(s.IsEngineSymbol ? "[Engine]" : "[Project]");
            return lines;
        }

        public static string PlainText(UnrealSymbol s) => Header(s) + "\n" + string.Join("\n", Body(s));
    }
}
