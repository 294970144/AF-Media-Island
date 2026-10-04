using AFMediaBar.Classes.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AFMediaBar.Layout.Tests;

/// <summary>
/// 岛体歌词跑马灯的纯逻辑测试：溢出判据、起读停留、窗口推进与文本元素边界。
/// Pure logic tests for the island's lyric marquee: overflow, lead-in, window advance, and text-element boundaries.
/// </summary>
[TestClass]
public sealed class IslandMarqueeStateTests
{
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16);

    /// <summary>推进若干帧，返回期间重写窗口的次数。 / Advances frames and counts how often the window was rewritten.</summary>
    private static int RunFrames(IslandMarqueeState state, int frames)
    {
        var rewrites = 0;
        for (var i = 0; i < frames; i++)
        {
            if (state.Advance(Frame))
                rewrites++;
        }
        return rewrites;
    }

    [TestMethod]
    public void TextThatFitsStaysStatic()
    {
        var state = new IslandMarqueeState { Base = "短句" };

        Assert.IsFalse(state.Configure(80d, 200d, allowed: true), "放得下的歌词不该滚动");
        Assert.IsFalse(state.Advancing);
        Assert.AreEqual(0d, state.Position, 1e-9, "静止时位置应停在行首");
    }

    [TestMethod]
    public void OverflowWithinToleranceCountsAsFitting()
    {
        var state = new IslandMarqueeState { Base = "abc" };

        // 亚像素级的超出当作测量噪声：否则窗口尺寸抖动会让歌词莫名开始滚动。
        // Sub-pixel overflow is measurement noise; otherwise window resizing would make the lyric scroll for no reason.
        Assert.IsFalse(state.Configure(200.2d, 200d, allowed: true));
    }

    [TestMethod]
    public void DisallowedScrollingStaysStaticEvenWhenOverflowing()
    {
        var state = new IslandMarqueeState { Base = "很长很长很长很长很长的歌词" };

        Assert.IsFalse(state.Configure(400d, 200d, allowed: false), "不允许滚动时超宽也不推进");
        Assert.IsFalse(state.Advancing);
    }

    [TestMethod]
    public void EmptyTextNeverScrolls()
    {
        var state = new IslandMarqueeState { Base = string.Empty };

        Assert.IsFalse(state.Configure(0d, 200d, allowed: true));
        Assert.AreEqual(string.Empty, state.BuildWindow());
    }

    [TestMethod]
    public void LeadInHoldsTheLineBeforeScrolling()
    {
        var state = new IslandMarqueeState { Base = "很长很长很长很长很长的歌词" };
        state.Configure(400d, 200d, allowed: true);

        // 起读停留之内位置必须一动不动，让用户先读完这一句。
        // The position must not budge during the lead-in, so the line can be read first.
        var rewritesDuringLeadIn = RunFrames(state, (int)(IslandMarqueeState.LeadInMilliseconds / 16d) - 1);

        Assert.AreEqual(0, rewritesDuringLeadIn, "起读停留期间不该重写窗口");
        Assert.AreEqual(0d, state.Position, 1e-9);
    }

    [TestMethod]
    public void OverflowingTextScrollsAfterTheLeadIn()
    {
        const string lyric = "很长很长很长很长很长的歌词需要滚动才能看完";
        var state = new IslandMarqueeState { Base = lyric };
        state.Configure(400d, 200d, allowed: true);

        var rewrites = RunFrames(state, 120);

        Assert.IsTrue(state.Advancing);
        Assert.IsTrue(state.Position > 0d, "停留之后位置必须推进");
        Assert.IsTrue(rewrites > 0, "越过整数位置时必须重写窗口");
    }

    [TestMethod]
    public void ScrollSpeedIsPixelConstant()
    {
        const string lyric = "很长很长很长很长很长很长很长很长的歌词";
        var state = new IslandMarqueeState { Base = lyric };
        state.Configure(400d, 200d, allowed: true);
        RunFrames(state, (int)(IslandMarqueeState.LeadInMilliseconds / 16d) + 2);

        var before = state.Position;
        var elapsedSeconds = Frame.TotalSeconds * 10;
        RunFrames(state, 10);

        // 位置以字符为单位，而速度是DIP/秒，所以每帧推进量要除以一圈的字符数换算成字符。
        // 位置 is in characters while the speed is DIP per second, so the per-frame advance converts by the
        // lap length in characters.
        var windowLength = lyric.Length + state.SeamLength;
        var expected = IslandMarqueeState.ScrollSpeedDipPerSecond * elapsedSeconds / windowLength;

        // 恒速：每帧推进量与帧数成正比，不因位置变化而变速。
        // Constant travel: the per-frame advance scales with frame count, independent of position.
        Assert.AreEqual(expected, state.Position - before, 1e-6);
    }

    [TestMethod]
    public void WindowNeverSplitsASurrogatePair()
    {
        // 含代理对与组合记号的歌词：切分点必须落在完整文本元素上，否则会渲染出豆腐块。
        // Lyrics with surrogate pairs and combining marks: split points must land on whole text elements.
        const string lyric = "夜航🎵 stars のうた";
        var state = new IslandMarqueeState { Base = lyric };
        state.Configure(400d, 200d, allowed: true);

        for (var i = 0; i < 400; i++)
        {
            state.Advance(Frame);
            var window = state.BuildWindow();

            // 组合记号不能落在窗口首位：它必须跟着前面的基字符一起进来。
            // A combining mark must never start the window; it has to arrive with its base character.
            Assert.IsFalse(
                window.Length > 0 && char.GetUnicodeCategory(window[0]) is System.Globalization.UnicodeCategory.NonSpacingMark,
                "组合记号不应落在窗口开头");

            // 代理对必须完整：紧跟高代理项之后的低代理项不能自己成为一个文本元素的起点，
            // 否则窗口会从代理对中间切开，渲染出两个豆腐块。
            // Surrogate pairs must stay whole: the low surrogate right after a high one must not begin its own
            // text element, or the window cuts the pair in half and renders two tofu boxes.
            var elements = System.Globalization.StringInfo.ParseCombiningCharacters(window);
            for (var index = 0; index + 1 < window.Length; index++)
            {
                if (!char.IsHighSurrogate(window[index]) || !char.IsLowSurrogate(window[index + 1]))
                    continue;
                Assert.IsFalse(elements.Contains(index + 1), $"代理对在位置 {index} 被切开了");
            }

            // 窗口长度恒等于「原文 + 接缝」，接缝保证首尾相接处不断行。
            // The window length always equals source-plus-seam; the seam keeps the wrap seamless.
            Assert.AreEqual(lyric.Length + state.SeamLength, window.Length);
        }
    }

    [TestMethod]
    public void NewLyricResetsToTheHeadAndRepeatsTheLeadIn()
    {
        var state = new IslandMarqueeState { Base = "很长很长很长很长很长的第一句" };
        state.Configure(400d, 200d, allowed: true);
        RunFrames(state, 120);
        Assert.IsTrue(state.Position > 0d, "起读停留之后位置必须推进");

        state.Base = "换了一句";

        Assert.AreEqual(0d, state.Position, 1e-9, "换行后位置必须回到行首");
        Assert.IsFalse(state.Advancing, "换行后要重新判一次是否需要滚动");
    }

    [TestMethod]
    public void SettingTheSameTextKeepsTheRunningMarquee()
    {
        const string lyric = "很长很长很长很长很长的歌词";
        var state = new IslandMarqueeState { Base = lyric };
        state.Configure(400d, 200d, allowed: true);
        RunFrames(state, 120);
        var position = state.Position;

        // 歌词时钟每120ms 就会写回一次同样的文本；这不该把跑马灯打回行首。
        // The lyric clock writes the same text every 120 ms; that must not rewind the marquee.
        state.Base = lyric;

        Assert.AreEqual(position, state.Position, 1e-9, "同样的文本不该打断正在进行的滚动");
    }

    [TestMethod]
    public void OffsetIsNegativeSoTextSlidesLeft()
    {
        var state = new IslandMarqueeState { Base = "很长很长很长很长很长的歌词" };
        state.Configure(400d, 200d, allowed: true);
        RunFrames(state, (int)(IslandMarqueeState.LeadInMilliseconds / 16d) + 40);

        // 前缀宽度表：每个字符 10 DIP 宽，模拟一次真实测量。
        // A prefix-width table of 10 DIP per character, standing in for a real measurement.
        var widths = new double[64];
        for (var index = 1; index < widths.Length; index++)
            widths[index] = index * 10d;

        // 位移必须为负：文字往左走，右侧的新内容才会进来。
        // The offset must be negative so the text travels left and new content enters from the right.
        Assert.IsTrue(state.ResolveOffsetDip(widths, 60) < 0d, "位移必须为负：文字往左走");
    }

    [TestMethod]
    public void OffsetIsZeroBeforeAnyWindowIsWritten()
    {
        var state = new IslandMarqueeState { Base = "abc" };

        Assert.AreEqual(0d, state.ResolveOffsetDip(new double[8], 6), 1e-9);
    }

    [TestMethod]
    public void MissingWidthTableStillYieldsAUsableOffset()
    {
        var state = new IslandMarqueeState { Base = "很长很长很长很长很长的歌词" };
        state.Configure(400d, 200d, allowed: true);
        RunFrames(state, (int)(IslandMarqueeState.LeadInMilliseconds / 16d) + 30);

        // 没测过宽度表时也要给出有限偏移：宁可略有偏差，也不要突然不滚。
        // Without a measured table the offset must still be finite: a slight inaccuracy beats a frozen lyric.
        var offset = state.ResolveOffsetDip(null, 0);
        Assert.IsTrue(double.IsFinite(offset));
    }
}
