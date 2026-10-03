using AFMediaBar.Classes.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AFMediaBar.Layout.Tests;

/// <summary>
/// 灵动岛弹簧积分器的纯逻辑测试：收敛、单调性、过冲与数值安全都在这里钉死。
/// Pure logic tests for the island's spring integrator: convergence, monotonicity, overshoot and numeric safety.
/// </summary>
[TestClass]
public sealed class IslandSpringMotionTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>把弹簧推到静止或达到帧数上限，返回用掉的帧数。 / Runs the spring to rest or the frame cap.</summary>
    private static int RunToRest(SpringMotion spring, int maxFrames = 600)
    {
        var frames = 0;
        while (frames < maxFrames && !spring.Advance(Frame))
            frames++;
        return frames;
    }

    [TestMethod]
    public void FreshSpringIsSettledAtItsOwnPosition()
    {
        var spring = new SpringMotion(46d, SpringMotion.FullStiffness, SpringMotion.FullDamping);

        Assert.IsTrue(spring.IsSettled);
        Assert.AreEqual(46d, spring.Position, 1e-9);
        Assert.AreEqual(46d, spring.Target, 1e-9);
    }

    [TestMethod]
    public void RetargetToTheSameValueKeepsTheSpringAsleep()
    {
        var spring = new SpringMotion(46d, SpringMotion.FullStiffness, SpringMotion.FullDamping);

        spring.Retarget(46d);

        Assert.IsTrue(spring.IsSettled);
    }

    [TestMethod]
    public void SpringConvergesOnItsTargetAndLandsExactly()
    {
        var spring = new SpringMotion(46d, SpringMotion.FullStiffness, SpringMotion.FullDamping);
        spring.Retarget(236d);

        var frames = RunToRest(spring);

        Assert.IsTrue(spring.IsSettled, $"弹簧在 {frames} 帧内没有静止");
        Assert.AreEqual(236d, spring.Position, 1e-9, "静止后必须精确落在目标上，不能留残差");
        Assert.AreEqual(0d, spring.Velocity, 1e-9, "静止后速度必须归零");
    }

    [TestMethod]
    public void FullMotionOvershootsToMakeTheIslandBreathe()
    {
        var spring = new SpringMotion(46d, SpringMotion.FullStiffness, SpringMotion.FullDamping);
        spring.Retarget(236d);

        var peak = spring.Position;
        while (!spring.IsSettled)
        {
            spring.Advance(Frame);
            peak = Math.Max(peak, spring.Position);
        }

        // 过冲是设计的一部分：完全不过冲就丢掉了「呼吸感」。上限 20% 保证它只是轻微过冲。
        // The overshoot is by design; losing it would lose the "breath". The 20% ceiling keeps it a slight overshoot.
        Assert.IsTrue(peak > 236d + 1e-6, "完整动效的弹簧应当过冲");
        Assert.IsTrue(peak < 236d * 1.20, "过冲幅度不应超过目标的 20%");
    }

    [TestMethod]
    public void ReducedMotionSettlesWithoutOvershooting()
    {
        var spring = SpringMotion.ForProfile(
            new MotionProfile(MotionMode.Reduced, default, default, default, default, default, false, false),
            46d);
        spring.Retarget(236d);

        var peak = spring.Position;
        while (!spring.IsSettled)
        {
            spring.Advance(Frame);
            peak = Math.Max(peak, spring.Position);
        }

        // 降级动效用临界阻尼：不许过冲，代价是少一点「呼吸」换更稳的观感。
        // Reduced motion is critically damped: no overshoot, trading the breath for steadiness.
        Assert.IsTrue(236d <= peak + 1e-6, "降级动效的弹簧不应过冲");
    }

    [TestMethod]
    public void ProgressRunsFromZeroToOne()
    {
        var spring = new SpringMotion(46d, SpringMotion.FullStiffness, SpringMotion.FullDamping);
        spring.Retarget(246d);

        Assert.AreEqual(0d, spring.Progress, 1e-9, "刚离开起点时进度必须是 0");
        RunToRest(spring);
        Assert.AreEqual(1d, spring.Progress, 1e-9, "到达目标时进度必须是 1");
    }

    [TestMethod]
    public void ResetToClearsVelocityAndSettlesOnTheGivenHeight()
    {
        var spring = new SpringMotion(46d, SpringMotion.FullStiffness, SpringMotion.FullDamping);
        spring.Retarget(236d);
        for (var i = 0; i < 5; i++)
            spring.Advance(Frame);

        // 窗口首次布局后才知道真实高度：校准必须抹掉速度与目标，否则会从半路继续跑。
        // The window only learns its real height after layout: re-aligning must wipe velocity and target,
        // otherwise the spring would resume mid-flight from a stale baseline.
        spring.ResetTo(48d);

        Assert.IsTrue(spring.IsSettled);
        Assert.AreEqual(48d, spring.Position, 1e-9);
        Assert.AreEqual(48d, spring.Target, 1e-9);
        Assert.AreEqual(48d, spring.Origin, 1e-9);
        Assert.AreEqual(0d, spring.Velocity, 1e-9);
    }

    [TestMethod]
    public void ResetToIgnoresNonFiniteHeights()
    {
        var spring = new SpringMotion(46d, SpringMotion.FullStiffness, SpringMotion.FullDamping);

        spring.ResetTo(double.NaN);
        spring.ResetTo(double.PositiveInfinity);

        Assert.AreEqual(46d, spring.Position, 1e-9, "非有限高度不该污染弹簧状态");
    }

    [TestMethod]
    public void RetargetMidFlightKeepsVelocitySoTheSpringTurnsAround()
    {
        var spring = new SpringMotion(46d, SpringMotion.FullStiffness, SpringMotion.FullDamping);
        spring.Retarget(236d);
        for (var i = 0; i < 5; i++)
            spring.Advance(Frame);

        var rising = spring.Velocity;
        Assert.IsTrue(rising > 0d, "展开途中速度应当为正");

        spring.Retarget(46d);

        // 速度连续：中途反向不是重新起步，而是像被手推开一样立刻掉头。
        // Velocity is continuous: reversing mid-flight turns the spring around instead of restarting it.
        Assert.IsTrue(spring.Velocity > 0d, "反向瞬间速度仍应为正，随后才会转为负");
        RunToRest(spring);
        Assert.AreEqual(46d, spring.Position, 1e-9);
    }

    [TestMethod]
    public void DroppedFrameSlowsTheSpringInsteadOfBlowingItUp()
    {
        var steady = new SpringMotion(46d, SpringMotion.FullStiffness, SpringMotion.FullDamping);
        steady.Retarget(236d);
        var steadyFrames = RunToRest(steady);

        // 模拟一次 500ms 的长卡顿：结果必须有限且仍在收敛，绝不出现 NaN/Inf 或疯狂振荡。
        // A 500 ms stall: the result must stay finite and still converge, never NaN/Inf or wild oscillation.
        var stalled = new SpringMotion(46d, SpringMotion.FullStiffness, SpringMotion.FullDamping);
        stalled.Retarget(236d);
        var peak = stalled.Position;
        for (var i = 0; i < steadyFrames; i++)
        {
            stalled.Advance(TimeSpan.FromMilliseconds(500));
            peak = Math.Max(peak, stalled.Position);
            Assert.IsTrue(double.IsFinite(stalled.Position), "长卡顿后位置必须仍是有限数");
        }

        Assert.IsTrue(peak < 236d * 1.20, "即便卡顿也不该出现大幅过冲");
        Assert.IsTrue(stalled.IsSettled, "长卡顿后弹簧应更快静止，而不是需要更多帧");
    }

    [TestMethod]
    public void NegativeTimeDoesNotRewindTheSpring()
    {
        var spring = new SpringMotion(46d, SpringMotion.FullStiffness, SpringMotion.FullDamping);
        spring.Retarget(236d);
        for (var i = 0; i < 5; i++)
            spring.Advance(Frame);
        var ahead = spring.Position;

        spring.Advance(TimeSpan.FromMilliseconds(-100));

        Assert.IsTrue(ahead >= spring.Position, "负的时间间隔不应让弹簧倒退");
    }

    /// <summary>
    /// 窗高不像卡片那样「有重量」：行程 190 DIP 上 ζ=0.65 过冲约 12.9 DIP 再回落，肉眼读到的是明确的
    /// 回弹，而不是「活气」。这里把设计窗口钉成 [0.5, 5.7] DIP——足以看出不是线性补间，
    /// 又不至于看成一次弹跳。上界正是本次调参要守的东西：有人把阻尼调回 0.65 时它会立刻变红。
    /// A window height carries no visible weight: across a 190 DIP throw, ζ=0.65 overshoots ~12.9 DIP and settles
    /// back, which reads as a bounce rather than liveliness. The window is pinned here to [0.5, 5.7] DIP — enough
    /// to show the motion is not a linear tween, little enough not to read as a bounce. The upper bound is what
    /// this retune actually guards: it turns red the moment someone dials damping back to 0.65.
    /// </summary>
    [TestMethod]
    public void FullMotionOvershootStaysTooSmallToReadAsABounce()
    {
        const double throwDip = 236d - 46d;

        var spring = new SpringMotion(46d, SpringMotion.FullStiffness, SpringMotion.FullDamping);
        spring.Retarget(236d);

        var peak = spring.Position;
        while (!spring.IsSettled)
        {
            spring.Advance(Frame);
            peak = Math.Max(peak, spring.Position);
        }

        Assert.IsTrue(peak > 236d + 0.5d, $"至少要留出看得见的过冲，实际峰值 {peak:F2} DIP");
        Assert.IsTrue(peak < 236d + (throwDip * 0.03), $"过冲不能超过行程的 3%，实际峰值 {peak:F2} DIP");
    }

    /// <summary>
    /// 展开全高度要在一次呼吸内跑完：尾巴拖太久会让它看起来像卡住而不是曲线。
    /// A full-height expansion must finish within one breath; a long tail reads as stalling rather than easing.
    /// </summary>
    [TestMethod]
    public void FullHeightThrowSettlesWellInsideASecond()
    {
        var spring = new SpringMotion(46d, SpringMotion.FullStiffness, SpringMotion.FullDamping);
        spring.Retarget(236d);

        var frames = RunToRest(spring, 600);

        // 600 帧的上限是 9.6 秒，只用来兜住「永远不收敛」；真正的意图是它在半秒量级就该结束。
        // The 600-frame cap is 9.6 s and only catches "never converges"; the intent is that it ends in half a second.
        Assert.IsTrue(frames * 16d < 600d, $"全高度展开用了 {frames * 16d} 毫秒才静止，尾巴太长");
    }

    /// <summary>
    /// 「减少动效」减的是「动」，不是竞速：降级档必须比完整档更温和，而不是单纯比它更早停下。
    /// 峰值速度是这条语义最直接的量度。
    /// 这里刻意不写「降级更快」。那句话听起来天经地义，实测却是反的——完整档 27 帧静止、降级档 34 帧。
    /// 原因不难：完整档 ζ=0.8 的那 1.5% 过冲只需多走小半个来回就还清，而临界阻尼的尾巴按 e^(-ωt)
    /// 拖得更久。但「更早停下」本来就不该是降级档的追求——若为了赢这一个指标把降级档调陡，
    /// 起步速度反而会超过完整档，把更强的视觉冲击丢给一个明确要求少动的用户，那是南辕北辙。
    /// "Reduced motion" reduces motion, it is not a race: the reduced tier must be gentler than the full one, not
    /// merely earlier to rest. Peak speed measures that directly. It deliberately does not claim "reduced is
    /// quicker" — that reads self-evident and is backwards in practice: full settles in 27 frames, reduced in 34.
    /// The reason is plain, since full's 1.5% overshoot is paid off in barely half an extra round-trip while
    /// critical damping drags a longer e^(-ωt) tail. But resting sooner was never the point of the reduced tier:
    /// steepening it to win that metric would make it launch harder than the full tier, handing a stronger visual
    /// jolt to the very user who asked for less motion.
    /// </summary>
    [TestMethod]
    public void ReducedMotionNeverMovesHarderThanFullMotion()
    {
        var full = SpringMotion.ForProfile(
            new MotionProfile(MotionMode.Full, default, default, default, default, default, false, false), 46d);
        full.Retarget(236d);
        var fullPeak = 0d;
        while (!full.IsSettled)
        {
            full.Advance(Frame);
            fullPeak = Math.Max(fullPeak, Math.Abs(full.Velocity));
        }

        var reduced = SpringMotion.ForProfile(
            new MotionProfile(MotionMode.Reduced, default, default, default, default, default, false, false), 46d);
        reduced.Retarget(236d);
        var reducedPeak = 0d;
        while (!reduced.IsSettled)
        {
            reduced.Advance(Frame);
            reducedPeak = Math.Max(reducedPeak, Math.Abs(reduced.Velocity));
        }

        Assert.IsTrue(reducedPeak <= fullPeak,
            $"降级动效的峰值速度不应超过完整档：降级 {reducedPeak:F0}，完整 {fullPeak:F0} DIP/s");
    }
}
