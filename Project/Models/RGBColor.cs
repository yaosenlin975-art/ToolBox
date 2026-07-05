using System.Windows.Media;

namespace ToolBox.Models;

public struct RGBColor
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }

    public RGBColor(byte red, byte green, byte blue)
    {
        R = red;
        G = green;
        B = blue;
    }

    public RGBColor(Color color)
    {
        R = color.R;
        G = color.G;
        B = color.B;
    }

    public Color GetColor() => Color.FromRgb(R, G, B);

    public RGBColor SetR(byte red)
    {
        R = red;
        return this;
    }

    public RGBColor SetG(byte green)
    {
        G = green;
        return this;
    }

    public RGBColor SetB(byte blue)
    {
        B = blue;
        return this;
    }
}
