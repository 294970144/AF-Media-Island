using AFMediaBar.Classes.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AFMediaBar.Layout.Tests;

/// <summary>悬停展开决策的纯策略测试。 / Pure policy tests for the hover expansion decision.</summary>
[TestClass]
public sealed class IslandHoverPolicyTests
{
    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [TestMethod]
    public void PointerMustStayBeforeTheIslandExpands()
    {
        var hover = new IslandHoverPolicy();

        hover.Update(pointerInside: true, Ms(100));
        Assert.IsFalse(hover.ShouldExpand, "掠过式进入不该立刻展开");

        hover.Update(pointerInside: true, Ms(200));
        Assert.IsTrue(hover.ShouldExpand, "停留够久就该展开");
    }

    [TestMethod]
    public void IslandWaitsBeforeCollapsingSoThePointerCanReachThePanel()
    {
        var hover = new IslandHoverPolicy();
        hover.Update(pointerInside: true, Ms(300));
        Assert.IsTrue(hover.ShouldExpand);

        hover.Update(pointerInside: false, Ms(100));
        Assert.IsTrue(hover.ShouldExpand, "刚离开就该收起会把去面板的路径挡掉");

        hover.Update(pointerInside: false, Ms(300));
        Assert.IsFalse(hover.ShouldExpand, "离开够久才收起");
    }

    [TestMethod]
    public void ReenteringBeforeTheCollapseDelayKeepsTheIslandOpen()
    {
        var hover = new IslandHoverPolicy();
        hover.Update(pointerInside: true, Ms(300));
        hover.Update(pointerInside: false, Ms(100));

        hover.Update(pointerInside: true, Ms(50));
        Assert.IsTrue(hover.ShouldExpand, "还没到收起时限就回来了，不该塌下去");
        Assert.AreEqual(0d, hover.PendingMilliseconds, 1e-9, "回到已达成状态时延迟计数应清零");
    }

    [TestMethod]
    public void ClickPinKeepsTheIslandOpenAfterThePointerLeaves()
    {
        var hover = new IslandHoverPolicy();
        hover.TogglePin();
        Assert.IsTrue(hover.IsPinned);
        Assert.IsTrue(hover.ShouldExpand, "点击锁定后立刻就是展开态");

        hover.Update(pointerInside: true, Ms(300));
        hover.Update(pointerInside: false, Ms(1000));
        Assert.IsTrue(hover.ShouldExpand, "锁定期间指针离开不收起");
    }

    [TestMethod]
    public void ClickingAgainReleasesThePin()
    {
        var hover = new IslandHoverPolicy();
        hover.TogglePin();
        hover.TogglePin();
        Assert.IsFalse(hover.IsPinned);

        hover.Update(pointerInside: false, Ms(1000));
        Assert.IsFalse(hover.ShouldExpand);
    }

    [TestMethod]
    public void HoverAndPinAgreeOnOneTargetState()
    {
        // 这条守的是接线层的契约：窗口按 ShouldExpand 的真值设定形态，悬停与点击读的是同一个状态位。
        // 两者若各自维护一份展开状态，就会像之前那样互相拉扯、谁也压不过谁。
        // This guards the wiring contract: the window drives the shape from ShouldExpand, and hover and click read
        // the same state bit. When each kept its own expansion flag they pulled against each other forever.
        var hover = new IslandHoverPolicy();

        hover.Update(pointerInside: true, Ms(1000));
        Assert.IsTrue(hover.ShouldExpand, "悬停展开");

        hover.TogglePin();
        Assert.IsTrue(hover.ShouldExpand, "点击在已展开时锁定，仍是展开");

        hover.Update(pointerInside: false, Ms(1000));
        Assert.IsTrue(hover.ShouldExpand, "锁定期间悬停不该把它推翻");

        hover.TogglePin();
        Assert.IsFalse(hover.ShouldExpand, "再点一次解锁，形态立刻随之改变");
    }

    [TestMethod]
    public void ClickingOutsideCollapsesAPinnedIslandImmediately()
    {
        var hover = new IslandHoverPolicy();
        hover.TogglePin();
        Assert.IsTrue(hover.ShouldExpand);

        hover.CollapseFromOutsideClick();
        Assert.IsFalse(hover.IsPinned, "点别处就是「不看了」，锁定必须解掉");
        Assert.IsFalse(hover.ShouldExpand, "点别处立刻收起，不等收起延迟");
    }

    [TestMethod]
    public void APinnedIslandCollapsesEvenWhileThePointerRestsOnIt()
    {
        var hover = new IslandHoverPolicy();
        hover.TogglePin();
        hover.Update(pointerInside: true, Ms(1000));

        // 点别处的那一刻，指针通常还压在这颗岛体上。不做抑制的话，下一拍悬停立刻又把面板展开。
        // At the moment of an outside click the pointer usually still sits on this very island: without the
        // suppression the next hover sample would expand the panel straight back.
        hover.CollapseFromOutsideClick();
        hover.Update(pointerInside: true, Ms(1000));
        Assert.IsFalse(hover.ShouldExpand, "抑制期内指针仍在岛上也不能重新展开");
    }

    [TestMethod]
    public void HoverWorksAgainOnceThePointerLeavesAfterAnOutsideClick()
    {
        var hover = new IslandHoverPolicy();
        hover.TogglePin();
        hover.CollapseFromOutsideClick();

        hover.Update(pointerInside: false, Ms(400));
        Assert.IsFalse(hover.ShouldExpand, "指针离开后仍是收起态");

        hover.Update(pointerInside: true, Ms(400));
        Assert.IsTrue(hover.ShouldExpand, "抑制随指针离开而解除，悬停重新生效");
    }

    [TestMethod]
    public void ClickingOnTheIslandLiftsTheOutsideClickSuppression()
    {
        var hover = new IslandHoverPolicy();
        hover.CollapseFromOutsideClick();
        hover.Update(pointerInside: true, Ms(1000));
        Assert.IsFalse(hover.ShouldExpand);

        // 用户重新点了一下岛体，「我要看它」这个意图必须立刻成立，不能被上一记外部点击压住。
        // The user clicked the island again: that intent must win immediately and must not stay suppressed by the
        // earlier outside click.
        hover.TogglePin();
        Assert.IsTrue(hover.ShouldExpand);
    }

    [TestMethod]
    public void AnOutsideClickNeverUnsuppressesItself()
    {
        var hover = new IslandHoverPolicy();
        hover.CollapseFromOutsideClick();

        // 指针在岛内外的每一拍都必须维持抑制，否则一次点击会引发反复展开收起。
        // Every sample must keep the suppression while the pointer is inside, or one click would start a loop.
        for (var sample = 0; sample < 5; sample++)
        {
            hover.Update(pointerInside: true, Ms(500));
            Assert.IsFalse(hover.ShouldExpand, $"第 {sample + 1} 拍不该重新展开");
        }
    }

    [TestMethod]
    public void NegativeElapsedNeverFiresTheHoverEarly()
    {
        var hover = new IslandHoverPolicy();

        // 时钟被回拨时传进来的负时间差，不该被当成「已经停留够久」。
        // A negative delta from a clock adjustment must not read as "already hovered long enough".
        hover.Update(pointerInside: true, Ms(-5000));
        Assert.IsFalse(hover.ShouldExpand);

        hover.Update(pointerInside: true, Ms(300));
        Assert.IsTrue(hover.ShouldExpand);
    }

    [TestMethod]
    public void AVeryLongElapsedSampleFiresTheHoverImmediately()
    {
        var hover = new IslandHoverPolicy();

        // 探针被挂起一阵后恢复（例如机器卡顿）：时间确实过去了很久，展开该立刻发生。
        // The probe resumes after being stalled (a machine hiccup): real time really did pass, so the island
        // must expand at once instead of waiting out the dwell again.
        hover.Update(pointerInside: true, TimeSpan.FromHours(1));
        Assert.IsTrue(hover.ShouldExpand);
    }

    [TestMethod]
    public void ResetClearsThePinAndTheDwellCount()
    {
        var hover = new IslandHoverPolicy();
        hover.TogglePin();
        hover.Update(pointerInside: true, Ms(300));

        hover.Reset();
        Assert.IsFalse(hover.IsPinned);
        Assert.IsFalse(hover.ShouldExpand);
        Assert.AreEqual(0d, hover.PendingMilliseconds, 1e-9);
    }
}
