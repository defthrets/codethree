using System;
using GTA;
using GTA.Native;

namespace Flatline.Core
{
    /// <summary>
    /// Playing a clip on somebody, and never hanging the game to do it.
    ///
    /// ANIMATION DICTIONARY NAMES ARE THE MOST FRAGILE STRINGS IN A GTA MOD. They are not
    /// checked by the compiler, they differ between the two editions in places, and a wrong one
    /// fails silently -- REQUEST_ANIM_DICT on a name that does not exist never loads and never
    /// errors. The usual way this is written is a `while (!HAS_ANIM_DICT_LOADED) Yield();` loop,
    /// which on a wrong name is an infinite loop inside a script tick: the game hangs, with no
    /// log line, and it looks like a crash.
    ///
    /// So every clip in this mod goes through here, everything is time-boxed, and a dictionary
    /// that does not arrive is skipped. The cost of a wrong name is then a paramedic who kneels
    /// instead of working -- cosmetic, findable in the log, and not a hang.
    ///
    /// EVERY PAIR BELOW WAS CHECKED AGAINST THE GAME'S OWN DUMP before it was written here, per
    /// the rule in hoodrich/ANIMS.md -- 20,179 dictionaries and 269,414 clips, from
    /// DurtyFree/gta-v-data-dumps/animDictsCompact.json. That check is the entire point: this
    /// codebase once shipped twenty-five bad pairs in a single file and nobody could tell,
    /// because the failure is silent by design.
    /// </summary>
    internal static class Anim
    {
        /// <summary>Long enough for a real load off disk, short enough not to be a stall.</summary>
        private const int LoadWaitMs = 900;

        /// <summary>Loads a dictionary, or gives up. True when it is ready to play from.</summary>
        public static bool Ready(string dict)
        {
            try
            {
                if (string.IsNullOrEmpty(dict)) return false;

                if (Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict)) return true;

                Function.Call(Hash.REQUEST_ANIM_DICT, dict);

                var until = Game.GameTime + LoadWaitMs;

                while (!Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict) &&
                       Game.GameTime < until)
                {
                    Script.Yield();
                }

                var ok = Function.Call<bool>(Hash.HAS_ANIM_DICT_LOADED, dict);

                if (!ok) Log.Warn("Animation dictionary did not load: " + dict);

                return ok;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not request " + dict + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Plays a clip. Returns whether it actually started.
        ///
        /// THE LAST THREE ARGUMENTS ARE NOT POSITION LOCKS, whatever every community header
        /// calls them. They are bPhaseControlled, IkFlags (an int) and bAllowOverrideCloneUpdate
        /// -- see ANIMS.md, where passing true,true,true is documented as leaving the clip
        /// parked on frame 0 with leg IK disabled. false, 0, false.
        ///
        /// ALREADY PLAYING IS NOT A REASON TO PLAY IT AGAIN. Every caller here sits in a
        /// per-tick loop and calls unconditionally; TASK_PLAY_ANIM does not check whether the
        /// clip is running, it RESTARTS it, and a pose restarted forty times a second is a ped
        /// juddering on the spot. Cheap to ask, and it makes "call it every tick" the correct
        /// way to hold a pose -- which is what every call site already assumed it was.
        /// </summary>
        public static bool Play(Ped who, string dict, string clip, int flags, int ms = -1)
        {
            try
            {
                if (!Crew.Alive(who)) return false;

                if (IsPlaying(who, dict, clip)) return true;

                if (!Ready(dict)) return false;

                Function.Call(Hash.TASK_PLAY_ANIM, who.Handle, dict, clip,
                              8f, -8f, ms, flags, 0f, false, 0, false);

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not play " + dict + "/" + clip + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>Whether this ped is already running this exact clip. Slot 3 covers both.</summary>
        public static bool IsPlaying(Ped who, string dict, string clip)
        {
            try
            {
                if (!Crew.Alive(who)) return false;

                return Function.Call<bool>(Hash.IS_ENTITY_PLAYING_ANIM, who.Handle, dict, clip, 3);
            }
            catch
            {
                // Cannot tell, so let it be re-issued. A twitch is better than a pose that
                // never starts.
                return false;
            }
        }

        /// <summary>Stops a clip without clearing the rest of their tasks.</summary>
        public static void Stop(Ped who, string dict, string clip)
        {
            try
            {
                if (!Crew.Alive(who)) return;

                Function.Call(Hash.STOP_ANIM_TASK, who.Handle, dict, clip, 3f);
            }
            catch
            {
                // Nothing worth saying. The clip ends when the ped is re-tasked anyway.
            }
        }

        // ---- the clips this mod uses -------------------------------------------
        //
        // Checked against animDictsCompact.json. The contents of each dictionary are written
        // out beside it so the next person does not have to download ten megabytes to find out
        // what else was available.

        /// <summary>
        /// The one doing the work: kneeling, hands on the chest, pushing.
        ///
        /// mini@cpr@char_a@cpr_str holds cpr_pumpchest, cpr_kol, cpr_kol_idle, cpr_kol_to_cpr,
        /// cpr_cpr_to_kol, cpr_success and cpr_fail. char_a is the one PERFORMING it -- char_b
        /// is the same seven clips cut for the body underneath, which this mod cannot use for
        /// the reason set out in Callout: a dead ped will not play an animation at all.
        ///
        /// So only half of a two-hander is ever on screen, and that turns out to be the half
        /// that matters. What you see is a medic kneeling over a limp body pushing on its
        /// chest, which is what CPR looks like from the pavement.
        /// </summary>
        public const string CprDict = "mini@cpr@char_a@cpr_str";
        public const string CprPump = "cpr_pumpchest";

        /// <summary>Sitting back on his heels between rounds. Same dictionary.</summary>
        public const string CprRest = "cpr_kol_idle";

        /// <summary>It worked, and it did not. Played once at the end, held on the last frame.</summary>
        public const string CprWorked = "cpr_success";
        public const string CprFailed = "cpr_fail";

        /// <summary>
        /// The second medic, stood back and looking at what the first one is doing.
        ///
        /// The same investigate idle Five0 Patrol's officers use over a body, for the same
        /// reason: it is the one clip in the game that reads as somebody crouched and looking
        /// at something on the ground. Two men both doing CPR on one chest is worse than one
        /// man doing CPR and one man watching.
        /// </summary>
        public const string LookDict = "amb@code_human_police_investigate@idle_a";
        public const string LookClip = "idle_a";

        // ---- flags ---------------------------------------------------------------

        /// <summary>Loop, and hold the ped where he is. See the table in ANIMS.md.</summary>
        public const int Loop = 1;

        /// <summary>Play once and stay in the final pose rather than snapping out of it.</summary>
        public const int HoldLast = 2;
    }
}
