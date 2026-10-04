using System.Windows;
using AFMediaBar.Classes.Models;
using AFMediaBar.Classes.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AFMediaBar.Layout.Tests;

/// <summary>岛体跨显示器解析的纯策略测试。 / Pure policy tests for resolving the island's display.</summary>
[TestClass]
public sealed class IslandMonitorPolicyTests
{
    private const double IslandWidth = 340d;
    private const double IslandHeight = 46d;

    private static DisplayMonitorInfo Monitor(string id, bool primary, Rect area, uint dpi) =>
        new(id, id, primary, area, area, dpi, dpi);

    [TestMethod]
    public void WindowOnThePrimaryDisplayResolvesToIt()
    {
        var primary = Monitor("DISPLAY1", true, new Rect(0, 0, 1920, 1040), 96);
        var secondary = Monitor("DISPLAY2", false, new Rect(1920, 0, 2560, 1400), 120);
        var monitors = new[] { primary, secondary };

        var resolved = IslandMonitorPolicy.ResolveByWindow(
            monitors, new Point(790, 10), new Size(IslandWidth, IslandHeight), 96, 96);

        Assert.AreEqual("DISPLAY1", resolved!.DeviceId);
    }

    [TestMethod]
    public void DraggedWindowResolvesToTheSecondaryDisplayInsteadOfThePrimary()
    {
        var primary = Monitor("DISPLAY1", true, new Rect(0, 0, 1920, 1040), 96);
        var secondary = Monitor("DISPLAY2", false, new Rect(1920, 0, 2560, 1400), 120);
        var monitors = new[] { primary, secondary };

        // 副屏物理左边界是 1920，120 DPI 下缩放 1.25，所以副屏的 DIP 左边界是 1920 / 1.25 = 1536。
        // 落点取 1600 DIP → 物理 2000，连同 340 DIP 宽的窗口一起落在副屏范围内。
        // The secondary display starts at 1920 physical; at 120 DPI the scale is 1.25, so its DIP left edge is
        // 1920 / 1.25 = 1536. A landing point of 1600 DIP is 2000 physical, well inside the secondary display.
        var resolved = IslandMonitorPolicy.ResolveByWindow(
            monitors, new Point(1600, 10), new Size(IslandWidth, IslandHeight), 120, 120);

        Assert.AreEqual("DISPLAY2", resolved!.DeviceId, "拖到副屏后必须按落点反查，而不是固定取主屏");
    }

    [TestMethod]
    public void NegativeCoordinateSecondaryDisplayResolvesCorrectly()
    {
        var primary = Monitor("DISPLAY1", true, new Rect(0, 0, 1920, 1040), 96);
        var left = Monitor("DISPLAY2", false, new Rect(-2560, 0, 2560, 1400), 144);
        var monitors = new[] { primary, left };

        var resolved = IslandMonitorPolicy.ResolveByWindow(
            monitors, new Point(-2000, 10), new Size(IslandWidth, IslandHeight), 144, 144);

        Assert.AreEqual("DISPLAY2", resolved!.DeviceId, "负坐标副屏（显示器摆在主屏左侧）也要能命中");
    }

    [TestMethod]
    public void WindowStraddlingTwoDisplaysResolvesToWhicheverContainsItsCenter()
    {
        var primary = Monitor("DISPLAY1", true, new Rect(0, 0, 1920, 1040), 96);
        var secondary = Monitor("DISPLAY2", false, new Rect(1920, 0, 2560, 1400), 96);
        var monitors = new[] { primary, secondary };

        // 窗口宽 340 DIP：左边缘 1900 时跨过分界线，但中心 2070 落在副屏。
        var straddling = IslandMonitorPolicy.ResolveByWindow(
            monitors, new Point(1900, 10), new Size(IslandWidth, IslandHeight), 96, 96);
        Assert.AreEqual("DISPLAY2", straddling!.DeviceId);

        var mostly = IslandMonitorPolicy.ResolveByWindow(
            monitors, new Point(1500, 10), new Size(IslandWidth, IslandHeight), 96, 96);
        Assert.AreEqual("DISPLAY1", mostly!.DeviceId, "中心在主屏就该归主屏");
    }

    [TestMethod]
    public void AnEmptySnapshotResolvesToNothing()
    {
        Assert.IsNull(IslandMonitorPolicy.ResolveByWindow(
            [], new Point(0, 0), new Size(IslandWidth, IslandHeight), 96, 96));
    }

    [TestMethod]
    public void DisplayWithoutAPrimaryFallsBackToTheFirstOne()
    {
        var first = Monitor("DISPLAY1", false, new Rect(0, 0, 1920, 1040), 96);
        var second = Monitor("DISPLAY2", false, new Rect(1920, 0, 1920, 1040), 96);

        // 窗口落在两块屏之间的空隙里，谁都不含它的中心，回退链必须给出一个可用结果。
        var resolved = IslandMonitorPolicy.ResolveByWindow(
            [first, second], new Point(1000, 5000), new Size(IslandWidth, IslandHeight), 96, 96);

        Assert.IsNotNull(resolved);
    }

    [TestMethod]
    public void AWindowFarOutsideEveryWorkAreaResolvesToTheNearestDisplay()
    {
        var primary = Monitor("DISPLAY1", true, new Rect(0, 0, 1920, 1040), 96);
        var secondary = Monitor("DISPLAY2", false, new Rect(1920, 0, 2560, 1400), 120);
        var monitors = new[] { primary, secondary };

        // 落点远在副屏右下、超出两块屏的工作区：按最近屏判定，而不是永远回退主屏。
        // The landing point is far past both work areas: the nearest display wins rather than a permanent
        // fallback to the primary.
        var resolved = IslandMonitorPolicy.ResolveByWindow(
            monitors, new Point(4400, 1300), new Size(IslandWidth, IslandHeight), 120, 120);

        Assert.AreEqual("DISPLAY2", resolved!.DeviceId);
    }

    [TestMethod]
    public void ResolvingUsesTheWindowDpiNotThePrimaryDisplayDpi()
    {
        // 主屏 100%、副屏 150%。窗口在副屏上，DPI 是 144；若错用主屏的 96 去换算，
        // 1300 DIP 会被当成 1300 物理像素，落在主屏范围内，反查就指错了屏。
        // Primary at 100%, secondary at 150%. The window is on the secondary display with DPI 144. Using the
        // primary's 96 instead would read 1300 DIP as 1300 physical pixels, land inside the primary, and the
        // lookup would name the wrong display.
        var primary = Monitor("DISPLAY1", true, new Rect(0, 0, 1920, 1040), 96);
        var secondary = Monitor("DISPLAY2", false, new Rect(1920, 0, 2560, 1400), 144);
        var monitors = new[] { primary, secondary };

        var resolved = IslandMonitorPolicy.ResolveByWindow(
            monitors, new Point(1300, 20), new Size(IslandWidth, IslandHeight), 144, 144);

        Assert.AreEqual("DISPLAY2", resolved!.DeviceId, "必须用窗口自己的 DPI 反查落点，不能用主屏 DPI 猜");
    }

    [TestMethod]
    public void ZeroDpiDegradesToNinetySixInsteadOfDividingByZero()
    {
        var primary = Monitor("DISPLAY1", true, new Rect(0, 0, 1920, 1040), 0);
        var monitors = new[] { primary };

        var resolved = IslandMonitorPolicy.ResolveByWindow(
            monitors, new Point(100, 10), new Size(IslandWidth, IslandHeight), 0, 0);

        Assert.IsNotNull(resolved, "DPI 报 0 时按 96 处理，不该产生 NaN");
        Assert.AreEqual("DISPLAY1", resolved!.DeviceId);
    }
}
