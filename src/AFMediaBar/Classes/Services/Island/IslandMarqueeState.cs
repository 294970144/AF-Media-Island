namespace AFMediaBar.Classes.Services;

/// <summary>
/// 岛体歌词跑马灯的判定与推进策略：只回答「该不该滚、滚到哪、显示哪段」，不碰窗口与控件。
/// 窗口字符串、接缝与文本元素边界直接复用 <see cref="MarqueeRotationPolicy"/>，与任务栏跑马灯同一套
/// 切分规则，因此代理对、组合记号、变体选择符不会被拆开；此处只补岛体自己的溢出判据与时钟。
/// </summary>
public sealed class IslandMarqueeState
{
    /// <summary>溢出判定容差（DIP）：小于一个像素的超出当作测量噪声，不触发滚动。</summary>
    public const double OverflowToleranceDip = 0.5d;

    private string _base = string.Empty;
    private int _windowStart = -1;
    private double _position;
    private TimeSpan _leadInRemaining;
    private bool _advancing;

    /// <summary>滚动速度（DIP/秒）。比任务栏略慢：岛体是全屏顶部的独享元素，太快会抢注意力。</summary>
    public const double ScrollSpeedDipPerSecond = 42d;

    /// <summary>推进节拍（毫秒）。与窗口的频谱帧同拍，避免多起一个高频计时器。</summary>
    public const double FrameIntervalMilliseconds = 16d;

    /// <summary>开始滚动前的停留（毫秒）：换行后先让用户读完这一句再滚。</summary>
    public const double LeadInMilliseconds = 900d;

    /// <summary>歌词原文；由窗口在属性变化时写入。 / The lyric source text, written by the window on property changes.</summary>
    public string Base
    {
        get => _base;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(next, _base, StringComparison.Ordinal))
                return;
            _base = next;
            Reset();
        }
    }

    /// <summary>是否正在滚动。 / Whether the text is currently scrolling.</summary>
    public bool Advancing => _advancing;

    /// <summary>当前小数位置（字符单位）。 / Current fractional position in characters.</summary>
    public double Position => _position;

    /// <summary>当前应显示的窗口；-1 表示尚未写过。 / The window to display; -1 means none written yet.</summary>
    public int WindowStart => _windowStart;

    /// <summary>接缝长度（字符），来自共享的轮转策略。 / Seam length in characters, from the shared rotation policy.</summary>
    public int SeamLength => MarqueeRotationPolicy.Separator.Length;

    /// <summary>内容变化时复位：回到行首并重走一遍起读停留。 / Resets on new content: back to the head, lead-in repeated.</summary>
    public void Reset()
    {
        _position = 0d;
        _windowStart = -1;
        _leadInRemaining = TimeSpan.FromMilliseconds(LeadInMilliseconds);
        _advancing = false;
    }

    /// <summary>
    /// 决定这一帧是否滚动。判据是量出的宽度真的超出可用宽度，而不是「歌词长不长」——
    /// 短句放得下就该老老实实静止，滚起来反而干扰阅读。
    /// Decides whether to scroll this frame. The criterion is measured overflow, not text length.
    /// </summary>
    /// <param name="measuredWidth">量出的原文宽度（DIP）。 / Measured width of the source text in DIP.</param>
    /// <param name="availableWidth">文字区可用宽度（DIP）。 / Available text width in DIP.</param>
    /// <param name="allowed">当前是否允许滚动（断开连接、动效降级等应传 false）。 / Whether scrolling is allowed at all.</param>
    /// <returns>需要推进一个节拍时为 true。 / True when one frame should be advanced.</returns>
    public bool Configure(double measuredWidth, double availableWidth, bool allowed)
    {
        var overflow = allowed && double.IsFinite(measuredWidth) && double.IsFinite(availableWidth) && availableWidth > 0d
            ? Math.Max(0d, measuredWidth - availableWidth)
            : 0d;
        var advancing = overflow > OverflowToleranceDip && _base.Length > 0;

        if (!advancing)
        {
            // 放不下但当前不允许滚动时保留省略号——它至少说明后面还有内容。
            // Keep the ellipsis when the text does not fit but advancing is not allowed: it says more text follows.
            _advancing = false;
            _position = 0d;
            _windowStart = -1;
            return false;
        }

        _advancing = true;
        return true;
    }

    /// <summary>
    /// 推进一个节拍，返回是否需要重写窗口字符串。整数位置跨过时才返回 true，小数位移由调用方写进渲染变换——
    /// 这样每帧只碰一次变换，不会在 60Hz 下反复重排文字。
    /// Advances one frame and reports whether the window string must be rewritten. Only integer crossings rewrite it;
    /// the fraction goes into the render transform, so the text is never re-laid-out at 60 Hz.
    /// </summary>
    /// <param name="elapsed">距上一帧的真实间隔。 / Real interval since the last frame.</param>
    public bool Advance(TimeSpan elapsed)
    {
        if (!_advancing)
            return false;

        if (_leadInRemaining > TimeSpan.Zero)
        {
            _leadInRemaining -= elapsed;
            return false;
        }

        _position += ScrollSpeedDipPerSecond * elapsed.TotalSeconds / _windowLengthForTiming();
        if (_position < 0d)
            _position = 0d;

        var start = MarqueeRotationPolicy.ResolveWindowStart(_base, (int)Math.Floor(_position));
        if (start == _windowStart)
            return false;

        _windowStart = start;
        return true;
    }

    /// <summary>按当前小数位置取窗口字符串。 / Builds the window string for the current fractional position.</summary>
    public string BuildWindow() => MarqueeRotationPolicy.BuildWindow(_base, _windowStart);

    /// <summary>
    /// 用前缀宽度表把小数字符位置换算成渲染偏移（DIP）。表为空时退化为按平均字宽估算，
    /// 宁可略有偏差，也不要因为没测过就不滚。
    /// Converts the fractional character position into a render offset in DIP via a prefix-width table.
    /// </summary>
    /// <param name="prefixWidths">前缀宽度表，索引 i 是前 i 个字符的宽度。 / Prefix widths where index i is the width of the first i characters.</param>
    /// <param name="measuredCharacters">表已测到的字符数。 / How many characters have been measured.</param>
    public double ResolveOffsetDip(double[]? prefixWidths, int measuredCharacters)
    {
        var at = _windowStart < 0 ? 0 : _windowStart;
        return -MarqueeRotationPolicy.ResolveWidthAt(prefixWidths, measuredCharacters, at + (_position - Math.Floor(_position)));
    }

    /// <summary>接缝让整圈比原文长出固定几个字符，计时用这个长度才能与位置推进对得上。 / The seam makes one lap longer than the source; timing uses this length.</summary>
    private double _windowLengthForTiming() =>
        Math.Max(1, _base.Length + MarqueeRotationPolicy.Separator.Length);
}
