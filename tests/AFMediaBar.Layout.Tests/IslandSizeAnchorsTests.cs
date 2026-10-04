using AFMediaBar.Classes.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;

namespace AFMediaBar.Layout.Tests;

/// <summary>灵动岛尺寸锚点的回归测试。 / Regression tests for the island size anchors.</summary>
[TestClass]
public sealed class IslandSizeAnchorsTests
{
    [TestMethod]
    public void CaptureRecordsEveryAnchor()
    {
        var anchors = IslandSizeAnchors.Capture(new Point(100, 200), new Size(340, 46));

        Assert.AreEqual(100, anchors.LeftDip);
        Assert.AreEqual(200, anchors.TopDip);
        Assert.AreEqual(440, anchors.RightDip);
        Assert.AreEqual(246, anchors.BottomDip);
        Assert.AreEqual(270, anchors.CenterXDip);
        Assert.AreEqual(223, anchors.CenterYDip);
    }

    [TestMethod]
    public void CaptureTreatsLayoutlessWindowAsZeroInsteadOfNaN()
    {
        // 窗口尚未布局时 Left/Top 是 NaN：让它流进锚点会把之后每一步都污染成 NaN。
        // Before layout Left/Top are NaN; letting that through would poison every later step.
        var anchors = IslandSizeAnchors.Capture(new Point(double.NaN, double.NaN), new Size(double.NaN, double.NaN));

        Assert.AreEqual(0, anchors.LeftDip);
        Assert.AreEqual(0, anchors.TopDip);
        Assert.AreEqual(0, anchors.RightDip);
        Assert.AreEqual(0, anchors.BottomDip);
        Assert.AreEqual(0, anchors.CenterXDip);
        Assert.AreEqual(0, anchors.CenterYDip);
    }

    [TestMethod]
    public void CenterAndTopKeepTheirPositionWhenTheHeightGrows()
    {
        var anchors = IslandSizeAnchors.Capture(new Point(100, 200), new Size(340, 46));
        var resolved = anchors.ResolveTopLeft(new Size(340, 236), IslandAnchorX.Center, IslandAnchorY.Top);

        // 本项目形态的默认锚点对：横向中心不动、顶边不动，于是展开只会向下生长。
        // The pair this project's shape defaults to: centre and top stay put, so expansion grows downward only.
        Assert.AreEqual(100, resolved.X);
        Assert.AreEqual(200, resolved.Y);
    }

    [TestMethod]
    public void CenterAnchorKeepsMiddleWhenTheWidthChanges()
    {
        var anchors = IslandSizeAnchors.Capture(new Point(100, 200), new Size(340, 46));
        var resolved = anchors.ResolveTopLeft(new Size(480, 46), IslandAnchorX.Center, IslandAnchorY.Top);

        // 中心从 270 不动，新宽 480 → 左上角左移到 30，右边也正好等量外扩 70。
        // The centre stays at 270 and the new width is 480, so the left edge moves to 30 while the right edge grows by
        // exactly the same 70.
        Assert.AreEqual(30, resolved.X, "宽度变化时两侧应等量生长。/ Growth should be equal on both sides.");
        Assert.AreEqual(200, resolved.Y);
    }

    [TestMethod]
    public void RightAndBottomAnchorsKeepTheOppositeEdge()
    {
        var anchors = IslandSizeAnchors.Capture(new Point(100, 200), new Size(340, 46));

        var rightResolved = anchors.ResolveTopLeft(new Size(480, 46), IslandAnchorX.Right, IslandAnchorY.Top);
        Assert.AreEqual(-40, rightResolved.X);
        Assert.AreEqual(200, rightResolved.Y);

        var bottomResolved = anchors.ResolveTopLeft(new Size(340, 236), IslandAnchorX.Center, IslandAnchorY.Bottom);
        Assert.AreEqual(100, bottomResolved.X);
        Assert.AreEqual(10, bottomResolved.Y);

        var centerResolved = anchors.ResolveTopLeft(new Size(340, 236), IslandAnchorX.Center, IslandAnchorY.Center);
        Assert.AreEqual(100, centerResolved.X);
        Assert.AreEqual(105, centerResolved.Y);
    }

    [TestMethod]
    public void ResolveSurvivesUnknownSizeAndNeverEmitsNaN()
    {
        var anchors = IslandSizeAnchors.Capture(new Point(10, 20), new Size(30, 40));
        var resolved = anchors.ResolveTopLeft(new Size(double.NaN, double.NaN), IslandAnchorX.Center, IslandAnchorY.Bottom);

        Assert.IsFalse(double.IsNaN(resolved.X));
        Assert.IsFalse(double.IsNaN(resolved.Y));
    }
}
