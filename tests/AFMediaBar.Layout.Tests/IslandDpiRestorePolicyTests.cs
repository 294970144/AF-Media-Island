using AFMediaBar.Classes.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;

namespace AFMediaBar.Layout.Tests;

/// <summary>DPI 归一化中心恢复的回归测试。 / Regression tests for the DPI-normalised-centre restore.</summary>
[TestClass]
public sealed class IslandDpiRestorePolicyTests
{
    private static readonly Rect WorkArea = new(0, 0, 1920, 1080);

    [TestMethod]
    public void CenterOfTheWindowBecomesHalfWhenPerfectlyCentered()
    {
        // 340 DIP 宽、96 DPI：中心落在 1920/2 → 归一化 0.5。
        // 340 DIP wide at 96 DPI: the centre lands at 1920/2, hence 0.5 normalised.
        var normalized = IslandDpiRestorePolicy.CaptureNormalizedCenterX(
            WorkArea, new Point(790, 10), new Size(340, 46), 96u, 96u);

        Assert.AreEqual(0.5, normalized, 0.0001);
    }

    [TestMethod]
    public void CaptureAndRestoreRoundTripKeepsTheSamePosition()
    {
        var topLeft = new Point(300, 10);
        var size = new Size(340, 46);

        var normalized = IslandDpiRestorePolicy.CaptureNormalizedCenterX(WorkArea, topLeft, size, 96u, 96u);
        var restored = IslandDpiRestorePolicy.RestoreTopLeft(WorkArea, size, normalized, 96u, 96u);

        Assert.AreEqual(topLeft.X, restored.X, 0.5);
        Assert.AreEqual(topLeft.Y, restored.Y, 0.5);
    }

    [TestMethod]
    public void RestoreHonoursTheNewWorkAreaInsteadOfTheOldPixels()
    {
        // 同一归一化中心，工作区从 1920 宽变成 3840 宽（换成 4K 屏）：绝对坐标必须跟着走，
        // 而不是沿用旧的像素数值。
        // Same normalised centre, work area grows from 1920 to 3840 wide (a 4K display swapped in): the absolute
        // coordinate has to follow instead of reusing the old pixel numbers.
        var size = new Size(340, 46);
        var wide = new Rect(0, 0, 3840, 2160);

        var restored = IslandDpiRestorePolicy.RestoreTopLeft(wide, size, 0.5, 96u, 96u);

        Assert.AreEqual(1750, restored.X, 0.5, "新工作区的横向中点才算还原。/ Only the new work area's midpoint counts.");
        Assert.AreEqual(10, restored.Y, 0.5, "纵向永远是贴顶悬浮点。/ Vertically it is always the top margin.");
    }

    [TestMethod]
    public void RestoreScalesWithDpiInsteadOfMixingPhysicalAndDip()
    {
        var size = new Size(340, 46);
        // 200% 缩放下：窗口物理宽度是 680px，落在横向中心 → 左上角物理 X = 960 - 340 = 620px，
        // 换回 DIP 要再除以 2 → 310 DIP。拿 620 当 DIP 用就会把岛体甩到屏幕右三分之一去。
        // At 200% the window is 680 physical px wide, so centring puts its left edge at 960 - 340 = 620 px, which is
        // 310 DIP once divided back down. Treating 620 as DIP would throw the island into the right third of the screen.
        var restored = IslandDpiRestorePolicy.RestoreTopLeft(WorkArea, size, 0.5, 192u, 192u);

        Assert.AreEqual(310, restored.X, 0.5);
        // 贴顶间距是 DIP 常量：不论缩放多少都应还是 10 DIP。
        // The top margin is a DIP constant: whatever the scaling, it must stay 10 DIP.
        Assert.AreEqual(10, restored.Y, 0.5);
    }

    [TestMethod]
    public void RestoreClampsBackIntoTheWorkArea()
    {
        var restored = IslandDpiRestorePolicy.RestoreTopLeft(WorkArea, new Size(340, 46), 1.0, 96u, 96u);

        Assert.IsTrue(restored.X <= 1920 - 340, "右边界不得溢出工作区。/ The right edge must not overflow the work area.");
        Assert.IsTrue(restored.X >= 0);
    }

    [TestMethod]
    public void UnusableGeometryFallsBackSafely()
    {
        Assert.AreEqual(
            IslandDpiRestorePolicy.FallbackNormalizedCenterX,
            IslandDpiRestorePolicy.CaptureNormalizedCenterX(Rect.Empty, new Point(10, 10), new Size(340, 46), 96u, 96u));

        var restored = IslandDpiRestorePolicy.RestoreTopLeft(Rect.Empty, new Size(340, 46), 0.5, 96u, 96u);
        Assert.AreEqual(0, restored.X);
        Assert.AreEqual(0, restored.Y);

        var noDpi = IslandDpiRestorePolicy.RestoreTopLeft(WorkArea, new Size(340, 46), double.NaN, 0u, 0u);
        Assert.IsFalse(double.IsNaN(noDpi.X));
        Assert.IsFalse(double.IsNaN(noDpi.Y));
    }
}
