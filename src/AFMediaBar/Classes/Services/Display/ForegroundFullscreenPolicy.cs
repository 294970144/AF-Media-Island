namespace AFMediaBar.Classes.Services;

/// <summary>
/// 判断前台窗口是否真处于全屏播放状态。
/// Determines whether the foreground window is genuinely in fullscreen playback.
/// </summary>
/// <remarks>
/// <para>
/// 判据是「窗口矩形是否盖住整块显示器」，但**必须先排除最大化**。
///
/// 只看矩形是一场灾难：在**没有任务栏的显示器**上，最大化窗口的矩形恰好等于整块屏幕，与全屏逐像素相同。
/// 而这类显示器恰恰是最容易触发的一台——笔记本的副屏默认不显示任务栏。用户把浏览器在副屏最大化，
/// 岛体就被判成「有人在前台全屏」而收起；焦点一离开又变回可见，于是岛体反复闪断。
///
/// 这不是理论推演：本机实测日志里，媒体会话全程 <c>connected=True</c> 未断一次，闪断期间只有任务栏 Z 序告警。
///
/// <para>
/// The test is "does the window rectangle cover the whole display", but a maximized window must be excluded first.
///
/// Rectangle alone is a disaster: on a <b>display with no taskbar</b> a maximized window's rectangle equals the entire
/// screen, pixel for pixel identical to fullscreen. Such displays are exactly the ones most likely to trigger this —
/// a laptop's secondary display shows no taskbar by default. Maximize a browser there and the island is judged
/// "something is fullscreen in the foreground" and retracts; the moment focus moves away it becomes visible again,
/// so the island flickers.
///
/// This is not speculation: on the real machine, the media session stayed <c>connected=True</c> throughout while the
/// island flickered, and the only related log line was a taskbar Z-order warning.
/// </para>
/// </remarks>
public static class ForegroundFullscreenPolicy
{
    /// <summary>
    /// 按允许的像素误差判断全屏覆盖。
    /// <paramref name="isMaximized"/> 为真时一律返回 false：最大化窗口占着整块屏幕，但用户看得见它的标题栏与边框，
    /// 它不是全屏播放，不该让旁的东西退让。
    /// Tests fullscreen coverage with a physical-pixel tolerance. A maximized window always returns false: it occupies the
    /// whole screen, but the user can still see its title bar and border, so it is not fullscreen playback and nothing
    /// else should yield to it.
    /// </summary>
    /// <param name="windowBounds">前台窗口矩形（物理像素）。 / The foreground window rectangle in physical pixels.</param>
    /// <param name="monitorBounds">该窗口所在显示器的整块矩形（物理像素）。 / The whole rectangle of the display that window is on.</param>
    /// <param name="isMaximized">窗口是否处于最大化状态。 / Whether the window is maximized.</param>
    /// <param name="tolerance">允许的物理像素误差。 / The physical-pixel tolerance.</param>
    public static bool IsFullscreen(Rect windowBounds, Rect monitorBounds, bool isMaximized, double tolerance = 2)
    {
        // 最大化先判：它与全屏在无任务栏显示器上矩形完全相同，只有窗口状态能把二者分开。
        // Maximized is checked first: on a taskbar-less display it has a rectangle identical to fullscreen, and only the
        // window state can tell them apart.
        if (isMaximized)
            return false;

        if (windowBounds.IsEmpty || monitorBounds.IsEmpty ||
            windowBounds.Width <= 0 || windowBounds.Height <= 0 ||
            monitorBounds.Width <= 0 || monitorBounds.Height <= 0)
        {
            return false;
        }

        var safeTolerance = double.IsFinite(tolerance) ? Math.Max(0, tolerance) : 0;
        return windowBounds.Left <= monitorBounds.Left + safeTolerance &&
               windowBounds.Top <= monitorBounds.Top + safeTolerance &&
               windowBounds.Right >= monitorBounds.Right - safeTolerance &&
               windowBounds.Bottom >= monitorBounds.Bottom - safeTolerance;
    }
}
