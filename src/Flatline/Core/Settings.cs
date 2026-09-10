using System;
using System.Windows.Forms;

namespace Flatline.Core
{
    /// <summary>
    /// Everything the player can change, and what it does when they have not.
    ///
    /// EVERY FIELD IS ALSO THE DEFAULT. There is no separate table of defaults and no required
    /// ini: a line that is missing takes the value declared here, so deleting a setting is safe
    /// and deleting the whole file is safe. That is not a convenience, it is the reason a bad
    /// ini can never stop the mod loading.
    /// </summary>
    internal sealed class Settings
    {
        // ---- general -----------------------------------------------------------

        /// <summary>Off entirely, for somebody who wants it installed and not running.</summary>
        public bool Enabled = true;

        public LogLevel Logging = LogLevel.Info;

        /// <summary>A line in the feed when something happens in front of you.</summary>
        public bool Announce = true;

        /// <summary>
        /// The key that opens the settings screen. F9.
        ///
        /// F9 BECAUSE EVERYTHING ELSE IS TAKEN, and that was checked rather than assumed. Across
        /// the set: Hoodrich is on F2, Bare Minimum F7, Five0 Patrol F10, Overspray F11, Bloody
        /// Mess Shift+B and Fumes Shift+F. F1, F3, F5 and F6 are claimed by other mods in the
        /// scripts folder, and F4 is ScriptHookVDotNet's own console -- which is not a key any
        /// mod gets to argue with.
        ///
        /// NOT P, and that matters even though this default is not P. P is GTA's own pause
        /// (control FrontendPauseAlternate) and SHVDN's key event does not consume the press --
        /// the game reads the same keyboard -- so a chord built on P opens this menu with the
        /// pause screen on top of it. Options.Frame holds that control down for anybody who
        /// rebinds here, which is the only place it can be fixed from.
        /// </summary>
        public Keys MenuKey = Keys.F9;

        /// <summary>
        /// Shift, Control, Alt, or None for a bare key.
        ///
        /// COMPARED EXACTLY RATHER THAN TESTED FOR PRESENCE -- see Options.Chord. Treating this
        /// as "is Shift among the modifiers" would make a Shift+F9 binding fire on Ctrl+Shift+F9
        /// as well, and stealing another mod's chord is precisely what the audit above exists to
        /// avoid.
        /// </summary>
        public Keys MenuModifier = Keys.None;

        /// <summary>
        /// Turn off the game's own ambulance dispatch.
        ///
        /// GTA V SENDS ONE OF ITS OWN, AND IT IS THE THING THIS MOD EXISTS TO REPLACE. Left on,
        /// a bad kill in Vinewood can produce two ambulances at one junction -- ours working on
        /// him and the game's parked behind it doing the nothing it always did. Dispatch service
        /// 5 is the ambulance department; switching it off is one call, and it is switched back
        /// on when the mod unloads.
        ///
        /// Worth turning off if you run something else that wants the vanilla crews.
        /// </summary>
        public bool SuppressVanillaAmbulances = true;

        // ---- what they will and will not do ------------------------------------

        /// <summary>
        /// Whether a workable death is ever actually worked.
        ///
        /// Off, they always lose him and he always goes in the back -- which is a colder mod
        /// and a defensible one. The judgement about WHICH deaths are workable is not a setting,
        /// because it is not an opinion: see Core.Cause.
        /// </summary>
        public bool Resuscitate = true;

        /// <summary>Whether the ones they lose are driven anywhere, or just left as before.</summary>
        public bool TakeToHospital = true;

        // ---- how far anything reaches ------------------------------------------

        /// <summary>
        /// How far out a death is noticed.
        ///
        /// The sweep runs over this every three-quarters of a second, so it is a real cost
        /// rather than a free number. Sixty metres is most of a block: far enough that a
        /// shooting across the road is answered, near enough that it is not sweeping a
        /// neighbourhood.
        /// </summary>
        public float NoticeRange = 60f;

        /// <summary>How far off the van is put out, and how much that varies.</summary>
        public float ComeFrom = 110f;
        public float ComeSpread = 70f;

        /// <summary>Close enough to the body to have arrived.</summary>
        public float ThereRange = 24f;

        /// <summary>Close enough to the body to kneel at it.</summary>
        public float KneelRange = 2.6f;

        /// <summary>Past this the whole thing is somebody else's problem again.</summary>
        public float LetGoRange = 320f;

        /// <summary>How near a scene has to be to count as the one they are already at.</summary>
        public float SameScene = 70f;

        /// <summary>How near you have to be for the mod to say anything about it.</summary>
        public float TellRange = 60f;

        /// <summary>Close enough to the hospital to call it arrived.</summary>
        public float ArrivedRange = 40f;

        /// <summary>How fast it drives. Lights and siren on the way in.</summary>
        public float Speed = 22f;

        // ---- how long each part takes ------------------------------------------

        /// <summary>How long it may spend getting there before the call-out is written off.</summary>
        public int ComeMs = 100000;

        /// <summary>And how long the crew are given to walk from the van to the body.</summary>
        public int ReachMs = 12000;

        /// <summary>
        /// How long they work on somebody with a chance.
        ///
        /// LONGER THAN THE CHECK, DELIBERATELY. A crew who arrive at a man with a pulse worth
        /// chasing work until they get him back; a crew who arrive at a man shot through the
        /// chest establish that quickly and stop. Vanilla gives both the same twenty-six
        /// seconds of standing about, which is what makes it read as a formality.
        /// </summary>
        public int WorkMs = 17000;

        /// <summary>And how long it takes to establish there is nothing to be done.</summary>
        public int CheckMs = 8000;

        /// <summary>The beat on the last frame of it, before anybody moves.</summary>
        public int VerdictMs = 2600;

        /// <summary>How long he is given to get up and be out of the road.</summary>
        public int RisingMs = 3500;

        /// <summary>Getting the trolley out and open beside him.</summary>
        public int FetchMs = 3200;

        /// <summary>And him onto it.</summary>
        public int LoadMs = 2800;

        /// <summary>Wheeling him back to the van.</summary>
        public int WheelMs = 14000;

        /// <summary>Into the back, doors, and everybody in.</summary>
        public int StowMs = 6000;

        /// <summary>How long the van waits for its crew before it goes without them.</summary>
        public int BoardMs = 9000;

        /// <summary>And the longest the whole drive is ever held for.</summary>
        public int GoneMs = 180000;

        // ---- where things sit --------------------------------------------------
        //
        // THESE ARE HERE BECAUSE THEY COULD NOT BE MEASURED. A prop's origin is wherever the
        // artist put it and there is no way to find that out from outside the running game, so
        // every number below is a considered starting point rather than a measurement. If the
        // body floats above the canvas or sinks into it, nudge it here -- that is what these are
        // for, and it beats a magic constant in a source file nobody dares touch.
        //
        // X is left and right, Y is forward and back, Z is up and down, all in metres and all
        // relative to the thing being attached to.

        /// <summary>The body on the trolley.</summary>
        public float BodyOnTrolleyX = 0f;
        public float BodyOnTrolleyY = 0f;
        public float BodyOnTrolleyZ = 0.55f;
        public float BodyOnTrolleyYaw = 90f;

        /// <summary>The trolley in front of the medic pushing it.</summary>
        public float TrolleyPushX = 0f;
        public float TrolleyPushY = 1.1f;
        public float TrolleyPushZ = -0.9f;

        /// <summary>And the trolley in the back of the van.</summary>
        public float TrolleyInVanX = 0f;
        public float TrolleyInVanY = -1.9f;
        public float TrolleyInVanZ = 0.3f;

        /// <summary>A body over a shoulder, on an install with no gurney prop at all.</summary>
        public float CarryX = 0f;
        public float CarryY = 0.15f;
        public float CarryZ = 0.62f;
        public float CarryYaw = 0f;

        /// <summary>And that body laid in the back, with no trolley under it.</summary>
        public float BodyInVanX = 0f;
        public float BodyInVanY = -1.9f;
        public float BodyInVanZ = 0.5f;
        public float BodyInVanYaw = 90f;

        // ---- loading -----------------------------------------------------------

        public static Settings Load()
        {
            var s = new Settings();

            try
            {
                var ini = IniFile.Load(Paths.Ini);

                if (ini == null)
                {
                    Log.Warn("No Flatline.ini beside the dll; running on built-in defaults.");
                    return s;
                }

                s.Enabled = ini.GetBool("General", "Enabled", s.Enabled);
                s.Logging = Level(ini.GetString("General", "Logging", null), s.Logging);
                s.Announce = ini.GetBool("General", "Announce", s.Announce);
                s.MenuKey = ini.GetKey("General", "MenuKey", s.MenuKey);
                s.MenuModifier = ini.GetKey("General", "MenuModifier", s.MenuModifier);
                s.SuppressVanillaAmbulances = ini.GetBool("General", "SuppressVanillaAmbulances",
                                                          s.SuppressVanillaAmbulances);

                s.Resuscitate = ini.GetBool("Crew", "Resuscitate", s.Resuscitate);
                s.TakeToHospital = ini.GetBool("Crew", "TakeToHospital", s.TakeToHospital);

                s.NoticeRange = Clamp(ini.GetFloat("Range", "NoticeRange", s.NoticeRange), 10f, 200f);
                s.ComeFrom = Clamp(ini.GetFloat("Range", "ComeFrom", s.ComeFrom), 30f, 400f);
                s.ComeSpread = Clamp(ini.GetFloat("Range", "ComeSpread", s.ComeSpread), 0f, 300f);
                s.ThereRange = Clamp(ini.GetFloat("Range", "ThereRange", s.ThereRange), 6f, 80f);
                s.KneelRange = Clamp(ini.GetFloat("Range", "KneelRange", s.KneelRange), 1f, 8f);
                s.LetGoRange = Clamp(ini.GetFloat("Range", "LetGoRange", s.LetGoRange), 80f, 1000f);
                s.SameScene = Clamp(ini.GetFloat("Range", "SameScene", s.SameScene), 10f, 200f);
                s.TellRange = Clamp(ini.GetFloat("Range", "TellRange", s.TellRange), 0f, 300f);
                s.ArrivedRange = Clamp(ini.GetFloat("Range", "ArrivedRange", s.ArrivedRange), 10f, 150f);
                s.Speed = Clamp(ini.GetFloat("Range", "Speed", s.Speed), 5f, 45f);

                s.ComeMs = Whole(ini.GetInt("Timing", "ComeMs", s.ComeMs), 10000, 300000);
                s.ReachMs = Whole(ini.GetInt("Timing", "ReachMs", s.ReachMs), 2000, 60000);
                s.WorkMs = Whole(ini.GetInt("Timing", "WorkMs", s.WorkMs), 1000, 120000);
                s.CheckMs = Whole(ini.GetInt("Timing", "CheckMs", s.CheckMs), 1000, 120000);
                s.VerdictMs = Whole(ini.GetInt("Timing", "VerdictMs", s.VerdictMs), 200, 30000);
                s.RisingMs = Whole(ini.GetInt("Timing", "RisingMs", s.RisingMs), 200, 30000);
                s.FetchMs = Whole(ini.GetInt("Timing", "FetchMs", s.FetchMs), 200, 30000);
                s.LoadMs = Whole(ini.GetInt("Timing", "LoadMs", s.LoadMs), 200, 30000);
                s.WheelMs = Whole(ini.GetInt("Timing", "WheelMs", s.WheelMs), 2000, 60000);
                s.StowMs = Whole(ini.GetInt("Timing", "StowMs", s.StowMs), 1000, 60000);
                s.BoardMs = Whole(ini.GetInt("Timing", "BoardMs", s.BoardMs), 1000, 60000);
                s.GoneMs = Whole(ini.GetInt("Timing", "GoneMs", s.GoneMs), 10000, 600000);

                s.BodyOnTrolleyX = Nudge(ini, "Fit", "BodyOnTrolleyX", s.BodyOnTrolleyX);
                s.BodyOnTrolleyY = Nudge(ini, "Fit", "BodyOnTrolleyY", s.BodyOnTrolleyY);
                s.BodyOnTrolleyZ = Nudge(ini, "Fit", "BodyOnTrolleyZ", s.BodyOnTrolleyZ);
                s.BodyOnTrolleyYaw = Spin(ini, "Fit", "BodyOnTrolleyYaw", s.BodyOnTrolleyYaw);

                s.TrolleyPushX = Nudge(ini, "Fit", "TrolleyPushX", s.TrolleyPushX);
                s.TrolleyPushY = Nudge(ini, "Fit", "TrolleyPushY", s.TrolleyPushY);
                s.TrolleyPushZ = Nudge(ini, "Fit", "TrolleyPushZ", s.TrolleyPushZ);

                s.TrolleyInVanX = Nudge(ini, "Fit", "TrolleyInVanX", s.TrolleyInVanX);
                s.TrolleyInVanY = Nudge(ini, "Fit", "TrolleyInVanY", s.TrolleyInVanY);
                s.TrolleyInVanZ = Nudge(ini, "Fit", "TrolleyInVanZ", s.TrolleyInVanZ);

                s.CarryX = Nudge(ini, "Fit", "CarryX", s.CarryX);
                s.CarryY = Nudge(ini, "Fit", "CarryY", s.CarryY);
                s.CarryZ = Nudge(ini, "Fit", "CarryZ", s.CarryZ);
                s.CarryYaw = Spin(ini, "Fit", "CarryYaw", s.CarryYaw);

                s.BodyInVanX = Nudge(ini, "Fit", "BodyInVanX", s.BodyInVanX);
                s.BodyInVanY = Nudge(ini, "Fit", "BodyInVanY", s.BodyInVanY);
                s.BodyInVanZ = Nudge(ini, "Fit", "BodyInVanZ", s.BodyInVanZ);
                s.BodyInVanYaw = Spin(ini, "Fit", "BodyInVanYaw", s.BodyInVanYaw);
            }
            catch (Exception ex)
            {
                Log.Error("Could not read Flatline.ini; running on built-in defaults.", ex);
            }

            return s;
        }

        /// <summary>An offset, kept inside arm's reach of the thing it hangs off.</summary>
        private static float Nudge(IniFile ini, string section, string key, float fallback)
        {
            return Clamp(ini.GetFloat(section, key, fallback), -5f, 5f);
        }

        private static float Spin(IniFile ini, string section, string key, float fallback)
        {
            return Clamp(ini.GetFloat(section, key, fallback), -360f, 360f);
        }

        private static float Clamp(float value, float low, float high)
        {
            if (float.IsNaN(value)) return low;
            return value < low ? low : value > high ? high : value;
        }

        private static int Whole(int value, int low, int high)
        {
            return value < low ? low : value > high ? high : value;
        }

        private static LogLevel Level(string word, LogLevel fallback)
        {
            if (string.IsNullOrEmpty(word)) return fallback;

            LogLevel found;
            return Enum.TryParse(word.Trim(), true, out found) ? found : fallback;
        }
    }
}
