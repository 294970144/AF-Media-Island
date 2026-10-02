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
}
