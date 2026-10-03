// 验证启动项服务对设置修改、整体替换、失败回退和订阅释放的响应。
// 通过替代登记操作记录调用，不读写测试机器的真实启动项。
using AFMediaBar.Classes.Services;
using AFMediaBar.Classes.Settings;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AFMediaBar.Layout.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StartupRegistrationTests
{
    private AppSettings _original = null!;
    private AppSettings? _originalDefaults;

    [TestInitialize]
    public void Initialize()
    {
        _original = SettingsManager.Current.Clone();
        _originalDefaults = SettingsManager.UserDefaults?.Clone();
        SettingsManager.SetUserDefaults(null);
        SettingsManager.Current = new AppSettings { LaunchAtStartup = false };
    }

    [TestCleanup]
    public void Cleanup()
    {
        SettingsManager.SetUserDefaults(_originalDefaults);
        SettingsManager.Current = _original;
    }

    [TestMethod]
    public void DirectChangesAndBothKindsOfDefaultsSynchronizeRegistration()
    {
        var calls = new List<bool>();
        using var service = new StartupRegistrationService(value => { calls.Add(value); return null; });
        service.Start();
        service.Start();
        SettingsManager.Current.LaunchAtStartup = true;
        SettingsManager.SetUserDefaults(new AppSettings { LaunchAtStartup = false });
        SettingsManager.ResetAll();
        SettingsManager.SetUserDefaults(null);
        SettingsManager.ResetAll();

        CollectionAssert.AreEqual(new[] { false, true, false, true }, calls);
        Assert.IsTrue(SettingsManager.Current.LaunchAtStartup);
    }

    [TestMethod]
    public void WholeSettingsReplacementSynchronizesWithoutApplyingUnrelatedChanges()
    {
        var calls = new List<bool>();
        using var service = new StartupRegistrationService(value => { calls.Add(value); return null; });
        service.Start();
        SettingsManager.Current.LyricsEnabled = false;
        SettingsManager.ResetLyrics();
        SettingsManager.Current = new AppSettings { LaunchAtStartup = true };

        CollectionAssert.AreEqual(new[] { false, true }, calls);
    }

    [TestMethod]
    public void FailedDirectChangeAndResetRestorePreviousValueWithoutRecursiveRegistration()
    {
        var calls = new List<bool>();
        using var service = new StartupRegistrationService(value =>
        {
            calls.Add(value);
            return value ? "registration failed" : null;
        });
        service.Start();
        var notifications = 0;
        service.StateChanged += (_, _) => notifications++;

        SettingsManager.Current.LaunchAtStartup = true;
        Assert.IsFalse(SettingsManager.Current.LaunchAtStartup);
        SettingsManager.ResetAll();

        Assert.IsFalse(SettingsManager.Current.LaunchAtStartup);
        Assert.AreEqual("registration failed", service.LastFailure);
        Assert.AreEqual(2, notifications);
        CollectionAssert.AreEqual(new[] { false, true, true }, calls);
    }

    [TestMethod]
    public void StartupFailurePreservesSavedIntentAndDisposeRemovesSubscription()
    {
        SettingsManager.Current.LaunchAtStartup = true;
        var calls = 0;
        using var service = new StartupRegistrationService(_ => { calls++; return "registration failed"; });
        Assert.AreEqual("registration failed", service.Start());
        Assert.IsTrue(SettingsManager.Current.LaunchAtStartup);

        service.Dispose();
        service.Dispose();
        SettingsManager.Current.LaunchAtStartup = false;
        SettingsManager.ResetAll();

        Assert.AreEqual(1, calls);
    }
}
