using System;
using GTA.Math;

namespace CodeThree.Core
{
    /// <summary>
    /// The small arithmetic of moving things smoothly, in the game's own conventions.
    ///
    /// ONE PLACE FOR THE HEADING MATHS, because it has a trap in it. A GTA heading of 0 faces
    /// north (+Y) and increases anticlockwise, so heading 90 faces WEST -- which is not what
    /// atan2 gives you and not what a compass does. Every file that worked this out for itself
    /// is a file that could have got the sign wrong, and a wrong sign is a gurney laid out
    /// backwards.
    /// </summary>
    internal static class Motion
    {
        /// <summary>
        /// Eases 0..1 in and out.
        ///
        /// Linear motion starts and stops dead, and a body that sets off at full speed and
        /// halts on the bed reads as a thing being moved by a machine. Smoothstep accelerates
        /// away and settles in, which reads as hands.
        /// </summary>
        public static float Smooth(float t)
        {
            if (t <= 0f) return 0f;
            if (t >= 1f) return 1f;

            return t * t * (3f - 2f * t);
        }

        public static Vector3 Lerp(Vector3 a, Vector3 b, float t)
        {
            return a + (b - a) * t;
        }

        /// <summary>Wraps an angle difference into -180..180, so a turn goes the short way round.</summary>
        public static float Wrap(float degrees)
        {
            while (degrees > 180f) degrees -= 360f;
            while (degrees < -180f) degrees += 360f;

            return degrees;
        }

        /// <summary>Part of the way from one heading to another, by the short way round.</summary>
        public static float Turn(float from, float to, float t)
        {
            return from + Wrap(to - from) * t;
        }

        /// <summary>
        /// The heading a direction faces, in the game's convention.
        ///
        /// atan2 measures from +X; a heading measures from +Y. Hence the ninety.
        /// </summary>
        public static float HeadingOf(Vector3 direction)
        {
            return (float)(Math.Atan2(direction.Y, direction.X) * 180d / Math.PI) - 90f;
        }

        /// <summary>The unit vector a heading faces, flat on the ground.</summary>
        public static Vector3 Facing(float heading)
        {
            var r = heading * Math.PI / 180d;

            return new Vector3((float)-Math.Sin(r), (float)Math.Cos(r), 0f);
        }

        /// <summary>
        /// A vector turned about the vertical by a heading.
        ///
        /// Anticlockwise, which is the game's direction -- so local forward, (0, 1), turned by
        /// a heading comes out exactly as Facing(heading).
        /// </summary>
        public static Vector3 Rotate(Vector3 v, float heading)
        {
            var r = heading * Math.PI / 180d;
            var c = (float)Math.Cos(r);
            var s = (float)Math.Sin(r);

            return new Vector3(v.X * c - v.Y * s, v.X * s + v.Y * c, v.Z);
        }

        public static Vector3 Flat(Vector3 v)
        {
            return new Vector3(v.X, v.Y, 0f);
        }

        /// <summary>Horizontal distance, which is the one that matters for "has he reached his mark".</summary>
        public static float FlatDistance(Vector3 a, Vector3 b)
        {
            return Flat(a - b).Length();
        }
    }
}
