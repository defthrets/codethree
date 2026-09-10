using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using GTA;

namespace Flatline.UI
{
    /// <summary>
    /// The ratboy seal, drawn on screen with the katakana band turning.
    ///
    /// Deliberately standalone. Hoodrich has Draw.File with a rotation argument and could
    /// draw this in two lines, but Five0 Patrol's Icon has no rotation and Fumes, Bare Minimum
    /// and Overspray each reach CustomSprite their own way. A shared mark that only works in
    /// one mod is not a shared mark, so this depends on nothing but SHVDN and drops into any
    /// of them with the namespace changed.
    ///
    /// TWO FILES, NOT ONE, and that is the whole trick. The band turns and the face does not,
    /// so they are separate textures drawn at the same place and the same size with only the
    /// band given an angle. The alternative is a spritesheet of thirty-odd frames, which is
    /// thirty-odd textures to ship and a stepped rotation instead of a smooth one.
    ///
    /// Both files are square and centred on the CIRCLE, not on the artwork. CustomSprite
    /// rotates about the sprite's own centre, and the seal's grip sticks out to one side --
    /// cropped to its ink, the circle's centre would sit left of the image centre and the band
    /// would orbit rather than spin. The canvas is padded out to put the two in the same place.
    /// make_seal_png.py is what produces them and says so at the top; do not crop them.
    ///
    /// White on transparent, tinted here, exactly like every other icon in the set -- so one
    /// pair of files serves whatever colour each mod already uses.
    /// </summary>
    internal static class Seal
    {
        /// <summary>
        /// Where seal-face.png and seal-ring.png live. Set this once at startup.
        ///
        /// Not resolved in here because every mod finds its own data folder differently --
        /// Hoodrich has Core.Paths.Data, others do not -- and a second guess at the path is a
        /// second thing that can be wrong. One line at startup:
        ///
        ///     Seal.Folder = Path.Combine(Paths.Data, "icons");
        /// </summary>
        public static string Folder;

        /// <summary>
        /// How long the band takes to come back round, in milliseconds.
        ///
        /// 26 seconds, matching the website, which is slow enough that it reads as a seal that
        /// happens to be turning rather than as a thing spinning at you.
        /// </summary>
        public const int SpinMs = 26000;

        /// <summary>
        /// The seal, centred on x,y, with the band turning by itself.
        ///
        /// x and y are normalised 0..1 across the screen and are the CENTRE of the mark, which
        /// is the convention the rest of the UI already uses. height is a fraction of screen
        /// HEIGHT -- the art is square, so equal width and height in ScaledDraw's space really
        /// is square on screen at any aspect, and there is no ratio constant to keep in step.
        ///
        /// Off Game.GameTime rather than a counter of its own, so it does not drift, does not
        /// need ticking from anywhere, and stops while the game is paused -- a mark spinning
        /// behind the pause menu is the sort of thing that looks like a bug.
        /// </summary>
        public static bool Draw(float x, float y, float height, Color c)
        {
            return Draw(x, y, height, c, (Game.GameTime % SpinMs) * 360f / SpinMs);
        }

        /// <summary>
        /// The same, with the angle supplied.
        ///
        /// For anywhere the spin should be driven rather than free-running: winding up out of
        /// a stop, easing down into one, or held still on a frame you are composing.
        /// </summary>
        public static bool Draw(float x, float y, float height, Color c, float ringDeg)
        {
            // Band first so the rules and the grip land ON TOP of it. That is what makes the
            // band read as passing behind the grip; the gap cut into the texture only covers
            // the part that would otherwise show through the paddle's open end.
            var band = Blit("seal-ring.png", x, y, height, ringDeg, c);
            var face = Blit("seal-face.png", x, y, height, 0f, c);
            return band && face;
        }

        /// <summary>The mark with nothing moving, from the single flattened file.</summary>
        public static bool DrawStill(float x, float y, float height, Color c)
        {
            return Blit("seal.png", x, y, height, 0f, c);
        }

        /// <summary>
        /// One texture onto the screen.
        ///
        /// ScaledDraw, NOT Draw. CustomSprite.Draw hands its internals a hardcoded 1280 by 720
        /// -- not a pixel space, a fixed 16:9 grid -- so on any other aspect it puts the art in
        /// the wrong place and stretches it. ScaledDraw uses Screen.ScaledWidth by 720, which
        /// is aspect-corrected. Same reasoning as Hoodrich's Draw.File, and the same trap.
        /// </summary>
        private static bool Blit(string file, float x, float y, float height,
                                 float deg, Color c)
        {
            var sprite = Load(file);
            if (sprite == null) return false;

            try
            {
                var side = height * ScaledHeight;
                if (side < 1f) return false;   // too small to be worth a draw call

                sprite.Size = new SizeF(side, side);
                sprite.Position = new PointF(x * GTA.UI.Screen.ScaledWidth, y * ScaledHeight);
                sprite.Color = c;
                sprite.Rotation = deg;
                sprite.ScaledDraw();
                return true;
            }
            catch
            {
                // A mark that will not draw is not worth taking a frame down for.
                return false;
            }
        }

        /// <summary>
        /// The height of the space ScaledDraw draws into. Fixed, and not the screen's --
        /// GTA.UI.Screen.Height is this same 720, a constant in the assembly rather than a
        /// resolution. Named so the arithmetic above reads as deliberate.
        /// </summary>
        private const float ScaledHeight = 720f;

        /// <summary>
        /// The sprite for a file, made once.
        ///
        /// Constructing one loads the texture, so one built per frame is one handle leaked per
        /// frame. Misses are cached as null too, so a file that is not there costs a single
        /// look at the disk rather than one every frame forever.
        /// </summary>
        private static GTA.UI.CustomSprite Load(string file)
        {
            GTA.UI.CustomSprite found;
            if (Cache.TryGetValue(file, out found)) return found;

            Cache[file] = null;
            if (string.IsNullOrEmpty(Folder)) return null;

            try
            {
                var path = Path.Combine(Folder, file);
                if (!File.Exists(path)) return null;

                found = new GTA.UI.CustomSprite(path, new SizeF(32f, 32f), new PointF(0f, 0f),
                                                Color.White, 0f, true);
                Cache[file] = found;
                return found;
            }
            catch
            {
                return null;
            }
        }

        private static readonly Dictionary<string, GTA.UI.CustomSprite> Cache =
            new Dictionary<string, GTA.UI.CustomSprite>(StringComparer.OrdinalIgnoreCase);
    }
}
