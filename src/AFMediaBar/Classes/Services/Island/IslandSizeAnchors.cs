namespace AFMediaBar.Classes.Services;

/// <summary>尺寸变化后要盯住的横向参照。 / Which horizontal reference stays put after a size change.</summary>
public enum IslandAnchorX
{
    /// <summary>左边界不动。 / The left edge stays put.</summary>
    Left,

    /// <summary>横向中心不动：岛体向两侧等量生长。 / The horizontal centre stays put, so the island grows equally to both sides.</summary>
    Center,

    /// <summary>右边界不动。 / The right edge stays put.</summary>
    Right
}

/// <summary>尺寸变化后要盯住的纵向参照。 / Which vertical reference stays put after a size change.</summary>
public enum IslandAnchorY
{
    /// <summary>顶边不动：高度往下长。 / The top edge stays put, so height grows downward.</summary>
    Top,

    /// <summary>纵向中心不动。 / The vertical centre stays put.</summary>
    Center,

    /// <summary>下边界不动。 / The bottom edge stays put.</summary>
    Bottom
}

/// <summary>
/// 一次尺寸变化之前抓下来的六个锚点：左、上、右、下、横向中心、纵向中心（全为 DIP）。
///
/// 为什么必须在变化「之前」抓：窗口只有 Left/Top/Width/Height 四个通道，Width/Height 一旦落地，
/// 旧的边缘已经被覆盖，之后再想让「原来那个中心」不动就只剩猜测。
/// 这套做法取自原作者被删掉的那版实现（<c>DynamicIslandWindow.Animation</c>），他那里的窗口宽高都会变，
/// 因此要按贴靠边挑不同的锚点；本项目的岛体是顶部居中的固定宽度胶囊，默认只用（横向中心 + 顶边）这一对，
/// 但六个锚点都留着，形态将来要长宽变化时不必再回来补几何。
/// Six anchors captured before a size change: left, top, right, bottom, horizontal centre and vertical centre (all DIP).
///
/// Why capture before: a window only exposes Left/Top/Width/Height, and once Width/Height land, the old edges are
/// already overwritten — asking afterwards for "the centre it used to have" would be guesswork. The pattern comes
/// from the author's deleted implementation (<c>DynamicIslandWindow.Animation</c>), where both dimensions change and
/// different anchors are picked per docked edge. This project's island is a top-centred capsule of fixed width, so
/// it defaults to the single pair (horizontal centre + top edge), with all six kept so future shape changes do not
/// require reopening the geometry.
/// </summary>
/// <param name="LeftDip">变化前的左边界（DIP）。 / Left edge before the change (DIP).</param>
/// <param name="TopDip">变化前的上边界（DIP）。 / Top edge before the change (DIP).</param>
/// <param name="RightDip">变化前的右边界（DIP）。 / Right edge before the change (DIP).</param>
/// <param name="BottomDip">变化前的下边界（DIP）。 / Bottom edge before the change (DIP).</param>
/// <param name="CenterXDip">变化前的横向中心（DIP）。 / Horizontal centre before the change (DIP).</param>
/// <param name="CenterYDip">变化前的纵向中心（DIP）。 / Vertical centre before the change (DIP).</param>
public readonly record struct IslandSizeAnchors(
    double LeftDip,
    double TopDip,
    double RightDip,
    double BottomDip,
    double CenterXDip,
    double CenterYDip)
{
    /// <summary>
    /// 按窗口当前的左上角与尺寸抓一组锚点。非有限值（窗口尚未布局时 Left/Top 仍是 NaN）一律按 0 参与运算，
    /// 因为让 NaN 流进去会把后面每一步都污染成 NaN。
    /// Captures a set of anchors from the window's current top-left corner and size. Non-finite values (before layout
    /// Left/Top are still NaN) participate as 0, because letting NaN through would poison every later step.
    /// </summary>
    /// <param name="topLeftDip">窗口左上角（DIP）。 / Window top-left corner (DIP).</param>
    /// <param name="sizeDip">窗口尺寸（DIP）。 / Window size (DIP).</param>
    /// <returns>抓好的锚点组。 / The captured anchors.</returns>
    public static IslandSizeAnchors Capture(Point topLeftDip, Size sizeDip)
    {
        var left = double.IsFinite(topLeftDip.X) ? topLeftDip.X : 0d;
        var top = double.IsFinite(topLeftDip.Y) ? topLeftDip.Y : 0d;
        var width = double.IsFinite(sizeDip.Width) ? Math.Max(0d, sizeDip.Width) : 0d;
        var height = double.IsFinite(sizeDip.Height) ? Math.Max(0d, sizeDip.Height) : 0d;
        return new IslandSizeAnchors(
            left,
            top,
            left + width,
            top + height,
            left + (width / 2d),
            top + (height / 2d));
    }

    /// <summary>
    /// 按指定的锚点对算出新尺寸下的左上角，使那条被选参照保持原位。
    /// Resolves the top-left corner for a new size so that the chosen reference keeps its position.
    /// </summary>
    /// <param name="newSizeDip">变化后的窗口尺寸（DIP）。 / Window size after the change (DIP).</param>
    /// <param name="anchorX">横向保留哪条参照。 / Which horizontal reference to keep.</param>
    /// <param name="anchorY">纵向保留哪条参照。 / Which vertical reference to keep.</param>
    /// <returns>新尺寸下的左上角（DIP）。 / Top-left corner for the new size (DIP).</returns>
    public Point ResolveTopLeft(Size newSizeDip, IslandAnchorX anchorX, IslandAnchorY anchorY)
    {
        var width = double.IsFinite(newSizeDip.Width) ? Math.Max(0d, newSizeDip.Width) : 0d;
        var height = double.IsFinite(newSizeDip.Height) ? Math.Max(0d, newSizeDip.Height) : 0d;
        var x = anchorX switch
        {
            IslandAnchorX.Left => LeftDip,
            IslandAnchorX.Right => RightDip - width,
            _ => CenterXDip - (width / 2d)
        };
        var y = anchorY switch
        {
            IslandAnchorY.Bottom => BottomDip - height,
            IslandAnchorY.Center => CenterYDip - (height / 2d),
            _ => TopDip
        };
        return new Point(x, y);
    }
}
