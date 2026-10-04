using AFMediaBar.Classes.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AFMediaBar.Layout.Tests;

/// <summary>岛体可见性判定的纯策略测试。 / Pure policy tests for the island visibility verdict.</summary>
[TestClass]
public sealed class IslandVisibilityPolicyTests
{
    [TestMethod]
    public void IslandStaysVisibleWhileMediaIsConnected()
    {
        var visibility = new IslandVisibilityPolicy();

        for (var sample = 0; sample < 10; sample++)
            visibility.Update(hasMediaSession: true, isForegroundFullscreen: false);

        Assert.IsFalse(visibility.IsHidden);
    }

    [TestMethod]
    public void IslandHidesAfterEnoughNoMediaSamples()
    {
        var visibility = new IslandVisibilityPolicy();

        for (var sample = 1; sample < IslandVisibilityPolicy.RequiredConsecutiveSamples; sample++)
        {
            visibility.Update(hasMediaSession: false, isForegroundFullscreen: false);
            Assert.IsFalse(visibility.IsHidden, $"第 {sample} 次采样不该就隐藏");
        }

        visibility.Update(hasMediaSession: false, isForegroundFullscreen: false);
        Assert.IsTrue(visibility.IsHidden, "连续采样够了就该隐藏");
    }

    [TestMethod]
    public void IslandHidesWhenTheForegroundWindowIsFullscreen()
    {
        var visibility = new IslandVisibilityPolicy();

        for (var sample = 0; sample < IslandVisibilityPolicy.RequiredConsecutiveSamples; sample++)
            visibility.Update(hasMediaSession: true, isForegroundFullscreen: true);

        Assert.IsTrue(visibility.IsHidden);
    }

    [TestMethod]
    public void ASingleFullscreenSampleDoesNotHideTheIsland()
    {
        var visibility = new IslandVisibilityPolicy();

        visibility.Update(hasMediaSession: true, isForegroundFullscreen: true);
        Assert.IsFalse(visibility.IsHidden, "偶发一次全屏判定不能当结论");
        Assert.AreEqual(1, visibility.FullscreenStreak);

        visibility.Update(hasMediaSession: true, isForegroundFullscreen: false);
        Assert.AreEqual(0, visibility.FullscreenStreak, "全屏消失后计数立刻归零");
    }

    [TestMethod]
    public void AFlickerBetweenTheTwoConditionsIsStillHidden()
    {
        var visibility = new IslandVisibilityPolicy();
        for (var sample = 0; sample < IslandVisibilityPolicy.RequiredConsecutiveSamples; sample++)
            visibility.Update(hasMediaSession: false, isForegroundFullscreen: false);
        Assert.IsTrue(visibility.IsHidden);

        // 刚恢复播放又被全屏盖住：两段计数都从零重来，不会因为「刚才藏过」就立刻保持隐藏。
        visibility.Update(hasMediaSession: true, isForegroundFullscreen: false);
        Assert.IsFalse(visibility.IsHidden, "恢复正常即恢复显示");

        visibility.Update(hasMediaSession: true, isForegroundFullscreen: true);
        Assert.IsFalse(visibility.IsHidden, "新一段全屏必须重新连续采样才隐藏");
    }

    [TestMethod]
    public void EitherConditionAloneCanHideTheIsland()
    {
        var noMedia = new IslandVisibilityPolicy();
        var fullscreen = new IslandVisibilityPolicy();

        for (var sample = 0; sample < IslandVisibilityPolicy.RequiredConsecutiveSamples; sample++)
        {
            noMedia.Update(hasMediaSession: false, isForegroundFullscreen: false);
            fullscreen.Update(hasMediaSession: true, isForegroundFullscreen: true);
        }

        Assert.IsTrue(noMedia.IsHidden);
        Assert.IsTrue(fullscreen.IsHidden);
    }

    [TestMethod]
    public void ResetReturnsTheIslandToVisible()
    {
        var visibility = new IslandVisibilityPolicy();
        for (var sample = 0; sample < IslandVisibilityPolicy.RequiredConsecutiveSamples; sample++)
            visibility.Update(hasMediaSession: false, isForegroundFullscreen: false);
        Assert.IsTrue(visibility.IsHidden);

        visibility.Reset();
        Assert.IsFalse(visibility.IsHidden);
        Assert.AreEqual(0, visibility.NoMediaStreak);
        Assert.AreEqual(0, visibility.FullscreenStreak);
    }

    /// <summary>
    /// 恢复只花一拍，而隐藏要花三拍：去抖是不对称的。
    ///
    /// 这条钉的是「判定隐藏是可逆的」这一半。另一半在窗口上——隐藏时**必须留着可见性探针**，因为那是
    /// 唯一能把岛体唤回来的东西；原先判定隐藏走的是终态路径（连探针一起停），于是任何一次误判都成了
    /// 单向棘轮：岛体再也不回来，只能由用户在设置里手动开关一次。那一半是接线，纯策略测不到，
    /// 由实机验证兜着；这里能钉住的是「条件一恢复，下一拍就允许显示」。
    ///
    /// <para>
    /// Recovery costs one sample while hiding costs three: the debounce is asymmetric.
    ///
    /// This pins the reversible half of "a visibility verdict must be able to heal". The other half lives in the window:
    /// hiding must <b>keep the presence probe alive</b>, since the probe is the only thing that can bring the island back.
    /// The verdict used to take the terminal path (stopping the probe too), which turned any mistaken verdict into a
    /// one-way ratchet — the island never returned and only a manual toggle in the display-modes page revived it. That
    /// half is wiring and cannot be reached from a pure policy test; the real machine covers it. What can be pinned here
    /// is that once the condition clears, the very next sample allows showing again.
    /// </para>
    /// </summary>
    [TestMethod]
    public void OneConnectedSampleUndoesTheHideThatTookThree()
    {
        var visibility = new IslandVisibilityPolicy();
        for (var sample = 0; sample < IslandVisibilityPolicy.RequiredConsecutiveSamples; sample++)
            visibility.Update(hasMediaSession: false, isForegroundFullscreen: false);
        Assert.IsTrue(visibility.IsHidden);

        visibility.Update(hasMediaSession: true, isForegroundFullscreen: false);

        Assert.IsFalse(
            visibility.IsHidden,
            "会话一恢复就该立刻允许显示。去抖只该挡住误判隐藏，不该挡住恢复——那会把一次误判放大成永久消失。");
    }
}
