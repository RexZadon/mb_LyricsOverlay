using System;
using System.Collections.Generic;
using System.Linq;
using LyricsOverlay.Core;
using Xunit;

namespace LyricsCore.Tests
{
    public class EasingTests
    {
        public static IEnumerable<object[]> Kinds => Enum.GetValues(typeof(EasingKind)).Cast<object>().Select(k => new[] { k });

        [Theory]
        [MemberData(nameof(Kinds))]
        public void StartsAtZeroEndsAtOneAndClamps(EasingKind kind)
        {
            Assert.Equal(0, Easing.Apply(kind, 0));
            Assert.Equal(1, Easing.Apply(kind, 1));
            Assert.Equal(0, Easing.Apply(kind, -0.5));
            Assert.Equal(1, Easing.Apply(kind, 7));
            Assert.Equal(0, Easing.Apply(kind, double.NaN));
        }

        [Theory]
        [MemberData(nameof(Kinds))]
        public void IsMonotonicWithoutOvershoot(EasingKind kind)
        {
            double prev = 0;
            for (int i = 1; i <= 1000; i++)
            {
                double v = Easing.Apply(kind, i / 1000.0);
                Assert.True(v >= prev - 1e-12, $"{kind} decreased at {i}");
                Assert.True(v <= 1 + 1e-12, $"{kind} overshot at {i}");
                prev = v;
            }
        }

        [Fact]
        public void KnownValues()
        {
            Assert.Equal(0.875, Easing.Apply(EasingKind.EaseOutCubic, 0.5), 10);
            Assert.Equal(0.5, Easing.Apply(EasingKind.EaseInOutCubic, 0.5), 10);
            Assert.Equal(0.25, Easing.Apply(EasingKind.Linear, 0.25), 10);
            // Critically damped spring: 1 - (1 + wt) e^(-wt), normalised to land on 1.
            double w = Easing.SpringOmega, raw = 1 - (1 + w * 0.5) * Math.Exp(-w * 0.5), end = 1 - (1 + w) * Math.Exp(-w);
            Assert.Equal(raw / end, Easing.Apply(EasingKind.Spring, 0.5), 10);
            Assert.True(end > 0.99 && end < 1); // genuinely settled at the end of the duration
        }

        [Fact]
        public void EaseOutCurvesAreAheadOfLinear()
        {
            for (int i = 1; i < 10; i++)
            {
                Assert.True(Easing.Apply(EasingKind.EaseOutCubic, i / 10.0) > i / 10.0);
                Assert.True(Easing.Apply(EasingKind.Spring, i / 10.0) > i / 10.0);
            }
        }
    }

    public class PlaybackClockTests
    {
        [Fact]
        public void InterpolatesBetweenPollsWhilePlaying()
        {
            var c = new PlaybackClock();
            Assert.True(c.Update(1000, playing: true, nowMs: 0)); // first anchor counts as a seek
            Assert.Equal(1030, c.PositionAt(30));
            Assert.Equal(1059, c.PositionAt(59));
            Assert.False(c.Update(1060, true, 60));               // agrees with the estimate
            Assert.Equal(1075, c.PositionAt(75));
        }

        [Fact]
        public void ReanchorsOnEveryPoll()
        {
            var c = new PlaybackClock();
            c.Update(0, true, 0);
            Assert.False(c.Update(100, true, 60)); // player is 40 ms ahead of the wall clock: follow it
            Assert.Equal(130, c.PositionAt(90));
        }

        [Fact]
        public void HoldsWhilePaused()
        {
            var c = new PlaybackClock();
            c.Update(5000, playing: false, nowMs: 0);
            Assert.Equal(5000, c.PositionAt(10));
            Assert.Equal(5000, c.PositionAt(10000));
        }

        [Fact]
        public void SmallBackwardJitterDoesNotMoveBackwards()
        {
            var c = new PlaybackClock();
            c.Update(1000, true, 0);
            Assert.Equal(1060, c.PositionAt(60));
            Assert.False(c.Update(1030, true, 60));  // 30 ms behind the estimate: not a seek
            Assert.Equal(1060, c.PositionAt(60));    // hold instead of jumping back
            Assert.Equal(1060, c.PositionAt(80));    // raw 1050: still holding
            Assert.Equal(1070, c.PositionAt(100));   // caught up, moving again
        }

        [Fact]
        public void LargeJumpIsASeekAndSnapsBothWays()
        {
            var c = new PlaybackClock();
            c.Update(1000, true, 0);
            c.PositionAt(60);
            Assert.True(c.Update(90000, true, 60));
            Assert.Equal(90000, c.PositionAt(60));
            Assert.True(c.Update(2000, true, 120)); // backwards seek is followed immediately
            Assert.Equal(2000, c.PositionAt(120));
        }

        [Fact]
        public void ExtrapolationIsCappedWhenPollsStop()
        {
            var c = new PlaybackClock();
            c.Update(1000, true, 0);
            Assert.Equal(1000 + PlaybackClock.MaxExtrapolationMs, c.PositionAt(60000));
        }

        [Fact]
        public void ResetMakesTheNextPollASeek()
        {
            var c = new PlaybackClock();
            c.Update(1000, true, 0);
            c.Reset();
            Assert.Equal(0, c.PositionAt(50));
            Assert.True(c.Update(1050, true, 50));
        }
    }

    public class ScrollAnimatorTests
    {
        [Fact]
        public void AnimatesWithEasingAndStops()
        {
            var a = new ScrollAnimator { DurationMs = 300, Easing = EasingKind.EaseOutCubic };
            a.SnapTo(4);
            a.AnimateTo(5, nowMs: 1000);
            Assert.True(a.IsAnimating(1000));
            Assert.Equal(4, a.ValueAt(1000), 10);
            Assert.Equal(4.875, a.ValueAt(1150), 10);
            Assert.Equal(5, a.ValueAt(1300), 10);
            Assert.False(a.IsAnimating(1300));
            Assert.Equal(5, a.ValueAt(99999), 10);
        }

        [Fact]
        public void RetargetingMidwayContinuesFromTheVisibleValue()
        {
            var a = new ScrollAnimator { DurationMs = 200, Easing = EasingKind.Linear };
            a.SnapTo(0);
            a.AnimateTo(1, 0);
            Assert.Equal(0.5, a.ValueAt(100), 10);
            a.AnimateTo(2, 100);
            Assert.Equal(0.5, a.ValueAt(100), 10); // no jump
            Assert.Equal(1.25, a.ValueAt(200), 10);
            Assert.Equal(2, a.ValueAt(300), 10);
        }

        [Fact]
        public void SnapAndZeroDurationAreInstant()
        {
            var a = new ScrollAnimator { DurationMs = 300 };
            a.AnimateTo(3, 0);
            a.SnapTo(7);
            Assert.False(a.IsAnimating(1));
            Assert.Equal(7, a.ValueAt(1));

            var none = new ScrollAnimator { DurationMs = 0 };
            none.AnimateTo(2, 0);
            Assert.False(none.IsAnimating(0));
            Assert.Equal(2, none.ValueAt(0));
        }

        [Fact]
        public void CrossfadeTracksFromAndToWithProgress()
        {
            var a = new ScrollAnimator { DurationMs = 100, Easing = EasingKind.Linear };
            a.SnapTo(3);
            a.CrossTo(4, 0);
            Assert.Equal(3, a.From);
            Assert.Equal(4, a.Target);
            Assert.Equal(0.25, a.ProgressAt(25), 10);
            Assert.Equal(1, a.ProgressAt(100));
        }
    }

    public class LineTransitionTests
    {
        [Theory]
        [InlineData(AnimationStyle.Slide, 3, 4, false, true)]   // normal advance
        [InlineData(AnimationStyle.Fade, 3, 4, false, true)]
        [InlineData(AnimationStyle.Slide, -1, 0, false, true)]  // first line arrives
        [InlineData(AnimationStyle.Slide, 3, 5, false, true)]   // two quick lines in one step
        [InlineData(AnimationStyle.Slide, 3, 9, false, false)]  // big jump: snap
        [InlineData(AnimationStyle.Slide, 3, 4, true, false)]   // seek: snap even to the next line
        [InlineData(AnimationStyle.Slide, 9, 3, false, false)]  // backwards: snap
        [InlineData(AnimationStyle.None, 3, 4, false, false)]   // "None" is the instant fallback
        [InlineData(AnimationStyle.Slide, int.MinValue, 4, false, false)] // fresh lyrics
        public void DecidesBetweenAnimateAndSnap(AnimationStyle style, int from, int to, bool seek, bool expected)
        {
            Assert.Equal(expected, LineTransition.ShouldAnimate(style, from, to, seek));
        }
    }

    public class SyncedLayoutTests
    {
        static readonly LyricLine[] Lines = Enumerable.Range(0, 10).Select(i => new LyricLine(i * 1000, "L" + i)).ToArray();
        readonly List<FrameItem> _out = new List<FrameItem>();

        FrameItem Item(string text) => _out.Single(i => i.Text == text);

        [Fact]
        public void IntegerPositionMatchesTheStaticWindow()
        {
            SyncedLayout.Slide(Lines, 4, 3, _out);
            Assert.Equal(new[] { "L3", "L4", "L5" }, _out.Select(i => i.Text));
            Assert.Equal(1f, Item("L4").Scale);
            Assert.Equal(1f, Item("L4").Alpha);
            Assert.Equal(0f, Item("L4").Y, 4);
            Assert.Equal(SyncedLayout.ContextScale, Item("L3").Scale);
            Assert.Equal(0.6f, Item("L5").Alpha, 4);
            // Stacked exactly like the original renderer: half the current slot plus half a context slot.
            float gap = SyncedLayout.LineSpacing / 2 + SyncedLayout.ContextScale * SyncedLayout.LineSpacing / 2;
            Assert.Equal(-gap, Item("L3").Y, 4);
            Assert.Equal(gap, Item("L5").Y, 4);
        }

        [Fact]
        public void HalfwayEverythingIsInterpolated()
        {
            SyncedLayout.Slide(Lines, 4.5, 3, _out);
            var leaving = Item("L4");
            var arriving = Item("L5");
            Assert.True(leaving.Y < 0 && arriving.Y > 0);                // both moving up
            Assert.Equal((1 + SyncedLayout.ContextScale) / 2, arriving.Scale, 4);
            Assert.Equal((0.6f + 1f) / 2, arriving.Alpha, 4);            // brightening
            Assert.Equal(0.3f, Item("L3").Alpha, 4);                     // fading out at the top
            Assert.Equal(0.3f, Item("L6").Alpha, 4);                     // fading in at the bottom
            Assert.DoesNotContain(_out, i => i.Text == "L2" || i.Text == "L7");
        }

        [Fact]
        public void MotionIsContinuousAcrossIntegerBoundaries()
        {
            SyncedLayout.Slide(Lines, 4.9999, 5, _out);
            var before = _out.ToDictionary(i => i.Text);
            SyncedLayout.Slide(Lines, 5.0, 5, _out);
            foreach (var i in _out)
            {
                if (!before.TryGetValue(i.Text, out var b)) { Assert.True(i.Alpha < 0.01f); continue; }
                Assert.Equal(b.Y, i.Y, 2);
                Assert.Equal(b.Scale, i.Scale, 2);
                Assert.Equal(b.Alpha, i.Alpha, 2);
            }
        }

        [Fact]
        public void BeforeTheFirstLineTheCentreIsEmpty()
        {
            SyncedLayout.Slide(Lines, -1, 3, _out);
            Assert.Equal(new[] { "L0" }, _out.Select(i => i.Text));
            Assert.True(Item("L0").Y > 0);
        }

        [Fact]
        public void FadeSplitsOpacityBetweenOldAndNewWindow()
        {
            SyncedLayout.Fade(Lines, 4, 5, 0.25, 1, _out);
            Assert.Equal(new[] { "L4", "L5" }, _out.Select(i => i.Text));
            Assert.Equal(0.75f, Item("L4").Alpha, 4);
            Assert.Equal(0.25f, Item("L5").Alpha, 4);
            Assert.All(_out, i => Assert.Equal(0f, i.Y, 4)); // crossfade in place
        }

        [Fact]
        public void WindowHeightMatchesTheStaticLayout()
        {
            Assert.Equal((1 + 2 * 0.72f) * 1.45f, SyncedLayout.WindowHeight(3), 4);
            Assert.Equal(1.45f, SyncedLayout.WindowHeight(1), 4);
        }
    }
}
