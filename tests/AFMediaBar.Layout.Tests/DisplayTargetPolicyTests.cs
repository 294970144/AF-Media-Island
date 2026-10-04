using AFMediaBar.Classes.Models;
using AFMediaBar.Classes.Services;
using AFMediaBar.Classes.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;

namespace AFMediaBar.Layout.Tests;

/// <summary>显示器目标、全屏边界和物理像素放置测试。 / Display-target, fullscreen-boundary, and physical-pixel placement tests.</summary>
[TestClass]
public sealed class DisplayTargetPolicyTests
{
    [TestMethod]
    public void FixedTargetFallsBackWithoutDiscardingReconnectPreference()
    {
        var primary = Monitor("DISPLAY1", true, new Rect(0, 0, 1920, 1040), 96);
        var secondary = Monitor("DISPLAY2", false, new Rect(-2560, 0, 2560, 1400), 144);

        Assert.AreSame(secondary, DisplayTargetPolicy.ResolveFixed([primary, secondary], "display2"));
        Assert.AreSame(primary, DisplayTargetPolicy.ResolveFixed([primary], "DISPLAY2"));
        Assert.AreSame(secondary, DisplayTargetPolicy.ResolveFixed([primary, secondary], "DISPLAY2"));
        Assert.AreSame(secondary, DisplayTargetPolicy.ResolveFixed([secondary], "missing"));
        Assert.IsNull(DisplayTargetPolicy.ResolveFixed([], "missing"));
    }

    [TestMethod]
    public void ExplicitTaskbarTargetsSupportOneOrManyWithPrimaryFirst()
    {
        var primary = Monitor("DISPLAY1", true, new Rect(0, 0, 1920, 1040), 96);
        var left = Monitor("DISPLAY3", false, new Rect(-1920, 0, 1920, 1040), 96);
        var right = Monitor("DISPLAY2", false, new Rect(1920, 0, 2560, 1400), 144);

        CollectionAssert.AreEqual(
            new[] { "DISPLAY1", "DISPLAY2", "DISPLAY3" },
            TaskbarTargetPolicy.ResolveDeviceIds(
                [right, left, primary],
                ["DISPLAY3", "DISPLAY1", "DISPLAY2"]).ToArray());
        CollectionAssert.AreEqual(
            new[] { "DISPLAY2" },
            TaskbarTargetPolicy.ResolveDeviceIds([primary, right], ["DISPLAY2"]).ToArray());
        CollectionAssert.AreEqual(
            new[] { "DISPLAY1" },
            TaskbarTargetPolicy.ResolveDeviceIds([primary, right], ["DISCONNECTED"]).ToArray());
        CollectionAssert.AreEqual(
            new[] { "DISPLAY1", "DISPLAY2" },
            TaskbarTargetPolicy.ResolveDeviceIds(
                [right, primary],
                null,
                TaskbarTargetPolicy.LegacyAllTaskbarsDeviceId).ToArray());
    }

    [TestMethod]
    public void MultipleTaskbarsPublishTheSharedLengthIntersection()
    {
        var constraints = new TaskbarLengthConstraintsService();
        var first = new object();
        var second = new object();

        constraints.Update(first, 240, 900);
        constraints.Update(second, 320, 700);
        Assert.AreEqual(320, constraints.MinimumLengthDip);
        Assert.AreEqual(700, constraints.MaximumLengthDip);

        constraints.Remove(second);
        Assert.AreEqual(240, constraints.MinimumLengthDip);
        Assert.AreEqual(900, constraints.MaximumLengthDip);
    }

    [TestMethod]
    public void NotificationTargetUsesForegroundThenFixedAndPrimaryFallbacks()
    {
        var primary = Monitor("DISPLAY1", true, new Rect(0, 0, 1920, 1040), 96);
        var fixedTarget = Monitor("DISPLAY2", false, new Rect(1920, 0, 2560, 1400), 120);
        var foreground = Monitor("DISPLAY3", false, new Rect(-1920, 0, 1920, 1040), 144);
        var monitors = new[] { primary, fixedTarget, foreground };

        Assert.AreSame(foreground, DisplayTargetPolicy.ResolveNotification(
            monitors, NotificationTargetMode.ForegroundWindow, "DISPLAY2", "DISPLAY3"));
        Assert.AreSame(fixedTarget, DisplayTargetPolicy.ResolveNotification(
            monitors, NotificationTargetMode.ForegroundWindow, "DISPLAY2", null));
        Assert.AreSame(primary, DisplayTargetPolicy.ResolveNotification(
            monitors, NotificationTargetMode.ForegroundWindow, "missing", "missing"));
        Assert.AreSame(fixedTarget, DisplayTargetPolicy.ResolveNotification(
            monitors, NotificationTargetMode.Fixed, "DISPLAY2", "DISPLAY3"));
    }

    [TestMethod]
    public void SixPositionsUseNegativePhysicalWorkAreaAndClampOversizedWindows()
    {
        var area = new Rect(-1920, 0, 1920, 1040);
        var size = new Size(360, 120);

        Assert.AreEqual(new Point(-1904, 16), NotificationPlacementCalculator.Calculate(area, size, TrackChangeNotificationPosition.TopLeft, 16));
        Assert.AreEqual(new Point(-1140, 16), NotificationPlacementCalculator.Calculate(area, size, TrackChangeNotificationPosition.TopCenter, 16));
        Assert.AreEqual(new Point(-376, 16), NotificationPlacementCalculator.Calculate(area, size, TrackChangeNotificationPosition.TopRight, 16));
        Assert.AreEqual(new Point(-1904, 904), NotificationPlacementCalculator.Calculate(area, size, TrackChangeNotificationPosition.BottomLeft, 16));
        Assert.AreEqual(new Point(-1140, 904), NotificationPlacementCalculator.Calculate(area, size, TrackChangeNotificationPosition.BottomCenter, 16));
        Assert.AreEqual(new Point(-376, 904), NotificationPlacementCalculator.Calculate(area, size, TrackChangeNotificationPosition.BottomRight, 16));

        Assert.AreEqual(
            new Point(-1904, 16),
            NotificationPlacementCalculator.Calculate(area, new Size(4000, 2000), TrackChangeNotificationPosition.BottomRight, 16));
    }

    [TestMethod]
    public void DipSizeConvertsAtCommonEffectiveDpiValues()
    {
        Assert.AreEqual(new Size(360, 120), NotificationPlacementCalculator.ToPhysicalSize(new Size(360, 120), 96, 96));
        Assert.AreEqual(new Size(450, 150), NotificationPlacementCalculator.ToPhysicalSize(new Size(360, 120), 120, 120));
        Assert.AreEqual(new Size(540, 180), NotificationPlacementCalculator.ToPhysicalSize(new Size(360, 120), 144, 144));
        Assert.AreEqual(new Size(360, 120), NotificationPlacementCalculator.ToPhysicalSize(new Size(360, 120), 0, 0));
    }

    [TestMethod]
    public void FullscreenCoverageAllowsSmallFrameToleranceOnly()
    {
        var monitor = new Rect(-1920, 0, 1920, 1080);
        Assert.IsTrue(ForegroundFullscreenPolicy.IsFullscreen(new Rect(-1921, -1, 1922, 1082), monitor, isMaximized: false));
        Assert.IsTrue(ForegroundFullscreenPolicy.IsFullscreen(new Rect(-1919, 1, 1918, 1078), monitor, isMaximized: false));
        Assert.IsFalse(ForegroundFullscreenPolicy.IsFullscreen(new Rect(-1900, 20, 1880, 1040), monitor, isMaximized: false));
        Assert.IsFalse(ForegroundFullscreenPolicy.IsFullscreen(Rect.Empty, monitor, isMaximized: false));
    }

    /// <summary>
    /// 无任务栏的显示器上，最大化与全屏的窗口矩形逐像素相同——只有窗口状态能把二者分开。
    ///
    /// 这条钉的是实机踩过的坑：笔记本副屏默认不显示任务栏，浏览器在副屏最大化后窗口矩形恰好等于整块屏幕，
    /// 纯几何判据把它当成全屏，灵动岛于是收起；焦点一离开又变可见，岛体反复闪断。实测日志里媒体会话全程
    /// <c>connected=True</c> 没断过一次，闪断期间只有任务栏 Z 序告警——纯几何判据是唯一的元凶。
    ///
    /// 带上 <c>isMaximized: true</c> 时必须返回 false，哪怕矩形完全覆盖显示器。
    /// On a display with no taskbar, a maximized window and a fullscreen one have pixel-identical rectangles — only the
    /// window state separates them.
    ///
    /// This pins a bug hit on real hardware: a laptop's secondary display shows no taskbar by default, so a browser
    /// maximized there has a rectangle equal to the whole screen. The pure geometry test called that fullscreen, the
    /// island retracted, and focus changes made it reappear — a flicker. The measured log showed the media session
    /// <c>connected=True</c> throughout, with only a taskbar Z-order warning during the flicker: the geometry verdict
    /// was the sole culprit.
    ///
    /// It must return false whenever <c>isMaximized</c> is set, even when the rectangle covers the display exactly.
    /// </summary>
    [TestMethod]
    public void AMaximizedWindowIsNotFullscreenEvenWhenItCoversTheWholeDisplay()
    {
        var monitor = new Rect(0, 0, 2560, 1440);

        // 无任务栏的副屏上，最大化就是这个矩形——与全屏无法用几何区分。
        // On a taskbar-less secondary display, maximized is exactly this rectangle — indistinguishable from fullscreen
        // by geometry alone.
        var maximizedWindow = new Rect(0, 0, 2560, 1440);

        Assert.IsFalse(
            ForegroundFullscreenPolicy.IsFullscreen(maximizedWindow, monitor, isMaximized: true),
            "最大化不是全屏播放。用户看得见标题栏与边框，不该让岛体退让。");

        // 同一个矩形，不带最大化标记时才算全屏——这条保证判据没有被写死成「永不成立」。
        // The same rectangle without the maximized flag does count as fullscreen — this keeps the test from passing
        // simply because the verdict was hardwired to false.
        Assert.IsTrue(
            ForegroundFullscreenPolicy.IsFullscreen(maximizedWindow, monitor, isMaximized: false),
            "不带最大化标记时，覆盖整块屏幕仍应判全屏，否则这条测试就只是在检查判据被写死。");
    }

    private static DisplayMonitorInfo Monitor(string id, bool primary, Rect workArea, uint dpi) =>
        new(id, id, primary, workArea, workArea, dpi, dpi);
}
