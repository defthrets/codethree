using System;

namespace CodeThree.Api
{
    /// <summary>
    /// What another mod is allowed to know about the ambulance.
    ///
    /// ONE QUESTION, REALLY: is Code Three handling ambulances here, and is a crew still standing
    /// at that spot. It exists because Five0 Patrol already has an ambulance of its own -- an
    /// officer finds a body, calls it in, a van turns up, the crew kneel for twenty-six seconds
    /// and drive off leaving him in the road. That was the best thing available when it was
    /// written and it is exactly what this mod replaces, so with neither side knowing about the
    /// other you get two vans at one junction and a coin toss about which one is the scene.
    ///
    /// IT IS THE SAME SHAPE AS Hoodrich.Api.Block, deliberately: one pattern on this machine
    /// rather than three. Only BCL types cross -- bool, string, float[] -- because the caller
    /// reaches this by REFLECTION and holds no reference to this assembly, so it cannot name
    /// Vector3. mscorlib is the one assembly both mods are guaranteed to agree about.
    ///
    /// NOTHING THROWN LEAVES THIS FILE. An exception crossing a reflection call arrives at the
    /// other end as a TargetInvocationException wrapping a type the caller does not have.
    ///
    /// IT IS SAFE BEFORE CODE THREE HAS STARTED. SHVDN builds scripts in whatever order it finds
    /// them, so the other mod can call in before Wire has run. Everything answers "no" until
    /// Ready, and the caller is expected to keep asking.
    /// </summary>
    public static class Medics
    {
        /// <summary>
        /// The contract version. Bumped when a signature here changes in a way that breaks.
        /// Read by the caller BEFORE anything else.
        /// </summary>
        public static int ApiVersion => 1;

        /// <summary>Code Three's own version string, for the other side's log.</summary>
        public static string Version
        {
            get { try { return Core.Build.Version; } catch { return "?"; } }
        }

        private static Func<bool> _owns;
        private static Func<bool> _running;
        private static Func<float[]> _at;
        private static Func<float[], bool> _still;

        /// <summary>Called by Main once these exist. Not for outside use.</summary>
        internal static void Wire(Func<bool> owns, Func<bool> running,
                                 Func<float[]> at, Func<float[], bool> still)
        {
            _owns = owns;
            _running = running;
            _at = at;
            _still = still;
        }

        internal static void Unwire()
        {
            _owns = null;
            _running = null;
            _at = null;
            _still = null;
        }

        /// <summary>Whether Code Three is here AND has finished starting up.</summary>
        public static bool Ready
        {
            get
            {
                try { return _owns != null; }
                catch { return false; }
            }
        }

        /// <summary>
        /// Whether this mod is actually taking the ambulances.
        ///
        /// NOT THE SAME QUESTION AS Ready, and the difference is the whole reason another mod
        /// asks. Code Three can be installed and switched off in its ini -- and a police mod that
        /// stood its own ambulance down merely because this assembly was PRESENT would leave a
        /// player with no ambulances at all, which is worse than the problem either mod set out
        /// to solve. Stand down for this, not for Ready.
        /// </summary>
        public static bool OwnsAmbulances
        {
            get
            {
                try { return _owns != null && _owns(); }
                catch { return false; }
            }
        }

        /// <summary>Whether a call-out is running anywhere right now.</summary>
        public static bool CalloutRunning
        {
            get
            {
                try { return _running != null && _running(); }
                catch { return false; }
            }
        }

        /// <summary>Where it is -- x, y, z. An empty array when there is not one on.</summary>
        public static float[] CalloutAt
        {
            get
            {
                try { return _at == null ? new float[0] : _at(); }
                catch { return new float[0]; }
            }
        }

        /// <summary>
        /// Whether a crew is still working at this spot.
        ///
        /// THIS IS THE ONE FIVE0 PATROL ACTUALLY NEEDS. Its officers stay at a body until the
        /// ambulance has left, which is a nice beat and worth keeping -- so rather than merely
        /// telling that mod to stand down and losing the behaviour, this answers the same
        /// question its own Medics.At used to. Takes x, y, z as three floats for the reason
        /// given on the class: no shared types.
        /// </summary>
        public static bool CrewStillAt(float x, float y, float z)
        {
            try
            {
                return _still != null && _still(new[] { x, y, z });
            }
            catch
            {
                return false;
            }
        }
    }
}
