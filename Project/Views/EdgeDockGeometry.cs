using System.Windows;

namespace ToolBox.Views;

internal enum DockSide { None, Left, Right, Top, Bottom }

internal static class EdgeDockGeometry
{
    // All geometry here is in physical screen pixels, including negative monitor origins.
    internal static DockSide FindSide(Rect window, Rect work, double thresholdX, double thresholdY)
    {
        double left = Math.Max(0, work.Left - window.Left);
        double right = Math.Max(0, window.Right - work.Right);
        double top = Math.Max(0, work.Top - window.Top);
        double bottom = Math.Max(0, window.Bottom - work.Bottom);
        double overflow = Math.Max(Math.Max(left, right), Math.Max(top, bottom));
        if (overflow > 0)
        {
            if (left == overflow) return DockSide.Left;
            if (right == overflow) return DockSide.Right;
            if (top == overflow) return DockSide.Top;
            return DockSide.Bottom;
        }

        double nearLeft = window.Left - work.Left;
        double nearRight = work.Right - window.Right;
        double nearTop = window.Top - work.Top;
        double nearBottom = work.Bottom - window.Bottom;
        double nearest = Math.Min(Math.Min(nearLeft, nearRight), Math.Min(nearTop, nearBottom));
        DockSide side = nearLeft == nearest ? DockSide.Left
            : nearRight == nearest ? DockSide.Right
            : nearTop == nearest ? DockSide.Top
            : DockSide.Bottom;
        return nearest <= (side is DockSide.Left or DockSide.Right ? thresholdX : thresholdY)
            ? side : DockSide.None;
    }

    internal static bool IsOuterEdge(Rect window, Rect work, DockSide side, IEnumerable<Rect> monitors)
    {
        if (side == DockSide.None) return false;
        Point center = new(work.Left + work.Width / 2, work.Top + work.Height / 2);
        var adjacent = monitors.Where(m => !m.Contains(center));
        return side switch
        {
            DockSide.Left => !adjacent.Any(m => work.Left - 1 >= m.Left && work.Left - 1 < m.Right &&
                Math.Min(window.Bottom, m.Bottom) > Math.Max(window.Top, m.Top)),
            DockSide.Right => !adjacent.Any(m => work.Right >= m.Left && work.Right < m.Right &&
                Math.Min(window.Bottom, m.Bottom) > Math.Max(window.Top, m.Top)),
            DockSide.Top => !adjacent.Any(m => work.Top - 1 >= m.Top && work.Top - 1 < m.Bottom &&
                Math.Min(window.Right, m.Right) > Math.Max(window.Left, m.Left)),
            DockSide.Bottom => !adjacent.Any(m => work.Bottom >= m.Top && work.Bottom < m.Bottom &&
                Math.Min(window.Right, m.Right) > Math.Max(window.Left, m.Left)),
            _ => false
        };
    }

    internal static Point TargetPosition(Rect work, Rect window, DockSide side, bool hidden, double strip)
    {
        double x = Math.Clamp(window.Left, work.Left, Math.Max(work.Left, work.Right - window.Width));
        double y = Math.Clamp(window.Top, work.Top, Math.Max(work.Top, work.Bottom - window.Height));
        switch (side)
        {
            case DockSide.Left:
                x = work.Left - (hidden ? Math.Max(0, window.Width - strip) : 0);
                break;
            case DockSide.Right:
                x = work.Right - (hidden ? Math.Min(window.Width, strip) : window.Width);
                break;
            case DockSide.Top:
                y = work.Top - (hidden ? Math.Max(0, window.Height - strip) : 0);
                break;
            case DockSide.Bottom:
                y = work.Bottom - (hidden ? Math.Min(window.Height, strip) : window.Height);
                break;
        }
        return new Point(x, y);
    }
}
