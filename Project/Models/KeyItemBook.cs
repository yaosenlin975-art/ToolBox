using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace ToolBox.Models;

public class KeyItemBook
{
    private readonly Dictionary<Key, KeyItem> keys = new();

    public KeyItemBook AddKeyItem(KeyItem key)
    {
        keys[key.KeyCode] = key;
        return this;
    }

    public KeyItemBook AddKeyItem(IEnumerable<KeyItem> keyList)
    {
        foreach (var key in keyList)
            AddKeyItem(key);
        return this;
    }

    public KeyItem FindKeyItem(Key key)
    {
        keys.TryGetValue(key, out var item);
        return item;
    }

    public KeyItemBook Clear()
    {
        keys.Clear();
        return this;
    }

    public IEnumerable<KeyItem> GetAllKeys() => keys.Values;

    public int Count => keys.Count;
}
