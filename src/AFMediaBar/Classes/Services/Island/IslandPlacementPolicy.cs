namespace AFMediaBar.Classes.Services;

/// <summary>
/// 灵动岛（顶部悬浮胶囊）的尺寸与屏幕定位纯策略：只做几何换算，不持有窗口与显示器服务，便于回归测试。
/// Pure placement math for the floating island capsule: geometry only, no window or monitor-service ownership,
/// which is what keeps it unit-testable.
/// 坐标约定与 <see cref="NotificationPlacementCalculator"/> 一致：显示器快照是物理像素，窗口 Left/Top 是 DIP。
/// Coordinate convention matches <see cref="NotificationPlacementCalculator"/>: monitor snapshots are physical
/// pixels while window Left/Top are DIP.
/// </summary>
public static class IslandPlacementPolicy
{
    /// <summary>紧凑态宽度（DIP）。 / Compact width in DIP.</summary>
    public const double CompactWidthDip = 340;

    /// <summary>紧凑态高度（DIP）。 / Compact height in DIP.</summary>
    public const double CompactHeightDip = 46;

    /// <summary>展开态的理想高度（DIP）；实际高度还会被工作区夹取。 / Ideal expanded height in DIP; still clamped by the work area.</summary>
    public const double ExpandedHeightDip = 236;

    /// <summary>岛体与屏幕顶边的悬浮间距（DIP）。 / Gap between the island and the screen top edge, in DIP.</summary>
    public const double TopMarginDip = 10;

    /// <summary>把 DIP 尺寸换算为目标显示器的物理像素（与通知定位同一算法，dpi 为 0 时按 96 处理）。 / Converts a DIP size to the target display's physical pixels.</summary>
    public static Size ToPhysicalSize(Size dipSize, uint dpiX, uint dpiY)
    {
        var scaleX = (dpiX == 0 ? 96u : dpiX) / 96d;
        var scaleY = (dpiY == 0 ? 96u : dpiY) / 96d;
        var width = double.IsFinite(dipSize.Width) ? Math.Max(0, dipSize.Width) : 0;
        var height = double.IsFinite(dipSize.Height) ? Math.Max(0, dipSize.Height) : 0;
        return new Size(Math.Ceiling(width * scaleX), Math.Ceiling(height * scaleY));
    }

    /// <summary>
    /// 计算岛体顶部居中时的窗口左上角（DIP）：先在物理工作区里居中、加顶部悬浮间距，再换算回 DIP。
    /// Top-center placement for the island: center inside the physical work area, add the top margin,
    /// then convert the top-left corner back to DIP.
    /// </summary>
    public static Point CalculateTopCenter(Rect workArea, Size dipSize, uint dpiX, uint dpiY)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0)
            return new Point(0, 0);

        var scaleX = (dpiX == 0 ? 96u : dpiX) / 96d;
        var scaleY = (dpiY == 0 ? 96u : dpiY) / 96d;
        var physical = ToPhysicalSize(dipSize, dpiX, dpiY);
        var x = workArea.Left + (workArea.Width - physical.Width) / 2;
        var y = workArea.Top + TopMarginDip * scaleY;
        return new Point(x / scaleX, y / scaleY);
    }

    /// <summary>
    /// 把窗口左上角（DIP）夹回工作区：拖拽落点与展开后的下边界都走这里，防止岛体被甩出屏幕。
    /// Clamps the window's top-left corner (DIP) back into the work area; both a drag landing and an
    /// expansion's bottom edge funnel through here so the island can never be thrown off-screen.
    /// </summary>
    public static Point ClampWithinWorkArea(Point topLeftDip, Size dipSize, Rect workArea, uint dpiX, uint dpiY)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0)
            return topLeftDip;

        var scaleX = (dpiX == 0 ? 96u : dpiX) / 96d;
        var scaleY = (dpiY == 0 ? 96u : dpiY) / 96d;
        var physical = ToPhysicalSize(dipSize, dpiX, dpiY);
        var minX = workArea.Left / scaleX;
        var maxX = (workArea.Right - physical.Width) / scaleX;
        var minY = workArea.Top / scaleY;
        var maxY = (workArea.Bottom - physical.Height) / scaleY;
        if (maxX < minX) maxX = minX;
        if (maxY < minY) maxY = minY;
        return new Point(Math.Clamp(topLeftDip.X, minX, maxX), Math.Clamp(topLeftDip.Y, minY, maxY));
    }

    /// <summary>
    /// 展开态的实际高度：不超过工作区剩余可用高度，且永远不小于紧凑态高度。
    /// Actual expanded height: capped by the work area's remaining height and never below the compact height.
    /// </summary>
    public static double ResolveExpandedHeight(Rect workArea, uint dpiX, uint dpiY)
    {
        var scaleY = (dpiY == 0 ? 96u : dpiY) / 96d;
        var availableDip = workArea.IsEmpty ? ExpandedHeightDip : workArea.Height / scaleY - TopMarginDip;
        return Math.Clamp(Math.Min(ExpandedHeightDip, availableDip), CompactHeightDip, ExpandedHeightDip);
    }
}
