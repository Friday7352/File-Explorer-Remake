using System.Windows.Input;

namespace Clearspace.Commands;

/// <summary>
/// A single thing Clearspace can do.
///
/// This is the extension point. To add a feature, write one class implementing this
/// interface and register it in <see cref="CommandManager"/>. The key binding, the
/// toolbar entry, the context menu entry, and the enabled/disabled state all follow
/// from the properties declared here.
/// </summary>
public interface IAction
{
    /// <summary>Identity used for key binding persistence and lookup.</summary>
    CommandCode Code { get; }

    string Label { get; }

    string Description { get; }

    /// <summary>Segoe Fluent Icons glyph, or empty for no icon.</summary>
    string Glyph => string.Empty;

    HotKey HotKey => HotKey.None;

    /// <summary>Whether the action can run against the current context right now.</summary>
    bool IsExecutable => true;

    Task ExecuteAsync(object? parameter = null);
}

public enum CommandCode
{
    None = 0,

    // Navigation
    NavigateBack,
    NavigateForward,
    NavigateUp,
    NavigateHome,
    Refresh,
    FocusAddressBar,

    // File system
    OpenItem,
    Delete,
    DeletePermanently,
    Rename,
    CopyItem,
    CutItem,
    PasteItem,
    CopyPath,
    NewFolder,
    ShowProperties,

    // Selection
    SelectAll,
    ClearSelection,
    InvertSelection,

    // View
    ToggleHiddenItems,
    OpenTerminal
}

/// <summary>Keyboard chord for an action.</summary>
public readonly record struct HotKey(Key Key, ModifierKeys Modifiers = ModifierKeys.None)
{
    public static HotKey None => new(Key.None);

    public bool IsNone => Key == Key.None;

    public string Label
    {
        get
        {
            if (IsNone) return string.Empty;

            var parts = new List<string>(4);
            if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            parts.Add(Key.ToString());

            return string.Join('+', parts);
        }
    }
}
