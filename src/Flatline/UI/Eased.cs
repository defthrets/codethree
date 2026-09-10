using System;
using GTA;

namespace Flatline.UI
{
    /// <summary>
    /// A number that catches up with itself instead of jumping.
    ///
    /// The heat bar and the reputation bar both read their value straight out of the state
    /// every frame, so a sale that adds heat made the bar TELEPORT to its new length. That is
    /// the difference between a readout and a gauge: a gauge that moves shows you a change
    /// happening, and one that snaps shows you a number that is already different, which the
    /// eye reads as a glitch rather than as an event.
    ///
    /// Exponential rather than linear, and driven off real elapsed time rather than frames.
    /// Linear catch-up arrives with a stop; this one is quick where the gap is wide and gentle
    /// where it is closing, which is the shape every needle and every dial has. And a HUD that
    /// animates at a different speed depending on frame rate is a HUD that feels different on
    /// a different machine.
    ///
    /// First read SNAPS, deliberately. Opening a screen should show you where you actually
    /// are, not sweep up from zero every time you look at it -- the sweep is for changes that
    /// happen while you are watching.
    /// </summary>
    internal sealed class Eased
    {
        /// <summary>How much of the remaining gap is closed per second. Higher is snappier.</summary>
        private const float DefaultRate = 6.5f;

        /// <summary>Below this the gap is not worth animating, so it is simply taken.</summary>
        private const float Close = 0.0015f;

        /// <summary>
        /// A frame this long or longer is a hitch, not a frame.
        ///
        /// Streaming a new area or opening the pause menu can leave a second between two
        /// draws, and easing across that in one step is a bar that visibly jumps -- the exact
        /// thing this exists to stop. Clamped, so a stall costs the animation a moment rather
        /// than skipping it.
        /// </summary>
        private const float MaxStep = 0.1f;

        private float _shown;
        private int _at;
        private bool _started;

        /// <summary>Where the needle actually is this frame.</summary>
        public float Now => _shown;

        /// <summary>Points it at a value and returns where it has got to.</summary>
        public float To(float target, float rate = DefaultRate)
        {
            var now = Game.GameTime;

            if (!_started)
            {
                _started = true;
                _shown = target;
                _at = now;
                return _shown;
            }

            var dt = Math.Min(MaxStep, Math.Max(0f, (now - _at) / 1000f));
            _at = now;

            var gap = target - _shown;

            if (Math.Abs(gap) <= Close || dt <= 0f)
            {
                _shown = target;
                return _shown;
            }

            // 1 - e^(-rate*dt) is the fraction of the gap closed in this slice of time. It is
            // the same curve however the frames land, which is the whole reason for using it
            // rather than a fixed step per frame.
            _shown += gap * (1f - (float)Math.Exp(-rate * dt));

            return _shown;
        }

        /// <summary>Puts it back where it started, for a screen that is opening fresh.</summary>
        public void Reset()
        {
            _started = false;
            _shown = 0f;
            _at = 0;
        }
    }
}
