using AFMediaBar.Classes.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AFMediaBar.Layout.Tests;

/// <summary>
/// 手感参数表的纯逻辑测试：解析、注释容忍、越界夹取。
/// 这里只测 <see cref="IslandMotionTuning.Parse"/>：文件监听与热重载改的是 <see cref="IslandMotionTuning.Current"/>
/// 这个全进程单例，测试里动它会污染同批跑的手感与弹簧用例，那属于实机验证的范畴。
/// Pure logic tests for the feel-parameter table: parsing, comment tolerance and clamping. Only
/// <see cref="IslandMotionTuning.Parse"/> is covered here: file watching and hot reload mutate
/// <see cref="IslandMotionTuning.Current"/>, the process-wide singleton, and touching it from a test would leak into
/// the feel and spring cases running alongside — that belongs to verification on the real machine.
/// </summary>
[TestClass]
public sealed class IslandMotionTuningTests
{
    [TestMethod]
    public void OutOfRangeValuesAreClampedInsteadOfRejected()
    {
        var tuning = IslandMotionTuning.Parse("""
            {
              "expandDelayMilliseconds": -500,
              "collapseDelayMilliseconds": 999999,
              "fullStiffness": 0,
              "fullDamping": -3
            }
            """);

        Assert.IsNotNull(tuning);
        Assert.AreEqual(0d, tuning.ExpandDelayMilliseconds, 1e-9, "负延迟应夹到 0，而不是让弹簧提前触发");
        Assert.AreEqual(5000d, tuning.CollapseDelayMilliseconds, 1e-9, "超长延迟应夹到上限");
        Assert.AreEqual(1d, tuning.FullStiffness, 1e-9, "零刚度等于没有弹簧，必须夹到下限之上");
        Assert.AreEqual(0d, tuning.FullDamping, 1e-9, "负阻尼会让弹簧发散，必须夹掉");
    }

    [TestMethod]
    public void CommentsAndTrailingCommasSurviveTheRoundTrip()
    {
        // 手感文件是给人改的，而人一写注释就顺手打上逗号。这两种写法必须照单全收，
        // 否则用户会发现「我加了一行注释，参数就全丢了」。
        // The feel file is written by a human, and humans write comments and trailing commas. Both must be accepted,
        // or a user discovers that adding one comment line silently discards every parameter.
        var tuning = IslandMotionTuning.Parse("""
            {
              // 这是解释，不是参数
              "expandDelayMilliseconds": 90,
              "collapseDelayMilliseconds": 400,
            }
            """);

        Assert.IsNotNull(tuning);
        Assert.AreEqual(90d, tuning.ExpandDelayMilliseconds, 1e-9);
        Assert.AreEqual(400d, tuning.CollapseDelayMilliseconds, 1e-9);
    }

    [TestMethod]
    public void AMissingEntryFallsBackToTheBuiltInDefault()
    {
        var tuning = IslandMotionTuning.Parse("""{ "expandDelayMilliseconds": 90 }""");

        Assert.IsNotNull(tuning);
        Assert.AreEqual(90d, tuning.ExpandDelayMilliseconds, 1e-9);
        Assert.AreEqual(320d, tuning.CollapseDelayMilliseconds, 1e-9, "没写的项应沿用内置值，而不是归零");
        Assert.AreEqual(400d, tuning.FullStiffness, 1e-9);
    }

    [TestMethod]
    public void ANonObjectPayloadIsRejectedRatherThanGuessedAt()
    {
        Assert.IsNull(IslandMotionTuning.Parse("[]"));
        Assert.IsNull(IslandMotionTuning.Parse("42"));
    }

    [TestMethod]
    public void TheShippedDefaultsSitAtTheAppleEndOfTheScale()
    {
        // 这条钉的是「我们默认有多快」。默认值取 Framer Motion 作者实测的黄金比例（400/30，ζ=0.75），
        // 也就是最广为引用的 Apple 复刻参数换算过来的结果。它同时是刻意选定的两端：比上一版慢、比上一版软。
        // 上一版是 540/37.2，ω 比 Apple 快 29%、过冲只剩 1.6%，看着干净但没了生气。
        // This pins how fast the shipped default is. It is Framer Motion's empirically found golden ratio (400/30,
        // ζ=0.75), which is also what the most-cited Apple recreation converts to. It is deliberately the softer,
        // slower end: the previous tuning (540/37.2) was 29% faster than Apple with only 1.6% overshoot left, which
        // read as clean but lifeless.
        var tuning = IslandMotionTuning.Parse("{}");

        Assert.IsNotNull(tuning);
        Assert.AreEqual(400d, tuning.FullStiffness, 1e-9);
        Assert.AreEqual(30d, tuning.FullDamping, 1e-9);
        Assert.AreEqual(120d, tuning.ExpandDelayMilliseconds, 1e-9);
        Assert.AreEqual(320d, tuning.CollapseDelayMilliseconds, 1e-9);
    }
}
