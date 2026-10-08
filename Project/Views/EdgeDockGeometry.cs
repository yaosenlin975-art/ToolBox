using System.Windows;

namespace ToolBox.Views;

internal enum DockSide { None, Left, Right }

internal static class EdgeDockGeometry
{
    // All geometry here is in physical screen pixels, including negative monitor origins.
    internal static DockSide FindSide(Rect window, Rect work, double threshold)
    {
        double left = Math.Abs(window.Left - work.Left);
        double right = Math.Abs(window.Right - work.Right);
        if (Math.Min(left, right) > threshold) return DockSide.None;
        return left <= right ? DockSide.Left : DockSide.Right;
    }

    internal static bool IsOuterEdge(Rect window, Rect work, DockSide side, IEnumerable<Rect> monitors)
    {
        if (side == DockSide.None) return false;
        double x = side == DockSide.Left ? work.Left - 1 : work.Right;
        return !monitors.Any(m => x >= m.Left && x < m.Right &&
            Math.Min(window.Bottom, m.Bottom) > Math.Max(window.Top, m.Top));
    }

    internal static double TargetLeft(Rect work, double width, DockSide side, bool hidden, double strip)
        => side == DockSide.Left
            ? work.Left - (hidden ? Math.Max(0, width - strip) : 0)
            : work.Right - (hidden ? Math.Min(width, strip) : width);
}
