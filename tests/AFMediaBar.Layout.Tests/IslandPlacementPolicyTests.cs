using AFMediaBar.Classes.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Windows;

namespace AFMediaBar.Layout.Tests;

/// <summary>
/// 灵动岛落位几何的边界测试：换算方向、DPI 基准、空/负/非有限输入的兜底，以及窗口比工作区还大时
/// 的退化情形。这里钉的是「岛体永远不能被甩出屏幕」这条不变量的四周边界。
/// Boundary tests for the island's placement geometry: conversion direction, DPI baseline, empty/negative/non-finite
/// fallbacks, and the degenerate case where the window is larger than the work area. They pin down the edges of
/// the invariant "the island can never be thrown off-screen".
/// </summary>
[TestClass]
public sealed class IslandPlacementPolicyTests
{
    // 紧凑态 340×46，展开态理想高度 236，顶部悬浮间距 10（DIP）。
    // Compact 340×46, ideal expanded height 236, top margin 10 (DIP).
    private static readonly Size CompactSize = new(340, 46);

    [TestMethod]
    public void ToPhysicalSizeScalesByDpiAndCeils()
    {
        // 恰好整数倍时应当原样返回：ceil 不该把居中算出来的整数推歪。
        // Exact multiples must come back unchanged: ceiling must not skew the integers centring relies on.
        var at96 = IslandPlacementPolicy.ToPhysicalSize(CompactSize, 96, 96);
        Assert.AreEqual(340d, at96.Width, 1e-9);
        Assert.AreEqual(46d, at96.Height, 1e-9);

        var at192 = IslandPlacementPolicy.ToPhysicalSize(CompactSize, 192, 192);
        Assert.AreEqual(680d, at192.Width, 1e-9);
        Assert.AreEqual(92d, at192.Height, 1e-9);
    }

    [TestMethod]
    public void ToPhysicalSizeTreatsZeroDpiAsBaseline96()
    {
        // 显示器快照拿不到 DPI（dpi=0）时必须按 96 基准处理，否则整个换算会变成除零或全零尺寸。
        // A monitor snapshot without DPI (0) has to fall back to the 96 baseline, or every conversion turns into a
        // divide-by-zero or an all-zero size.
        var physical = IslandPlacementPolicy.ToPhysicalSize(CompactSize, 0, 0);

        Assert.AreEqual(340d, physical.Width, 1e-9);
        Assert.AreEqual(46d, physical.Height, 1e-9);
    }

    [TestMethod]
    public void ToPhysicalSizeCollapsesNonFiniteToZero()
    {
        // NaN/Inf 是能真实进来的：WPF 的 Size 只挡负值而不挡 NaN，窗口布局前的 ActualWidth 正是 NaN。
        // NaN and Inf genuinely arrive here: WPF's Size rejects negatives but not NaN, and ActualWidth is exactly
        // NaN before layout.
        var nonsense = IslandPlacementPolicy.ToPhysicalSize(new Size(double.NaN, double.PositiveInfinity), 96, 96);

        Assert.AreEqual(0d, nonsense.Width, 1e-9, "NaN/Inf 必须兜成 0，不能让它流进 GDI 区域尺寸");
        Assert.AreEqual(0d, nonsense.Height, 1e-9);

        // 负值这里测不到：Size 的构造函数自己就抛 ArgumentException，负尺寸从来构造不出来。
        // ToPhysicalSize 里的 Math.Max(0, …) 因此是纯防御性的——留着它，是为了不把这条防线交给调用方。
        // Negatives are untestable here: Size's own constructor throws ArgumentException, so a negative Size never
        // exists. The Math.Max(0, …) inside ToPhysicalSize is purely defensive — it stays so that guard is never
        // left to the caller.
    }

    [TestMethod]
    public void CalculateTopCenterCentresHorizontallyAndAppliesTopMargin()
    {
        var workArea = new Rect(0, 0, 1920, 1080);

        var topLeft = IslandPlacementPolicy.CalculateTopCenter(workArea, CompactSize, 96, 96);

        Assert.AreEqual(790d, topLeft.X, 1e-9, "(1920-340)/2");
        Assert.AreEqual(10d, topLeft.Y, 1e-9, "顶边距 10 DIP");
    }

    [TestMethod]
    public void CalculateTopCenterKeepsTheSameDipSpotAcrossDpi()
    {
        // 同一块屏在 96 与 192 DPI 下必须落在同一个 DIP 坐标上：先在物理像素里居中、再除回 DIP，
        // 若在这里先除或后乘搞错一档，岛体就会随缩放漂移。
        // The same display at 96 and 192 DPI must land on the same DIP coordinate: centring happens in physical
        // pixels and only then divides back. Getting either order wrong makes the island drift with scaling.
        var doubled = new Rect(0, 0, 3840, 2160);

        var topLeft = IslandPlacementPolicy.CalculateTopCenter(doubled, CompactSize, 192, 192);

        Assert.AreEqual(790d, topLeft.X, 1e-9);
        Assert.AreEqual(10d, topLeft.Y, 1e-9);
    }

    [TestMethod]
    public void CalculateTopCenterHonoursAWorkAreaThatDoesNotStartAtZero()
    {
        // 副屏的工作区通常不是从 (0,0) 起算：忘了加 workArea 的原点，岛体会跑到主屏上去。
        // A secondary display's work area rarely starts at (0,0): forgetting its origin sends the island onto the
        // primary display.
        var workArea = new Rect(100, 50, 1000, 800);

        var topLeft = IslandPlacementPolicy.CalculateTopCenter(workArea, CompactSize, 96, 96);

        Assert.AreEqual(430d, topLeft.X, 1e-9, "100 + (1000-340)/2");
        Assert.AreEqual(60d, topLeft.Y, 1e-9, "50 + 10");
    }

    [TestMethod]
    public void CalculateTopCenterFallsBackToOriginOnAnUselessWorkArea()
    {
        // 显示器快照缺失时给出原点，而不是 NaN：NaN 落进 Window.Left 会抛异常并直接杀掉窗口。
        // A missing monitor snapshot yields the origin rather than NaN: NaN into Window.Left throws and kills the
        // window outright.
        var empty = IslandPlacementPolicy.CalculateTopCenter(Rect.Empty, CompactSize, 96, 96);
        Assert.AreEqual(new Point(0, 0), empty);

        var flat = IslandPlacementPolicy.CalculateTopCenter(new Rect(0, 0, 1920, 0), CompactSize, 96, 96);
        Assert.AreEqual(new Point(0, 0), flat);
    }

    [TestMethod]
    public void ClampPullsEveryEdgeBackInsideTheWorkArea()
    {
        var workArea = new Rect(0, 0, 1920, 1080);

        var tooLeft = IslandPlacementPolicy.ClampWithinWorkArea(new Point(-500, 10), CompactSize, workArea, 96, 96);
        Assert.AreEqual(0d, tooLeft.X, 1e-9, "左边界之外必须夹回 0");

        var tooRight = IslandPlacementPolicy.ClampWithinWorkArea(new Point(2000, 10), CompactSize, workArea, 96, 96);
        Assert.AreEqual(1580d, tooRight.X, 1e-9, "(1920-340) 是左侧的最大值");

        var tooLow = IslandPlacementPolicy.ClampWithinWorkArea(
            new Point(100, 5000), CompactSize, workArea, 96, 96);
        Assert.AreEqual(1034d, tooLow.Y, 1e-9, "(1080-46) 是顶部的最大值");
    }

    [TestMethod]
    public void ClampPrefersTheTopWhenTheWindowIsTallerThanTheWorkArea()
    {
        // 退化情形：矮屏上展开态比可用高度还高。此时 maxY 会小于 minY，不修正的话 Math.Clamp 会
        // 因为 min>max 直接抛异常，把窗口炸掉。这里必须退到「贴着顶边」。
        // Degenerate case: the expanded shape is taller than the available height on a short display. Then maxY
        // drops below minY, and without the guard Math.Clamp itself throws for min>max and kills the window. It has
        // to fall back to hugging the top edge.
        var shortWorkArea = new Rect(0, 0, 1920, 200);
        var expanded = new Size(340, 236);

        var clamped = IslandPlacementPolicy.ClampWithinWorkArea(new Point(100, 100), expanded, shortWorkArea, 96, 96);

        Assert.AreEqual(100d, clamped.X, 1e-9, "横向仍有余量，不该被改动");
        Assert.AreEqual(0d, clamped.Y, 1e-9, "装不下时贴顶，而不是崩掉");
    }

    [TestMethod]
    public void ClampLeavesAnAlreadyValidPositionUntouched()
    {
        var workArea = new Rect(0, 0, 1920, 1080);

        var valid = IslandPlacementPolicy.ClampWithinWorkArea(new Point(790, 10), CompactSize, workArea, 96, 96);

        Assert.AreEqual(790d, valid.X, 1e-9, "夹取必须是幂等的，静止时反复调用不该偷偷挪动岛体");
        Assert.AreEqual(10d, valid.Y, 1e-9);
    }

    [TestMethod]
    public void ClampReturnsTheInputWhenTheWorkAreaIsUnknown()
    {
        // 工作区未知时原样返回：宁可让上一轮的位置生效，也不要把岛体硬拽到一个凭空猜的点上。
        // With no work area the input comes straight back: better to keep the last position than to force the
        // island onto some invented coordinate.
        var original = new Point(640, 30);

        var clamped = IslandPlacementPolicy.ClampWithinWorkArea(original, CompactSize, Rect.Empty, 96, 96);

        Assert.AreEqual(original, clamped);
    }

    [TestMethod]
    public void ResolveExpandedHeightUsesTheIdealWhenThereIsRoom()
    {
        var height = IslandPlacementPolicy.ResolveExpandedHeight(new Rect(0, 0, 1920, 1080), 96, 96);

        Assert.AreEqual(236d, height, 1e-9);
    }

    [TestMethod]
    public void ResolveExpandedHeightCapsAtWhatTheWorkAreaCanSpare()
    {
        // 可用高度 = 工作区高度 - 顶部悬浮间距。矮屏上展开态必须让位给它。
        // Available height = work-area height minus the top margin; expansion yields to it on a short display.
        var height = IslandPlacementPolicy.ResolveExpandedHeight(new Rect(0, 0, 1920, 150), 96, 96);

        Assert.AreEqual(140d, height, 1e-9, "150 - 10");
    }

    [TestMethod]
    public void ResolveExpandedHeightNeverDropsBelowTheCompactHeight()
    {
        // 极端矮的工作区也不能让展开态变得比胶囊还矮：那样弹簧会在同一个值上 ReTarget，
        // 展开动画整体失效。
        // Even an absurdly short work area must not make the expanded shape shorter than the capsule: the spring
        // would retarget to its own value and the whole animation would silently vanish.
        var height = IslandPlacementPolicy.ResolveExpandedHeight(new Rect(0, 0, 1920, 20), 96, 96);

        Assert.AreEqual(46d, height, 1e-9);
    }

    [TestMethod]
    public void ResolveExpandedHeightAccountsForDpiBeforeComparing()
    {
        // 工作区是物理像素，必须先按 scaleY 折算成 DIP 再与 DIP 常量比较。直接比较会把
        // 200% 缩放的高分屏误判成「屏幕很高」，然后在矮屏上弹出一个超出下边界的面板。
        // The work area is in physical pixels and has to be divided by scaleY before meeting a DIP constant.
        // Comparing raw would read a 200% hidpi screen as "very tall" and pop a panel past the bottom edge.
        var height = IslandPlacementPolicy.ResolveExpandedHeight(new Rect(0, 0, 3840, 432), 192, 192);

        Assert.AreEqual(206d, height, 1e-9, "432/2 - 10");
    }

    [TestMethod]
    public void ResolveExpandedHeightOnAnUnknownWorkAreaReturnsTheIdeal()
    {
        var height = IslandPlacementPolicy.ResolveExpandedHeight(Rect.Empty, 96, 96);

        Assert.AreEqual(236d, height, 1e-9, "快照缺失时按理想高度起步，后续 update 会把它夹回来");
    }
}
