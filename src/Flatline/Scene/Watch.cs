using System;
using System.Collections.Generic;
using GTA;
using GTA.Math;
using GTA.Native;
using Flatline.Core;

namespace Flatline.Scene
{
    /// <summary>Somebody who has just died, and what is known about it.</summary>
    internal sealed class Death
    {
        public Ped Body;
        public Vector3 Where;
        public int When;
    }

    /// <summary>
    /// Noticing that somebody has died, which is harder than it sounds.
    ///
    /// A CORPSE IS NOT AN EVENT. The obvious implementation is to sweep for dead peds and treat
    /// each new one as a death, and it is wrong in a way that only shows up in play: the city
    /// is full of bodies the player never saw die. Drive into a street where a gang fight
    /// finished ten minutes ago and every corpse in it is "new" to a sweep that has just
    /// arrived, so an ambulance is dispatched to a man who has been cold since before you got
    /// there -- and then another, and another, for as long as you stand in the road.
    ///
    /// So this watches the LIVING. A ped has to have been seen alive by this sweep before its
    /// death counts, which makes the event a transition rather than a state, and means the mod
    /// only ever answers deaths that happened in front of somebody. That is also the honest
    /// scope: an ambulance service that responds to things nobody witnessed is a different and
    /// much larger mod.
    ///
    /// AND EACH BODY IS ANSWERED ONCE. The handles that have been dispatched to are remembered,
    /// so a crew that gives up on a scene does not immediately cause a second van to be sent to
    /// the same man.
    /// </summary>
    internal sealed class Watch
    {
        private readonly Settings _cfg;

        /// <summary>Everybody currently in range who was alive last time we looked.</summary>
        private readonly HashSet<int> _standing = new HashSet<int>();

        /// <summary>And everybody already answered, so nobody is answered twice.</summary>
        private readonly HashSet<int> _answered = new HashSet<int>();

        /// <summary>Deaths seen and not yet handed out.</summary>
        private readonly List<Death> _fresh = new List<Death>();

        private int _sweptAt;

        /// <summary>
        /// How often the sweep runs.
        ///
        /// Not every frame, and it does not need to be. A death is answered by a van that takes
        /// the better part of a minute to arrive, so a second of latency in noticing it is
        /// invisible -- and GetNearbyPeds over sixty metres is not something to do forty times
        /// a second for no reason.
        /// </summary>
        private const int SweepMs = 700;

        /// <summary>
        /// How long a death stays worth answering.
        ///
        /// A body found by the sweep and then left for a minute while another call-out finishes
        /// is a body the crew would no longer be scrambling for. It also stops the queue growing
        /// without limit during a firefight.
        /// </summary>
        private const int StaleMs = 45000;

        /// <summary>The most we will ever hold. A massacre is not a reason to leak.</summary>
        private const int MostHeld = 12;

        /// <summary>How big the memory of already-answered bodies is allowed to get.</summary>
        private const int MostRemembered = 200;

        public Watch(Settings cfg)
        {
            _cfg = cfg;
        }

        /// <summary>
        /// Look around. Cheap, and mostly does nothing.
        /// </summary>
        public void Update()
        {
            var now = Game.GameTime;

            if (now - _sweptAt < SweepMs) return;
            _sweptAt = now;

            try
            {
                var me = Game.Player.Character;
                if (me == null || !me.Exists()) return;

                var here = me.Position;

                Expire(now);

                var seen = new HashSet<int>();

                foreach (var ped in Crew.Near(here, _cfg.NoticeRange))
                {
                    var handle = ped.Handle;

                    // The player is not this mod's business. Deliberate, and settled: the
                    // vanilla wasted flow and Hoodrich's hospital bill both own that moment
                    // already, and three systems answering one death is how you get a mod that
                    // fights the game over what just happened to you.
                    if (handle == me.Handle) continue;

                    if (!IsPerson(ped)) continue;

                    seen.Add(handle);

                    if (!ped.IsDead)
                    {
                        _standing.Add(handle);
                        continue;
                    }

                    // Dead. Only interesting if we watched him standing.
                    if (!_standing.Remove(handle)) continue;
                    if (_answered.Contains(handle)) continue;
                    if (_fresh.Count >= MostHeld) continue;

                    _fresh.Add(new Death { Body = ped, Where = ped.Position, When = now });

                    Log.Debug("Somebody went down " +
                              (int)ped.Position.DistanceTo(here) + "m away.");
                }

                // ANYBODY WHO HAS LEFT THE SWEEP IS FORGOTTEN, and that is not the same as
                // forgetting he was alive. He walked out of range still standing; if he dies
                // out there it is not a death this mod saw, and when he walks back in he is
                // simply recorded as standing again.
                _standing.IntersectWith(seen);
            }
            catch (Exception ex)
            {
                Log.Debug("The sweep failed: " + ex.Message);
            }
        }

        /// <summary>
        /// The next death worth sending somebody to, or null.
        ///
        /// THE NEAREST RATHER THAN THE OLDEST. During a firefight several land within a few
        /// seconds of each other, and the one the player is standing over is the one where an
        /// ambulance turning up is a thing they will actually watch.
        /// </summary>
        public Death Next(Vector3 from)
        {
            Death best = null;
            var bestAt = float.MaxValue;

            for (var i = _fresh.Count - 1; i >= 0; i--)
            {
                var death = _fresh[i];

                if (!Crew.There(death.Body)) { _fresh.RemoveAt(i); continue; }

                try
                {
                    var d = death.Body.Position.DistanceTo(from);
                    if (d >= bestAt) continue;

                    bestAt = d;
                    best = death;
                }
                catch
                {
                    _fresh.RemoveAt(i);
                }
            }

            return best;
        }

        /// <summary>Taken off the list and never offered again.</summary>
        public void Claim(Death death)
        {
            if (death == null) return;

            _fresh.Remove(death);

            try
            {
                if (Crew.There(death.Body)) Remember(death.Body.Handle);
            }
            catch
            {
                // Nothing to remember.
            }
        }

        /// <summary>
        /// A handle we have already dealt with.
        ///
        /// CAPPED, because this set is the one thing in the file that only ever grows. A long
        /// session in a busy part of town is thousands of handles, and handles are reused by
        /// the engine -- so an unbounded set eventually starts refusing to answer a live death
        /// because a completely different ped once had that number.
        /// </summary>
        private void Remember(int handle)
        {
            if (_answered.Count >= MostRemembered) _answered.Clear();

            _answered.Add(handle);
        }

        /// <summary>Drops the ones that have gone cold or gone away.</summary>
        private void Expire(int now)
        {
            for (var i = _fresh.Count - 1; i >= 0; i--)
            {
                var death = _fresh[i];

                if (!Crew.There(death.Body) || now - death.When > StaleMs) _fresh.RemoveAt(i);
            }
        }

        /// <summary>
        /// Whether this is somebody rather than something.
        ///
        /// The city is full of peds that are not people for our purposes -- animals, and
        /// whatever else a mod has spawned. An ambulance for a dead coyote is a joke the second
        /// time and a bug the third.
        /// </summary>
        private static bool IsPerson(Ped ped)
        {
            try
            {
                // THE ENGINE'S OWN ANSWER rather than a guess off the model name. SHVDN 3.9 has
                // no Species on Ped, and a list of animal model hashes maintained by hand is a
                // list that is wrong the next time a DLC adds a dog.
                return Function.Call<bool>(Hash.IS_PED_HUMAN, ped.Handle);
            }
            catch
            {
                // Cannot tell, so treat him as somebody. An ambulance for a coyote is a worse
                // failure than one for a man, but a mod that answers nothing at all because one
                // native went missing is worse than both.
                return true;
            }
        }

        public void Release()
        {
            _standing.Clear();
            _answered.Clear();
            _fresh.Clear();
        }
    }
}
