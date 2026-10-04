namespace AFMediaBar.Classes.Services;

/// <summary>
/// DPI 切换时用「工作区归一化中心」保住岛体相对位置的纯算法：不碰窗口与显示器服务，便于回归测试。
/// Pure math that preserves the island's relative position across a DPI change using a work-area-normalised centre;
/// it touches neither the window nor the display service, which is what keeps it testable.
///
/// 为什么用归一化而不是绝对坐标：DPI 变化时窗口的 DIP 尺寸与实际占据的物理像素之间的换算会变，
/// 拿「切换前那个左上角」去套新工作区，位置会漂。归一化中心是相对量（占工作区的比例），
/// 先记下来、等切换完成后再按新工作区还原，换算误差就不进来了。同理不用显示器序号：
/// 系统可能把窗口挪到另一块屏，序号这时候是错的。
/// Why normalise instead of keeping an absolute coordinate: a DPI change alters the conversion between the window's
/// DIP size and the physical pixels it occupies, so applying the pre-switch top-left corner to the new work area
/// drifts. A normalised centre is a relative quantity (a fraction of the work area), recorded before and restored
/// against the new work area afterwards, so the conversion error never enters. The same reasoning rules out the
/// monitor index: the system may have moved the window to another display, in which case the index is wrong.
/// </summary>
public static class IslandDpiRestorePolicy
{
    /// <summary>抓不到有效几何时的兜底中心：工作区横向中点。 / Fallback centre when no valid geometry exists: the work area's horizontal midpoint.</summary>
    public const double FallbackNormalizedCenterX = 0.5;

    /// <summary>
    /// 把窗口当前的横向中心折算成工作区里的归一化横坐标（0~1）。
    /// Normalises the window's current horizontal centre into the work area (0..1).
    /// </summary>
    /// <param name="workArea">窗口所在显示器的工作区（物理像素）。 / Work area of the display the window sits on (physical pixels).</param>
    /// <param name="topLeftDip">窗口左上角（DIP）。 / Window top-left corner (DIP).</param>
    /// <param name="sizeDip">窗口尺寸（DIP）。 / Window size (DIP).</param>
    /// <param name="dpiX">横向 DPI。 / Horizontal DPI.</param>
    /// <param name="dpiY">纵向 DPI。 / Vertical DPI.</param>
    /// <returns>归一化中心（0~1）；工作区不可用时返回 <see cref="FallbackNormalizedCenterX"/>。 / Normalised centre (0..1), or <see cref="FallbackNormalizedCenterX"/> when the work area is unusable.</returns>
    public static double CaptureNormalizedCenterX(Rect workArea, Point topLeftDip, Size sizeDip, uint dpiX, uint dpiY)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0)
            return FallbackNormalizedCenterX;

        var scaleX = (dpiX == 0u ? 96u : dpiX) / 96d;
        var left = double.IsFinite(topLeftDip.X) ? topLeftDip.X * scaleX : 0d;
        var width = double.IsFinite(sizeDip.Width) ? Math.Max(0d, sizeDip.Width) * scaleX : 0d;
        var centerX = left + (width / 2d);
        var normalized = (centerX - workArea.Left) / workArea.Width;
        return double.IsFinite(normalized) ? Math.Clamp(normalized, 0d, 1d) : FallbackNormalizedCenterX;
    }

    /// <summary>
    /// 按归一化中心把窗口放回新的工作区。横向按记下的相对位置还原，纵向回到贴顶悬浮点——
    /// 本项目的岛体是「顶部居中、固定不可拖」的形态，横向该跟着用户上次看到的位置，纵向没有自由度可言。
    /// Restores the window into the new work area from the normalised centre: horizontally it follows the recorded
    /// relative position, vertically it returns to the top margin. This project's island is "top-centred, fixed, not
    /// draggable", so horizontally it should follow where the user last saw it while vertically there is no freedom.
    /// </summary>
    /// <param name="workArea">切换后的工作区（物理像素）。 / Work area after the switch (physical pixels).</param>
    /// <param name="sizeDip">窗口尺寸（DIP）。 / Window size (DIP).</param>
    /// <param name="normalizedCenterX">切换前记下的归一化中心。 / Normalised centre recorded before the switch.</param>
    /// <param name="dpiX">横向 DPI。 / Horizontal DPI.</param>
    /// <param name="dpiY">纵向 DPI。 / Vertical DPI.</param>
    /// <returns>应当落到的工作区左上角坐标（DIP，已夹回工作区）。 / Top-left corner to land on (DIP, already clamped into the work area).</returns>
    public static Point RestoreTopLeft(Rect workArea, Size sizeDip, double normalizedCenterX, uint dpiX, uint dpiY)
    {
        if (workArea.IsEmpty || workArea.Width <= 0 || workArea.Height <= 0)
            return new Point(0d, 0d);

        var scaleX = (dpiX == 0u ? 96u : dpiX) / 96d;
        var scaleY = (dpiY == 0u ? 96u : dpiY) / 96d;
        var width = double.IsFinite(sizeDip.Width) ? Math.Max(0d, sizeDip.Width) : 0d;
        var height = double.IsFinite(sizeDip.Height) ? Math.Max(0d, sizeDip.Height) : 0d;
        var normalized = double.IsFinite(normalizedCenterX)
            ? Math.Clamp(normalizedCenterX, 0d, 1d)
            : FallbackNormalizedCenterX;

        // 先在物理像素里对准：归一化中心指向的点减去半个窗口宽度，才是该放的左上角。
        // Aim in physical pixels first: the normalised centre minus half the window width is the top-left to use.
        var leftPx = workArea.Left + (normalized * workArea.Width) - (width * scaleX / 2d);
        var topPx = workArea.Top + (IslandPlacementPolicy.TopMarginDip * scaleY);
        var candidate = new Point(leftPx / scaleX, topPx / scaleY);
        return IslandPlacementPolicy.ClampWithinWorkArea(candidate, new Size(width, height), workArea, dpiX, dpiY);
    }
}
