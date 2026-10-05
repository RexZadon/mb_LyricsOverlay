using System;
using System.Collections.Generic;

namespace LyricsOverlay.Core
{
    public enum AnimationStyle { None = 0, Fade = 1, Slide = 2 }

    public enum EasingKind { EaseOutCubic = 0, EaseInOutCubic = 1, Spring = 2, Linear = 3 }

    /// <summary>Easing curves on t in [0, 1]. All start at 0, end exactly at 1, and clamp t outside the range.</summary>
    public static class Easing
    {
        /// <summary>
        /// Angular frequency of the critically damped spring, in units of the animation duration:
        /// x(t) = 1 - (1 + wt) e^(-wt) is within 1% of rest at t = 1 for w = 6.64.
        /// </summary>
        public const double SpringOmega = 6.64;
        static readonly double SpringEnd = 1 - (1 + SpringOmega) * Math.Exp(-SpringOmega);

        public static double Apply(EasingKind kind, double t)
        {
            if (double.IsNaN(t) || t <= 0) return 0;
            if (t >= 1) return 1;
            switch (kind)
            {
                case EasingKind.Linear:
                    return t;
                case EasingKind.EaseInOutCubic:
                    return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
                case EasingKind.Spring:
                    // Normalised so the curve lands exactly on 1 at t = 1 instead of 0.99.
                    return (1 - (1 + SpringOmega * t) * Math.Exp(-SpringOmega * t)) / SpringEnd;
                default:
                    double u = 1 - t;
                    return 1 - u * u * u;
            }
        }
    }

    /// <summary>
    /// Smooth playback position between polls. Each poll re-anchors the clock to the player's reported
    /// position; in between, the position advances with wall-clock time while playing. A poll that
    /// disagrees with the estimate by more than <see cref="SeekThresholdMs"/> is a seek. While playing,
    /// the output never runs backwards because of small poll jitter: it holds until the player catches up.
    /// Times are milliseconds from any monotonic clock (a Stopwatch in the plugin, plain numbers in tests).
    /// </summary>
    public sealed class PlaybackClock
    {
        public const double SeekThresholdMs = 350;
        /// <summary>Never extrapolate further than this past the last poll (player stalled or polls stopped).</summary>
        public const double MaxExtrapolationMs = 1000;

        double _anchorPos, _anchorTime, _lastOut;
        bool _playing, _hasAnchor;

        public bool IsPlaying => _playing;

        /// <summary>Re-anchors on a poll. Returns true when the jump means a seek (or the first poll).</summary>
        public bool Update(double reportedMs, bool playing, double nowMs)
        {
            bool seek = !_hasAnchor || Math.Abs(reportedMs - Raw(nowMs)) > SeekThresholdMs;
            _anchorPos = reportedMs;
            _anchorTime = nowMs;
            _playing = playing;
            _hasAnchor = true;
            if (seek) _lastOut = reportedMs;
            return seek;
        }

        /// <summary>Forget everything (track change, stop). The next Update counts as a seek.</summary>
        public void Reset()
        {
            _hasAnchor = false;
            _playing = false;
            _anchorPos = _lastOut = 0;
        }

        /// <summary>Estimated position at <paramref name="nowMs"/>.</summary>
        public double PositionAt(double nowMs)
        {
            if (!_hasAnchor) return 0;
            double raw = Raw(nowMs);
            if (raw < _lastOut && _lastOut - raw <= SeekThresholdMs) raw = _lastOut; // jitter: hold, don't go back
            _lastOut = raw;
            return raw;
        }

        double Raw(double nowMs)
        {
            if (!_hasAnchor) return 0;
            if (!_playing) return _anchorPos;
            return _anchorPos + Math.Max(0, Math.Min(MaxExtrapolationMs, nowMs - _anchorTime));
        }
    }

    /// <summary>
    /// Animated scalar (the fractional "current line" index the overlay is showing). Retargeting
    /// mid-animation continues from the value currently on screen, so motion never jumps.
    /// </summary>
    public sealed class ScrollAnimator
    {
        double _from, _to, _start = double.NegativeInfinity;

        public double DurationMs { get; set; } = 320;
        public EasingKind Easing { get; set; } = EasingKind.EaseOutCubic;
        public double Target => _to;
        public double From => _from;

        public void SnapTo(double target)
        {
            _from = _to = target;
            _start = double.NegativeInfinity;
        }

        /// <summary>Slide: animate from the value shown at <paramref name="nowMs"/> to the new target.</summary>
        public void AnimateTo(double target, double nowMs)
        {
            _from = ValueAt(nowMs);
            _to = target;
            _start = nowMs;
        }

        /// <summary>Crossfade: the old target becomes the fade-out state, the new one fades in.</summary>
        public void CrossTo(double target, double nowMs)
        {
            _from = _to;
            _to = target;
            _start = nowMs;
        }

        /// <summary>Eased progress 0..1 of the current animation (1 when idle).</summary>
        public double ProgressAt(double nowMs)
        {
            if (DurationMs <= 0 || _from == _to) return 1;
            return Core.Easing.Apply(Easing, (nowMs - _start) / DurationMs);
        }

        public double ValueAt(double nowMs) => _from + (_to - _from) * ProgressAt(nowMs);

        public bool IsAnimating(double nowMs) => _from != _to && DurationMs > 0 && nowMs - _start < DurationMs;
    }

    /// <summary>Which line changes animate and which snap.</summary>
    public static class LineTransition
    {
        /// <summary>Bigger forward jumps (or any jump caused by a seek) snap straight to the new line.</summary>
        public const int MaxAnimatedJump = 2;

        public static bool ShouldAnimate(AnimationStyle style, int fromIndex, int toIndex, bool seek) =>
            style != AnimationStyle.None && !seek && toIndex > fromIndex && (long)toIndex - fromIndex <= MaxAnimatedJump;
    }

    /// <summary>One line to draw: vertical centre (in units of the current line's font size, relative to the
    /// middle of the overlay), size relative to the current line, and opacity.</summary>
    public struct FrameItem
    {
        public string Text;
        public float Y;
        public float Scale;
        public float Alpha;
    }

    /// <summary>
    /// Positions of synced lyric lines for any fractional scroll position. At an integer position the
    /// layout equals the original static window (current line in the middle, neighbours smaller and
    /// dimmer); between two integers every line's position, size and opacity are interpolated, so
    /// lines slide up, the incoming line grows and brightens, and lines leaving the window fade out.
    /// Fills a caller-owned list so a running animation doesn't allocate per frame.
    /// </summary>
    public static class SyncedLayout
    {
        public const float ContextScale = 0.72f;
        public const float LineSpacing = 1.45f;

        /// <summary>Height of a window of <paramref name="count"/> lines, in current-line font sizes.</summary>
        public static float WindowHeight(int count) => (1 + (Math.Max(1, count) - 1) * ContextScale) * LineSpacing;

        public static void Slide(IReadOnlyList<LyricLine> lines, double position, int count, List<FrameItem> output)
        {
            output.Clear();
            count = Math.Max(1, count);
            int before = (count - 1) / 2, after = count - 1 - before;
            int c0 = (int)Math.Floor(position);
            float f = (float)(position - c0);
            for (int i = c0 - before - 1; i <= c0 + after + 2; i++)
            {
                if (i < 0 || i >= lines.Count || lines[i].Text.Length == 0) continue;
                int k0 = i - c0, k1 = k0 - 1;
                var item = new FrameItem
                {
                    Text = lines[i].Text,
                    Y = Lerp(SlotY(k0, before, after), SlotY(k1, before, after), f),
                    Scale = Lerp(SlotScale(k0), SlotScale(k1), f),
                    Alpha = Lerp(SlotAlpha(k0, before, after), SlotAlpha(k1, before, after), f),
                };
                if (item.Alpha > 0.004f) output.Add(item);
            }
        }

        /// <summary>Crossfade between the static windows at <paramref name="from"/> and <paramref name="to"/>.</summary>
        public static void Fade(IReadOnlyList<LyricLine> lines, int from, int to, double progress, int count, List<FrameItem> output)
        {
            output.Clear();
            float p = (float)Math.Max(0, Math.Min(1, progress));
            AddWindow(lines, from, count, 1 - p, output);
            AddWindow(lines, to, count, p, output);
        }

        static void AddWindow(IReadOnlyList<LyricLine> lines, int center, int count, float alpha, List<FrameItem> output)
        {
            if (alpha <= 0.004f) return;
            count = Math.Max(1, count);
            int before = (count - 1) / 2, after = count - 1 - before;
            for (int k = -before; k <= after; k++)
            {
                int i = center + k;
                if (i < 0 || i >= lines.Count || lines[i].Text.Length == 0) continue;
                output.Add(new FrameItem { Text = lines[i].Text, Y = SlotY(k, before, after), Scale = SlotScale(k), Alpha = SlotAlpha(k, before, after) * alpha });
            }
        }

        /// <summary>Centre of slot k (0 = current line) relative to the window's middle. Slots outside the
        /// window continue the stack, so lines enter and leave from just beyond the edges.</summary>
        public static float SlotY(int k, int before, int after)
        {
            float ls = LineSpacing, c = ContextScale;
            float y0 = (before - after) * c * ls / 2f;
            if (k == 0) return y0;
            float dist = ls / 2f + c * ls * (Math.Abs(k) - 0.5f);
            return k > 0 ? y0 + dist : y0 - dist;
        }

        public static float SlotScale(int k) => k == 0 ? 1f : ContextScale;

        public static float SlotAlpha(int k, int before, int after)
        {
            if (k == 0) return 1f;
            if (k < -before || k > after) return 0f;
            return Math.Max(0.25f, 0.6f - 0.15f * (Math.Abs(k) - 1));
        }

        static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
