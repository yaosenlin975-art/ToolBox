using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Xml.Serialization;

namespace ToolBox.Models;

public interface IStyleItem
{
    string GetName();
    string GetDisplayName();
    string GetDescription();
    string StateText { get; }
    string NameAndState { get; }
    bool IsTerminate { get; }
    bool IsInitApply { get; }
    bool IsSetting { get; }
    void Apply(ScrapWindow scrap, Point clickPoint);
    object Clone();
}

public class CStyle
{
    private static int makeStyleId;

    public string StyleName { get; set; } = "";
    public int StyleId { get; set; }

    [XmlIgnore]
    public List<IStyleItem> Items { get; set; } = new();

    // ponytail: XmlSerializer can't serialize List<IStyleItem>; surrogate stores
    // type name + JSON per item so config.xml round-trips without a shared base class.
    [XmlElement("Item")]
    public List<SerializableStyleItem> ItemsXml
    {
        get => Items.ConvertAll(i => new SerializableStyleItem
        {
            TypeName = i.GetType().AssemblyQualifiedName ?? "",
            Json = JsonSerializer.Serialize(i, i.GetType())
        });
        set
        {
            Items = new List<IStyleItem>();
            if (value == null) return;
            foreach (var s in value)
            {
                var type = Type.GetType(s.TypeName);
                if (type == null) continue;
                var restored = JsonSerializer.Deserialize(s.Json, type);
                if (restored is IStyleItem item) Items.Add(item);
            }
        }
    }

    public List<KeyItem> KeyItems { get; set; } = new();

    public CStyle()
    {
        StyleId = ++makeStyleId;
    }

    public CStyle AddStyle(IStyleItem item)
    {
        Items.Add(item);
        return this;
    }

    public CStyle AddKeyItem(Key key)
    {
        KeyItems.Add(new KeyItem(key, StyleId));
        return this;
    }

    public CStyle AddKeyItem(int rawKey)
    {
        KeyItems.Add(new KeyItem(rawKey, StyleId));
        return this;
    }

    public virtual void Apply(ScrapWindow scrap, Point clickPoint)
    {
        foreach (var item in Items)
        {
            if (item.IsTerminate)
                break;
            item.Apply(scrap, clickPoint);
        }
    }

    public CStyle DeepCopy()
    {
        var clone = new CStyle
        {
            StyleName = StyleName,
            StyleId = StyleId
        };
        foreach (var item in Items)
            clone.Items.Add((IStyleItem)item.Clone());
        foreach (var key in KeyItems)
            clone.KeyItems.Add(new KeyItem(key.KeyCode, key.StyleId));
        return clone;
    }
}

// ponytail: trivial DTO so XmlSerializer handles the interface-typed style items.
public class SerializableStyleItem
{
    public string TypeName { get; set; } = "";
    public string Json { get; set; } = "";
}
