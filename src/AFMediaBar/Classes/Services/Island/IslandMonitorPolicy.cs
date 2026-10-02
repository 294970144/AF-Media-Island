using AFMediaBar.Classes.Models;

namespace AFMediaBar.Classes.Services;

/// <summary>
/// 岛体所在显示器的解析纯策略：按窗口当前占据的物理区域反查它落在哪块屏上，落点判定与回退链都在这里，
/// 不碰 Win32 也不碰窗口，便于用负坐标与混合 DPI 的夹具回归测试。
/// Pure policy for resolving which display the island sits on: it looks up the display covering the window's
/// current physical rectangle and owns the fallback chain, touching neither Win32 nor the window so it can be
/// tested with negative-coordinate and mixed-DPI fixtures.
/// </summary>
/// <remarks>
/// 之前的实现固定取主屏，于是岛体被拖到副屏后，落点夹取又按主屏工作区把它拽回去——跨屏拖拽因此是坏的。
/// 这里改成按落点反查显示器，窗口跨到哪块屏就按那块屏的工作区与 DPI 算数。
/// The previous implementation always took the primary display, so after dragging the island to a secondary
/// display the clamp pulled it straight back: cross-screen dragging was broken. Resolving by the landing point
/// makes the window use the work area and DPI of the display it actually landed on.
/// </remarks>
public static class IslandMonitorPolicy
{
    /// <summary>
    /// 反查窗口当前所在显示器：优先取「窗口中心点落在其物理边界内」的屏，没有则取与窗口中心最近的那块，
    /// 都没有时回退到主屏、再回退到首屏。窗口尺寸取 DIP，需按 <paramref name="dpiX"/>/<paramref name="dpiY"/>
    /// 换算成物理像素后再比较。
    /// Resolves the display the window currently sits on: the display whose physical bounds contain the window
    /// center wins, otherwise the nearest one by center distance, otherwise the primary, otherwise the first.
    /// </summary>
    /// <param name="monitors">显示器快照。 / The display snapshot.</param>
    /// <param name="windowTopLeftDip">窗口左上角（DIP）。 / The window's top-left corner in DIP.</param>
    /// <param name="windowSizeDip">窗口尺寸（DIP）。 / The window size in DIP.</param>
    /// <param name="dpiX">换算用的横向 DPI（0 视作 96）。 / Horizontal DPI for the conversion (0 is treated as 96).</param>
    /// <param name="dpiY">换算用的纵向 DPI（0 视作 96）。 / Vertical DPI for the conversion (0 is treated as 96).</param>
    /// <returns>命中的显示器；快照为空时返回 null。 / The matching display, or null for an empty snapshot.</returns>
    public static DisplayMonitorInfo? ResolveByWindow(
        IReadOnlyList<DisplayMonitorInfo> monitors,
        Point windowTopLeftDip,
        Size windowSizeDip,
        uint dpiX,
        uint dpiY)
    {
        if (monitors is null || monitors.Count == 0)
            return null;

        var scaleX = dpiX == 0 ? 96d : dpiX / 96d;
        var scaleY = dpiY == 0 ? 96d : dpiY / 96d;
        var centerPhysical = new Point(
            (windowTopLeftDip.X + windowSizeDip.Width / 2d) * scaleX,
            (windowTopLeftDip.Y + windowSizeDip.Height / 2d) * scaleY);

        DisplayMonitorInfo? nearest = null;
        var nearestDistance = double.MaxValue;
        foreach (var monitor in monitors)
        {
            if (monitor.MonitorArea.Contains(centerPhysical))
                return monitor;

            var distance = SquaredDistanceToRect(centerPhysical, monitor.MonitorArea);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = monitor;
            }
        }

        return nearest ?? Fallback(monitors);
    }

    /// <summary>点落在矩形外时到矩形的最小平方距离；落在内部时为 0。 / The smallest squared distance from a point to a rect; zero when inside.</summary>
    private static double SquaredDistanceToRect(Point point, Rect rect)
    {
        if (rect.IsEmpty)
            return double.MaxValue;

        var dx = Math.Max(rect.Left - point.X, 0d) + Math.Max(point.X - rect.Right, 0d);
        var dy = Math.Max(rect.Top - point.Y, 0d) + Math.Max(point.Y - rect.Bottom, 0d);
        return (dx * dx) + (dy * dy);
    }

    private static DisplayMonitorInfo? Fallback(IReadOnlyList<DisplayMonitorInfo> monitors)
    {
        foreach (var monitor in monitors)
        {
            if (monitor.IsPrimary)
                return monitor;
        }

        return monitors.Count > 0 ? monitors[0] : null;
    }
}
