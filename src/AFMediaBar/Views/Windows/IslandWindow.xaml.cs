using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
    /// <summary>位移小于该阈值视为点击而不是拖拽。 / Movement below this threshold counts as a click, not a drag.</summary>
    private const double DragThresholdDip = 4;

    /// <summary>展开动画时长。 / Expand animation duration.</summary>
    private static readonly TimeSpan ExpandDuration = TimeSpan.FromMilliseconds(260);

    /// <summary>收起动画时长：比展开短一截，离场不拖沓。 / Collapse duration: shorter than expand so the exit feels snappy.</summary>
    private static readonly TimeSpan CollapseDuration = TimeSpan.FromMilliseconds(190);

    /// <summary>频谱条静止时的最小缩放，对应「残桩」观感。 / Minimum bar scale at rest — the resting-stub look.</summary>
    private const double SpectrumBarMinimumScale = 0.08;

    /// <summary>紧凑态胶囊圆角半径（DIP），等于胶囊高度的一半。 / Compact capsule corner radius (DIP), half the capsule height.</summary>
    private const double CapsuleCornerRadiusDip = 23;

    private readonly IslandWindowViewModel _viewModel;
    private readonly IDisplayMonitorService _displayMonitorService;
    private readonly List<(Border Bar, ScaleTransform Scale)> _spectrumBars = [];
    private Point _dragStartPosition;
    private Point _dragWindowOrigin;
    private bool _dragMoved;
    private bool _isExpanded;

    /// <summary>创建岛体窗口并接通外观、显示器与状态源。 / Creates the island window and wires appearance, monitors, and state.</summary>
    public IslandWindow(IslandWindowViewModel viewModel, WindowAppearanceService appearanceService, IDisplayMonitorService displayMonitorService)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _displayMonitorService = displayMonitorService;

        // 岛体悬浮在桌面与全屏内容之上，绝不能抢焦点：与曲目通知窗口同一套非激活窗口处理。
        // The island floats above the desktop and must never steal focus: same non-activating treatment as the
        // track-change notification window.
        WindowHelper.SetNoActivate(this);
        appearanceService.AttachNonActivatingTransient(this);
        DataContext = viewModel;

        BuildSpectrumBars();
        viewModel.PropertyChanged += ViewModel_PropertyChanged;
        viewModel.SpectrumFrame += OnSpectrumFrame;
        RefreshMediaVisuals();

        // 非 layered 窗口没有 AllowsTransparency 可用，胶囊轮廓用 GDI 圆角区域裁出：
        // 尺寸一变（含展开高度动画的每一帧）就重设一次区域，半径跟随高度内插。
        // Without AllowsTransparency the capsule silhouette is clipped by a GDI round region,
        // reapplied on every size change (including each frame of the expand animation).
        SourceInitialized += (_, _) => ApplyCapsuleRegion();
        SizeChanged += (_, _) => ApplyCapsuleRegion();

        Closed += (_, _) =>
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.SpectrumFrame -= OnSpectrumFrame;
            _viewModel.Detach();
        };
    }

    /// <summary>把岛体定位到主显示器顶部居中并显示，随后启动状态源。 / Places the island at the top-center of the primary display, shows it, and starts the state source.</summary>
    public void ShowIsland()
    {
        // TODO(prototype): 生命周期调试日志，正式发布前随本注释一并移除。 / Lifecycle debug logging; remove before release.
        AppLogService.Current?.Info("Island", $"显示岛体 / showing island at ({Left:F0},{Top:F0}) h={Height:F0}");
        ApplyPlacement(IslandPlacementPolicy.CompactHeightDip);
        _viewModel.Start();
        Show();
    }

    /// <summary>隐藏岛体并停掉状态源；窗口实例保留，供显示模式页或协调器再次唤起。 / Hides the island and stops its state source; the instance stays alive for re-showing.</summary>
    public void HideIsland()
    {
        // TODO(prototype): 调试日志，正式发布前移除。 / Debug logging; remove before release.
        AppLogService.Current?.Info("Island", "隐藏岛体 / hiding island");
        Hide();
        _viewModel.Stop();
    }

    /// <summary>
    /// 用 GDI 圆角矩形区域把窗口裁成胶囊/圆角面板轮廓。半径取「23 DIP 与高度一半的较小者」：
    /// 紧凑态（46 DIP）正好是半圆端帽，展开态退化为 23 DIP 圆角的矩形。
    /// set 成功后区域归系统所有，旧区域由系统释放，这里不做任何 DeleteObject。
    /// Clips the window to a capsule / rounded-panel silhouette with a GDI round region. The radius is the
    /// smaller of 23 DIP and half the height: a half-circle cap when compact (46 DIP), a 23 DIP rounded
    /// rectangle when expanded. After a successful set the region belongs to the system — never DeleteObject it here.
    /// </summary>
    private void ApplyCapsuleRegion()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source || source.Handle == nint.Zero)
            return;
        if (ActualWidth <= 0 || ActualHeight <= 0)
            return;

        // TODO(prototype): 调试日志，正式发布前移除。 / Debug logging; remove before release.
        AppLogService.Current?.Verbose("Island", $"重设窗口区域 / reapply region {ActualWidth:F0}x{ActualHeight:F0}");

        var dpi = VisualTreeHelper.GetDpi(this);
        var widthPx = (int)Math.Ceiling(ActualWidth * dpi.PixelsPerDip);
        var heightPx = (int)Math.Ceiling(ActualHeight * dpi.PixelsPerDip);
        var radiusPx = (int)Math.Ceiling(Math.Min(CapsuleCornerRadiusDip, ActualHeight / 2) * dpi.PixelsPerDip);
        var region = NativeMethods.CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, radiusPx, radiusPx);
        if (region == nint.Zero)
            return;
        NativeMethods.SetWindowRgn(source.Handle, region, true);
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

    /// <summary>解析岛体所在显示器：优先主屏，缺失时回退到首个可用显示器。 / Resolves the target monitor: primary first, then the first available.</summary>
    private DisplayMonitorInfo? ResolveMonitor()
    {
        var monitors = _displayMonitorService.GetMonitors();
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
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

        PrimaryText.Text = _viewModel.PrimaryText;
        ExpandedTitleText.Text = snapshot.IsConnected ? snapshot.Title : string.Empty;
        ExpandedArtistText.Text = snapshot.IsConnected ? snapshot.Artist : string.Empty;
        PlayPauseButton.Content = _viewModel.IsPlaying ? "\u23F8" : "\u25B6";

        // 未连接时压暗整颗胶囊：告诉用户岛在待命，而不是「坏了」。
        // Dim the whole capsule while disconnected: the island is standing by, not broken.
        Opacity = _viewModel.IsConnected ? 1 : 0.45;
    }

    private void Pill_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPosition = e.GetPosition(this);
        _dragWindowOrigin = new Point(Left, Top);
        _dragMoved = false;
        Pill.CaptureMouse();
    }

    private void Pill_MouseMove(object sender, MouseEventArgs e)
    {
        if (!Pill.IsMouseCaptured)
            return;
        var position = e.GetPosition(this);
        var delta = position - _dragStartPosition;
        if (!_dragMoved && Math.Abs(delta.X) < DragThresholdDip && Math.Abs(delta.Y) < DragThresholdDip)
            return;
        _dragMoved = true;
        Left = _dragWindowOrigin.X + delta.X;
        Top = _dragWindowOrigin.Y + delta.Y;
    }

    private void Pill_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Pill.IsMouseCaptured)
            Pill.ReleaseMouseCapture();

        // 拖完顺手把落点夹回工作区；没拖动则视为点击，切换展开态。
        // Clamp the landing point back into the work area; a non-drag release counts as a click and toggles expansion.
        var monitor = ResolveMonitor();
        if (_dragMoved)
        {
            if (monitor is not null)
            {
                var clamped = IslandPlacementPolicy.ClampWithinWorkArea(
                    new Point(Left, Top),
                    new Size(IslandPlacementPolicy.CompactWidthDip, ActualHeight),
                    monitor.WorkArea, monitor.DpiX, monitor.DpiY);
                Left = clamped.X;
                Top = clamped.Y;
            }
            return;
        }
        ToggleExpanded(monitor);
    }

    /// <summary>就地长高/收回：高度动画走窗口本身，顶边保持不动，因此展开向下生长，符合顶部岛体的直觉。 / Grows or collapses in place: the window animates its own height while the top edge stays put.</summary>
    private void ToggleExpanded(DisplayMonitorInfo? monitor)
    {
        // 进入本方法时 _isExpanded 描述的是「当前」状态：true 表示正在展开、即将收起。
        // On entry _isExpanded describes the current state: true means expanded and about to collapse.
        var collapsing = _isExpanded;
        var targetHeight = collapsing
            ? IslandPlacementPolicy.CompactHeightDip
            : IslandPlacementPolicy.ResolveExpandedHeight(
                monitor?.WorkArea ?? Rect.Empty, monitor?.DpiX ?? 0, monitor?.DpiY ?? 0);

        if (monitor is not null)
        {
            var clamped = IslandPlacementPolicy.ClampWithinWorkArea(
                new Point(Left, Top),
                new Size(IslandPlacementPolicy.CompactWidthDip, targetHeight),
                monitor.WorkArea, monitor.DpiX, monitor.DpiY);
            Left = clamped.X;
            Top = clamped.Y;
        }

        if (!collapsing)
        {
            // 内容错峰淡入的起点：面板先全透明，随高度增长一起浮现。
            // Start of the staggered reveal: the panel begins fully transparent and fades in as the window grows.
            ExpandedPanel.Opacity = 0;
            ExpandedPanel.Visibility = Visibility.Visible;
        }

        // 手感分工：展开用 BackEase 轻微过冲（灵动岛的「呼吸感」），收起用加速曲线快速离场。
        // Feel: expand overshoots slightly (BackEase, the island "breath"), collapse accelerates away.
        IEasingFunction easing = collapsing
            ? new QuadraticEase { EasingMode = EasingMode.EaseIn }
            : new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.18 };
        var duration = collapsing ? CollapseDuration : ExpandDuration;

        var animation = new DoubleAnimation(ActualHeight, targetHeight, duration) { EasingFunction = easing };
        // 面板透明度与高度同轨：展开时长高先行、内容跟上；收起时内容先隐、空壳再缩。
        // Panel opacity rides along: content follows the growth on expand, leads the shrink on collapse.
        var fade = new DoubleAnimation(collapsing ? 1 : 0, collapsing ? 0 : 1, duration) { EasingFunction = easing };
        // TODO(prototype): 调试日志，正式发布前移除。 / Debug logging; remove before release.
        AppLogService.Current?.Info("Island", $"切换展开态 / toggle expanded: {ActualHeight:F0} → {targetHeight:F0}");
        animation.Completed += (_, _) =>
        {
            // 动画结束后把高度落回普通属性值，否则 HoldEnd 会钉住布局。
            // Land the height back onto the plain property after the animation; HoldEnd would pin the layout.
            BeginAnimation(HeightProperty, null);
            Height = targetHeight;
            if (collapsing)
            {
                ExpandedPanel.Visibility = Visibility.Collapsed;
            }
            // 透明度同理落回静态值，别让 HoldEnd 钉在中间帧。 / Land opacity too; HoldEnd would pin it.
            ExpandedPanel.BeginAnimation(OpacityProperty, null);
            ExpandedPanel.Opacity = collapsing ? 0 : 1;
        };

        _isExpanded = !collapsing;
        BeginAnimation(HeightProperty, animation);
        ExpandedPanel.BeginAnimation(OpacityProperty, fade);
    }

    private async void OnPlayPauseClick(object sender, RoutedEventArgs e) => await _viewModel.TogglePlayPauseAsync();

    private async void OnSkipPreviousClick(object sender, RoutedEventArgs e) => await _viewModel.SkipPreviousAsync();

    private async void OnSkipNextClick(object sender, RoutedEventArgs e) => await _viewModel.SkipNextAsync();
}
