namespace AFMediaBar.Classes.Services;

/// <summary>
/// 岛体该「出现」还是「藏起来」的判定：把「有没有媒体会话」与「前台是否全屏」两个条件合成一个可见性结论，
/// 并要求连续采样一致才改变结论，避免边界反复横跳。
/// Decides whether the island should be shown or tucked away: it folds "is there a media session" and
/// "is the foreground window fullscreen" into one visibility verdict, and only changes that verdict after
/// consecutive agreeing samples so the island does not flicker across a boundary.
/// </summary>
/// <remarks>
/// 「无播放就藏起来」是相对上一版的行为：原型在断连时只把胶囊压暗到 0.45，胶囊仍占着屏幕正中。
/// 「全屏就藏起来」沿用 <see cref="ForegroundFullscreenPolicy"/> 的判定内核（经由
/// <see cref="IDisplayMonitorService.IsForegroundWindowFullscreen"/> 取得结果），不在这里重写矩形比较。
/// Hiding while nothing plays tightens the prototype's dim-to-0.45 behavior, which still occupied screen center.
/// Fullscreen hiding reuses <see cref="ForegroundFullscreenPolicy"/> through
/// <see cref="IDisplayMonitorService.IsForegroundWindowFullscreen"/> instead of re-implementing the rectangle test.
/// </remarks>
public sealed class IslandVisibilityPolicy
{
    /// <summary>改变可见性所需的连续一致采样数。 / Agreeing samples required before the verdict flips.</summary>
    public const int RequiredConsecutiveSamples = 3;

    private int _noMediaStreak;
    private int _fullscreenStreak;
    private bool _hidden;

    /// <summary>当前可见性结论。 / The current visibility verdict.</summary>
    public bool IsHidden => _hidden;

    /// <summary>最近一次采样判为「无媒体」的连续次数。 / Consecutive samples that saw no media session.</summary>
    public int NoMediaStreak => _noMediaStreak;

    /// <summary>最近一次采样判为「前台全屏」的连续次数。 / Consecutive samples that saw a fullscreen foreground.</summary>
    public int FullscreenStreak => _fullscreenStreak;

    /// <summary>
    /// 喂一次采样并推进去抖计数器。两个条件各自独立计数，因此「刚停播又进全屏」不会因为只满足一个条件
    /// 就被提前判成隐藏。
    /// Feeds one sample and advances the debounce counters. The two conditions are counted independently, so
    /// "playback just stopped and a video went fullscreen at once" is not judged hidden on one condition alone.
    /// </summary>
    /// <param name="hasMediaSession">是否存在已连接的媒体会话。 / Whether a media session is connected.</param>
    /// <param name="isForegroundFullscreen">前台窗口是否全屏。 / Whether the foreground window is fullscreen.</param>
    public void Update(bool hasMediaSession, bool isForegroundFullscreen)
    {
        _noMediaStreak = hasMediaSession ? 0 : _noMediaStreak + 1;
        _fullscreenStreak = isForegroundFullscreen ? _fullscreenStreak + 1 : 0;

        var shouldHide = _noMediaStreak >= RequiredConsecutiveSamples || _fullscreenStreak >= RequiredConsecutiveSamples;
        if (shouldHide != _hidden)
        {
            _hidden = shouldHide;
            // 结论翻转即清零计数，下一次连续采样重新从头数起，不留上一段的余量。
            // Clearing the counters on a flip makes the next run count from zero instead of inheriting a tail.
            _noMediaStreak = 0;
            _fullscreenStreak = 0;
        }
    }

    /// <summary>重置到「可见」的初始态，供窗口重新显示时复用同一实例。 / Resets to the visible state for reuse on the next show.</summary>
    public void Reset()
    {
        _noMediaStreak = 0;
        _fullscreenStreak = 0;
        _hidden = false;
    }
}
