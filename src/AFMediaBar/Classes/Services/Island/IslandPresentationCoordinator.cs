using AFMediaBar.Views.Windows;

namespace AFMediaBar.Classes.Services;

/// <summary>
/// 灵动岛窗口的呈现协调器：显示模式页选中「灵动岛」时显示岛体，切回其它模式时隐藏。
/// <see cref="IslandWindow"/> 注册为 Transient，协调器是它唯一的持有者并缓存实例复用，
/// 因此设置页与 <c>--island</c> 启动开关唤起的是同一个岛，不会叠出两个。
/// Presentation coordinator for the experimental island: the display-modes page shows the island when the
/// dynamic-island mode is selected and hides it for any other mode. IslandWindow is registered transient;
/// this coordinator is its single owner and caches the instance, so the settings page and the --island
/// launch switch bring up the same island instead of stacking two.
/// </summary>
public sealed class IslandPresentationCoordinator
{
    private readonly Func<IslandWindow>? _windowFactory;
    private IslandWindow? _window;

    /// <summary>创建协调器；岛体窗口延迟到首次显示时才创建。工厂为 null 时（测试场景）Show 退化为无操作。 / Creates the coordinator; the window is created lazily on first show. With a null factory (tests) Show is a no-op.</summary>
    public IslandPresentationCoordinator(Func<IslandWindow>? windowFactory = null) => _windowFactory = windowFactory;

    /// <summary>显示岛体（首次调用时创建窗口实例；已显示则仅重新定位）。 / Shows the island (creates the window on first use; re-places it if already visible).</summary>
    public void Show()
    {
        if (_window is null)
        {
            if (_windowFactory is null)
                return;
            _window = _windowFactory();
        }
        _window.ShowIsland();
    }

    /// <summary>隐藏岛体并停掉其状态源；实例保留，供再次显示复用。 / Hides the island and stops its state source; the instance is kept for reuse.</summary>
    public void Hide() => _window?.HideIsland();
}
