using System.Windows;
using ToolBox.Views;

int checks = 0;
void Assert(bool condition, string name)
{
    checks++;
    if (!condition) throw new InvalidOperationException($"Failed: {name}");
}

var negativeWork = new Rect(-1920, 0, 1920, 1080);
Assert(EdgeDockGeometry.FindSide(new Rect(-1920, 100, 400, 300), negativeWork, 12) == DockSide.Left,
    "negative monitor origin");
Assert(EdgeDockGeometry.TargetLeft(negativeWork, 300, DockSide.Left, true, 8) == -2212,
    "left target strip at negative origin");

var work = new Rect(0, 0, 1920, 1080);
foreach (var (scale, logicalThreshold) in new[] { (1.0, 12.0), (1.5, 12.0), (2.0, 12.0) })
{
    var physicalThreshold = scale * logicalThreshold;
    var leftAtThreshold = new Rect(-physicalThreshold, 50, 300, 200);
    var leftPastThreshold = new Rect(-physicalThreshold - 1, 50, 300, 200);
    var rightAtThreshold = new Rect(work.Right - 300 + physicalThreshold, 50, 300, 200);
    var rightPastThreshold = new Rect(work.Right - 300 + physicalThreshold + 1, 50, 300, 200);

    Assert(EdgeDockGeometry.FindSide(leftAtThreshold, work, physicalThreshold) == DockSide.Left,
        $"left edge at {physicalThreshold}px ({scale:P0})");
    Assert(EdgeDockGeometry.FindSide(leftPastThreshold, work, physicalThreshold) == DockSide.None,
        $"left edge beyond {physicalThreshold}px ({scale:P0})");
    Assert(EdgeDockGeometry.FindSide(rightAtThreshold, work, physicalThreshold) == DockSide.Right,
        $"right edge at {physicalThreshold}px ({scale:P0})");
    Assert(EdgeDockGeometry.FindSide(rightPastThreshold, work, physicalThreshold) == DockSide.None,
        $"right edge beyond {physicalThreshold}px ({scale:P0})");
}

Assert(EdgeDockGeometry.TargetLeft(work, 300, DockSide.Left, true, 8) == -292,
    "left hidden target preserves strip");
Assert(EdgeDockGeometry.TargetLeft(work, 300, DockSide.Right, true, 8) == 1912,
    "right hidden target preserves strip");
Assert(EdgeDockGeometry.TargetLeft(work, 300, DockSide.Left, false, 8) == 0,
    "left visible target");
Assert(EdgeDockGeometry.TargetLeft(work, 300, DockSide.Right, false, 8) == 1620,
    "right visible target");

var primaryWindow = new Rect(0, 200, 600, 400);
var primary = new Rect(0, 0, 1920, 1080);
var leftNeighbor = new Rect(-1920, 0, 1920, 1080);
var rightNeighbor = new Rect(1920, 0, 1920, 1080);
Assert(!EdgeDockGeometry.IsOuterEdge(primaryWindow, primary, DockSide.Left, new[] { primary, leftNeighbor }),
    "left edge at adjacent monitor seam");
Assert(!EdgeDockGeometry.IsOuterEdge(primaryWindow, primary, DockSide.Right, new[] { primary, rightNeighbor }),
    "right edge at adjacent monitor seam");

var offsetNeighbor = new Rect(-1920, 250, 1920, 1080);
var noOverlapNeighbor = new Rect(-1920, 600, 1920, 1080);
Assert(!EdgeDockGeometry.IsOuterEdge(primaryWindow, primary, DockSide.Left, new[] { primary, offsetNeighbor }),
    "vertically offset adjacent monitor overlaps window");
Assert(EdgeDockGeometry.IsOuterEdge(primaryWindow, primary, DockSide.Left, new[] { primary, noOverlapNeighbor }),
    "adjacent monitor without vertical overlap");

var awayFromEdge = new Rect(100, 100, 300, 200);
Assert(EdgeDockGeometry.FindSide(awayFromEdge, work, 12) == DockSide.None, "no edge within threshold");
Assert(!EdgeDockGeometry.IsOuterEdge(awayFromEdge, work, DockSide.None, new[] { work }), "none is not an outer edge");

Console.WriteLine($"Passed {checks} EdgeDockGeometry checks.");
