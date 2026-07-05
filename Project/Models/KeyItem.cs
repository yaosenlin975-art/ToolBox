using System;
using System.Windows.Input;

namespace ToolBox.Models;

public class KeyItem
{
    public Key KeyCode { get; set; } = Key.None;
    public int StyleId { get; set; }

    public KeyItem() { }

    public KeyItem(Key key, int styleId)
    {
        KeyCode = key;
        StyleId = styleId;
    }

    public KeyItem(int rawKey, int styleId)
    {
        KeyCode = (Key)rawKey;
        StyleId = styleId;
    }

    public override string ToString()
    {
        var modifiers = new System.Text.StringBuilder();
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            modifiers.Append("Ctrl+");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            modifiers.Append("Shift+");
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
            modifiers.Append("Alt+");
        modifiers.Append(KeyCode);
        return modifiers.ToString();
    }
}
