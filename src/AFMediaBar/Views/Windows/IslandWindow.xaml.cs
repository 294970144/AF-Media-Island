using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using AFMediaBar.Classes.Interop;
using AFMediaBar.Classes.Models;
using AFMediaBar.Classes.Services;
using AFMediaBar.Classes.Utils;
using AFMediaBar.ViewModels.Windows;
using Wpf.Ui.Controls;

namespace AFMediaBar.Views.Windows;

/// <summary>
/// 顶部悬浮的「灵动岛」实验窗口：紧凑胶囊（封面 + 当前行歌词 + 迷你频谱），点击胶囊就地长高成大面板。
/// Experimental floating island window: a compact capsule (cover + current lyric line + mini spectrum)
/// that grows in place into a full panel when clicked.
/// 窗口只负责呈现、拖拽与展开动画；状态与采样全部来自 <see cref="IslandWindowViewModel"/>。
/// </summary>
public partial class IslandWindow : FluentWindow
{
    /// <summary>频谱条静止时的最小缩放，对应「残桩」观感。 / Minimum bar scale at rest — the resting-stub look.</summary>
    private const double SpectrumBarMinimumScale = 0.08;

    /// <summary>紧凑态胶囊圆角半径（DIP），等于胶囊高度的一半。 / Compact capsule corner radius (DIP), half the capsule height.</summary>
    private const double CapsuleCornerRadiusDip = 23;

    /// <summary>展开态圆角半径（DIP）：外壳不再是半圆端帽，退成一个更克制的圆角矩形。</summary>
    private const double ExpandedCornerRadiusDip = 18;

    private readonly IslandWindowViewModel _viewModel;
    private readonly IDisplayMonitorService _displayMonitorService;
    private readonly NativeMouseInputMonitor _mouseInputMonitor;
    private readonly List<(Border Bar, ScaleTransform Scale)> _spectrumBars = [];

    // 动效级别在窗口构造时解析一次即可：它只取决于系统动画开关、高对比度与渲染层级，
    // 而这些在一次运行中不会变。每帧调ResolveCurrent 会去读渲染能力，代价不成比例。
    // The motion level is resolved once at construction: it depends only on the system animation setting,
    // high contrast and the rendering tier, none of which change while the app runs. Calling ResolveCurrent
    // every frame would query the rendering capability for no benefit.
    private readonly MotionProfile _motion;

    // 展开/收起交给弹簧积分，歌词滚动与频谱共用同一条 16ms 帧节拍：多起一个高频计时器只会多一次调度。
    // Expansion runs on a spring; lyric scrolling shares the 16 ms spectrum frame beat — a second high-frequency
    // timer would only mean a second dispatch.
    private readonly DispatcherTimer _frameTimer;
    private readonly IslandMarqueeState _marquee = new();
    private readonly IslandHoverPolicy _hover = new();
    private readonly IslandVisibilityPolicy _visibility = new();

    // 全屏与无播放都要主动发现状态变化，而 IDisplayMonitorService 只提供拉取式快照、没有事件源，
    // 所以用一个低频探针去问。频率取 450ms：全屏切换与人是否还在听歌本来就是秒级事件，
    // 问得更勤只是白调度；帧节拍（16ms）留给弹簧与跑马灯，不混用。
    // Neither fullscreen nor "nothing is playing" announces itself, and IDisplayMonitorService only offers pull-style
    // snapshots with no event source, so a low-frequency probe asks. 450 ms is deliberate: fullscreen transitions and
    // whether the user still listens are second-scale events, and asking more often only burns dispatches. The 16 ms
    // beat stays with the spring and the marquee.
    private static readonly TimeSpan PresenceProbeInterval = TimeSpan.FromMilliseconds(450);

    /// <summary>
    /// 一次悬停采样允许计入的最大时间跨度（毫秒）。超过它说明这一拍之前漏过采样——帧节拍刚起来、
    /// 系统刚休眠唤醒、或 UI 线程刚卡了一段——中间那段空白并不是「指针停留」。按此上限计入，
    /// 停留计时只会偏慢，绝不会抢跑。
    /// Ceiling on the time one hover sample may count. Anything larger means samples were missed in between: the
    /// frame beat has only just started, the machine just woke, or the UI thread stalled. The gap in between was
    /// not the pointer lingering, so it is capped — dwell may lag behind, never fire early.
    /// </summary>
    private const double MaxHoverStepMilliseconds = 60d;

    private readonly DispatcherTimer _presenceProbe;
    private DateTimeOffset _lastHoverSampleAt = DateTimeOffset.MinValue;

    // Wpf.Ui.Controls 与 System.Windows.Media 都有同名变换类型，此处显式限定取系统那一个。
    // Both Wpf.Ui.Controls and System.Windows.Media declare transforms of the same name; qualify the system one.
    private readonly System.Windows.Media.TranslateTransform _marqueeTransform = new();
    private readonly SpringMotion _heightSpring;
    private DateTimeOffset _lastFrameAt = DateTimeOffset.MinValue;
    private double[]? _marqueePrefixWidths;
    private int _marqueeMeasuredCharacters;
    private string _marqueeMeasuredFont = string.Empty;
    private bool _isExpanded;
    private HwndSource? _hwndSource;
    private bool _dpiRestoreQueued;
    private double? _pendingNormalizedCenterX;
    private Point? _pendingPlacementDip;
    private double _lastShellRadius = CapsuleCornerRadiusDip;

    // 上一次交给 GDI 的窗口区域尺寸（物理像素）。三者都没变就不重建区域：SizeChanged 一次布局可能
    // 连着触发好几遍，而每次重建都要走一次 GDI 区域分配。
    // The last window-region geometry handed to GDI, in physical pixels. All three unchanged means no rebuild:
    // one layout pass can raise SizeChanged several times and each rebuild costs a GDI region allocation.
    private int _lastRegionWidthPx = -1;
    private int _lastRegionHeightPx = -1;
    private int _lastRegionRadiusPx = -1;

    /// <summary>创建岛体窗口并接通外观、显示器与状态源。 / Creates the island window and wires appearance, monitors, and state.</summary>
    public IslandWindow(
        IslandWindowViewModel viewModel,
        WindowAppearanceService appearanceService,
        IDisplayMonitorService displayMonitorService,
        NativeMouseInputMonitor mouseInputMonitor)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _displayMonitorService = displayMonitorService;
        _mouseInputMonitor = mouseInputMonitor;
        _motion = MotionPolicy.ResolveCurrent();
        // 构造阶段窗口还没布局，ActualHeight 通常是 0；用 XAML 写死的紧凑态高度起步，
        // ShowIsland 会再跟实际高度对齐一次。
        // The window has no layout yet during construction, so ActualHeight is usually 0: start from the compact
        // height declared in XAML, and let ShowIsland re-sync against the real height.
        _heightSpring = SpringMotion.ForProfile(_motion, IslandPlacementPolicy.CompactHeightDip);

        // 岛体悬浮在桌面与全屏内容之上，绝不能抢焦点：与曲目通知窗口同一套非激活窗口处理。
        // The island floats above the desktop and must never steal focus: same non-activating treatment as the
        // track-change notification window.
        WindowHelper.SetNoActivate(this);
        appearanceService.AttachNonActivatingTransient(this);
        DataContext = viewModel;

        // 跑马灯的小数位移挂在渲染变换上：滚动期间文本本身不重排，只有位移在变。
        // The marquee's fractional offset rides on a render transform: the text is never re-laid out while scrolling.
        PrimaryText.RenderTransform = _marqueeTransform;
        PrimaryText.TextTrimming = TextTrimming.None;

        BuildSpectrumBars();
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.SpectrumFrame += OnSpectrumFrame;
        RefreshMediaVisuals();

        _frameTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(IslandMarqueeState.FrameIntervalMilliseconds)
        };
        _frameTimer.Tick += OnFrameTick;
        _lastFrameAt = DateTimeOffset.UtcNow;

        // 悬停与可见性探针共用帧节拍之外的单条低频计时器：帧节拍空闲时会停表，而这两种判定不能停。
        // The hover dwell and the presence probe share one low-frequency timer apart from the frame beat: the beat
        // stops when idle, while these two decisions must keep running.
        _presenceProbe = new DispatcherTimer(DispatcherPriority.Background) { Interval = PresenceProbeInterval };
        _presenceProbe.Tick += OnPresenceProbeTick;
        _lastHoverSampleAt = DateTimeOffset.UtcNow;

        // 「点别处收起」靠全局鼠标钩子：岛体是 no-activate 窗口，永远拿不到焦点事件，Deactivated 永不触发。
        // 钩子服务是单例常驻的（托盘滚轮已在用），这里只是多订阅一个事件，不新增钩子线程。
        // "Collapse on an outside click" rides the global mouse hook: the island is a no-activate window that never
        // takes focus, so Deactivated never fires. The hook service is an app-wide singleton already running for the
        // tray wheel, so this only adds one more subscription and no extra hook thread.
        _mouseInputMonitor.LeftButtonPressed += OnGlobalLeftButtonPressed;

        // 非 layered 窗口没有 AllowsTransparency 可用，外壳轮廓用 GDI 圆角区域裁出：
        // 尺寸一变（含展开高度动画的每一帧）就重设一次区域，半径跟随形态内插。
        // Without AllowsTransparency the shell silhouette is clipped by a GDI round region, reapplied on every size
        // change (including each frame of the expand animation) with a radius interpolated across the two shapes.
        SourceInitialized += (_, _) =>
        {
            ApplyCapsuleRegion();
            ClearSystemFrameBorder();
            AttachWindowMessageHook();
        };
        SizeChanged += (_, _) => ApplyCapsuleRegion();

        // 指针进出必须有事件源。悬停计时原本只挂在 450ms 的低频探针上，于是「发现指针已经进来了」
        // 这件事本身就要等半拍，而那一拍会被当成停留时间一次性喂进 dwell——220ms 的阈值形同虚设，
        // 真实延迟是与探针周期同宽的 0~450ms 随机数。事件负责「立刻开始计时」，16ms 采样负责「计时准确」。
        // Pointer enter/leave needs an event source. The dwell clock used to ride the 450 ms probe alone, so merely
        // noticing "the pointer arrived" cost half a tick — and that tick was fed to the dwell in one gulp, leaving
        // the 220 ms threshold meaningless: real latency became a 0–450 ms random value locked to the probe period.
        // The event starts the clock at once; the 16 ms sampling keeps it honest.
        MouseEnter += OnIslandMouseEnter;
        MouseLeave += OnIslandMouseLeave;

        // 手感参数热重载：文件监听在后台线程上，弹簧与悬停计时都在 UI 线程上，因此切回去再落地。
        // Hot reload of the feel parameters: the file watcher runs on a background thread while the spring and the
        // dwell counter live on the UI thread, so the update is marshalled back before it lands.
        IslandMotionTuning.Current.Changed += OnMotionTuningChanged;

        Closed += (_, _) =>
        {
            MouseEnter -= OnIslandMouseEnter;
            MouseLeave -= OnIslandMouseLeave;
            IslandMotionTuning.Current.Changed -= OnMotionTuningChanged;
            if (_hwndSource is not null)
            {
                _hwndSource.RemoveHook(OnWindowMessage);
                _hwndSource = null;
            }
            _frameTimer.Stop();
            _frameTimer.Tick -= OnFrameTick;
            _presenceProbe.Stop();
            _presenceProbe.Tick -= OnPresenceProbeTick;
            _mouseInputMonitor.LeftButtonPressed -= OnGlobalLeftButtonPressed;
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.SpectrumFrame -= OnSpectrumFrame;
            _viewModel.Detach();
        };
    }

    /// <summary>把岛体定位到主显示器顶部居中并显示，随后启动状态源。 / Places the island at the top-center of the primary display, shows it, and starts the state source.</summary>
    public void ShowIsland()
    {
        ApplyPlacement(IslandPlacementPolicy.CompactHeightDip);
        // 此刻窗口才刚布局完，真实高度才可信：把弹簧起点校准到它，再开始跑。
        // Only now is the window laid out and its real height trustworthy: align the spring's origin before rolling.
        Height = IslandPlacementPolicy.CompactHeightDip;
        _heightSpring.ResetTo(Height);
        // 重新显示时把两个策略复位：上一轮残留的锁定与隐藏结论不该带进新一轮。
        // Both policies reset on re-show: a leftover pin or hide verdict from last time must not carry over.
        _hover.Reset();
        _visibility.Reset();
        // 隐藏期间窗口区域可能被系统丢弃，重新显示时强制重建一次：否则去重逻辑会以为轮廓没变而跳过。
        // The window region may have been dropped by the system while hidden, so force one rebuild on re-show:
        // otherwise the de-duplication would think the silhouette is unchanged and skip it.
        InvalidateCapsuleRegion();
        _viewModel.Start();
        Show();
        // 重新显示后再擦一次系统色描边：隐藏期间系统可能重建了非客户区，Show() 本身也可能触发激活路径。
        // Wipe the system outline again after re-showing: the system may have rebuilt the non-client frame while the
        // window was hidden, and Show() itself can run the activation path.
        ClearSystemFrameBorder();
        // 歌词可能一上来就超宽，起帧节拍让跑马灯立即有机会滚动。
        // The lyric may already be too wide, so start the beat to let the marquee scroll right away.
        StartFrameTimer();
        StartPresenceProbe();
        // 指针一开始就可能已经压在这颗刚长出来的岛体上，而 WPF 不会为「窗口自己移到指针底下」
        // 补发 MouseEnter。探针也不再喂悬停，缺了这一下计时永远不会起步。
        // The pointer may already rest on this just-created island, and WPF does not synthesise MouseEnter for a
        // window that moved itself under the cursor. With the probe no longer feeding hover, skipping this call
        // would leave the dwell clock permanently unstarted.
        SampleHover();
    }

    /// <summary>
    /// 彻底收起岛体：停掉状态源**与可见性探针**。只有「用户切走了显示模式」才配得上这条路径——
    /// 协调器（显示模式页、<c>--island</c>）调它，因为用户已经明确表示不想看岛了，探针留着也只是空转。
    /// 彻底停探针。 / Hides the island for good: stops the state source <b>and the presence probe</b>. Only a display-mode
    /// switch earns this path, via the coordinator, because the user has said they do not want the island and a live probe
    /// would only spin.
    /// </summary>
    public void HideIsland()
    {
        Hide();
        _viewModel.Stop();
        // 隐藏后没有任何东西需要推进，停表省掉后台 60Hz 空转（窗口只是 Hide，Dispatcher 仍在跑）。
        // Nothing needs advancing while hidden: stop the beat so it does not spin at 60 Hz behind a hidden window.
        _frameTimer.Stop();
        _presenceProbe.Stop();
    }

    /// <summary>
    /// 临时收起岛体，**保留可见性探针**，让岛体能在条件恢复时自己回来。
    ///
    /// 这里是「播放一会儿就再也不出现」那个 bug 的修法。可见性判定此前经 <see cref="HideIsland"/> 收起，而那条
    /// 路径会把探针一起停掉——探针正是唯一能把岛体重新唤起的东西（<see cref="OnPresenceProbeTick"/> 末尾的
    /// <c>if (!IsVisible) ShowIsland()</c>）。于是任何一次误判隐藏都是**单向棘轮**：岛体再也回不来，
    /// 只能由用户在设置里手动开关一次重走协调器。
    ///
    /// 误判很容易发生：启动时 SMTC 会话目录尚未就绪，看门狗的首次探测与随后的目录重建期间会发布一串
    /// 断连快照（<c>RefreshSnapshot</c> 里 <c>session is null || !_catalog.IsStarted</c> 走的就是
    /// <c>Publish(MediaSnapshot.Disconnected)</c>）。三连采样只要 1.35 秒，一次启动期的假断连就足以判隐藏。
    /// 那个窗口只出现在冷启动，之后目录已热不会再重建，所以症状精确地表现为「仅限启动后第一次」。
    ///
    /// 这条路径把三种「隐藏」区分开：判定隐藏是可逆的（保留探针），切走模式才是终态（停探针）。
    /// Temporarily hides the island while <b>keeping the probe alive</b>, so the island can return on its own when the
    /// condition clears.
    ///
    /// <para>
    /// This is the fix for "it closes after a bit of playback and never comes back". Visibility used to hide through
    /// <see cref="HideIsland"/>, which also stopped the probe — and the probe is the only thing that can bring the island
    /// back (the <c>if (!IsVisible) ShowIsland()</c> at the end of <see cref="OnPresenceProbeTick"/>). Any mistaken hide
    /// was therefore a <b>one-way ratchet</b>: the island could never return on its own, and only a manual toggle in the
    /// display-modes page could revive it through the coordinator.
    ///
    /// <para>
    /// The mistaken verdict is easy to hit at startup: the SMTC catalog is not ready yet, and the watchdog's first probe
    /// plus the catalog rebuild that may follow publish a run of disconnected snapshots. Three agreeing samples take only
    /// 1.35 s, so one startup-time false disconnect is enough. That window exists only on a cold start — the catalog is
    /// warm afterwards and stops rebuilding — which is exactly why the symptom shows up on the first launch only.
    ///
    /// <para>
    /// This path is what separates the three kinds of hiding: a verdict is reversible (probe stays on), while switching
    /// away from the mode is terminal (probe stops).
    /// </para>
    /// </para>
    /// </summary>
    private void SuspendIsland()
    {
        Hide();
        _viewModel.Stop();
        // 只停 16ms 帧节拍：隐藏后没有弹簧与跑马灯要推进，但可见性探针必须留着，它是唯一的自愈路径。
        // Only the 16 ms beat stops: with the window hidden there is no spring or marquee to advance, but the probe must
        // stay on because it is the only route back.
        _frameTimer.Stop();
    }

    /// <summary>
    /// 低频探针的一拍：只判可见性（无播放 / 前台全屏则整个藏起来）。
    /// A presence probe tick: it settles visibility and nothing else (tuck away entirely when nothing plays or the
    /// foreground is fullscreen).
    /// </summary>
    private void OnPresenceProbeTick(object? sender, EventArgs e)
    {
        // 悬停计时不在这里：450ms 的节拍撑不起 220ms 的 dwell 分辨率，它已移到 16ms 的帧节拍
        // （<see cref="SampleHover"/>），由 MouseEnter/MouseLeave 起表。原因见事件订阅处的说明。
        // Hover timing is not done here: a 450 ms beat cannot resolve a 220 ms dwell. It moved onto the 16 ms frame
        // beat (SampleHover), started by MouseEnter/MouseLeave — see the note where those handlers are wired.
        // 可见性要在悬停之前判：窗口被藏起来时，悬停状态已经没有意义了。
        // Visibility comes before hover: once the window is tucked away, hover state is meaningless.
        _visibility.Update(_viewModel.IsConnected, _displayMonitorService.IsForegroundWindowFullscreen());
        if (_visibility.IsHidden)
        {
            // 藏起来时顺手把锁定解开，否则用户回来后岛体会莫名其妙停在展开态。
            // Drop the pin while hidden, otherwise the island reappears stuck open for no visible reason.
            _hover.Reset();
            // 走可逆的那条：判定隐藏必须还能自愈。原先这里调 HideIsland，把探针一起停了，
            // 而探针是唯一能把岛体唤回来的东西——于是任何一次误判都是单向棘轮。
            // This takes the reversible path: a visibility verdict must stay able to heal. It used to call
            // HideIsland, which stopped the probe as well — and the probe is the only thing that can bring the island
            // back, so any mistaken verdict became a one-way ratchet.
            if (IsVisible)
                SuspendIsland();
            return;
        }

        // 重新显示必须走 ShowIsland 而不是裸 Show：协调器（显示模式页切换、--island 启动开关）也调它，
        // 两处若各走一条，重新显示时就会漏掉 ViewModel.Start、位置校准与探针起表——症状是岛体回来了却
        // 不再更新媒体。
        // Re-showing must go through ShowIsland rather than a bare Show: the coordinator (display-mode switches,
        // the --island launch switch) calls it too, and two separate paths would let a re-show skip
        // ViewModel.Start, placement and the probe — the symptom being an island that returns but stops updating.
        if (!IsVisible)
            ShowIsland();
    }

    /// <summary>指针进入岛体：立刻开始停留计时，不等探针。 / Pointer entered the island: start the dwell clock now instead of waiting for the probe.</summary>
    private void OnIslandMouseEnter(object sender, MouseEventArgs e) => SampleHover();

    /// <summary>指针离开岛体：立刻开始反向计时。 / Pointer left the island: start counting the other way at once.</summary>
    private void OnIslandMouseLeave(object sender, MouseEventArgs e) => SampleHover();

    /// <summary>
    /// 手感参数文件被改动后把新参数落到正在跑的弹簧与悬停计时上。
    /// 弹簧用 <see cref="SpringMotion.Retune"/> 换参数而不是重建：重建会把位置与速度清零，一次改参数的
    /// 动作本身就成了可见的跳变；带着当前速度换参数，动画才会平滑地改用新脾气。
    /// Lands new parameters on the running spring and dwell clock after the feel file changed. The spring is retuned
    /// rather than rebuilt: rebuilding zeroes position and velocity, which turns the parameter change itself into a
    /// visible jump; retuning carries the current velocity so the motion smoothly takes on its new temperament.
    /// </summary>
    private void OnMotionTuningChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyMotionTuning();
            return;
        }

        Dispatcher.Invoke(ApplyMotionTuning);
    }

    private void ApplyMotionTuning()
    {
        _hover.RefreshDelays();
        var tuning = IslandMotionTuning.Current;
        _heightSpring.Retune(
            _motion.Mode == MotionMode.Reduced ? tuning.ReducedStiffness : tuning.FullStiffness,
            _motion.Mode == MotionMode.Reduced ? tuning.ReducedDamping : tuning.FullDamping);
    }

    /// <summary>
    /// 抓一次悬停样本并推进停留计时，由 16ms 的帧节拍与指针进出事件共同驱动。
    /// 形态只有一个写入者：目标态来自悬停策略，这里绝不自作主张把当前形态取反。
    /// Takes one hover sample and advances the dwell clock, driven by both the 16 ms frame beat and the pointer
    /// enter/leave events. The shape has exactly one writer: the target comes from the hover policy and is never
    /// inverted here.
    /// </summary>
    private void SampleHover()
    {
        // 窗口藏着的时候指针进出的结论毫无意义，也不会有意外的 MouseEnter 落进来。
        // While the window is hidden nothing about pointer presence matters, and stray enter events would only mislead.
        if (!IsVisible)
            return;

        var now = DateTimeOffset.UtcNow;
        var elapsedMilliseconds = (now - _lastHoverSampleAt).TotalMilliseconds;
        _lastHoverSampleAt = now;

        // 负跨度（时钟被回拨）与非有限值都按零计：宁可慢一拍，也不能凭一个假数字提前触发。
        // Negative spans (the clock stepped back) and non-finite ones count as zero: a late hover beats one that
        // fires on a bogus number.
        if (!double.IsFinite(elapsedMilliseconds) || elapsedMilliseconds < 0d)
            elapsedMilliseconds = 0d;

        _hover.Update(IsMouseOver, TimeSpan.FromMilliseconds(Math.Min(elapsedMilliseconds, MaxHoverStepMilliseconds)));
        SetExpanded(_hover.ShouldExpand);

        // 计时未决时必须让节拍继续跑：停了就再没有人为它计时，这次展开会永远停在「差半拍」的位置。
        // An undecided dwell has to keep the beat alive; stopping it strands the clock one tick short forever.
        if (_hover.PendingMilliseconds > 0d)
            StartFrameTimer();
    }

    /// <summary>
    /// 用 GDI 圆角矩形区域把窗口裁成外壳轮廓。半径取「目标形态半径与高度一半的较小者」：紧凑态
    /// （46 DIP，23 DIP 半径）正好是半圆端帽，展开态退化为 18 DIP 圆角的矩形。
    /// 半径必须与 <see cref="Shell"/> 的 XAML 圆角一致，否则 WPF 画出的圆角会被 GDI 区域切出台阶。
    /// set 成功后区域归系统所有，旧区域由系统释放，这里不做任何 DeleteObject。
    /// Clips the window to the shell silhouette with a GDI round region. The radius is the smaller of the target
    /// shape's radius and half the height: a half-circle cap when compact (46 DIP at 23 DIP), a 18 DIP rounded
    /// rectangle when expanded. It must match the XAML radius on <see cref="Shell"/>, or the GDI region cuts
    /// steps into the corners WPF painted. After a successful set the region belongs to the system — never
    /// DeleteObject it here.
    /// </summary>
    private void ApplyCapsuleRegion()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source || source.Handle == nint.Zero)
            return;
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        // 动画期间按弹簧进度在两种圆角之间插值，否则收起/展开时轮廓会在最后一帧突然换形状。
        // 与 Shell 的 XAML 圆角共用同一计算，两处才不会错位。
        // Interpolating between the two radii along the spring progress avoids the outline snapping to a new shape
        // on the animation's last frame. Shell's XAML radius reads the same computation so the two never drift.
        var radiusDip = ResolveShellRadiusDip();

        var dpi = VisualTreeHelper.GetDpi(this);
        var widthPx = (int)Math.Ceiling(ActualWidth * dpi.PixelsPerDip);
        var heightPx = (int)Math.Ceiling(ActualHeight * dpi.PixelsPerDip);
        var radiusPx = (int)Math.Ceiling(Math.Min(radiusDip, ActualHeight / 2) * dpi.PixelsPerDip);

        // 尺寸与半径都没变就跳过：SizeChanged 在布局时可能连着触发好几次，而重建窗口区域要走一次
        // GDI CreateRoundRectRgn 分配。静止时轮廓本就不变，重建纯属白做。
        // Skip when neither the size nor the radius moved: SizeChanged can fire several times per layout pass,
        // and rebuilding the region costs a GDI CreateRoundRectRgn allocation. At rest the silhouette is
        // unchanged, so rebuilding it is pure waste.
        if (widthPx == _lastRegionWidthPx && heightPx == _lastRegionHeightPx && radiusPx == _lastRegionRadiusPx)
            return;

        _lastRegionWidthPx = widthPx;
        _lastRegionHeightPx = heightPx;
        _lastRegionRadiusPx = radiusPx;

        var region = NativeMethods.CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, radiusPx, radiusPx);
        if (region == nint.Zero)
            return;
        NativeMethods.SetWindowRgn(source.Handle, region, true);
    }

    /// <summary>
    /// 作废已记录的窗口区域几何，逼下一次 <see cref="ApplyCapsuleRegion"/> 真的重建一次。
    /// 隐藏再显示、显示器热插拔这类外部变化不会体现为尺寸变化，去重逻辑就看不出轮廓其实该重建了。
    /// Invalidates the recorded window-region geometry so the next <see cref="ApplyCapsuleRegion"/> really rebuilds.
    /// External changes such as hide-then-show or a monitor hot-plug never show up as a size change, so the
    /// de-duplication cannot tell that the silhouette should be rebuilt.
    /// </summary>
    private void InvalidateCapsuleRegion()
    {
        _lastRegionWidthPx = -1;
        _lastRegionHeightPx = -1;
        _lastRegionRadiusPx = -1;
    }

    /// <summary>
    /// 窗口激活后再次取消系统色描边。
    /// WPF-UI 的 <see cref="FluentWindow.OnActivated"/> 会调 <c>ApplyBorderColor(SystemAccent)</c> 把那条线刷回来，
    /// 所以只在 <c>SourceInitialized</c> 清一次不够——窗口一被点击激活，绿线就重新出现。必须在基类刷完之后再擦一次。
    /// <c>WS_EX_NOACTIVATE</c> 只保证岛体不抢焦点，并不保证 <c>IsActive</c> 恒为 false，所以这条路径真的会走到。
    /// Clears the system-colored outline again after activation. WPF-UI's <see cref="FluentWindow.OnActivated"/> calls
    /// <c>ApplyBorderColor(SystemAccent)</c> and puts the line back, so clearing once at
    /// <c>SourceInitialized</c> is not enough: the moment the window is activated by a click, the line returns.
    /// It has to be wiped after the base class paints it. <c>WS_EX_NOACTIVATE</c> only keeps the island from stealing
    /// focus; it does not keep <c>IsActive</c> permanently false, so this path is genuinely reachable.
    /// </summary>
    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        ClearSystemFrameBorder();
    }

    /// <summary>
    /// 取消 Windows 11 给无边框窗口画的系统色描边。那条线画在窗口外框（非客户区）上，颜色跟着系统强调色走，
    /// 深色主题下就是你看到的那圈深绿细线——它不在 WPF 视觉树里，改主题资源或 XAML 样式都去不掉。
    /// 现在 XAML 已不再设 <c>ExtendsContentIntoTitleBar</c>（岛体自绘标题栏，用不着它，而它会把 WindowStyle
    /// 强制成 SingleBorderWindow 并带来这条线），所以这里是双保险：万一某个系统仍画了，DWMWA_COLOR_NONE 能擦掉。
    /// 让 DWM 不再画（DWMWA_COLOR_NONE）就干净了。失败也不影响使用，只是留着那条线。
    /// Drops the system-colored outline Windows 11 draws around frameless windows. It lives on the non-client frame
    /// and follows the system accent color, which is the thin dark green line on dark themes. It is not in the WPF
    /// visual tree, so no theme resource or XAML style can remove it. The XAML no longer sets
    /// <c>ExtendsContentIntoTitleBar</c> — the island paints its own chrome and never needed it, while it forced
    /// WindowStyle to SingleBorderWindow and brought the line along — so this is now belt-and-braces: should any
    /// system still paint it, DWMWA_COLOR_NONE wipes it. A failure here is harmless and only keeps the line.
    /// </summary>
    private void ClearSystemFrameBorder()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source || source.Handle == nint.Zero)
            return;

        var none = NativeMethods.DWMWA_COLOR_NONE;
        // DwmSetWindowAttribute 失败只表示这台系统不支持该属性，不抛异常：外观问题不该影响功能。
        // A failure only means this system does not support the attribute, so it is swallowed: cosmetics must
        // never break the island.
        NativeMethods.DwmSetWindowAttribute(
            source.Handle, NativeMethods.DWMWA_BORDER_COLOR, ref none, sizeof(int));
    }

    /// <summary>
    /// 挂上窗口消息钩子，只为监听 DPI 变化：系统既不提供 WPF 事件，也不保证 <c>SizeChanged</c> 会触发——
    /// per-monitor 缩放变化时窗口 DIP 尺寸可能一点没动，只有物理像素变了，
    /// 于是上层既没尺寸变化也没 DPI 提示，岛体就挂在错误的物理位置上。
    /// Attaches the window message hook purely to listen for DPI changes: the system offers no WPF event and does not
    /// promise a SizeChanged either — with per-monitor scaling the DIP size can stay identical while only the physical
    /// pixels change, so nothing above learns that the island now sits at the wrong physical spot.
    /// </summary>
    private void AttachWindowMessageHook()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source)
            return;

        _hwndSource = source;
        source.AddHook(OnWindowMessage);
    }

    private nint OnWindowMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_DPICHANGED)
            ScheduleDpiRestore();
        return nint.Zero;
    }

    /// <summary>
    /// DPI 刚变：此刻旧的窗口矩形还有效，先把中心在工作区里的归一化位置存下来，
    /// 再排一次「过渡完成后」的恢复——与 <see cref="RecoverAfterDpiChange"/> 成一对。
    /// A DPI change just arrived: the old window rectangle is still valid, so record the normalised work-area centre
    /// now and queue a recovery for once the transition settles — the counterpart of <see cref="RecoverAfterDpiChange"/>.
    /// </summary>
    private void ScheduleDpiRestore()
    {
        if (_dpiRestoreQueued)
            return;
        _dpiRestoreQueued = true;

        var monitor = ResolveMonitor();
        if (monitor is not null)
        {
            var (dpiX, dpiY) = CurrentWindowDpi();
            _pendingNormalizedCenterX = IslandDpiRestorePolicy.CaptureNormalizedCenterX(
                monitor.WorkArea, new Point(Left, Top), new Size(ActualWidth, ActualHeight), dpiX, dpiY);
        }

        // ContextIdle：等 WPF 自己完成一轮 DPI 换算与重排，再去问尺寸与 WorkArea，否则读到的是过渡中间态。
        // ContextIdle: let WPF finish its own DPI conversion and layout pass before asking for sizes, or what comes
        // back is a mid-transition snapshot.
        Dispatcher.BeginInvoke(RecoverAfterDpiChange, DispatcherPriority.ContextIdle);
    }

    /// <summary>
    /// 过渡结束后的落地：按归一化中心把岛体放回新工作区，并重算一眼 GDI 圆角区域——
    /// 只有 Left/Top 变化而尺寸没变时 <c>SizeChanged</c> 不触发，区域会留着旧物理尺寸。
    /// Landing after the transition: put the island back via the normalised centre and rebuild the GDI region —
    /// when only Left/Top move and no size changes, SizeChanged never fires and the region keeps stale pixels.
    /// </summary>
    private void RecoverAfterDpiChange()
    {
        _dpiRestoreQueued = false;
        if (_hwndSource is null)
            return;

        var monitor = ResolveMonitor();
        if (monitor is null)
            return;

        var (dpiX, dpiY) = CurrentWindowDpi();
        var sizeDip = new Size(
            double.IsFinite(ActualWidth) && ActualWidth > 0 ? ActualWidth : IslandPlacementPolicy.CompactWidthDip,
            double.IsFinite(ActualHeight) && ActualHeight > 0 ? ActualHeight : IslandPlacementPolicy.CompactHeightDip);
        var topLeft = IslandDpiRestorePolicy.RestoreTopLeft(
            monitor.WorkArea, sizeDip, _pendingNormalizedCenterX ?? IslandDpiRestorePolicy.FallbackNormalizedCenterX, dpiX, dpiY);
        _pendingNormalizedCenterX = null;

        InvalidateCapsuleRegion();
        ApplyCapsuleRegion();

        // 动画进行中就别插手：跑去改 Left/Top 会让弹簧还在生长的高度与「该在哪」互相打架。
        // 先把最新的这一次请求存着，弹簧静止后统一落一次——只认最新，中途的过期请求全部作废。
        // Do not intervene mid-animation: changing Left/Top while the spring still grows a height makes the two
        // fight. The newest request is parked and landed once after the spring settles — the newest wins and
        // anything outdated in between is simply dropped.
        if (_heightSpring.IsSettled)
            ApplyPlacementCoordinates(topLeft);
        else
            _pendingPlacementDip = topLeft;
    }

    private void ApplyPlacementCoordinates(Point topLeftDip)
    {
        Left = topLeftDip.X;
        Top = topLeftDip.Y;
    }

    private void ApplyPlacement(double heightDip)
    {
        _displayMonitorService.Refresh();
        var monitor = ResolveMonitor();
        if (monitor is null)
            return;
        var topLeft = IslandPlacementPolicy.CalculateTopCenter(
            monitor.WorkArea, new Size(IslandPlacementPolicy.CompactWidthDip, heightDip), monitor.DpiX, monitor.DpiY);
        Left = topLeft.X;
        Top = topLeft.Y;
    }

    /// <summary>
    /// 解析岛体所在显示器：按窗口当前占据的位置反查，命中不了才回退主屏、再回首屏。
    /// 早先固定取主屏，岛体被拖到副屏后夹取又按主屏工作区把它拽回去，跨屏拖拽因此是坏的。
    /// Resolves the display the island sits on by looking up where the window currently is, falling back to the
    /// primary and then the first display. It used to always take the primary, which broke cross-screen dragging
    /// because the clamp kept pulling the island back into the primary work area.
    /// </summary>
    private DisplayMonitorInfo? ResolveMonitor()
    {
        var monitors = RefreshedMonitors();
        var (dpiX, dpiY) = CurrentWindowDpi();
        return IslandMonitorPolicy.ResolveByWindow(
            monitors, new Point(Left, Top), new Size(IslandPlacementPolicy.CompactWidthDip, ActualHeight), dpiX, dpiY);
    }

    /// <summary>刷新显示器快照。 / Refreshes the display snapshot.</summary>
    private IReadOnlyList<DisplayMonitorInfo> RefreshedMonitors()
    {
        _displayMonitorService.Refresh();
        return _displayMonitorService.GetMonitors();
    }

    /// <summary>
    /// 窗口当前的 DPI。布局策略约定「窗口 Left/Top 是 DIP、显示器快照是物理像素」，拿错缩放会把
    /// DIP 坐标算到另一块屏上去，所以这里只认窗口自己的 DPI——系统在窗口跨屏时经 WM_DPICHANGED 更新它。
    /// 窗口尚未创建 HWND、没有 DPI 上下文时退到主屏，避免用 0 缩放把坐标算飞。
    /// The window's current DPI. The placement policy assumes "window Left/Top are DIP while display snapshots are
    /// physical pixels", so only the window's own DPI is trusted here: the system updates it via WM_DPICHANGED when
    /// the window crosses screens. Before the HWND exists there is no DPI context, so it falls back to the primary.
    /// </summary>
    private (uint DpiX, uint DpiY) CurrentWindowDpi()
    {
        if (PresentationSource.FromVisual(this) is not null)
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var x = (uint)Math.Round(dpi.DpiScaleX * 96d);
            var y = (uint)Math.Round(dpi.DpiScaleY * 96d);
            if (x > 0u && y > 0u)
                return (x, y);
        }

        var fallback = PrimaryDisplayDpi();
        return (fallback, fallback);
    }

    private uint PrimaryDisplayDpi()
    {
        foreach (var monitor in RefreshedMonitors())
        {
            if (monitor.IsPrimary)
                return monitor.DpiX;
        }
        return 96u;
    }

    private void BuildSpectrumBars()
    {
        var barBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF)) { Opacity = 0.9 };
        barBrush.Freeze();
        for (var band = 0; band < IslandWindowViewModel.SpectrumBandCount; band++)
        {
            // ScaleTransform 自底向上缩放：柱子只改 ScaleY，不触发布局，帧成本可以忽略。
            // Bottom-anchored ScaleTransform: only ScaleY changes per frame, so layout is never invalidated.
            var scale = new ScaleTransform(1, SpectrumBarMinimumScale);
            var bar = new Border
            {
                Width = SpectrumPresentationPolicy.BarWidthDip,
                Height = SpectrumCanvas.Height,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = barBrush,
                RenderTransform = scale,
                RenderTransformOrigin = new Point(0.5, 1)
            };
            Canvas.SetLeft(bar, band * SpectrumPresentationPolicy.BarPitchDip);
            SpectrumCanvas.Children.Add(bar);
            _spectrumBars.Add((bar, scale));
        }
    }

    private void OnSpectrumFrame(object? sender, IReadOnlyList<float> levels)
    {
        for (var band = 0; band < _spectrumBars.Count && band < levels.Count; band++)
        {
            var level = Math.Clamp(levels[band], 0f, 1f);
            _spectrumBars[band].Scale.ScaleY = SpectrumBarMinimumScale + (1 - SpectrumBarMinimumScale) * level;
        }
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IslandWindowViewModel.Artwork)
            or nameof(IslandWindowViewModel.PrimaryText)
            or nameof(IslandWindowViewModel.IsPlaying)
            or nameof(IslandWindowViewModel.IsConnected))
        {
            RefreshMediaVisuals();
        }
    }

    private void RefreshMediaVisuals()
    {
        var snapshot = _viewModel.Snapshot;
        var artwork = _viewModel.Artwork;
        CoverImage.Source = artwork;
        ExpandedCoverImage.Source = artwork;
        CoverImage.Visibility = artwork is null ? Visibility.Collapsed : Visibility.Visible;
        ExpandedCoverImage.Visibility = artwork is null ? Visibility.Collapsed : Visibility.Visible;
        CoverPlaceholder.Visibility = artwork is null ? Visibility.Visible : Visibility.Collapsed;
        ExpandedCoverPlaceholder.Visibility = artwork is null ? Visibility.Visible : Visibility.Collapsed;

        // 歌词交给跑马灯状态源：换行即复位并重走起读停留。
        // The lyric goes through the marquee state: a new line resets it and repeats the lead-in pause.
        _marquee.Base = _viewModel.PrimaryText;
        if (!_marquee.Advancing)
            PrimaryText.Text = _viewModel.PrimaryText;
        ExpandedTitleText.Text = snapshot.IsConnected ? snapshot.Title : string.Empty;
        ExpandedArtistText.Text = snapshot.IsConnected ? snapshot.Artist : string.Empty;
        PlayPauseButton.Content = _viewModel.IsPlaying ? "\u23F8" : "\u25B6";

        // 未连接时压暗整颗胶囊：告诉用户岛在待命，而不是「坏了」。
        // Dim the whole capsule while disconnected: the island is standing by, not broken.
        Opacity = _viewModel.IsConnected ? 1 : 0.45;
    }

    /// <summary>
    /// 点击胶囊：锁定展开态，指针移开也不收起，再点一次解锁收起。
    /// 岛体不做拖动：它顶部居中的位置本来就是按当前显示器工作区算出来的，拖到别处的结果活不过一次
    /// 隐藏（下次显示仍会弹回顶部居中），而基于窗口相对坐标的拖拽会形成自激反馈——窗口一动，坐标系跟着
    /// 动，下一帧的位移基准又变了，表现为持续抽搐。
    /// Clicking the capsule pins the expansion so moving away does not collapse it, and clicking again releases it.
    /// The island is not draggable: its top-center placement is computed from the current display's work area, so
    /// a moved position would not survive the next hide (showing always re-centers it at the top), and a drag
    /// based on window-relative coordinates feeds back on itself — moving the window moves the coordinate system,
    /// which changes the next frame's baseline, showing up as continuous jitter.
    /// </summary>
    private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _hover.TogglePin();
        SetExpanded(_hover.ShouldExpand);
    }

    /// <summary>
    /// 每帧推进：先积分弹簧高度，再推一帧歌词滚动。两件事都只在需要时才有开销。
    /// Advances one frame: integrates the height spring first, then nudges the lyric marquee.
    /// </summary>
    private void OnFrameTick(object? sender, EventArgs e) => OnFrame();

    /// <summary>
    /// 每帧推进：先积分弹簧高度，再推一帧歌词滚动。两者都静止时顺手停掉帧节拍。
    /// Advances one frame: integrates the height spring first, then nudges the lyric marquee, and idles the beat once both are at rest.
    /// </summary>
    private void OnFrame()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _lastFrameAt;
        _lastFrameAt = now;

        // 停留计时的分辨率就挂在这条 16ms 的节拍上：220ms 的 dwell 才真的是 220ms。
        // The dwell clock's resolution rides this 16 ms beat, so a 220 ms dwell really is 220 ms.
        SampleHover();

        if (!_heightSpring.IsSettled)
        {
            _heightSpring.Advance(elapsed);
            // 弹簧可能过冲到目标之上，窗口高度永远不许为负：下界钉在紧凑态高度。
            // The spring may overshoot past its target, but a window height is never allowed to go negative.
            Height = Math.Max(IslandPlacementPolicy.CompactHeightDip, _heightSpring.Position);

            // 面板透明度跟着弹簧进度走：展开时内容随高度浮现，收起时先隐再缩。
            // Panel opacity rides the spring's progress so content appears with the growth and leaves before the shrink.
            ExpandedPanel.Opacity = Math.Clamp(_heightSpring.Progress, 0d, 1d);

            if (_heightSpring.IsSettled)
                CompleteExpansion();
        }

        // 外壳圆角与 GDI 区域半径同步跟进，否则 WPF 画的圆角和窗口裁剪轮廓会在动画中错位。
        // The shell's XAML radius tracks the same interpolation, or WPF's painted corners drift out of step with
        // the window's clipping region mid-animation.
        SyncShellCornerRadius();

        AdvanceMarquee(elapsed);
        StopFrameTimerIfIdle();
    }

    /// <summary>
    /// 让 <see cref="Shell"/> 的圆角跟 GDI 区域用同一个半径。两处必须一致：XAML 圆角决定 WPF 画出来的
    /// 弧线，窗口区域决定 HWND 真正被裁成什么形状，不一致就会在四角看到台阶。
    /// Keeps <see cref="Shell"/>'s XAML radius identical to the GDI region radius. The two must agree: the XAML
    /// radius paints the arc while the window region clips the HWND, and a mismatch shows as steps in the corners.
    /// </summary>
    private void SyncShellCornerRadius()
    {
        var radius = ResolveShellRadiusDip();
        if (Math.Abs(radius - _lastShellRadius) < 0.05)
            return;

        _lastShellRadius = radius;
        Shell.CornerRadius = new CornerRadius(radius);
    }

    /// <summary>
    /// 当前形态该用的圆角半径（DIP）：运动中按弹簧进度在紧凑态与展开态之间插值，静止时取形态的定值。
    /// 插值避免收起/展开的最后一帧突然换形状，而窗口区域与 XAML 圆角共用这个结果。
    /// The corner radius the current shape wants (DIP): interpolated along the spring progress while moving, and
    /// the shape's fixed value at rest. Interpolating avoids a snap on the animation's last frame, and both the
    /// window region and the XAML radius read this one result.
    /// </summary>
    private double ResolveShellRadiusDip()
    {
        // 静止时取目标形态的定值。
        // At rest, the target shape's fixed value applies.
        if (_heightSpring.IsSettled)
            return _isExpanded ? ExpandedCornerRadiusDip : CapsuleCornerRadiusDip;

        // 运动中在两个定值之间插值。插值方向与弹簧的起止一致：
        // 展开时半径从胶囊的 23 走到展开态的 18，收起时反向。
        // While moving, interpolate between the two fixed radii. The direction follows the spring: expanding goes
        // from the capsule's 23 to the expanded 18, collapsing goes back.
        var from = _isExpanded ? CapsuleCornerRadiusDip : ExpandedCornerRadiusDip;
        var to = _isExpanded ? ExpandedCornerRadiusDip : CapsuleCornerRadiusDip;
        var progress = Math.Clamp(_heightSpring.Progress, 0d, 1d);
        return from + ((to - from) * progress);
    }

    /// <summary>弹簧静止：把高度与透明度落回静态值。 / Spring at rest: land the height and opacity on plain values.</summary>
    private void CompleteExpansion()
    {
        Height = _heightSpring.Target;
        ExpandedPanel.Opacity = _isExpanded ? 1d : 0d;
        ExpandedPanel.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;

        // 延后的定位请求在这里落地：动画期间进来的（DPI 切换、跨屏）被收在 _pendingPlacementDip，
        // 只保留最后一次，此刻一次性应用，避免动画每帧都与新目标来回拉扯。
        // Deferred placement lands here: requests that arrived mid-animation (a DPI switch, a display hop) were parked
        // in _pendingPlacementDip keeping only the newest, and are applied once here so no frame fights the target.
        if (_pendingPlacementDip is { } pending)
        {
            _pendingPlacementDip = null;
            ApplyPlacementCoordinates(pending);
        }
    }

    /// <summary>推一帧歌词跑马灯。 / Advances the lyric marquee one frame.</summary>
    private void AdvanceMarquee(TimeSpan elapsed)
    {
        // 可用宽度取自裁剪容器而非TextBlock：滚动时 TextBlock 里装的是窗口字符串，
        // 它的ActualWidth 会随内容变化，拿它当基准就成了自激反馈。
        // The available width comes from the clipping host, not the TextBlock: while scrolling the TextBlock holds
        // the window string and its ActualWidth moves with the content, which would feed back on itself.
        var available = PrimaryTextHost.ActualWidth;
        if (available <= 0d)
            return;

        var source = _marquee.Base;
        if (string.IsNullOrEmpty(source))
        {
            _marqueeTransform.X = 0d;
            PrimaryText.TextTrimming = TextTrimming.CharacterEllipsis;
            return;
        }

        // 先把表打好：溢出判定与滚动位移共用它，一条歌词因此每帧只排版一次。
        // Build the table first: the overflow test and the scroll offset share it, so one line is laid out once
        // per frame at most.
        var sourceWidth = EnsureMarqueePrefixWidths(source);
        if (_marquee.Configure(sourceWidth, available, _motion.UseContinuousMotion))
        {
            if (_marquee.Advance(elapsed))
                PrimaryText.Text = _marquee.BuildWindow();

            // 位移走平移变换的 X；写入前先看是否真的变了，避免每帧无谓地弄脏渲染树。
            // The offset rides the translate transform's X, and only a real change touches the render tree.
            var offset = _marquee.ResolveOffsetDip(_marqueePrefixWidths, _marqueeMeasuredCharacters);
            if (Math.Abs(_marqueeTransform.X - offset) > 0.01)
                _marqueeTransform.X = offset;
            PrimaryText.TextTrimming = TextTrimming.None;
            return;
        }

        // 放得下（或当前不允许滚动）：回到静态呈现，省略号该留着就留着。
        // 宽度表不主动清——它按长度与字体判定有效性，换行时新文本长度不同自然失效。
        // It fits, or advancing is not allowed: fall back to the static presentation. The width table is not
        // dropped here; it invalidates itself by length and font when the lyric changes.
        if (_marquee.Advancing)
            PrimaryText.Text = source;

        _marqueeTransform.X = 0d;
        PrimaryText.TextTrimming = string.IsNullOrEmpty(source) ? TextTrimming.CharacterEllipsis : TextTrimming.None;
    }

    /// <summary>
    /// 懒测「原文 + 接缝」的前缀宽度表：溢出判定、滚动的小数位移与行进速度全部从这张表读，
    /// 一条歌词因此只排版一次，而不是每帧一次。表按需向前生长，换文字、字号或字重时整表重测。
    /// Lazily measures the prefix widths of source-plus-seam; the overflow test, the fractional scroll offset and
    /// the travel speed all read from it, so one line is laid out once instead of once per frame. The table
    /// grows on demand; new text, size, or weight rebuilds it.
    /// </summary>
    /// <returns>原文字宽（DIP）。 / The width of the source text in DIP.</returns>
    private double EnsureMarqueePrefixWidths(string source)
    {
        var text = MarqueeRotationPolicy.BuildSource(source);
        var font = $"{PrimaryText.FontSize:0.##}|{PrimaryText.FontWeight}|{PrimaryText.FontFamily.Source}";
        var valid = _marqueePrefixWidths is { } cached
            && cached.Length == text.Length + 1
            && string.Equals(_marqueeMeasuredFont, font, StringComparison.Ordinal);
        if (!valid)
        {
            _marqueePrefixWidths = new double[text.Length + 1];
            _marqueeMeasuredCharacters = 0;
            _marqueeMeasuredFont = font;
        }

        // 只补到当前窗口起点再多一个字符：够算偏移就行，不必把整句都排版一遍。
        // Measure only up to the window start plus one character: enough for the offset, without laying out
        // the whole line.
        var required = Math.Min(text.Length, (_marquee.WindowStart < 0 ? 0 : _marquee.WindowStart) + 1);
        for (var index = _marqueeMeasuredCharacters + 1; index <= required; index++)
            _marqueePrefixWidths![index] = MeasureTextWidth(text[..index], PrimaryText);
        _marqueeMeasuredCharacters = Math.Max(_marqueeMeasuredCharacters, required);

        // 溢出判定要的是原文（不含接缝）的宽度：接缝只有 3 个空格，不影响「放不放得下」的结论。
        // The overflow test wants the source width without the seam; three spaces never change whether it fits.
        return MeasureTextWidth(source, PrimaryText);
    }

    /// <summary>量一段文字在给定文本元素上的精确宽度（DIP），不触发布局。 / Exact width of a string on a text element, in DIP.</summary>
    private static double MeasureTextWidth(string text, System.Windows.Controls.TextBlock source)
    {
        if (string.IsNullOrEmpty(text))
            return 0d;

        var formatted = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(source.FontFamily, source.FontStyle, source.FontWeight, source.FontStretch),
            source.FontSize,
            Brushes.Transparent,
            VisualTreeHelper.GetDpi(source).PixelsPerDip)
        {
            Trimming = TextTrimming.None
        };
        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>只在弹簧在动或歌词在滚时才有帧节拍，其余时间完全停表。 / The frame beat only runs while the spring or the marquee needs it.</summary>
    private void StartFrameTimer()
    {
        _lastFrameAt = DateTimeOffset.UtcNow;
        if (!_frameTimer.IsEnabled)
            _frameTimer.Start();
    }

    /// <summary>起低频探针。 / Starts the presence probe.</summary>
    private void StartPresenceProbe()
    {
        _lastHoverSampleAt = DateTimeOffset.UtcNow;
        if (!_presenceProbe.IsEnabled)
            _presenceProbe.Start();
    }

    private void StopFrameTimerIfIdle()
    {
        // dwell 未决时不能停表：没有人为它计时，这次展开就永远停在「差半拍」上再也不涨。
        // A pending dwell must not idle the beat: nobody would be left to clock it and it would sit one tick short.
        if (_heightSpring.IsSettled && !_marquee.Advancing && _hover.PendingMilliseconds <= 0d)
            _frameTimer.Stop();
    }

    /// <summary>
    /// 把形态设为目标态：<paramref name="expanded"/> 为 true 就展开，为 false 就收起。
    /// 刻意做成「设定」而不是「取反」——悬停（临时）与点击锁定（粘滞）是同一个状态位的两个来源，
    /// 对「鼠标移开」的期望相反；调用方若自己取反，两边就会永远拉扯、谁也压不过谁。
    /// 高度交给弹簧积分，顶边保持不动，因此展开向下生长，符合顶部岛体的直觉。收起复用同一根弹簧
    /// （速度连续），中途反向会像被手推开一样立刻掉头，而不是重新起步。
    /// Drives the shape to a target: expanding when <paramref name="expanded"/> is true, collapsing when it is false.
    /// Deliberately a "set" rather than a "toggle": hover (transient) and the click pin (sticky) are two sources
    /// for one state bit and expect opposite things when the pointer leaves. A caller that inverts the current
    /// shape instead would leave the two sources pulling against each other forever. The height runs on the
    /// spring while the top edge stays put, so expansion grows downward as a top island should. Collapse reuses
    /// the same spring, so a mid-flight reversal keeps continuous velocity instead of restarting from rest.
    /// </summary>
    /// <param name="expanded">目标展开态。 / The target expansion state.</param>
    private void SetExpanded(bool expanded)
    {
        if (_isExpanded == expanded)
            return;

        // 展开与收起都要知道目标屏：展开高度按该屏工作区算，收起时则用紧凑态高度去夹住位置，
        // 免得面板在矮屏上超出下边界。
        // Both directions need the target display: the expanded height comes from its work area, and collapsing
        // clamps with the compact height so the panel cannot overflow the bottom on a short display.
        var monitor = ResolveMonitor();
        var targetHeight = expanded
            ? IslandPlacementPolicy.ResolveExpandedHeight(
                monitor?.WorkArea ?? Rect.Empty, monitor?.DpiX ?? 0, monitor?.DpiY ?? 0)
            : IslandPlacementPolicy.CompactHeightDip;

        // 高度动之前先抓锚点：Width/Height 落地之后旧的边缘就拿不回来了，而这一次的展开/收起
        // 要保证「原来的横向中心」和「原来的顶边」都不漂。（祖传自原作者被删的那版 Window.Animation，
        // 他那版宽高都变、按贴靠边挑锚点；本形态固定宽度且顶部居中，只用（中心 + 顶边）这一对。）
        // Capture the anchors before the height moves: once Width/Height land the old edges are gone, and this
        // expansion/collapse must keep both "the previous horizontal centre" and "the previous top edge" from drifting.
        var anchors = IslandSizeAnchors.Capture(new Point(Left, Top), new Size(ActualWidth, Height));

        if (monitor is not null)
        {
            var targetSize = new Size(IslandPlacementPolicy.CompactWidthDip, Math.Max(targetHeight, Height));
            var anchored = anchors.ResolveTopLeft(targetSize, IslandAnchorX.Center, IslandAnchorY.Top);
            var clamped = IslandPlacementPolicy.ClampWithinWorkArea(
                anchored, targetSize, monitor.WorkArea, monitor.DpiX, monitor.DpiY);
            Left = clamped.X;
            Top = clamped.Y;
        }

        if (expanded)
        {
            // 内容错峰淡入的起点：面板先全透明，随高度增长一起浮现。
            // Start of the staggered reveal: the panel begins fully transparent and fades in as the window grows.
            ExpandedPanel.Visibility = Visibility.Visible;
        }

        _isExpanded = expanded;
        _heightSpring.Retarget(targetHeight);

        // 动效被系统关掉时直接落值，不进积分循环。
        // When the environment has animations off, land the value directly instead of integrating.
        if (!_motion.UseTransitions)
        {
            _heightSpring.Advance(TimeSpan.FromSeconds(1));
            CompleteExpansion();
            return;
        }

        StartFrameTimer();
    }

    /// <summary>
    /// 全局左键按在岛体之外：解锁并立刻收起。岛体是 no-activate 窗口，拿不到焦点事件，
    /// 所以「点别处」只能由全局鼠标钩子通知，再按屏幕坐标判断落点是否落在自己身上。
    /// A global left click outside the island: drops the pin and collapses at once. The island is a no-activate
    /// window and never receives focus events, so "clicked elsewhere" can only come from the global mouse hook,
    /// which reports screen coordinates for us to test against our own bounds.
    /// </summary>
    private void OnGlobalLeftButtonPressed(object? sender, NativeMouseButtonEventArgs e)
    {
        if (!_isExpanded)
            return;
        if (IsPointInsideWindow(e.ScreenX, e.ScreenY))
            return;

        _hover.CollapseFromOutsideClick();
        SetExpanded(false);
    }

    /// <summary>
    /// 判断屏幕物理坐标是否落在岛体窗口内。不用 <see cref="PointFromScreen"/>：per-monitor DPI 过渡期间它会用
    /// 上一块屏的旧变换，把本该在岛内的点判成外面。窗口的 <see cref="Window.Left"/> 与 <see cref="Window.Top"/>
    /// 是 DIP、钩子给的是物理像素，所以两个方向各按自己的 DPI 换算——横向与纵向缩放不必相等。
    /// Decides whether a physical screen point falls inside the island window. <see cref="PointFromScreen"/> is
    /// avoided because during a per-monitor DPI transition it uses the previous screen's stale transform and reports
    /// an inside point as outside. The window's <see cref="Window.Left"/>/<see cref="Window.Top"/> are DIP while the
    /// hook reports physical pixels, so each axis converts with its own DPI — the two scales need not match.
    /// </summary>
    private bool IsPointInsideWindow(int screenX, int screenY)
    {
        var (dpiX, dpiY) = CurrentWindowDpi();
        var scaleX = dpiX == 0u ? 1d : dpiX / 96d;
        var scaleY = dpiY == 0u ? 1d : dpiY / 96d;
        var width = ActualWidth > 0 ? ActualWidth : Width;
        var height = ActualHeight > 0 ? ActualHeight : Height;
        var left = Left * scaleX;
        var top = Top * scaleY;
        return screenX >= left && screenX <= left + (width * scaleX)
            && screenY >= top && screenY <= top + (height * scaleY);
    }

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e) => await _viewModel.TogglePlayPauseAsync();

    private async void OnSkipPreviousClick(object sender, RoutedEventArgs e) => await _viewModel.SkipPreviousAsync();

    private async void OnSkipNextClick(object sender, RoutedEventArgs e) => await _viewModel.SkipNextAsync();
}
