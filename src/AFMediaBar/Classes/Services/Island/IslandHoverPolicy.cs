namespace AFMediaBar.Classes.Services;

/// <summary>
/// 悬停展开的决策状态机：把「指针进出胶囊、是否正在拖拽、是否被点击锁定」合成一个目标展开态，
/// 从窗口里剥出来成为可回归测试的纯逻辑。
/// The decision state machine behind hover expansion: it folds pointer presence, dragging and click-pinning
/// into one target expansion state, lifted out of the window so it can be regression tested.
/// </summary>
/// <remarks>
/// 展开判定刻意只依赖指针与锁定，不掺任何播放状态：参考项目 FluidBar踩过的坑正是卡片状态在
/// collapse/expand 迁移中被播放状态污染，媒体一暂停就收起成blank pill。把播放状态排除在决策之外，
/// 指针在岛上时无论播着还是暂停着，展开态都不会被外力改写。
/// Expansion is decided purely from pointer and pin state, deliberately excluding playback: the reference
/// project FluidBar had cards whose state got polluted during collapse/expand transitions, collapsing into a
/// blank pill as soon as playback paused. Keeping playback out of the decision makes that unreachable.
///
/// <para>
/// 本类只产出「目标展开态」，不负责怎么落地：调用方必须按 <see cref="ShouldExpand"/> 的真值去设定形态，
/// 绝不能自己取反当前形态。悬停（临时）与点击锁定（粘滞）对「鼠标移开」的期望相反，而它们是同一个状态位
/// 的两个来源，只有统一成「设定目标态」才不会互相拉扯。
/// This class only produces the target expansion state and never performs it: callers must drive the shape from
/// <see cref="ShouldExpand"/> and must never invert the current shape themselves. Hover (transient) and the click pin
/// (sticky) expect opposite things when the pointer leaves, and they are two sources for one state bit, so folding
/// them into "set the target state" is what stops them from fighting.
/// </para>
/// </remarks>
public sealed class IslandHoverPolicy
{
    /// <summary>指针进入后多久才展开（DIP 无关的时间常数，毫秒）：掠过时不该被误当成想看内容。</summary>
    public const double ExpandDelayMilliseconds = 220;

    /// <summary>指针离开后多久才收起（毫秒）：从胶囊移到面板上时不至于中途塌掉。</summary>
    public const double CollapseDelayMilliseconds = 320;

    private bool _pointerInside;
    private bool _pinned;
    private bool _suppressedUntilLeave;
    private double _pendingMilliseconds;
    private bool _targetExpanded;

    /// <summary>目标展开态：点击锁定期间恒为展开，否则跟随指针并计入进出延迟。 / The target expansion state.</summary>
    public bool ShouldExpand => _pinned || _targetExpanded;

    /// <summary>是否被点击锁定；锁定期间指针离开也不收起。 / Whether a click has pinned the island open.</summary>
    public bool IsPinned => _pinned;

    /// <summary>累计的进出延迟时长（毫秒）。 / How long the pointer has stayed on its current side.</summary>
    public double PendingMilliseconds => _pendingMilliseconds;

    /// <summary>
    /// 点击岛体之外：解掉锁定并立刻收起，不等收起延迟。用户点别处就是「不看了」，让面板留在原地
    /// 挡着下面的内容毫无道理。
    /// A click outside the island: drops the pin and collapses at once, without waiting out the collapse delay.
    /// Clicking elsewhere means "I'm done looking", so leaving the panel there covering the content is pointless.
    /// </summary>
    public void CollapseFromOutsideClick()
    {
        _pinned = false;
        _pointerInside = false;
        // 按下鼠标别处的那一刻，指针往往还压在这颗岛体上。若不做抑制，下一拍的悬停计时会立刻
        // 开始往展开走，面板刚收起就又弹回来。抑制到指针真正离开为止。
        // At the moment the mouse is pressed elsewhere the pointer is usually still sitting on this very island.
        // Without a suppression the hover dwell would immediately start counting toward expansion and the panel
        // would pop back open right after collapsing. Suppress until the pointer genuinely leaves.
        _suppressedUntilLeave = true;
        _targetExpanded = false;
        _pendingMilliseconds = 0d;
    }

    /// <summary>
    /// 切换点击锁定：锁定期间指针移开也保持展开，再点一次解除。
    /// 在岛体上点击同时解除「点别处收起」的抑制——用户重新表达了「我要看它」，这份意图必须立刻成立。
    /// Toggles the click pin, which keeps the island open after the pointer leaves. Clicking on the island also
    /// lifts the outside-click suppression: the user has re-expressed "show me" and that intent must win at once.
    /// </summary>
    public void TogglePin()
    {
        _pinned = !_pinned;
        // 锁定态是绝对的：立刻把目标态对齐，不必再等延迟走完。
        // A pin is absolute: the target state follows immediately, with no delay to wait out.
        _targetExpanded = _pinned;
        _pendingMilliseconds = 0d;
        _suppressedUntilLeave = false;
    }

    /// <summary>
    /// 喂一次指针采样并推进延迟计时。<paramref name="elapsed"/> 用真实流逝时间，掉帧只会让悬停慢一点，
    /// 不会让它提前或卡住。
    /// Feeds one pointer sample and advances the dwell timer. Real elapsed time means a dropped frame only
    /// delays the hover slightly; it can neither fire early nor get stuck.
    /// </summary>
    /// <param name="pointerInside">指针是否落在岛体窗口内。 / Whether the pointer is over the island window.</param>
    /// <param name="elapsed">距上次采样经过的真实时间。 / Real time elapsed since the last sample.</param>
    public void Update(bool pointerInside, TimeSpan elapsed)
    {
        // 「点别处收起」之后先按住不动，直到指针真的离开这颗岛体为止：此时形态固定为收起，
        // 悬停不参与竞争，否则面板会在一次点击里收起又立刻弹回。
        // After an outside click, hold still until the pointer genuinely leaves: the shape stays collapsed and
        // hover stops competing, otherwise a single click would collapse the panel and pop it straight back.
        if (_suppressedUntilLeave)
        {
            _pendingMilliseconds = 0d;
            if (pointerInside)
                return;

            _suppressedUntilLeave = false;
            _targetExpanded = false;
        }

        _pointerInside = pointerInside;

        // 负的时间差（时钟被回拨）当成零：宁可让悬停慢一拍，也不能让它凭负数提前触发。
        // A negative delta (clock adjusted backwards) counts as zero: a slow hover beats one firing early.
        var step = Math.Max(0d, elapsed.TotalMilliseconds);
        var wanted = _pointerInside;
        var delay = wanted ? ExpandDelayMilliseconds : CollapseDelayMilliseconds;

        if (_targetExpanded == wanted)
        {
            // 已经停在目标态上：把延迟清零，下次反向时从头计时。
            // Already at the target: reset the dwell so the next reversal starts counting from zero.
            _pendingMilliseconds = 0d;
            return;
        }

        _pendingMilliseconds += step;
        if (_pendingMilliseconds < delay)
            return;

        _targetExpanded = wanted;
        _pendingMilliseconds = 0d;
    }

    /// <summary>重置到未展开的静止态，供窗口隐藏后复用同一实例。 / Resets to the collapsed idle state for reuse after a hide.</summary>
    public void Reset()
    {
        _pointerInside = false;
        _pinned = false;
        _suppressedUntilLeave = false;
        _pendingMilliseconds = 0d;
        _targetExpanded = false;
    }
}
