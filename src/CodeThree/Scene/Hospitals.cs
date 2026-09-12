using System;
using GTA;
using GTA.Math;
using GTA.Native;
using CodeThree.Core;

namespace CodeThree.Scene
{
    /// <summary>
    /// Where the ambulance is taking him.
    ///
    /// ASKED OF THE MAP RATHER THAN WRITTEN DOWN. The obvious way to do this is a table of five
    /// hospitals with their coordinates in it, and the obvious way is wrong twice over: the
    /// numbers have to come from somewhere, and "somewhere" means a forum post that may be for
    /// a different edition -- and a table cannot know about a hospital that arrived in a DLC
    /// after it was typed. Five0 Patrol has a stations.json that shipped empty for exactly this
    /// reason, and nothing noticed for months.
    ///
    /// So this iterates the blips the game itself has already placed. Sprite 61 is
    /// radar_hospital, the H on the minimap, and every hospital in the game has one because the
    /// game puts it there. That is the same principle as reading a cop's ethnicity off his
    /// voice bank instead of guessing from his model name: the engine already knows, so ask it.
    ///
    /// AND THERE IS A FLOOR UNDER IT. If the iteration finds nothing at all -- a save with the
    /// map hidden, a mod that has stripped the blips, an edition that numbers sprites
    /// differently -- Central Los Santos Medical is used, whose coordinates are the one pair in
    /// this mod that ARE written down. They are lifted from Hoodrich's taxi rank, where they
    /// have been driven to a few thousand times.
    /// </summary>
    internal static class Hospitals
    {
        /// <summary>The H on the minimap. See the class note.</summary>
        private const int HospitalSprite = 61;

        /// <summary>
        /// Central Los Santos Medical, in Pillbox Hill.
        ///
        /// The backstop, and the only coordinate in this mod that is a constant. Taken from
        /// Hoodrich's Luber rank rather than from a map site, because that one has had cars
        /// driven to it in play for months and a kerb you can actually stop at is a different
        /// fact from a pin on a map.
        /// </summary>
        private static readonly Vector3 Pillbox = new Vector3(293.6f, -1448.0f, 29.9f);

        /// <summary>
        /// How often the list is rebuilt. Hospitals do not move.
        ///
        /// Once every couple of minutes rather than never, because a blip can appear late --
        /// the map is not fully populated on the first tick after a load, and a cache built
        /// then would hold one hospital for the rest of the session.
        /// </summary>
        private const int RefreshMs = 120000;

        private static Vector3[] _known;
        private static int _builtAt;

        /// <summary>
        /// The nearest hospital to a point, and how far it is.
        ///
        /// Never fails. The worst case is Pillbox, which is a real hospital in the middle of
        /// the city, so even the worst case is a van driving somewhere sensible.
        /// </summary>
        public static Vector3 Nearest(Vector3 from)
        {
            var all = All();

            var best = Pillbox;
            var bestAt = float.MaxValue;

            foreach (var where in all)
            {
                try
                {
                    var d = where.DistanceTo(from);
                    if (d >= bestAt) continue;

                    bestAt = d;
                    best = where;
                }
                catch
                {
                    // Skip it.
                }
            }

            return best;
        }

        /// <summary>
        /// Every hospital the map knows about, rebuilt occasionally.
        ///
        /// THE ITERATOR IS A REAL ITERATOR AND HAS TO BE DRAINED. GET_FIRST_BLIP_INFO_ID starts
        /// a walk over one sprite and GET_NEXT_BLIP_INFO_ID continues it; stopping early leaves
        /// the game's own cursor part way through, which is the sort of thing that shows up as
        /// somebody else's blip loop behaving oddly three files away.
        /// </summary>
        private static Vector3[] All()
        {
            var now = Game.GameTime;

            if (_known != null && _known.Length > 0 && now - _builtAt < RefreshMs) return _known;

            _builtAt = now;

            try
            {
                var found = new System.Collections.Generic.List<Vector3>();

                var blip = Function.Call<int>(Hash.GET_FIRST_BLIP_INFO_ID, HospitalSprite);

                // A HARD CEILING ON THE WALK. Every loop in this codebase that talks to a game
                // iterator has one, because the cost of the engine handing back an id that
                // never terminates is a hung tick rather than a wrong answer.
                for (var guard = 0; guard < 64 && Function.Call<bool>(Hash.DOES_BLIP_EXIST, blip); guard++)
                {
                    var at = Function.Call<Vector3>(Hash.GET_BLIP_INFO_ID_COORD, blip);

                    if (at != Vector3.Zero) found.Add(at);

                    blip = Function.Call<int>(Hash.GET_NEXT_BLIP_INFO_ID, HospitalSprite);
                }

                if (found.Count > 0)
                {
                    _known = found.ToArray();

                    Log.Debug("Hospitals on the map: " + _known.Length + ".");

                    return _known;
                }

                Log.Debug("No hospital blips found; falling back to Central Los Santos Medical.");
            }
            catch (Exception ex)
            {
                Log.Debug("Could not read the hospital blips: " + ex.Message);
            }

            _known = new[] { Pillbox };
            return _known;
        }
    }
}
