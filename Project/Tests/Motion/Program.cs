using System.Windows;
using ToolBox.Views;

int checks = 0;
void Assert(bool condition, string name)
{
    checks++;
    if (!condition) throw new InvalidOperationException($"Failed: {name}");
}

var negativeWork = new Rect(-1920, 0, 1920, 1080);
var negativeWindow = new Rect(-1920, 100, 300, 300);
Assert(EdgeDockGeometry.FindSide(negativeWindow, negativeWork, 12, 12) == DockSide.Left,
    "negative monitor origin");
Assert(EdgeDockGeometry.TargetPosition(negativeWork, negativeWindow, DockSide.Left, true, 8) == new Point(-2212, 100),
    "left target strip at negative origin");

var work = new Rect(0, 0, 1920, 1080);
foreach (var (scaleX, scaleY) in new[] { (1.0, 1.0), (1.5, 1.5), (2.0, 2.0), (1.25, 1.75) })
{
    double thresholdX = scaleX * 12, thresholdY = scaleY * 12;
    Assert(EdgeDockGeometry.FindSide(new Rect(thresholdX, 50, 300, 200), work, thresholdX, thresholdY) == DockSide.Left,
        $"left edge at {thresholdX}px ({scaleX:P0})");
    Assert(EdgeDockGeometry.FindSide(new Rect(thresholdX + 1, 50, 300, 200), work, thresholdX, thresholdY) == DockSide.None,
        $"left edge beyond {thresholdX}px ({scaleX:P0})");
    Assert(EdgeDockGeometry.FindSide(new Rect(work.Right - 300 - thresholdX, 50, 300, 200), work, thresholdX, thresholdY) == DockSide.Right,
        $"right edge at {thresholdX}px ({scaleX:P0})");
    Assert(EdgeDockGeometry.FindSide(new Rect(work.Right - 300 - thresholdX - 1, 50, 300, 200), work, thresholdX, thresholdY) == DockSide.None,
        $"right edge beyond {thresholdX}px ({scaleX:P0})");
}

Assert(EdgeDockGeometry.FindSide(new Rect(-80, 100, 300, 200), work, 12, 12) == DockSide.Left,
    "large left overflow still docks");
Assert(EdgeDockGeometry.FindSide(new Rect(1900, 100, 100, 200), work, 12, 12) == DockSide.Right,
    "large right overflow still docks");
Assert(EdgeDockGeometry.FindSide(new Rect(100, -80, 300, 200), work, 12, 12) == DockSide.Top,
    "large top overflow still docks");
Assert(EdgeDockGeometry.FindSide(new Rect(100, 1000, 300, 100), work, 12, 12) == DockSide.Bottom,
    "large bottom overflow still docks");
Assert(EdgeDockGeometry.FindSide(new Rect(-40, -60, 300, 200), work, 12, 12) == DockSide.Top,
    "deeper vertical corner overflow wins");
Assert(EdgeDockGeometry.FindSide(new Rect(-40, -40, 300, 200), work, 12, 12) == DockSide.Left,
    "equal horizontal and vertical overflow prefers left");
Assert(EdgeDockGeometry.FindSide(new Rect(-40, 100, 2000, 200), work, 12, 12) == DockSide.Left,
    "equal left and right overflow prefers left");
Assert(EdgeDockGeometry.FindSide(new Rect(100, 20, 300, 200), work, 24, 16) == DockSide.None,
    "vertical threshold uses vertical DPI scale");

Assert(EdgeDockGeometry.TargetPosition(work, new Rect(0, 100, 300, 200), DockSide.Left, true, 8) == new Point(-292, 100),
    "left hidden target preserves strip");
Assert(EdgeDockGeometry.TargetPosition(work, new Rect(1620, 100, 300, 200), DockSide.Right, true, 8) == new Point(1912, 100),
    "right hidden target preserves strip");
Assert(EdgeDockGeometry.TargetPosition(work, new Rect(0, 100, 300, 200), DockSide.Left, false, 8) == new Point(0, 100),
    "left visible target");
Assert(EdgeDockGeometry.TargetPosition(work, new Rect(1620, 100, 300, 200), DockSide.Right, false, 8) == new Point(1620, 100),
    "right visible target");
Assert(EdgeDockGeometry.TargetPosition(work, new Rect(500, 0, 300, 200), DockSide.Top, true, 8) == new Point(500, -192),
    "top hidden target preserves horizontal strip");
Assert(EdgeDockGeometry.TargetPosition(work, new Rect(500, 872, 300, 200), DockSide.Bottom, true, 8) == new Point(500, 1072),
    "bottom hidden target preserves horizontal strip");
Assert(EdgeDockGeometry.TargetPosition(work, new Rect(-50, 40, 300, 200), DockSide.Top, false, 8) == new Point(0, 0),
    "top visible target clamps other axis");
Assert(EdgeDockGeometry.TargetPosition(work, new Rect(2000, 900, 300, 200), DockSide.Bottom, false, 8) == new Point(1620, 880),
    "bottom visible target clamps other axis");
Assert(EdgeDockGeometry.TargetPosition(negativeWork, new Rect(-1900, 1000, 300, 200), DockSide.None, false, 0) == new Point(-1900, 880),
    "unattached target clamps on negative-origin display");

var primaryWindow = new Rect(0, 200, 600, 400);
var primary = new Rect(0, 0, 1920, 1080);
var leftNeighbor = new Rect(-1920, 0, 1920, 1080);
var rightNeighbor = new Rect(1920, 0, 1920, 1080);
var topNeighbor = new Rect(0, -1080, 1920, 1080);
var bottomNeighbor = new Rect(0, 1080, 1920, 1080);
Assert(!EdgeDockGeometry.IsOuterEdge(primaryWindow, primary, DockSide.Left, new[] { primary, leftNeighbor }),
    "left edge at adjacent monitor seam");
Assert(!EdgeDockGeometry.IsOuterEdge(primaryWindow, primary, DockSide.Right, new[] { primary, rightNeighbor }),
    "right edge at adjacent monitor seam");
Assert(!EdgeDockGeometry.IsOuterEdge(new Rect(600, 0, 600, 400), primary, DockSide.Top, new[] { primary, topNeighbor }),
    "top edge at adjacent monitor seam");
Assert(!EdgeDockGeometry.IsOuterEdge(new Rect(600, 680, 600, 400), primary, DockSide.Bottom, new[] { primary, bottomNeighbor }),
    "bottom edge at adjacent monitor seam");

var offsetNeighbor = new Rect(-1920, 250, 1920, 1080);
var noOverlapNeighbor = new Rect(-1920, 600, 1920, 1080);
Assert(!EdgeDockGeometry.IsOuterEdge(primaryWindow, primary, DockSide.Left, new[] { primary, offsetNeighbor }),
    "vertically offset adjacent monitor overlaps window");
Assert(EdgeDockGeometry.IsOuterEdge(primaryWindow, primary, DockSide.Left, new[] { primary, noOverlapNeighbor }),
    "adjacent monitor without vertical overlap");
var workBelowTaskbar = new Rect(0, 40, 1920, 1040);
Assert(EdgeDockGeometry.IsOuterEdge(new Rect(600, 40, 600, 400), workBelowTaskbar, DockSide.Top, new[] { primary }),
    "current monitor does not block its own work-area edge");

Assert(EdgeDockGeometry.FindSide(new Rect(100, 100, 300, 200), work, 12, 12) == DockSide.None,
    "no edge within threshold");
Assert(!EdgeDockGeometry.IsOuterEdge(primaryWindow, primary, DockSide.None, new[] { primary }),
    "none is not an outer edge");

Console.WriteLine($"Passed {checks} EdgeDockGeometry checks.");
