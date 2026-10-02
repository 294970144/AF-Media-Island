using System.Windows;

namespace AFMediaBar.Classes.Services;

/// <summary>
/// 单个弹簧的状态与积分器：把「当前值追到目标值」这件物理过程从窗口里剥出来，成为可回归测试的纯逻辑。
/// 固定步长半隐式（辛）欧拉——相比显式欧拉，它在同样的步长下能量不发散，弹簧不会自己越晃越凶。
/// </summary>
/// <remarks>
/// 动效降级不靠参数硬调，而是换一组参数：完整动效用欠阻尼（ζ≈0.65）留一点过冲，即灵动岛的「呼吸感」；
/// 降级动效换临界阻尼（ζ=1）且刚度更高，不过冲、收敛更快；即时动效由调用方直接跳到目标值，根本不进积分。
/// </remarks>
public sealed class SpringMotion
{
    /// <summary>完整动效的刚度（1/s²）。对应角频率约 17.9 rad/s，收敛时间约 0.34 s。</summary>
    public const double FullStiffness = 340d;

    /// <summary>完整动效的阻尼（1/s）。ζ ≈ 0.65，静止时约 6% 过冲。</summary>
    public const double FullDamping = 24d;

    /// <summary>降级动效的刚度（1/s²）：收敛更快，配合临界阻尼几乎不产生过冲。</summary>
    public const double ReducedStiffness = 520d;

    /// <summary>降级动效的阻尼（1/s），等于 2√(ReducedStiffness)，即临界阻尼。</summary>
    public const double ReducedDamping = 45.6d;

    /// <summary>位置判定静止的阈值（DIP）：小于半个像素，肉眼不可见。</summary>
    public const double RestPositionDip = 0.1d;

    /// <summary>速度判定静止的阈值（DIP/秒）。</summary>
    public const double RestVelocityDipPerSecond = 0.5d;

    /// <summary>单次积分的步长上限（秒）。掉帧时按此切分成多步，宁可慢放也不让弹簧爆掉。</summary>
    public const double MaxStepSeconds = 1d / 240d;

    private const double MinimumDeltaSeconds = 1d / 1000d;

    private double _stiffness;
    private double _damping;
    private double _accumulator;

    /// <summary>创建弹簧并按给定参数初始化。 / Creates a spring with the given parameters.</summary>
    /// <param name="position">起始值（DIP）。 / Starting value in DIP.</param>
    /// <param name="stiffness">刚度（1/s²）。 / Stiffness.</param>
    /// <param name="damping">阻尼（1/s）。 / Damping.</param>
    public SpringMotion(double position, double stiffness, double damping)
    {
        Position = position;
        Velocity = 0d;
        Target = position;
        // 新建的弹簧已经在自己的目标上，必须是静止态：否则窗口一建好就会去追一个等于当前值的"目标"。
        // A fresh spring already sits on its target and must read as settled: otherwise the window would start
        // chasing a target equal to its current height the moment it is created.
        IsSettled = true;
        _stiffness = stiffness > 0d ? stiffness : FullStiffness;
        _damping = damping > 0d ? damping : FullDamping;
    }

    /// <summary>按当前动效级别创建弹簧。 / Creates a spring suited to the given motion profile.</summary>
    /// <param name="profile">当前环境动效级别。 / Motion level of the current environment.</param>
    /// <param name="position">起始高度（DIP）。 / Starting height in DIP.</param>
    public static SpringMotion ForProfile(MotionProfile profile, double position) =>
        profile.Mode == MotionMode.Reduced
            ? new SpringMotion(position, ReducedStiffness, ReducedDamping)
            : new SpringMotion(position, FullStiffness, FullDamping);

    /// <summary>当前值（DIP）。 / Current value in DIP.</summary>
    public double Position { get; private set; }

    /// <summary>当前速度（DIP/秒）。 / Current velocity in DIP per second.</summary>
    public double Velocity { get; private set; }

    /// <summary>目标值（DIP）。 / Target value in DIP.</summary>
    public double Target { get; private set; }

    /// <summary>是否已静止。 / Whether the spring has come to rest.</summary>
    public bool IsSettled { get; private set; }

    /// <summary>本次动效的起点（DIP），用于把进度归一化到 0..1。 / Origin of the current motion, used to normalize progress to 0..1.</summary>
    public double Origin { get; private set; }

    /// <summary>
    /// 无动画地把弹簧挪到给定位置并重新落定，不留速度。窗口在真正布局之后才知道自己的高度，
    /// 首次显示前需要用它校准起点，否则第一段动画的进度基准是错的。
    /// Teleports the spring to a position with no velocity, leaving it settled. The window only learns its real
    /// height after layout, so the first show re-aligns the origin; otherwise the first animation measures its
    /// progress against a stale baseline.
    /// </summary>
    /// <param name="position">实际高度（DIP）。 / The actual height in DIP.</param>
    public void ResetTo(double position)
    {
        if (!double.IsFinite(position))
            return;

        Position = position;
        Target = position;
        Origin = position;
        Velocity = 0d;
        _accumulator = 0d;
        IsSettled = true;
    }

    /// <summary>切换目标值；静止中的弹簧被唤醒。 / Retargets the spring; a settled spring wakes up.</summary>
    /// <param name="target">新的目标值（DIP）。 / The new target in DIP.</param>
    public void Retarget(double target)
    {
        if (!double.IsFinite(target) || Math.Abs(target - Target) < double.Epsilon)
            return;

        Origin = Position;
        Target = target;
        _accumulator = 0d;
        IsSettled = false;
    }

    /// <summary>
    /// 推进一段真实时间。真实时间被切成固定步长逐步积分，因此掉帧只会让动画变慢，不会让它失稳。
    /// Advances by real elapsed time, cut into fixed steps: a dropped frame slows the motion down instead of destabilizing it.
    /// </summary>
    /// <param name="delta">距上一帧经过的真实时间。 / Real time elapsed since the last frame.</param>
    /// <returns>静止时为 true。 / True once the spring has settled.</returns>
    public bool Advance(TimeSpan delta)
    {
        if (IsSettled)
            return true;

        _accumulator += Math.Clamp(delta.TotalSeconds, 0d, MaxStepSeconds * 16d);
        while (_accumulator >= MaxStepSeconds)
        {
            _accumulator -= MaxStepSeconds;
            Integrate(MaxStepSeconds);
            if (IsSettled)
                return true;
        }

        return IsSettled;
    }

    /// <summary>
    /// 归一化进度：0 表示刚离开起点，1 表示到达目标。弹簧过冲时会略大于 1，展开面板据此多亮一点再回落。
    /// Normalized progress: 0 at the origin, 1 at the target. An overshoot briefly exceeds 1.
    /// </summary>
    public double Progress
    {
        get
        {
            var span = Target - Origin;
            if (Math.Abs(span) < double.Epsilon)
                return 1d;
            return (Position - Origin) / span;
        }
    }

    private void Integrate(double step)
    {
        // 半隐式欧拉：先更新速度再用新速度更新位置， symplectic 形式在同等步长下比显式欧拉稳定得多。
        // Semi-implicit (symplectic) Euler: update velocity first, then position with the new velocity.
        var displacement = Position - Target;
        var acceleration = (-_stiffness * displacement) - (_damping * Velocity);

        Velocity += acceleration * step;
        if (Velocity is < -MaxSpeedDipPerSecond or > MaxSpeedDipPerSecond)
            Velocity = Math.Clamp(Velocity, -MaxSpeedDipPerSecond, MaxSpeedDipPerSecond);

        Position += Velocity * step;
        if (!double.IsFinite(Position))
        {
            // 数值发散时回到目标值静默停住，绝不把 NaN/Inf 写进窗口高度。
            // On numerical blow-up, rest exactly on the target: never write NaN or Inf into a window height.
            Position = Target;
            Velocity = 0d;
            IsSettled = true;
            return;
        }

        if (Math.Abs(Position - Target) < RestPositionDip && Math.Abs(Velocity) < RestVelocityDipPerSecond)
        {
            Position = Target;
            Velocity = 0d;
            _accumulator = 0d;
            IsSettled = true;
        }
    }

    /// <summary>速度上限（DIP/秒），防止目标大幅跳变时第一帧就冲出屏幕。 / Velocity ceiling in DIP per second.</summary>
    private const double MaxSpeedDipPerSecond = 6000d;
}
