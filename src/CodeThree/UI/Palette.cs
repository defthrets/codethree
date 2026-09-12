using System.Drawing;

namespace CodeThree.UI
{
    /// <summary>
    /// Code Three's colours.
    ///
    /// THE SAME SHAPE AS FIVE0 PATROL'S AND HOODRICH'S PALETTES, IN A DIFFERENT KEY. Each mod in
    /// the set is built on one brand pair -- a light end and a dark end of a single hue -- with
    /// everything else white, dim white, or a colour that MEANS something. Doing it the same way
    /// is what makes a player running four of them see one family of screens rather than four
    /// mods that happened to install together.
    ///
    /// The pair here is the red off the side of an ambulance, because that is what the mod is
    /// about and because it is a long way from Five0 Patrol's police blue -- which matters more
    /// than usual for these two, since they are the pair most likely to be open one after the
    /// other while somebody works out which mod is doing what at a scene.
    ///
    /// WARMED AND PULLED BACK FROM PURE RED, deliberately. A saturated red panel highlight does
    /// not read as a brand, it reads as an error -- every piece of software the player has ever
    /// used has taught them that. Lifting the green and blue channels off the floor turns it
    /// from a warning into a colour, without taking it anywhere near pink.
    /// </summary>
    internal static class Palette
    {
        public static readonly Color Text = Color.FromArgb(245, 240, 240, 246);
        public static readonly Color TextDim = Color.FromArgb(190, 190, 186, 188);

        /// <summary>The mark, the rules, the lit half of anything.</summary>
        public static readonly Color Brand = Color.FromArgb(255, 232, 86, 80);

        /// <summary>The deep end of it. Backing, shadow, the far side of a fill.</summary>
        public static readonly Color BrandDeep = Color.FromArgb(255, 96, 26, 28);

        /// <summary>The empty half of a slider. Nearly the panel, so a bar at zero is not a line.</summary>
        public static readonly Color Track = Color.FromArgb(178, 19, 10, 11);

        /// <summary>The panel itself. Almost black, warmed by a point or two towards the brand.</summary>
        public static readonly Color Panel = Color.FromArgb(242, 17, 11, 12);

        /// <summary>
        /// He is worth working on.
        ///
        /// A green, because it is the one colour that means this and arguing with that would be
        /// clever rather than useful. Only ever appears on the call-out readout.
        /// </summary>
        public static readonly Color Good = Color.FromArgb(255, 108, 198, 128);

        public static Color Alpha(Color c, int alpha)
        {
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;

            return Color.FromArgb(alpha, c.R, c.G, c.B);
        }
    }
}
