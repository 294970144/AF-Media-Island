using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using AFMediaBar.Classes.Models;
using AFMediaBar.Classes.Services;
using AFMediaBar.Classes.Services.Audio;

namespace AFMediaBar.ViewModels.Windows;

/// <summary>
/// 灵动岛窗口的状态源：订阅 <see cref="MediaSessionService"/> 快照、按播放进度推进歌词时钟、
/// 按 16ms 帧节拍采样频谱电平。它不接触窗口句柄与控件，呈现全部经由属性与事件交给窗口。
/// State source for the island window: subscribes to media snapshots, advances a lyric clock from the
/// playback timeline, and samples spectrum levels on a 16 ms frame beat. It never touches window handles
/// or controls; everything reaches the view through properties and events.
/// </summary>
public sealed class IslandWindowViewModel : INotifyPropertyChanged
{
    /// <summary>
    /// 迷你频谱的柱数；岛体宽度按此与 <see cref="SpectrumPresentationPolicy"/> 的柱距推算。
    /// ⚠️ 下限必须 ≥ <see cref="SpectrumComponentSettings.MinimumBandCount"/>（当前 9）：
    /// <see cref="AudioMonitorService.GetSpectrum"/> 会先把柱数夹取到持久化区间再校验缓冲区长度，
    /// 传低于下限的柱数会直接抛 ArgumentException（实战炸过一次进程）。
    /// Mini-spectrum bar count; must stay ≥ the persisted minimum (9) or GetSpectrum throws.
    /// </summary>
    public const int SpectrumBandCount = 9;

    private static readonly TimeSpan LyricClockInterval = TimeSpan.FromMilliseconds(120);
    private static readonly TimeSpan SpectrumFrameInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>电平上冲的跟随系数：快攻让柱子对鼓点起得来。 / Attack factor: fast rise so bars follow transients.</summary>
    private const float SpectrumAttack = 0.45f;

    /// <summary>电平回落系数：慢放让柱子自然落回，暂停时就是网易云那种「残桩」观感。 / Release factor: slow fall gives the resting-stub look when paused.</summary>
    private const float SpectrumRelease = 0.90f;

    private readonly MediaSessionService _mediaSessionService;
    private readonly AudioMonitorService _audioMonitorService;
    private readonly DispatcherTimer _lyricClock;
    private readonly DispatcherTimer _spectrumFrame;
    private readonly float[] _targetSpectrum = new float[SpectrumBandCount];
    private readonly float[] _displayedSpectrum = new float[SpectrumBandCount];

    private MediaSnapshot _snapshot = MediaSnapshot.Disconnected;
    private string _primaryText = string.Empty;
    private ImageSource? _artwork;
    private bool _isConnected;
    private bool _isPlaying;

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>每帧发布平滑后的频谱电平（0..1），由窗口绘制到迷你频谱。 / Publishes smoothed levels (0..1) each frame for the mini spectrum.</summary>
    public event EventHandler<IReadOnlyList<float>>? SpectrumFrame;

    /// <summary>创建岛体状态源并接通媒体快照订阅。 / Creates the state source and wires the snapshot subscription.</summary>
    public IslandWindowViewModel(MediaSessionService mediaSessionService, AudioMonitorService audioMonitorService)
    {
        _mediaSessionService = mediaSessionService;
        _audioMonitorService = audioMonitorService;
        _mediaSessionService.SnapshotChanged += OnSnapshotChanged;
        _lyricClock = new DispatcherTimer(DispatcherPriority.Background) { Interval = LyricClockInterval };
        _lyricClock.Tick += (_, _) => UpdatePrimaryText();
        _spectrumFrame = new DispatcherTimer(DispatcherPriority.Render) { Interval = SpectrumFrameInterval };
        _spectrumFrame.Tick += (_, _) => AdvanceSpectrumFrame();
    }

    /// <summary>最新媒体快照；断开时为 <see cref="MediaSnapshot.Disconnected"/>。 / Latest media snapshot.</summary>
    public MediaSnapshot Snapshot => _snapshot;

    /// <summary>紧凑态主文本：有歌词行时是当前歌词行，否则回落到曲目标题。 / Compact primary text: current lyric line, falling back to the title.</summary>
    public string PrimaryText
    {
        get => _primaryText;
        private set => SetProperty(ref _primaryText, value);
    }

    /// <summary>当前封面；无封面时为 null，窗口显示占位音符。 / Current artwork, or null for the placeholder.</summary>
    public ImageSource? Artwork
    {
        get => _artwork;
        private set => SetProperty(ref _artwork, value);
    }

    /// <summary>是否连接到媒体会话。 / Whether a media session is connected.</summary>
    public bool IsConnected
    {
        get => _isConnected;
        private set => SetProperty(ref _isConnected, value);
    }

    /// <summary>是否正在播放；驱动播放/暂停图标与频谱的活跳。 / Whether playback is running.</summary>
    public bool IsPlaying
    {
        get => _isPlaying;
        private set => SetProperty(ref _isPlaying, value);
    }

    /// <summary>平滑后的频谱电平只读视图。 / Read-only view of the smoothed spectrum levels.</summary>
    public IReadOnlyList<float> DisplayedSpectrum => _displayedSpectrum;

    /// <summary>开始时钟与频谱帧；在窗口显示时调用。 / Starts the clocks; call when the window is shown.</summary>
    public void Start()
    {
        ApplySnapshot(_mediaSessionService.CurrentSnapshot);
        _lyricClock.Start();
        _spectrumFrame.Start();
    }

    /// <summary>停止时钟；窗口隐藏时调用，实例可再次 <see cref="Start"/> 复用。 / Stops the clocks; called when hidden. The instance can be started again.</summary>
    public void Stop()
    {
        _lyricClock.Stop();
        _spectrumFrame.Stop();
    }

    /// <summary>停止时钟并退订快照；窗口彻底关闭、实例废弃时调用。 / Stops the clocks and unsubscribes; call when the window closes for good.</summary>
    public void Detach()
    {
        Stop();
        _mediaSessionService.SnapshotChanged -= OnSnapshotChanged;
    }

    /// <summary>切换播放/暂停（转交媒体服务）。 / Toggles play/pause via the media service.</summary>
    public Task TogglePlayPauseAsync() => _mediaSessionService.TogglePlayPauseAsync();

    /// <summary>上一曲（转交媒体服务）。 / Skips to the previous track.</summary>
    public Task SkipPreviousAsync() => _mediaSessionService.SkipPreviousAsync();

    /// <summary>下一曲（转交媒体服务）。 / Skips to the next track.</summary>
    public Task SkipNextAsync() => _mediaSessionService.SkipNextAsync();

    private void OnSnapshotChanged(object? sender, MediaSnapshot? snapshot)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || !dispatcher.CheckAccess())
        {
            dispatcher?.BeginInvoke(() => ApplySnapshot(snapshot));
            return;
        }
        ApplySnapshot(snapshot);
    }

    private void ApplySnapshot(MediaSnapshot? snapshot)
    {
        _snapshot = snapshot ?? MediaSnapshot.Disconnected;
        IsConnected = _snapshot.IsConnected;
        IsPlaying = _snapshot.IsPlaying;
        Artwork = _snapshot.IsConnected ? _snapshot.Artwork : null;
        UpdatePrimaryText();
    }

    /// <summary>
    /// 按快照时间轴外推当前播放位置：快照的 Position 是 TimelineUpdatedAt 时刻的值，
    /// 播放中要补上此后流逝的时间（乘以倍速），暂停时就是 Position 本身。
    /// Extrapolates the playback position: Position is anchored at TimelineUpdatedAt, so playing state
    /// adds the elapsed time scaled by the playback rate, and paused state is Position as-is.
    /// </summary>
    private double EstimatePositionSeconds()
    {
        var snapshot = _snapshot;
        if (!snapshot.IsConnected)
            return 0;
        var elapsed = snapshot.IsPlaying
            ? (DateTimeOffset.UtcNow - snapshot.TimelineUpdatedAt).TotalSeconds * snapshot.PlaybackRate
            : 0;
        var position = snapshot.Position + elapsed;
        return snapshot.Duration > 0 ? Math.Clamp(position, 0, snapshot.Duration) : position;
    }

    private void UpdatePrimaryText()
    {
        var text = ResolveCurrentLyricLine();
        if (string.IsNullOrWhiteSpace(text))
            text = _snapshot.IsConnected ? _snapshot.Title : string.Empty;
        if (PrimaryText != text)
            PrimaryText = text;
    }

    /// <summary>取当前时间点命中的歌词行：最后一行 Start 不晚于当前位置的行；无歌词或未命中时为空。 / Resolves the lyric line at the current position.</summary>
    private string? ResolveCurrentLyricLine()
    {
        var document = _snapshot.Lyrics?.Document;
        if (document is null || document.Lines.Count == 0)
            return null;

        var position = EstimatePositionSeconds();
        string? current = null;
        foreach (var line in document.Lines)
        {
            // IsBackground 是和声/背景词，混进单行胶囊里会打断阅读。
            // Background vocals break the single-line pill, so they are skipped.
            if (line.IsBackground)
                continue;
            if (line.Start <= position + 0.05)
                current = line.Text;
            else
                break;
        }
        return current;
    }

    private void AdvanceSpectrumFrame()
    {
        // 频谱是装饰性呈现，采样异常（缓冲区契约、设备抖动等）只允许丢帧，绝不允许杀进程——
        // 这个方法跑在 DispatcherTimer 里，未处理异常会直接终止整个应用（实战炸过一次）。
        // Spectrum sampling is decorative: any exception may only drop a frame, never kill the app —
        // this runs inside a DispatcherTimer, and an unhandled exception terminates the whole process.
        try
        {
            // 环回静音（含暂停）时 GetSpectrum 自身会把参考电平衰减到 0，这里只做跟随与发布。
            // When loopback is silent (including paused), GetSpectrum already decays toward zero; only follow and publish here.
            _audioMonitorService.GetSpectrum(_targetSpectrum, SpectrumBandCount);
        }
        catch (Exception)
        {
            // 这里连日志都不写：这条路径每 16ms 就可能走一次，任何一条日志自己都会变成噪声源头。
            // Not even logging here: this path can be reached every 16 ms, so any log line would itself become noise.
            return;
        }
        for (var band = 0; band < SpectrumBandCount; band++)
        {
            var target = Math.Clamp(_targetSpectrum[band], 0f, 1f);
            var displayed = _displayedSpectrum[band];
            var factor = target > displayed ? SpectrumAttack : SpectrumRelease;
            _displayedSpectrum[band] = displayed + (target - displayed) * factor;
        }
        SpectrumFrame?.Invoke(this, _displayedSpectrum);
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
