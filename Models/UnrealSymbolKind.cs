namespace PenguinExtention.Models
{
    /// <summary>
    /// Categorizes Unreal Engine symbols for filtering, display, and icon assignment.
    /// Integer values are persisted in SQLite — do not reorder.
    /// </summary>
    public enum UnrealSymbolKind
    {
        Class = 0,
        Struct = 1,
        Enum = 2,
        EnumValue = 3,
        Function = 4,
        Property = 5,
        Delegate = 6,
        Macro = 7,
        Interface = 8
    }
}
