using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using GTA;
using GTA.Native;
using Flatline.Core;

namespace Flatline.UI
{
    /// <summary>
    /// One line in the menu: a heading, a switch, or something with a range.
    ///
    /// DELEGATES RATHER THAN REFLECTION. The obvious build walks the Settings fields and infers
    /// a control from each type, and it is wrong for the reason every reflective settings screen
    /// is wrong: it knows a float is a float and has no idea that BodyOnTrolleyZ runs -5 to 5 in
    /// hundredths while ComeMs runs to a hundred thousand, or that Logging is four names rather
    /// than a number. The bounds ARE the design, they already exist in Settings.Load's clamps,
    /// and a screen that invented its own would drift from them silently.
    /// </summary>
    internal sealed class Knob
    {
        public string Label;

        /// <summary>The consequence, shown under the highlighted row. Never a restatement.</summary>
        public string Detail;

        /// <summary>Where it lives in the ini. Null for a heading.</summary>
        public string Section;
        public string Key;

        /// <summary>What the right-hand column reads.</summary>
        public Func<string> Read;

        /// <summary>Left or right. Null for a heading.</summary>
        public Action<int> Nudge;

        /// <summary>What to write to the ini.</summary>
        public Func<string> Ini;

        /// <summary>How full the bar is, 0 to 1. Negative means no bar -- a switch or a name.</summary>
        public Func<float> Fill;

        public bool IsHeading => Nudge == null;
    }

    /// <summary>
    /// Every setting in the mod, on screen, without opening a text file.
    ///
    /// AN INI IS NOT A SETTINGS SCREEN, and for this mod that is not a convenience argument --
    /// it is the difference between one section being tunable and not.
    ///
    /// The [Fit] offsets are the one part of Flatline that could not be verified before it
    /// shipped. Everything else was checked against the game's own dumps: the animation clips
    /// exist, the gurney props exist, the damage-cause hashes were computed and cross-checked.
    /// But a prop's origin is wherever the artist put it, and there is no way to measure one
    /// from outside the running game. So those numbers are considered guesses, and the only way
    /// to improve a guess is to look at it.
    ///
    /// Through the ini that loop is: alt-tab, find a file under Program Files, edit it, tab
    /// back, press Insert, kill somebody with a gun, wait for the van, watch the load, decide
    /// the body is still four inches into the canvas, and start again. Nobody does that twice.
    /// Here it is: hold Right while looking at the body on the trolley in front of you.
    ///
    /// CHANGES APPLY THE INSTANT YOU MAKE THEM, which for most rows falls out for free -- every
    /// system holds the same Settings object. The [Fit] rows need one extra step, because an
    /// attachment offset is copied into the engine at the moment of attaching and does not
    /// track the number afterwards; see Refit.
    ///
    /// AND THEY ARE WRITTEN BACK TO THE INI, or the session ends and the work is lost. Through
    /// IniFile.SetValue, one key at a time, which rewrites only the line it owns -- the file is
    /// mostly comments explaining what each setting is FOR, and a screen that saved by
    /// serialising the whole object would silently replace all of it with bare key=value pairs.
    /// </summary>
    internal sealed class Options
    {
        private const float Width = 0.46f;
        private const float RowH = 0.030f;
        private const float Pad = 0.014f;

        /// <summary>How many rows are on screen at once, headings included.</summary>
        private const int Window = 13;

        private const float TitleScale = 0.32f;
        private const float RowScale = 0.335f;
        private const float DetailScale = 0.30f;

        /// <summary>How long the panel takes to arrive.</summary>
        private const float Arrive = 11f;
        private const float Rise = 0.02f;

        /// <summary>The bar behind a value with a range, as a fraction of the row.</summary>
        private const float BarW = 0.115f;
        private const float BarH = 0.0055f;

        /// <summary>The section whose rows have to be re-applied to take effect. See Refit.</summary>
        private const string Fit = "Fit";

        private readonly Settings _cfg;
        private readonly List<Knob> _rows = new List<Knob>();
        private readonly Eased _arrive = new Eased();

        /// <summary>Which keys have been touched, so a close writes only those.</summary>
        private readonly HashSet<Knob> _dirty = new HashSet<Knob>();

        private int _at;
        private int _top;

        public bool Open { get; private set; }

        /// <summary>Said out loud on save. Wired by Main.</summary>
        public Action<string> Say;

        /// <summary>
        /// Put the scene back together at the numbers that are now set. Wired by Main.
        ///
        /// THE WHOLE REASON THE [FIT] ROWS ARE WORTH HAVING. An attachment offset is read once,
        /// when the attach happens, and the engine keeps no link back to the variable it came
        /// from -- so moving the number does nothing to a body that is already strapped down.
        /// This re-attaches whatever is currently attached, and it is what turns those rows from
        /// a setting that applies to the NEXT call-out into one you can aim.
        /// </summary>
        public Action Refit;

        /// <summary>What the crew are doing, or null when nothing is on. Wired by Main.</summary>
        public Func<string> Scene;

        /// <summary>Whether the man at that scene was one they could have had. Wired by Main.</summary>
        public Func<bool> Hopeful;

        public Options(Settings cfg)
        {
            _cfg = cfg;

            Rows();

            // Never start on a heading; the first row is one.
            _at = Next(0, 1);
        }

        // ---- the list -----------------------------------------------------------

        /// <summary>
        /// Every row, in order.
        ///
        /// NOT CALLED Build. Core.Build is the class holding the version string, and a method of
        /// that name here shadows it inside this file -- so `Build.Version` in the draw resolves
        /// to the method and fails with a message about a method being invalid in the given
        /// context, which says nothing at all about the actual problem.
        /// </summary>
        private void Rows()
        {
            Head("GENERAL");

            Toggle("Mod enabled", "Everything off, without uninstalling anything.",
                   "General", "Enabled", () => _cfg.Enabled, v => _cfg.Enabled = v);

            Pick("Logging", "Debug names every sweep, every verdict and every attach, in Flatline.log.",
                 "General", "Logging",
                 new[] { "Error", "Warn", "Info", "Debug" },
                 () => (int)_cfg.Logging,
                 i => { _cfg.Logging = (LogLevel)i; Log.Level = _cfg.Logging; });

            Toggle("Say what happened",
                   "One line when he comes round or they stop. Never a running commentary.",
                   "General", "Announce", () => _cfg.Announce, v => _cfg.Announce = v);

            Toggle("Silence the game's ambulances",
                   "GTA sends its own to a body. Two at one junction is a pile-up. Put back on unload.",
                   "General", "SuppressVanillaAmbulances",
                   () => _cfg.SuppressVanillaAmbulances,
                   v => _cfg.SuppressVanillaAmbulances = v);

            Head("THE CREW");

            Toggle("They try to bring him round",
                   "Off, they lose everybody. WHICH deaths are workable is not a setting -- it is read off what killed him.",
                   "Crew", "Resuscitate", () => _cfg.Resuscitate, v => _cfg.Resuscitate = v);

            Toggle("They take him to hospital",
                   "Off, the ones they lose are left where they fell. This is the half that moves other mods' corpses.",
                   "Crew", "TakeToHospital",
                   () => _cfg.TakeToHospital, v => _cfg.TakeToHospital = v);

            Toggle("The second man brings the bag",
                   "The red one from the back of every ambulance, carried over and set down beside him.",
                   "Crew", "MedicBag", () => _cfg.MedicBag, v => _cfg.MedicBag = v);

            Toggle("They write down the time",
                   "When they lose him: the vanilla paramedic's own clipboard scenario before the trolley comes out.",
                   "Crew", "TimeOfDeath", () => _cfg.TimeOfDeath, v => _cfg.TimeOfDeath = v);

            Toggle("He limps away",
                   "A man they bring round walks off injured, and keeps walking that way. Off, he strolls.",
                   "Crew", "InjuredWalk", () => _cfg.InjuredWalk, v => _cfg.InjuredWalk = v);

            Head("HOW LONG IT TAKES");

            Millis("Compressions, somebody with a chance",
                   "Two rounds, split. The kneeling, looking and leaning are the clips' own length.",
                   "Timing", "WorkMs", 1000, 120000, 500,
                   () => _cfg.WorkMs, v => _cfg.WorkMs = v);

            Millis("Compressions, somebody without one",
                   "One round. Vanilla gives both cases the same twenty-six seconds, which is what makes it a formality.",
                   "Timing", "CheckMs", 1000, 120000, 500,
                   () => _cfg.CheckMs, v => _cfg.CheckMs = v);

            Millis("The beat on the last frame",
                   "How long he holds the pose that says it did or did not take, before anybody moves.",
                   "Timing", "VerdictMs", 200, 30000, 100,
                   () => _cfg.VerdictMs, v => _cfg.VerdictMs = v);

            Millis("Helping him to his feet, at most",
                   "A ceiling. The paired clip normally ends itself well inside this.",
                   "Timing", "RisingMs", 200, 30000, 100,
                   () => _cfg.RisingMs, v => _cfg.RisingMs = v);

            Millis("On the clipboard", "Time of death, before anybody reaches for the trolley.",
                   "Timing", "PronounceMs", 200, 30000, 100,
                   () => _cfg.PronounceMs, v => _cfg.PronounceMs = v);

            Millis("Getting the trolley out", "The pause with the back doors open and nothing loaded yet.",
                   "Timing", "FetchMs", 200, 30000, 100,
                   () => _cfg.FetchMs, v => _cfg.FetchMs = v);

            Millis("Loading him onto it", "Watch this one with the fit rows below.",
                   "Timing", "LoadMs", 200, 30000, 100,
                   () => _cfg.LoadMs, v => _cfg.LoadMs = v);

            Millis("Wheeling him back", "A ceiling, not a duration -- they stop when they reach the van.",
                   "Timing", "WheelMs", 2000, 60000, 500,
                   () => _cfg.WheelMs, v => _cfg.WheelMs = v);

            Millis("Into the back", "Doors, trolley and both of them aboard.",
                   "Timing", "StowMs", 1000, 60000, 500,
                   () => _cfg.StowMs, v => _cfg.StowMs = v);

            Millis("The van waits for its crew",
                   "Too short and it pulls away leaving a paramedic stood in the road for the session.",
                   "Timing", "BoardMs", 1000, 60000, 500,
                   () => _cfg.BoardMs, v => _cfg.BoardMs = v);

            Millis("Longest it may spend arriving",
                   "A van that cannot find the street is written off. Worse than no van.",
                   "Timing", "ComeMs", 10000, 300000, 5000,
                   () => _cfg.ComeMs, v => _cfg.ComeMs = v);

            Millis("Walking from the van to him", "A ceiling. They stop when they get there.",
                   "Timing", "ReachMs", 2000, 60000, 500,
                   () => _cfg.ReachMs, v => _cfg.ReachMs = v);

            Millis("Longest the drive is held", "The backstop that guarantees everything is handed back.",
                   "Timing", "GoneMs", 10000, 600000, 10000,
                   () => _cfg.GoneMs, v => _cfg.GoneMs = v);

            Head("DISTANCES");

            Slide("How far out a death is noticed",
                  "The sweep runs over this every three-quarters of a second, so it is a real cost.",
                  "Range", "NoticeRange", 10f, 200f, 5f,
                  () => _cfg.NoticeRange, v => _cfg.NoticeRange = v);

            Slide("How far off it starts", "Far enough to be a drive, near enough to arrive.",
                  "Range", "ComeFrom", 30f, 400f, 10f,
                  () => _cfg.ComeFrom, v => _cfg.ComeFrom = v);

            Slide("And how much that varies", "So it does not come from the same corner every time.",
                  "Range", "ComeSpread", 0f, 300f, 10f,
                  () => _cfg.ComeSpread, v => _cfg.ComeSpread = v);

            Slide("Close enough to have arrived", "Where the siren goes off and the crew get out.",
                  "Range", "ThereRange", 6f, 80f, 1f,
                  () => _cfg.ThereRange, v => _cfg.ThereRange = v);

            Slide("Close enough to kneel at him", "Too tight and they stand there; too loose and they work on the road.",
                  "Range", "KneelRange", 1f, 8f, 0.2f,
                  () => _cfg.KneelRange, v => _cfg.KneelRange = v);

            Slide("Drive away and it is let go",
                  "Holding a van, two crew and a trolley three streets away is the leak this set keeps finding.",
                  "Range", "LetGoRange", 80f, 1000f, 20f,
                  () => _cfg.LetGoRange, v => _cfg.LetGoRange = v);

            Slide("What counts as the same scene",
                  "Also the answer Five0 Patrol gets when it asks whether its officers should still be waiting.",
                  "Range", "SameScene", 10f, 200f, 5f,
                  () => _cfg.SameScene, v => _cfg.SameScene = v);

            Slide("Near enough to be told", "How close you must be for the mod to say anything. 0 is never.",
                  "Range", "TellRange", 0f, 300f, 10f,
                  () => _cfg.TellRange, v => _cfg.TellRange = v);

            Slide("Close enough to the hospital", "Where the drive ends and everything is handed back.",
                  "Range", "ArrivedRange", 10f, 150f, 5f,
                  () => _cfg.ArrivedRange, v => _cfg.ArrivedRange = v);

            Slide("How fast it drives", "Lights and siren on the way in, quietly on the way out.",
                  "Range", "Speed", 5f, 45f, 1f,
                  () => _cfg.Speed, v => _cfg.Speed = v);

            Head("THE FIT  --  WATCH THE BODY WHILE YOU MOVE THESE");

            Slide("Body on trolley, sideways",
                  "These are the one part of this mod that could not be measured. Aim them by looking.",
                  Fit, "BodyOnTrolleyX", -5f, 5f, 0.02f,
                  () => _cfg.BodyOnTrolleyX, v => _cfg.BodyOnTrolleyX = v);

            Slide("Body on trolley, along",
                  "Forward and back down the canvas. Positive is towards the head end.",
                  Fit, "BodyOnTrolleyY", -5f, 5f, 0.02f,
                  () => _cfg.BodyOnTrolleyY, v => _cfg.BodyOnTrolleyY = v);

            Slide("Body on trolley, height",
                  "The one that matters. Too low and he is inside the canvas; too high and he floats.",
                  Fit, "BodyOnTrolleyZ", -5f, 5f, 0.02f,
                  () => _cfg.BodyOnTrolleyZ, v => _cfg.BodyOnTrolleyZ = v);

            Slide("Body on trolley, turn",
                  "Which way he lies. 90 puts him along the trolley rather than across it.",
                  Fit, "BodyOnTrolleyYaw", -360f, 360f, 5f,
                  () => _cfg.BodyOnTrolleyYaw, v => _cfg.BodyOnTrolleyYaw = v);

            Slide("Trolley from medic, sideways", "Only needed if it clips his arm on one side.",
                  Fit, "TrolleyPushX", -5f, 5f, 0.02f,
                  () => _cfg.TrolleyPushX, v => _cfg.TrolleyPushX = v);

            Slide("Trolley from medic, in front", "How far out ahead of him it rides.",
                  Fit, "TrolleyPushY", -5f, 5f, 0.02f,
                  () => _cfg.TrolleyPushY, v => _cfg.TrolleyPushY = v);

            Slide("Trolley from medic, height",
                  "Negative, because it hangs off him at the root and the wheels belong on the road.",
                  Fit, "TrolleyPushZ", -5f, 5f, 0.02f,
                  () => _cfg.TrolleyPushZ, v => _cfg.TrolleyPushZ = v);

            Slide("Trolley in van, sideways", "Centre it in the back.",
                  Fit, "TrolleyInVanX", -5f, 5f, 0.02f,
                  () => _cfg.TrolleyInVanX, v => _cfg.TrolleyInVanX = v);

            Slide("Trolley in van, along", "Negative is towards the back doors.",
                  Fit, "TrolleyInVanY", -5f, 5f, 0.02f,
                  () => _cfg.TrolleyInVanY, v => _cfg.TrolleyInVanY = v);

            Slide("Trolley in van, height", "Off the floor of the load bay.",
                  Fit, "TrolleyInVanZ", -5f, 5f, 0.02f,
                  () => _cfg.TrolleyInVanZ, v => _cfg.TrolleyInVanZ = v);

            Slide("Carried body, sideways",
                  "Only used where NEITHER gurney prop exists. Both came in recent updates.",
                  Fit, "CarryX", -5f, 5f, 0.02f,
                  () => _cfg.CarryX, v => _cfg.CarryX = v);

            Slide("Carried body, in front", "How far off his chest the body rides.",
                  Fit, "CarryY", -5f, 5f, 0.02f,
                  () => _cfg.CarryY, v => _cfg.CarryY = v);

            Slide("Carried body, height", "Shoulder height, roughly.",
                  Fit, "CarryZ", -5f, 5f, 0.02f,
                  () => _cfg.CarryZ, v => _cfg.CarryZ = v);

            Slide("Carried body, turn", "Across the shoulders rather than in line with him.",
                  Fit, "CarryYaw", -360f, 360f, 5f,
                  () => _cfg.CarryYaw, v => _cfg.CarryYaw = v);

            Slide("Body in van, sideways", "Also only used when there is no trolley under him.",
                  Fit, "BodyInVanX", -5f, 5f, 0.02f,
                  () => _cfg.BodyInVanX, v => _cfg.BodyInVanX = v);

            Slide("Body in van, along", "Negative is towards the back doors.",
                  Fit, "BodyInVanY", -5f, 5f, 0.02f,
                  () => _cfg.BodyInVanY, v => _cfg.BodyInVanY = v);

            Slide("Body in van, height", "Off the floor of the load bay.",
                  Fit, "BodyInVanZ", -5f, 5f, 0.02f,
                  () => _cfg.BodyInVanZ, v => _cfg.BodyInVanZ = v);

            Slide("Body in van, turn", "Which way he lies in the back.",
                  Fit, "BodyInVanYaw", -360f, 360f, 5f,
                  () => _cfg.BodyInVanYaw, v => _cfg.BodyInVanYaw = v);
        }

        // ---- the builders -------------------------------------------------------

        private void Head(string label)
        {
            _rows.Add(new Knob { Label = label });
        }

        private void Toggle(string label, string detail, string section, string key,
                            Func<bool> get, Action<bool> set)
        {
            var knob = new Knob
            {
                Label = label,
                Detail = detail,
                Section = section,
                Key = key,
                Read = () => get() ? "ON" : "OFF",
                Ini = () => get() ? "true" : "false",
                Fill = () => -1f,
            };

            // EITHER DIRECTION FLIPS IT. Left-for-off and right-for-on is tidier in theory and
            // in practice means pressing left on something already off does nothing, which
            // reads as the menu having frozen.
            knob.Nudge = d => { set(!get()); Touch(knob); };

            _rows.Add(knob);
        }

        private void Pick(string label, string detail, string section, string key,
                          string[] names, Func<int> get, Action<int> set)
        {
            var knob = new Knob
            {
                Label = label,
                Detail = detail,
                Section = section,
                Key = key,
                Read = () => names[Wrap(get(), names.Length)].ToUpperInvariant(),
                Ini = () => names[Wrap(get(), names.Length)],
                Fill = () => -1f,
            };

            knob.Nudge = d => { set(Wrap(get() + d, names.Length)); Touch(knob); };

            _rows.Add(knob);
        }

        private void Slide(string label, string detail, string section, string key,
                           float min, float max, float step,
                           Func<float> get, Action<float> set)
        {
            var knob = new Knob
            {
                Label = label,
                Detail = detail,
                Section = section,
                Key = key,
                Read = () => get().ToString(step < 0.05f ? "0.00" : "0.0#",
                                            CultureInfo.InvariantCulture),
                Ini = () => get().ToString("0.###", CultureInfo.InvariantCulture),
                Fill = () => (get() - min) / (max - min),
            };

            knob.Nudge = d =>
            {
                var v = get() + step * d;

                // ROUNDED TO THE STEP, because floating point does not land on it. Twenty
                // presses of 0.02 from zero arrives at 0.40000002, which the ini then records
                // verbatim and the menu displays as a number nobody chose.
                v = (float)(Math.Round(v / step) * step);

                if (v < min) v = min;
                if (v > max) v = max;

                set(v);
                Touch(knob);
            };

            _rows.Add(knob);
        }

        /// <summary>
        /// A duration, shown in seconds and stored in milliseconds.
        ///
        /// NAMED RATHER THAN A THIRD Slide OVERLOAD. An int lambda converts to Func&lt;float&gt;
        /// implicitly, so `Slide(..., 1000, 120000, 500, () =&gt; _cfg.WorkMs, ...)` is genuinely
        /// ambiguous between the float and int forms and the compiler is right to say so. A
        /// different name is clearer than winning that argument with a cast.
        ///
        /// AND IT READS IN SECONDS, because milliseconds are how the engine counts and seconds
        /// are how a person decides whether a scene is too long. 17000 tells you nothing; 17.0s
        /// tells you it is about right.
        /// </summary>
        private void Millis(string label, string detail, string section, string key,
                            int min, int max, int step, Func<int> get, Action<int> set)
        {
            var knob = new Knob
            {
                Label = label,
                Detail = detail,
                Section = section,
                Key = key,
                Read = () => (get() / 1000f).ToString("0.0",
                                                      CultureInfo.InvariantCulture) + "s",
                Ini = () => get().ToString(CultureInfo.InvariantCulture),
                Fill = () => max == min ? 0f : (float)(get() - min) / (max - min),
            };

            knob.Nudge = d =>
            {
                var v = get() + step * d;

                if (v < min) v = min;
                if (v > max) v = max;

                set(v);
                Touch(knob);
            };

            _rows.Add(knob);
        }

        /// <summary>
        /// Remembers a row was changed, and re-fits the scene when it was a [Fit] row.
        ///
        /// DONE HERE RATHER THAN AT EACH OF THE EIGHTEEN CALL SITES. Every fit row would
        /// otherwise need to remember to ask, and the one that forgot would be the one that
        /// looked broken -- a slider that moves a number and nothing else.
        /// </summary>
        private void Touch(Knob knob)
        {
            _dirty.Add(knob);

            if (knob.Section == Fit && Refit != null) Refit();
        }

        private static int Wrap(int i, int n)
        {
            if (n <= 0) return 0;

            return ((i % n) + n) % n;
        }

        // ---- keys ---------------------------------------------------------------

        /// <summary>
        /// One key press, from Main's KeyDown.
        ///
        /// THE KEYBOARD EVENT RATHER THAN IsControlJustPressed, because a menu wants keys the
        /// game has no control for -- Escape, the arrows as arrows -- and because KeyDown gives
        /// real edge detection for nothing. Polling a control needs a frame-perfect tick.
        /// </summary>
        public void Key(Keys key, Keys mods)
        {
            try
            {
                if (Chord(key, mods)) { Toggle(); return; }

                if (!Open) return;

                switch (key)
                {
                    case Keys.Escape:
                    case Keys.Back:
                        Toggle();
                        return;

                    case Keys.Up:
                    case Keys.NumPad8:
                        _at = Next(_at - 1, -1);
                        Show();
                        return;

                    case Keys.Down:
                    case Keys.NumPad2:
                        _at = Next(_at + 1, 1);
                        Show();
                        return;

                    case Keys.Left:
                    case Keys.NumPad4:
                        Move(-1);
                        return;

                    case Keys.Right:
                    case Keys.NumPad6:
                    case Keys.Enter:
                        Move(1);
                        return;
                }
            }
            catch (Exception ex)
            {
                Log.Debug("Could not read a menu key: " + ex.Message);
            }
        }

        /// <summary>
        /// Whether this press is the menu chord.
        ///
        /// THE MODIFIER IS COMPARED EXACTLY RATHER THAN TESTED FOR PRESENCE. `mods.HasFlag`
        /// would make a Shift binding fire on Ctrl+Shift and Alt+Shift as well -- which are
        /// other mods' chords, and stealing them is precisely the failure the key audit in
        /// Settings.MenuKey exists to avoid. Masked to the three real modifiers first, because
        /// Keys.Modifiers also carries bits we have no opinion about.
        /// </summary>
        private bool Chord(Keys key, Keys mods)
        {
            if (key != _cfg.MenuKey) return false;

            const Keys Three = Keys.Shift | Keys.Control | Keys.Alt;

            return (mods & Three) == (_cfg.MenuModifier & Three);
        }

        /// <summary>
        /// Every frame, open or not.
        ///
        /// WHILE IT IS OPEN, the game's controls are off -- otherwise the arrows still steer,
        /// Enter still answers the phone, and closing the menu leaves you somewhere else facing
        /// the wrong way.
        ///
        /// WHILE IT IS SHUT, this does nothing at all unless the menu key is P. That case earns
        /// its own branch because P IS GTA's pause control, and SHVDN's key event does not
        /// consume the press -- so Shift+P would open this menu with the pause screen on top of
        /// it. It cannot be fixed inside the key handler, because by the time KeyDown runs we
        /// are already in the frame the game reads that control in.
        ///
        /// The default is F9 and needs none of this. It is kept for anybody who rebinds to P.
        /// </summary>
        public void Frame()
        {
            try
            {
                if (Open)
                {
                    // 2 is "all but a few essentials". It does not cover the frontend group, so
                    // the pause controls are named separately.
                    Function.Call(Hash.DISABLE_ALL_CONTROL_ACTIONS, 2);
                    Hush();
                    return;
                }

                if (_cfg.MenuKey != Keys.P) return;

                if (Held(_cfg.MenuModifier)) Hush();
            }
            catch
            {
                // One frame of a control not being suppressed. Never worth more than that.
            }
        }

        private static void Hush()
        {
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0, (int)GTA.Control.FrontendPause, true);
            Function.Call(Hash.DISABLE_CONTROL_ACTION, 0,
                          (int)GTA.Control.FrontendPauseAlternate, true);
        }

        /// <summary>Whether the chord's modifier is currently down. None is never held.</summary>
        private static bool Held(Keys modifier)
        {
            if ((modifier & (Keys.Shift | Keys.Control | Keys.Alt)) == 0) return false;

            if ((modifier & Keys.Shift) != 0 && Game.IsKeyPressed(Keys.ShiftKey)) return true;
            if ((modifier & Keys.Control) != 0 && Game.IsKeyPressed(Keys.ControlKey)) return true;
            if ((modifier & Keys.Alt) != 0 && Game.IsKeyPressed(Keys.Menu)) return true;

            return false;
        }

        private void Move(int by)
        {
            if (_at < 0 || _at >= _rows.Count) return;

            var row = _rows[_at];

            if (row.Nudge != null) row.Nudge(by);
        }

        /// <summary>
        /// The next row that is not a heading, in the given direction, wrapping.
        ///
        /// The loop is bounded by the row count rather than by finding one, because a list that
        /// somehow contained only headings would otherwise spin forever inside a key press.
        /// </summary>
        private int Next(int from, int dir)
        {
            var n = _rows.Count;
            if (n == 0) return 0;

            var i = ((from % n) + n) % n;

            for (var tries = 0; tries < n; tries++)
            {
                if (!_rows[i].IsHeading) return i;

                i = ((i + dir) % n + n) % n;
            }

            return 0;
        }

        /// <summary>Keeps the selection inside the window, scrolling by as little as possible.</summary>
        private void Show()
        {
            if (_at < _top) _top = _at;
            if (_at >= _top + Window) _top = _at - Window + 1;

            // A HEADING ABOVE THE SELECTION IS WORTH A ROW. Scrolling so the selected line is
            // the top one hides which section it belongs to, which is most of what the headings
            // are for.
            if (_top > 0 && _at == _top && _rows[_top - 1].IsHeading) _top--;

            if (_top < 0) _top = 0;
            if (_top > _rows.Count - Window) _top = Math.Max(0, _rows.Count - Window);
        }

        public void Toggle()
        {
            Open = !Open;

            if (Open)
            {
                _arrive.Reset();
                Show();
                return;
            }

            Save();
        }

        /// <summary>
        /// Writes back what changed.
        ///
        /// Failures are counted rather than thrown: a read-only ini -- the game folder is under
        /// Program Files, which an unelevated process cannot write to -- must not lose the
        /// settings for THIS session, which are already applied and working. It says so instead.
        /// </summary>
        private void Save()
        {
            if (_dirty.Count == 0) return;

            var wrote = 0;
            var failed = 0;

            foreach (var knob in _dirty)
            {
                if (IniFile.SetValue(Paths.Ini, knob.Section, knob.Key, knob.Ini())) wrote++;
                else failed++;
            }

            _dirty.Clear();

            if (Say == null) return;

            Say(failed == 0
                ? "Saved " + wrote + (wrote == 1 ? " setting." : " settings.")
                : "Applied for this session, but " + failed + " could not be written to the ini.");
        }

        /// <summary>How much taller the foot of the panel gets for a description that wraps.</summary>
        private const float DetailStep = 0.55f;

        // ---- drawing -------------------------------------------------------------

        public void Draw()
        {
            if (!Open) return;

            try
            {
                var arrive = _arrive.To(1f, Arrive);
                var a = (int)(arrive * 255f);

                var shown = Math.Min(Window, _rows.Count);

                var here = _at >= 0 && _at < _rows.Count ? _rows[_at] : null;

                // ROOM FOR THE SECOND LINE, WHEN THERE IS GOING TO BE ONE. Descriptions wrap
                // against the panel, so a long one takes two lines and would otherwise be drawn
                // straight through the key hints underneath. Measured rather than guessed, so it
                // is the same answer the text command is about to give when it draws.
                var deep = here != null &&
                           Screen.Wide(here.Detail, DetailScale) > Width - Pad * 2f;

                var foot = deep ? 1.4f + DetailStep : 1.4f;

                var tall = Pad + RowH * 1.5f + RowH * shown + RowH * foot + Pad;

                var left = 0.5f - Width * 0.5f;
                var top = 0.5f - tall * 0.5f + (1f - arrive) * Rise;

                Screen.Rect(0.5f, top + tall * 0.5f, Width, tall,
                            Palette.Alpha(Palette.Panel, a));

                Screen.Rect(0.5f, top + 0.0014f, Width, 0.0028f,
                            Palette.Alpha(Palette.Brand, a));

                var y = top + Pad;

                Screen.Text("FLATLINE", left + Pad, y - 0.006f, TitleScale,
                            Palette.Alpha(Palette.Brand, a));

                // WHAT THE CREW ARE DOING, WHERE THE VERSION WOULD OTHERWISE GO.
                //
                // The version is in the log and on the load row and nobody has ever needed it
                // from here. What somebody adjusting the fit DOES need is to know whether the
                // trolley is out yet -- because the menu has the controls disabled, so the only
                // other way to find out is to close it and look.
                var scene = Scene == null ? null : Scene();

                Screen.Text(string.IsNullOrEmpty(scene) ? Build.Version : scene,
                            left + Width - Pad, y - 0.006f, DetailScale,
                            Palette.Alpha(
                                string.IsNullOrEmpty(scene) ? Palette.TextDim
                                    : Hopeful != null && Hopeful() ? Palette.Good : Palette.Brand,
                                (int)(arrive * (string.IsNullOrEmpty(scene) ? 150f : 220f))),
                            rightAligned: true);

                y += RowH * 1.4f;

                for (var i = 0; i < shown; i++)
                {
                    var index = _top + i;
                    if (index >= _rows.Count) break;

                    Row(_rows[index], index == _at, left, y, arrive);

                    y += RowH;
                }

                y += RowH * 0.25f;

                if (here != null && !string.IsNullOrEmpty(here.Detail))
                {
                    // WRAPPED AGAINST THE PANEL, NOT AGAINST THE MONITOR. Without a window this
                    // gets the default one Screen.Text gives a plain line -- from x to the right
                    // edge of the SCREEN -- so a description longer than the panel is wide keeps
                    // going: out through the side of the menu and across the street behind it.
                    // Half these lines are that long, because the point of them is to say what a
                    // setting COSTS rather than to restate its name.
                    Screen.Text(here.Detail, left + Pad, y, DetailScale,
                                Palette.Alpha(Palette.TextDim, (int)(arrive * 190f)),
                                wrapTo: left + Width - Pad);
                }

                Screen.Text("[UP/DOWN] PICK   [LEFT/RIGHT] CHANGE   [" + Chord() + "] CLOSE",
                            left + Pad, y + RowH * (deep ? 0.75f + DetailStep : 0.75f), DetailScale,
                            Palette.Alpha(Palette.Brand, (int)(arrive * 155f)));
            }
            catch (Exception ex)
            {
                Log.Debug("Could not draw the menu: " + ex.Message);
            }
        }

        /// <summary>The chord as a player would write it, for the hint line.</summary>
        private string Chord()
        {
            var mod = _cfg.MenuModifier;

            var name = (mod & Keys.Shift) != 0 ? "SHIFT+"
                     : (mod & Keys.Control) != 0 ? "CTRL+"
                     : (mod & Keys.Alt) != 0 ? "ALT+"
                     : string.Empty;

            return name + _cfg.MenuKey.ToString().ToUpperInvariant();
        }

        private void Row(Knob row, bool on, float left, float y, float arrive)
        {
            var a = (int)(arrive * 255f);

            if (row.IsHeading)
            {
                Screen.Text(row.Label, left + Pad, y + RowH * 0.2f, DetailScale,
                            Palette.Alpha(Palette.Brand, (int)(arrive * 175f)));
                return;
            }

            if (on)
            {
                Screen.Rect(0.5f, y + RowH * 0.5f, Width, RowH,
                            Palette.Alpha(Palette.BrandDeep, (int)(arrive * 165f)));

                Screen.Rect(left + 0.0015f, y + RowH * 0.5f, 0.003f, RowH,
                            Palette.Alpha(Palette.Brand, a));
            }

            Screen.Text(row.Label, left + Pad * 1.7f, y + RowH * 0.19f, RowScale,
                        Palette.Alpha(on ? Palette.Text : Palette.TextDim,
                                      (int)(arrive * (on ? 255f : 170f))));

            var fill = row.Fill();

            // THE BAR SITS LEFT OF THE NUMBER, NOT BEHIND IT. Behind, the number has to stay
            // readable over both the filled and the empty half, which means it can only be a
            // colour that contrasts with everything -- and there is no such colour.
            if (fill >= 0f)
            {
                if (fill < 0f) fill = 0f;
                if (fill > 1f) fill = 1f;

                var barX = left + Width - Pad - 0.052f - BarW;

                Screen.Rect(barX + BarW * 0.5f, y + RowH * 0.5f, BarW, BarH,
                            Palette.Alpha(Palette.Track, (int)(arrive * 210f)));

                if (fill > 0f)
                {
                    Screen.Rect(barX + BarW * fill * 0.5f, y + RowH * 0.5f,
                                BarW * fill, BarH,
                                Palette.Alpha(on ? Palette.Brand : Palette.BrandDeep, a));
                }
            }

            var value = row.Read();

            // ON is the brand colour and OFF is dim, so the state of every switch on screen can
            // be read without reading any of the words.
            var lit = value == "ON" ? Palette.Brand
                    : value == "OFF" ? Palette.TextDim
                    : on ? Palette.Text : Palette.TextDim;

            Screen.Text(value, left + Width - Pad, y + RowH * 0.19f, RowScale,
                        Palette.Alpha(lit, (int)(arrive * (on ? 255f : 185f))),
                        rightAligned: true);
        }
    }
}
