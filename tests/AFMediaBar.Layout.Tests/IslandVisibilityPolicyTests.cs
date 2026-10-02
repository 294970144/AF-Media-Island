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
}
