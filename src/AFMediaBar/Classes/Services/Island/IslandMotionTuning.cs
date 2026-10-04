using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AFMediaBar.Classes.Services;

/// <summary>
/// 灵动岛的手感参数总表：悬停展开与收起的进出延迟，以及完整／降级两档动效各用的弹簧刚度与阻尼。
/// 这六个数字原先是散在 <see cref="IslandHoverPolicy"/> 与 <see cref="SpringMotion"/> 里的常量，改一个值
/// 就要改代码、编译、重启。它们现在集中在这里，并可从磁盘上的 JSON 读入——改数字、存盘，下一帧就生效。
/// The island's feel parameters in one place: the hover enter/leave delays plus the spring stiffness and damping
/// for each motion tier. These six numbers used to be constants scattered across <see cref="IslandHoverPolicy"/> and
/// <see cref="SpringMotion"/>, so changing one meant editing code, rebuilding and restarting. They now live here and
/// can be read from a JSON file on disk — change the number, save, and the next frame already uses it.
/// </remarks>
/// <para>
/// 越界值一律夹回区间，不报错也不拒绝。这些数字直接积分成窗口高度，发散的结果是窗口飞出屏幕或永远停在
/// 半路，而触发它的场景只是有人手抖写了一个数——一个手感文件不该有能力把窗口弄坏。
/// Out-of-range values are clamped, never rejected. These numbers are integrated straight into the window height, so
/// a divergence means the window flies off screen or parks mid-flight, and the only thing that causes it is a mistyped
/// number: a feel file must never be able to break the window.
/// </para>
/// <para>
/// 刻意只收手感参数，不收采样机制（<c>PresenceProbeInterval</c>、<c>MaxHoverStepMilliseconds</c>）。那些决定
/// 「多久采样一次」，属于正确性而非观感；一并暴露只会诱导后来者把机制当参数调。
/// Only feel parameters live here, never the sampling mechanism (<c>PresenceProbeInterval</c>,
/// <c>MaxHoverStepMilliseconds</c>). Those decide how often the island samples, which is a correctness matter rather
/// than a taste one; exposing them would only invite tuning the mechanism as if it were a preference.
/// </para>
/// </remarks>
public sealed class IslandMotionTuning
{
    /// <summary>注释与尾随逗号都要容忍：手感文件是给人改的，而人一写注释就顺手打上逗号。</summary>
    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    private readonly object _gate = new();
    private string? _watchedPath;
    private FileSystemWatcher? _watcher;

    /// <summary>指针进入后多久才展开（毫秒）：掠过时不该被误当成想看内容。</summary>
    public double ExpandDelayMilliseconds { get; private set; } = 120d;

    /// <summary>指针离开后多久才收起（毫秒）：从胶囊移到面板上时不至于中途塌掉。</summary>
    public double CollapseDelayMilliseconds { get; private set; } = 320d;

    /// <summary>完整动效的刚度（1/s²）。对应角频率 20 rad/s，即 Framer Motion 作者实测出的"黄金比例"。</summary>
    public double FullStiffness { get; private set; } = 400d;

    /// <summary>完整动效的阻尼（1/s），等于 0.75 × 2√400，即 ζ = 0.75，与 Apple 系复刻所用的比例一致。</summary>
    public double FullDamping { get; private set; } = 30d;

    /// <summary>降级动效的刚度（1/s²）：收敛更快，配合临界阻尼几乎不产生过冲。</summary>
    public double ReducedStiffness { get; private set; } = 520d;

    /// <summary>降级动效的阻尼（1/s），等于 2√520，即临界阻尼。</summary>
    public double ReducedDamping { get; private set; } = 45.6d;

    /// <summary>全进程共用的那一份。 / The one instance the whole process shares.</summary>
    public static IslandMotionTuning Current { get; } = new();

    /// <summary>参数文件路径：与设置文件同目录。 / Where the parameter file lives — beside the settings file.</summary>
    public static string DefaultFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AFMediaBar",
        "island-motion.json");

    /// <summary>参数已变更。订阅方须自己切到 UI 线程再落地。 / Raised after the parameters change; subscribers must marshal to the UI thread.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 从磁盘读入并开始监听。文件不存在时写入一份带注释的模板，好让第一次打开就有地方可改。
    /// 读失败（文件正被编辑器占用、JSON 写坏）保留内存里的现值，不抛异常也不清零。
    /// Reads from disk and starts watching. A missing file is written out as an annotated template so there is
    /// something to edit on the first run. A failed read (file locked by an editor, malformed JSON) keeps the
    /// in-memory values and never throws.
    /// </summary>
    /// <param name="path">参数文件路径；不传时用 <see cref="DefaultFilePath"/>。 / The file path, defaulting to <see cref="DefaultFilePath"/>.</param>
    public static void Initialize(string? path = null)
    {
        var target = path ?? DefaultFilePath;
        IslandMotionTuning? loaded = null;
        try
        {
            if (File.Exists(target))
                loaded = Parse(File.ReadAllText(target));
        }
        catch (Exception exception)
        {
            // 参数文件是可选的手感调校，不该让应用起不来：读失败就沿用内置默认值。
            // The file is an optional feel override and must never stop the app from starting: a failed read falls
            // back to the built-in defaults.
            AppLogService.Current?.Warn("Island", $"读取手感参数失败，沿用内置默认值 / failed to read motion tuning, keeping defaults: {exception.Message}");
        }

        if (loaded is null)
        {
            Current.TryWriteTemplate(target);
            Current.Watch(target);
            return;
        }

        Current.Assign(loaded);
        Current.Watch(target);
        Current.Changed?.Invoke(Current, EventArgs.Empty);
    }

    /// <summary>
    /// 解析一份手感 JSON，缺项保留内置默认值，越界夹回区间。整体不是对象时返回 null。
    /// Parses one feel JSON: missing entries keep the built-in defaults, out-of-range entries are clamped. A payload
    /// that is not an object returns null.
    /// </summary>
    /// <param name="json">文件内容。 / The file contents.</param>
    public static IslandMotionTuning? Parse(string json)
    {
        using var document = JsonDocument.Parse(json, ReadOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            return null;

        var tuning = new IslandMotionTuning();
        tuning.ExpandDelayMilliseconds = ReadNumber(document.RootElement, "expandDelayMilliseconds", tuning.ExpandDelayMilliseconds);
        tuning.CollapseDelayMilliseconds = ReadNumber(document.RootElement, "collapseDelayMilliseconds", tuning.CollapseDelayMilliseconds);
        tuning.FullStiffness = ReadNumber(document.RootElement, "fullStiffness", tuning.FullStiffness);
        tuning.FullDamping = ReadNumber(document.RootElement, "fullDamping", tuning.FullDamping);
        tuning.ReducedStiffness = ReadNumber(document.RootElement, "reducedStiffness", tuning.ReducedStiffness);
        tuning.ReducedDamping = ReadNumber(document.RootElement, "reducedDamping", tuning.ReducedDamping);
        return tuning.Sanitize();
    }

    private static double ReadNumber(JsonElement root, string name, double fallback) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : fallback;

    /// <summary>
    /// 把六个值夹回可用区间后返回本表。刚度下限取 1：零刚度等于没有弹簧，窗口会直接跳到目标而完全没有动效。
    /// Clamps the six values into a usable range and returns this table. The stiffness floor is 1: zero stiffness means
    /// no spring at all, and the window would simply jump to its target.
    /// </summary>
    public IslandMotionTuning Sanitize()
    {
        lock (_gate)
        {
            ExpandDelayMilliseconds = Math.Clamp(ExpandDelayMilliseconds, 0d, 2000d);
            CollapseDelayMilliseconds = Math.Clamp(CollapseDelayMilliseconds, 0d, 5000d);
            FullStiffness = Math.Clamp(FullStiffness, 1d, 20000d);
            FullDamping = Math.Clamp(FullDamping, 0d, 2000d);
            ReducedStiffness = Math.Clamp(ReducedStiffness, 1d, 20000d);
            ReducedDamping = Math.Clamp(ReducedDamping, 0d, 2000d);
            return this;
        }
    }

    private void Assign(IslandMotionTuning other)
    {
        lock (_gate)
        {
            ExpandDelayMilliseconds = other.ExpandDelayMilliseconds;
            CollapseDelayMilliseconds = other.CollapseDelayMilliseconds;
            FullStiffness = other.FullStiffness;
            FullDamping = other.FullDamping;
            ReducedStiffness = other.ReducedStiffness;
            ReducedDamping = other.ReducedDamping;
        }
    }

    private void Watch(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return;

            _watcher?.Dispose();
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
            {
                // 编辑器保存多为「写临时文件 + 替换」，只盯 Changed 会漏掉替换本身。
                // Editors usually save as "write a temp file, then replace it", so watching Changed alone misses the
                // replacement itself.
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true
            };
            _watcher.Changed += OnFileTouched;
            _watcher.Created += OnFileTouched;
            _watcher.Renamed += OnFileTouched;
            _watchedPath = path;
        }
        catch (Exception exception)
        {
            AppLogService.Current?.Warn("Island", $"监听手感参数失败，改参数后需重启才能生效 / failed to watch the motion tuning file, a restart is needed to pick up changes: {exception.Message}");
        }
    }

    private void OnFileTouched(object sender, FileSystemEventArgs e)
    {
        if (_watchedPath is null || !string.Equals(e.FullPath, _watchedPath, StringComparison.OrdinalIgnoreCase))
            return;

        // 监听跑在后台线程，而参数要喂给弹簧积分器和悬停计时器——那些都在 UI 线程上。
        // 这里只读文件并抛事件，落地由订阅方切回 UI 线程。
        // The watcher runs on a background thread while the parameters feed the spring integrator and the dwell
        // counter, both of which live on the UI thread. Only the read happens here; subscribers marshal the rest.
        try
        {
            var loaded = Parse(File.ReadAllText(_watchedPath));
            if (loaded is null)
                return;

            Assign(loaded);
            Changed?.Invoke(this, EventArgs.Empty);
            AppLogService.Current?.Info("Island", "手感参数已热重载 / motion tuning hot-reloaded");
        }
        catch (IOException)
        {
            // 存盘过程中文件可能短暂不可读；下一次事件会再试，不必打扰用户。
            // The file can be briefly unreadable mid-save; the next event retries, so there is nothing to report.
        }
        catch (Exception exception)
        {
            AppLogService.Current?.Warn("Island", $"热重载手感参数失败，沿用现值 / hot reload of motion tuning failed, keeping current values: {exception.Message}");
        }
    }

    /// <summary>
    /// 写出带注释的模板，让每个数字的来由写在它旁边。注释只在写文件时用得上：读回来时会被跳过。
    /// Writes an annotated template so each number carries its rationale next to it. The comments exist for the write
    /// path only: they are skipped when the file is read back.
    /// </summary>
    private void TryWriteTemplate(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, BuildTemplate());
        }
        catch (Exception exception)
        {
            // 写不出模板只意味着没有现成文件可改，内置默认值照样生效。
            // Failing to write the template only means there is no file to edit; the built-in defaults still apply.
            AppLogService.Current?.Warn("Island", $"写出手感参数模板失败 / failed to write the motion tuning template: {exception.Message}");
        }
    }

    private string BuildTemplate()
    {
        string Number(double value) => value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        return $$"""
            {
              // 指针进入后多久才展开（毫秒）。掠过时不该被误当成想看内容。
              // Apple 系复刻点击触发时是 0；悬停触发需要一个过滤，默认取 120。
              "expandDelayMilliseconds": {{Number(ExpandDelayMilliseconds)}},

              // 指针离开后多久才收起（毫秒）。够让指针从胶囊移到面板上而不被挡掉。
              // Apple 的收起步是 250ms，这里留 320ms 略宽一点。
              "collapseDelayMilliseconds": {{Number(CollapseDelayMilliseconds)}},

              // 完整动效的弹簧刚度与阻尼。400/30 即 Framer Motion 作者实测的"黄金比例"，
              // 也是最广为引用的 Apple 复刻参数（response 0.35 / dampingFraction 0.75）换算过来的结果。
              // 刚度决定多快到位（ω = √k = 20 rad/s），阻尼决定回弹多少（ζ = 0.75，过冲约 2.8%）。
              "fullStiffness": {{Number(FullStiffness)}},
              "fullDamping": {{Number(FullDamping)}},

              // 降级动效（系统关动画 / 高对比度 / 软件渲染）：临界阻尼，不过冲。
              "reducedStiffness": {{Number(ReducedStiffness)}},
              "reducedDamping": {{Number(ReducedDamping)}}
            }
            """;
    }
}
