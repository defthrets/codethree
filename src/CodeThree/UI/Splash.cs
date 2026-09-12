using System;
using System.Drawing;
using GTA;

namespace CodeThree.UI
{
    /// <summary>
    /// One mark, and everything installed listed beside it, for a few seconds after load.
    ///
    /// ONE SEAL, NOT ONE EACH. It drew a mark per row to begin with, and a column of eight
    /// identical spinning seals is not a set of mods, it is a column of eight spinning seals.
    /// The mark is the thing you recognise and it only needs to be there once -- so it is
    /// drawn big, once, centred on the whole column, and each mod contributes nothing but its
    /// own line of text beside it. Which is also what makes the list read as a list.
    ///
    /// One of these ships in every mod in the set, which is the whole point -- six mods that
    /// each announce themselves differently are six mods; six that announce themselves the
    /// same way are a set. It replaces nothing: Hello's ticker line still names the key, and a
    /// ticker is a sentence you read while this is a mark you recognise.
    ///
    /// THE STACKING IS THE HARD PART. Every mod in the set gates its greeting the same way --
    /// a few seconds in, once the player is real -- so left alone all six draw in the same
    /// corner at the same moment and the result is illegible. They cannot share code to sort
    /// it out either: they are separate assemblies that do not reference each other and must
    /// each work alone.
    ///
    /// So each one claims a row through the AppDomain, which is the one thing they genuinely
    /// do share. SHVDN loads every script into a single AppDomain and ticks them cooperatively
    /// on one thread, so a counter parked in AppDomain data is both visible to all of them and
    /// safe to touch without locking. First to ask gets the bottom row, the next sits above it,
    /// and so on -- and a mod installed on its own asks, gets row nought, and never knows the
    /// mechanism existed. A reload throws the AppDomain away, which resets the count, which is
    /// exactly what should happen.
    /// </summary>
    internal static class Splash
    {
        /// <summary>How long the whole thing lasts, and how much of that is the fades.</summary>
        private const int ShowMs = 4000;
        private const int FadeInMs = 420;
        private const int FadeOutMs = 800;

        /// <summary>
        /// Not before this. Scripts start while the game is still on the loading screen, so
        /// anything drawn early is drawn to nobody -- the same reason Hello waits.
        /// </summary>
        private const int NotBeforeMs = 6000;

        /// <summary>
        /// Row geometry, in fractions of the screen.
        ///
        /// THE PITCH IS TIGHT AND THE TEXT IS SMALL, because this is a LIST now rather than a
        /// stack of separate announcements. Eight names set close together read as one thing
        /// with eight items in it; the same eight spread out read as eight things that happen
        /// to be near each other.
        ///
        /// And the mark is twice what it was. There is only one of it on screen now, so it can
        /// afford to be the size somebody would actually recognise it at.
        /// </summary>
        private const float RightEdge = 0.980f;
        private const float BottomRow = 0.930f;
        private const float RowPitch = 0.032f;
        private const float MarkHeight = 0.092f;
        private const float TextSize = 0.30f;

        /// <summary>
        /// Call once per tick from Main. Costs a comparison once it has had its turn.
        /// </summary>
        public static void Render()
        {
            if (_done) return;

            if (_startedAt == 0)
            {
                if (Game.GameTime < NotBeforeMs) return;

                // Only once there is somebody to show it to.
                try
                {
                    var me = Game.Player.Character;
                    if (me == null || !me.Exists()) return;
                }
                catch
                {
                    _done = true;
                    return;
                }

                _startedAt = Game.GameTime;
                _slot = ClaimRow();
                if (Seal.Folder == null) Seal.Folder = FindIcons();
            }

            var age = Game.GameTime - _startedAt;
            if (age >= ShowMs) { _done = true; return; }

            var a = Alpha(age);
            if (a <= 0) return;

            var y = BottomRow - _slot * RowPitch;

            // The mark sits hard against the right edge and the name is set to its left, so a
            // long name grows leftwards into empty screen rather than pushing the mark about.
            var markCx = RightEdge - ToX(MarkHeight) * 0.5f;

            // ONE OF US DRAWS IT, AND IT IS CENTRED ON ALL OF US. The count is read every
            // frame rather than at the start: the others claim their rows within a tick or two
            // of this one, so a height worked out on the first frame would be the height of
            // however many had got there first.
            if (OwnsSeal())
            {
                var middle = BottomRow - (Rows() - 1) * RowPitch * 0.5f;

                Seal.Draw(markCx, middle, MarkHeight, Color.FromArgb(a, Ink));
            }

            var text = Core.Build.Name + "  " + Core.Build.Version;
            Text(text, markCx - ToX(MarkHeight) * 0.5f - 0.008f, y, a);
        }


        /// <summary>
        /// Where the two seal files are, worked out rather than told.
        ///
        /// THIS IS THE LINE THAT STOPPED THIS BEING A DROP-IN FILE. Every copy of it read the
        /// host mod's own Core.Paths.Icons, which is a class half the mods in the set do not
        /// have -- so a file whose entire purpose is to be identical everywhere could not be
        /// copied anywhere without being edited first, which is how six copies of it drifted.
        ///
        /// It asks the assembly where it is instead. A mod deployed as scripts\Thing.dll keeps
        /// its data in scripts\Thing\, which is the convention every mod in the set already
        /// follows, so its own name is the only thing this needs to know and the assembly
        /// carries that.
        ///
        /// AND IT WILL BORROW. A small mod that ships no art at all -- no data folder, nothing
        /// to deploy -- would otherwise draw a name with no mark beside it. So the last resort
        /// is any sibling's copy: one seal in the scripts folder serves everything in it, and
        /// the tiny mods get the mark without five repos growing a data folder to hold one
        /// picture they all share.
        /// </summary>
        private static string FindIcons()
        {
            try
            {
                var dll = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (string.IsNullOrEmpty(dll)) return null;

                var scripts = System.IO.Path.GetDirectoryName(dll);
                if (string.IsNullOrEmpty(scripts)) return null;

                var mine = System.IO.Path.GetFileNameWithoutExtension(dll);

                var tries = new[]
                {
                    System.IO.Path.Combine(System.IO.Path.Combine(scripts, mine), "icons"),
                    System.IO.Path.Combine(System.IO.Path.Combine(scripts, mine), "data\\icons"),
                    System.IO.Path.Combine(System.IO.Path.Combine(scripts, "spitmux"), "icons"),
                    System.IO.Path.Combine(scripts, "icons")
                };

                foreach (var where in tries)
                {
                    if (System.IO.File.Exists(System.IO.Path.Combine(where, "seal-face.png"))) return where;
                }

                // Somebody else's, then.
                foreach (var dir in System.IO.Directory.GetDirectories(scripts))
                {
                    var where = System.IO.Path.Combine(dir, "icons");

                    if (System.IO.File.Exists(System.IO.Path.Combine(where, "seal-face.png"))) return where;
                }
            }
            catch
            {
                // The name still draws without it.
            }

            return null;
        }

        /// <summary>In over the first moments, out over the last, full in between.</summary>
        private static int Alpha(int age)
        {
            if (age < FadeInMs) return (int)(255f * age / FadeInMs);
            var left = ShowMs - age;
            if (left < FadeOutMs) return (int)(255f * left / FadeOutMs);
            return 255;
        }

        /// <summary>
        /// The next free row, counted in the AppDomain so the whole set stacks rather than
        /// piling into one corner. See the class note -- this is the only channel the mods have.
        /// </summary>
        private static int ClaimRow()
        {
            try
            {
                var domain = AppDomain.CurrentDomain;
                var taken = domain.GetData(RowKey) as int?;
                var row = taken ?? 0;
                domain.SetData(RowKey, row + 1);
                return row;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>How many rows there are, which is how tall the column is.</summary>
        private static int Rows()
        {
            try
            {
                var many = AppDomain.CurrentDomain.GetData(RowKey) as int?;
                return many.HasValue && many.Value > 0 ? many.Value : 1;
            }
            catch
            {
                return 1;
            }
        }

        /// <summary>
        /// Whether this mod is the one drawing the mark.
        ///
        /// CLAIMED BY THE FIRST ONE WITH SOMETHING TO DRAW, not by the first one to load. A
        /// mod that cannot find the art would otherwise take the claim and then draw nothing,
        /// and the whole set would go without a seal because of whichever assembly SHVDN
        /// happened to construct first. A source script compiled at runtime has no file on
        /// disk to look next to at all, so this is not a corner case -- it is Street Golf.
        ///
        /// Asked every frame rather than settled once, so a mod that resolves its folder late
        /// can still take a claim nobody else has.
        /// </summary>
        private static bool OwnsSeal()
        {
            if (Seal.Folder == null) return false;

            try
            {
                var domain = AppDomain.CurrentDomain;
                var who = domain.GetData(SealKey) as string;

                if (who == null)
                {
                    domain.SetData(SealKey, Me);
                    return true;
                }

                return who == Me;
            }
            catch
            {
                // On its own, then, which is the right answer for a single mod.
                return true;
            }
        }

        /// <summary>This assembly, as a name the others can compare against.</summary>
        private static readonly string Me = typeof(Splash).Assembly.FullName;

        private const string RowKey = "spitmux.splash.rows";
        private const string SealKey = "spitmux.splash.seal";

        /// <summary>
        /// The name, right-aligned so it ends at the mark.
        ///
        /// TextElement rather than whichever text helper the host mod owns, because those all
        /// differ and this file has to drop into any of them unchanged.
        /// </summary>
        private static void Text(string s, float rightX, float middleY, int a)
        {
            try
            {
                var t = new GTA.UI.TextElement(
                    s,
                    new PointF(rightX * GTA.UI.Screen.ScaledWidth, middleY * 720f - 11f),
                    TextSize,
                    Color.FromArgb(a, Ink),
                    GTA.UI.Font.ChaletComprimeCologne,
                    GTA.UI.Alignment.Right);
                t.Outline = true;
                t.ScaledDraw();
            }
            catch
            {
                // A greeting is not worth taking a frame down for.
            }
        }

        /// <summary>
        /// A height fraction as a width fraction, so a square stays square on any aspect.
        /// Screen.ScaledWidth is already 720-space, so its ratio to 720 IS the aspect.
        /// </summary>
        private static float ToX(float heightFraction)
        {
            var w = GTA.UI.Screen.ScaledWidth;
            if (w < 1f) return heightFraction;
            return heightFraction * 720f / w;
        }

        /// <summary>The set's colour, the one the site and the seal are drawn in.</summary>
        private static readonly Color Ink = Color.FromArgb(255, 255, 148, 24);

        private static int _startedAt;
        private static int _slot;
        private static bool _done;
    }
}
