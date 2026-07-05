using System;

namespace ToolBox.Core.Windows;

public struct WindowInfo
{
    public static WindowInfo Empty { get; } = new() { Handle = IntPtr.Zero };

    public IntPtr Handle { get; set; }
    public string TitleName { get; set; }
    public string ClassName { get; set; }
    public int ZOrder { get; set; }
    public int Left { get; set; }
    public int Top { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    public bool IsEmpty => Handle == IntPtr.Zero;

    public override string ToString() =>
        $"Handle:{Handle},Title:{TitleName},Class:{ClassName},Rect:({Left},{Top},{Width},{Height}),ZOrder:{ZOrder}";

    public override int GetHashCode() => ~Handle.ToInt32();

    public override bool Equals(object obj) => obj is WindowInfo other && Handle == other.Handle;

    public static bool operator ==(WindowInfo left, WindowInfo right) => left.Handle == right.Handle;
    public static bool operator !=(WindowInfo left, WindowInfo right) => left.Handle != right.Handle;
}

