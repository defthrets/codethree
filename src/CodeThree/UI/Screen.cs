using System;
using System.Drawing;
using GTA;
using GTA.Native;
using CodeThree.Core;

namespace CodeThree.UI
{
    /// <summary>
    /// The only place this mod writes anything on screen, and it is one line at a time.
    ///
    /// DELIBERATELY ALMOST NOTHING. Everything this mod does is a thing you watch happen in the
    /// street -- a van arriving, a man kneeling, a trolley going into the back -- and a mod that
    /// narrates that in the corner is a mod telling you about a scene instead of letting you
    /// look at it. The few lines that exist are for the moments where something is decided
    /// rather than shown: he came round, or they stopped trying.
    /// </summary>
    internal static class Screen
    {
        private static string _last = string.Empty;
        private static int _lastAt;

        /// <summary>
        /// A line in the feed, top left, in the game's own style.
        ///
        /// Notification.Show is obsolete in SHVDN 3.9; PostTicker is the replacement and takes
        /// the two flags that decide whether it is important and whether it is kept in the
        /// phone's notification list.
        ///
        /// Repeats inside four seconds are dropped, which is the same guard Five0 Patrol grew
        /// for the same reason: several parts of one scene can conclude the same thing on one
        /// tick, and three identical lines stacked up reads as a bug.
        /// </summary>
        public static void Ticker(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                var now = Game.GameTime;

                if (text == _last && now - _lastAt < 4000) return;

                _last = text;
                _lastAt = now;

                GTA.UI.Notification.PostTicker(text, false, false);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not post a ticker line: " + ex.Message);
            }
        }

        // ---- and the settings screen, which is the only thing that draws -------

        /// <summary>ChaletComprimeCologne. The condensed one every mod in the set sets in.</summary>
        public const int Body = 4;

        /// <summary>
        /// The most one text component will carry, and how many of them there are.
        ///
        /// NINETY-NINE CHARACTERS IS A HARD CEILING and past it the engine simply stops --
        /// mid-word, with nothing anywhere to say it did. Half the descriptions on the settings
        /// screen run past it, because the whole point of them is to say what a setting COSTS
        /// rather than to restate its name. "STRING" has one slot; CELL_EMAIL_BCON has ten and
        /// the engine concatenates whatever is added into them, which is the documented way to
        /// draw a line of any length.
        /// </summary>
        private const int Piece = 99;
        private const int Pieces = 10;

        /// <summary>A filled rectangle, in screen fractions, centred on x and y.</summary>
        public static void Rect(float x, float y, float w, float h, Color colour)
        {
            try
            {
                Function.Call(Hash.DRAW_RECT, x, y, w, h,
                              colour.R, colour.G, colour.B, colour.A, false);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw a rect: " + ex.Message);
            }
        }

        /// <summary>
        /// A line of text.
        ///
        /// THE ALIGNMENT IS SAID OUTRIGHT, EVERY TIME, AND SO IS THE WRAP WINDOW. This is Five0
        /// Patrol's Screen.Text carried over with its hard-won comments, because every trap in
        /// it is a trap here too. SET_TEXT_CENTRE(false) does not un-centre anything -- it is a
        /// switch that only turns centring ON, and whatever the last line of text asked for,
        /// ours or another mod's drawn a moment before, is what the next one inherits unless it
        /// says otherwise.
        ///
        /// RIGHT-ALIGNED TEXT NEEDS A WRAP WINDOW or it does nothing at all, and the window's
        /// RIGHT edge is what the text is pushed against -- so x is the right-hand edge in that
        /// case and the left-hand one in every other. It is the most confusing corner of the
        /// text API and it silently no-ops when you get it wrong.
        /// </summary>
        public static void Text(string text, float x, float y, float scale, Color colour,
                                bool centred = false, bool rightAligned = false,
                                float wrapTo = 0f, int font = Body)
        {
            if (string.IsNullOrEmpty(text)) return;

            try
            {
                Function.Call(Hash.SET_TEXT_FONT, font);
                Function.Call(Hash.SET_TEXT_SCALE, scale, scale);
                Function.Call(Hash.SET_TEXT_COLOUR, colour.R, colour.G, colour.B, colour.A);
                Function.Call(Hash.SET_TEXT_CENTRE, centred);

                // Nought centre, one left, two right. Set by number so no line inherits a thing.
                Function.Call(Hash.SET_TEXT_JUSTIFICATION, centred ? 0 : rightAligned ? 2 : 1);

                if (rightAligned)
                {
                    Function.Call(Hash.SET_TEXT_RIGHT_JUSTIFY, true);
                    Function.Call(Hash.SET_TEXT_WRAP, 0f, x);
                }
                else if (wrapTo > 0f)
                {
                    // The pair is (left edge, right edge) and the game breaks at the last word
                    // that fits, which is the only way to draw a sentence of unknown length into
                    // a panel of known width. Without it the text runs off the end of the panel
                    // and across whatever is behind it.
                    Function.Call(Hash.SET_TEXT_WRAP, x, wrapTo);
                }
                else if (centred)
                {
                    Function.Call(Hash.SET_TEXT_WRAP, 0f, 1f);
                }
                else
                {
                    Function.Call(Hash.SET_TEXT_WRAP, x, 1f);
                }

                Function.Call(Hash.SET_TEXT_DROP_SHADOW);

                if (text.Length <= Piece)
                {
                    Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "STRING");
                    Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME, text);
                }
                else
                {
                    Function.Call(Hash.BEGIN_TEXT_COMMAND_DISPLAY_TEXT, "CELL_EMAIL_BCON");

                    for (var at = 0; at < text.Length && at < Piece * Pieces; at += Piece)
                    {
                        var take = text.Length - at;
                        if (take > Piece) take = Piece;

                        Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME,
                                      text.Substring(at, take));
                    }
                }

                Function.Call(Hash.END_TEXT_COMMAND_DISPLAY_TEXT, x, y, 0);
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw a line of text: " + ex.Message);
            }
        }

        /// <summary>
        /// How wide a line would be, so the panel can make room for one that wraps.
        ///
        /// Unmeasurable is treated as fitting. The worst that costs is the layout this was
        /// added to improve, which is where it started.
        /// </summary>
        public static float Wide(string text, float scale, int font = Body)
        {
            if (string.IsNullOrEmpty(text)) return 0f;

            try
            {
                Function.Call(Hash.SET_TEXT_FONT, font);
                Function.Call(Hash.SET_TEXT_SCALE, scale, scale);

                Function.Call(Hash.BEGIN_TEXT_COMMAND_GET_SCREEN_WIDTH_OF_DISPLAY_TEXT, "STRING");

                Function.Call(Hash.ADD_TEXT_COMPONENT_SUBSTRING_PLAYER_NAME,
                              text.Length <= Piece ? text : text.Substring(0, Piece));

                return Function.Call<float>(
                    Hash.END_TEXT_COMMAND_GET_SCREEN_WIDTH_OF_DISPLAY_TEXT, true);
            }
            catch
            {
                return 0f;
            }
        }
    }
}
