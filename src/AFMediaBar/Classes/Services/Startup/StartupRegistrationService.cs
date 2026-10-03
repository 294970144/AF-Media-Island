using AFMediaBar.Resources;
using System.Diagnostics;
using Microsoft.Win32;
using AFMediaBar.Classes.Settings;

namespace AFMediaBar.Classes.Services;

/// <summary>
/// 开机自动启动的注册表登记项：只管理当前用户的 Run 键，不申请提权，也不改动 Windows 自己的启动项审批状态。
/// 同时拥有设置变更订阅，由组合根启动，DI 容器释放；设置页不直接执行登记。
/// The run-at-startup registration: it manages the current user's Run key only, asks for no elevation, and never touches Windows'
/// own per-entry approval state.
/// </summary>
public sealed class StartupRegistrationService : IDisposable
{
    private readonly Func<bool, string?> _applyRegistration;
    private readonly Func<bool?> _readRegistration;
    private bool _started;
    private bool _disposed;
    private bool _applying;
    private bool _previousValue;

    /// <summary>创建启动项服务；由组合根启动，DI 容器释放设置订阅。</summary>
    public StartupRegistrationService()
    {
        _applyRegistration = Apply;
        _readRegistration = IsRegistered;
    }

    internal StartupRegistrationService(Func<bool, string?> applyRegistration, Func<bool?> readRegistration)
    {
        _applyRegistration = applyRegistration;
        _readRegistration = readRegistration;
    }

    /// <summary>最近一次登记失败的原因；成功时为 null。</summary>
    public string? LastFailure { get; private set; }

    /// <summary>启动时无法读取登记状态，且后续尚未成功应用用户选择。</summary>
    public bool RegistrationStateUnknown { get; private set; }

    /// <summary>登记完成或失败回退后通知设置页刷新状态。</summary>
    public event EventHandler? StateChanged;

    /// <summary>加载设置后读取实际登记状态同步到设置，并订阅后续修改和整体重置。</summary>
    public string? Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return LastFailure;
        var registered = _readRegistration();
        RegistrationStateUnknown = registered is null;
        // 必须先同步、再订阅，避免读取到的实际状态触发一次注册表写入。
        // 无法读取时保留用户设置，但由设置页明确提示状态未经核实。
        if (registered is { } actual)
            SettingsManager.Current.LaunchAtStartup = actual;
        _previousValue = SettingsManager.Current.LaunchAtStartup;
        SettingsManager.SettingsChanged += OnSettingsChanged;
        _started = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        return LastFailure;
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (_disposed || _applying) return;
        if (e.PropertyName != nameof(AppSettings.LaunchAtStartup) &&
            !(e.PropertyName is null && e.ResetScope is null or SettingsResetScope.All)) return;

        // 整体替换不会经过设置页 setter。登记与失败回退必须由服务统一处理，
        // 回退产生的嵌套通知不再写注册表，避免重复登记和递归。
        _applying = true;
        try
        {
            var requested = SettingsManager.Current.LaunchAtStartup;
            LastFailure = _applyRegistration(requested);
            if (LastFailure is null)
            {
                _previousValue = requested;
                RegistrationStateUnknown = false;
            }
            else
                SettingsManager.Current.LaunchAtStartup = _previousValue;
        }
        finally
        {
            _applying = false;
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>取消设置订阅；重复释放不会再次操作注册表。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started) SettingsManager.SettingsChanged -= OnSettingsChanged;
    }

    /// <summary>注册表值名；固定值使重复写入始终覆盖同一条记录。 / Registry value name; a fixed name keeps repeated writes on one single entry.</summary>
    public const string ValueName = "AFMediaBar";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// 登记或取消登记开机自动启动。
    /// Registers or unregisters run-at-startup.
    /// </summary>
    /// <param name="enabled">是否随登录启动。/ Whether the application should start with the session.</param>
    /// <returns>失败原因；成功时为 null。/ The failure reason, or null on success.</returns>
    public string? Apply(bool enabled)
    {
        var executable = ResolveExecutablePath();
        if (executable is null)
            return Translations.Get("Service.Startup.ExecutablePathUnavailable");

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
                return Translations.Get("Service.Startup.RegistryKeyUnavailable");

            if (enabled)
            {
                var command = StartupRegistrationPolicy.BuildCommandLine(executable);
                if (key.GetValue(ValueName) as string == command)
                    return null;

                key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            return null;
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[StartupRegistrationService] Apply failed: {exception}");
            return exception.Message;
        }
    }

    /// <summary>
    /// 读取当前是否已登记开机自动启动。注册表不可读时返回 null，调用方据此保留设置里的意图而不谎报状态。
    /// Reads whether run-at-startup is currently registered. An unreadable registry returns null so callers can keep the stored
    /// intent instead of reporting a state they could not verify.
    /// </summary>
    public bool? IsRegistered()
    {
        var executable = ResolveExecutablePath();
        if (executable is null)
            return null;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var command = key?.GetValue(ValueName) as string;
            return StartupRegistrationPolicy.Matches(command, executable);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"[StartupRegistrationService] Read failed: {exception}");
            return null;
        }
    }

    private static string? ResolveExecutablePath()
    {
        var path = Environment.ProcessPath;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }
}
