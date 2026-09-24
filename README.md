# Code Three

Paramedics who actually do something, for GTA V.

The game already sends an ambulance to a body. It arrives with its lights on, two paramedics
get out, they walk over, they stand there, they get back in, and they drive away — and the body
is still lying in the road behind them. Whatever those crews were meant to be for, they are not
for that, and the effect of watching it twice is that you stop looking at ambulances at all.

Code Three is the rest of that call-out. They kneel and they work on him, and then one of two
things happens, decided by what actually killed him:

- **A beating, a bat, a knife, a taser.** They get him back. He comes round sat on the road,
  the medic hauls him to his feet, and he limps off with a third of his health.
- **A gun, a car, a fire, an explosion, a long drop.** They stop. The second man stands over him
  and writes down the time. Then they fetch the trolley, load him onto it, wheel him to the
  van, put him in the back, shut the doors and drive him to the nearest hospital.

Pure ScriptHookVDotNet: one dll, one ini, two PNGs. **No game file is modified**, no RPF edits,
no asset replacement, no dependencies beyond ScriptHookVDotNet itself. One build runs on both
GTA V editions.

---

## What it does

**Decides on the death, not on a dice roll.** [Cause.cs](src/CodeThree/Core/Cause.cs) reads
`GET_PED_CAUSE_OF_DEATH`, which the engine fills in the moment a ped dies. Fists and melee
weapons are workable; guns, vehicles, fire, explosions, drowning and falls are not. A mod that
resuscitates a headshot and a fist fight at the same rate has not said anything — it has put an
animation in front of a coin toss.

**The patient is a participant, not a prop.** A corpse is a ragdoll and cannot be animated, so
the first version of this played half a two-hander over a heap. Now, when the crew reach him,
he is brought back *into arrest* — alive in the engine's eyes, unconscious in everybody else's,
un-targetable, unable to fall — and from then on he is one of two people in a **synchronised
scene**, placed by the animation data rather than by the mod. The hands land on his sternum and
his chest goes with them, because the game authored both halves of every clip around one origin.
See [Sync.cs](src/CodeThree/Core/Sync.cs).

**Plays the whole sequence the game authored.** `mini@cpr` has seven clips in matched pairs and
they are a story: down to a knee, a look at him, the lean in, the compressions, sitting back,
another look — then either `cpr_success` or `cpr_fail`. A man with a chance gets two rounds; a
man without gets one. It advances on each clip finishing, not on a stopwatch. Every dictionary
and clip was checked against the game's own dump before it was written down, per the rule in
Hoodrich's `ANIMS.md`. `TASK_PLAY_ANIM` returns void: a wrong name is accepted and nothing
moves, with no error and no log line.

**Gets him to his feet.** `random@crash_rescue@help_victim_up` is the roadside random event's
pair of clips for hauling somebody upright — the beat `mini@cpr` has no clip for. Then
`move_m@injured` / `move_f@injured` as his movement clipset, so he limps from then on.

**The second man has a job.** He carries the bag — `prop_med_bag_01`, the red one from the back
of every ambulance interior — sets it down beside the patient, and kneels opposite in
`CODE_HUMAN_MEDIC_TEND_TO_DEAD`, the vanilla paramedic's own scenario. When they lose him, he
stands and does `CODE_HUMAN_MEDIC_TIME_OF_DEATH` — the clipboard.

**Lays him out flat.** Once he has been through the scene he is alive and animatable, and the
game ships eight lying-dead poses. He holds `dead_a` and the trolley picks him up in it, so the
attachment offsets are tuned against one shape that never varies — instead of against however
each man happened to fall, which was what made them unverifiable. He is killed again, properly,
the moment the call-out releases him, so no other mod ever meets a corpse that stood up.

**Uses a real gurney.** `m25_2_prop_m52_gurney_01a`, with `m23_2_prop_m32_lgstretcher_01a` as
the fallback. Both are genuine props that shipped with recent updates, which is why nobody uses
them — every other EMS mod for this game ships a *replacement* model, usually by overwriting an
unrelated prop (the popular pack turns `prop_ld_binbag_01`, a bin bag, into a stretcher). That
needs an RPF edit. This does not. On an install with neither prop, the crew carry him.

**Backs off if you shoot the patient.** He is alive during the CPR, so you can kill him again.
If you do, the crew play the scenario's own `exit_flee` and leave.

**Finds the hospital by asking the map.** [Hospitals.cs](src/CodeThree/Scene/Hospitals.cs)
iterates the blips the game has already placed — sprite 61, `radar_hospital` — rather than
carrying a table of coordinates that cannot know about a hospital added in a DLC.

**Only answers deaths somebody saw.** [Watch.cs](src/CodeThree/Scene/Watch.cs) watches the
*living*: a ped has to have been seen standing before its death counts. Sweeping for corpses
instead would dispatch an ambulance to every body in a street where a gang fight finished ten
minutes before you arrived. The body is held from the moment of dispatch, so the engine cannot
tidy it away before the van gets there.

**Turns off the game's own ambulance dispatch** so two do not turn up at one junction — and
turns it back on when the mod unloads.

---

## Working with the other mods

**Five0 Patrol** already had an ambulance of its own: an officer stands over a body, calls it
in, a van turns up, the crew kneel for twenty-six seconds and leave him where he is. That is
exactly what this mod replaces, so the two would otherwise put two vans at one junction.

Code Three publishes [`CodeThree.Api.Medics`](src/CodeThree/Api/Medics.cs) and Five0 Patrol's
[`Core/Ambo.cs`](https://github.com/defthrets/five0patrol/blob/main/src/Five0Patrol/Core/Ambo.cs)
reads it by reflection — the same late-bound pattern that mod already uses against
`Hoodrich.Api.Block` and `Hoodrich.Api.Corpse`. When Code Three is installed **and switched on**,
Five0 Patrol stands its own ambulance down.

The one beat worth keeping is kept: Five0 Patrol's officers wait at a body until the ambulance
has left, and rather than losing that, `Ambo.Still()` asks Code Three the same question its own
`Medics.At()` used to answer. The police behave exactly as they did.

It fails open in both directions. Without Code Three, Five0 Patrol keeps its own ambulance and
nothing changes. Without Five0 Patrol, Code Three neither knows nor cares.

**One thing to know about the resurrection.** For the length of the scene — roughly half a
minute, from the crew kneeling to the trolley going in the back — the patient is alive in the
engine. Five0 Patrol's `Bodies` will not count him as a corpse during that window, and Bloody
Mess will stop pooling under him. Both resume the moment he is killed again on release. That
window starts when the crew *reach* him, not when he dies, so the corpse is an ordinary corpse
for the whole minute the van takes to arrive.

`TakeToHospital` is the half of Code Three that *moves other people's corpses*. If another mod
wants a body where it fell, turn it off and keep the resuscitation, which is most of the value.

---

## The player

Code Three does not touch your own death, deliberately. The vanilla Wasted flow and Hoodrich's
hospital bill both already own that moment, and three systems answering one death is how you
get a mod that argues with the game about what just happened to you.

---

## Seeing it

Open the settings screen (**Shift+H**) and the top two rows stage a call-out in front of you:
**a beating** (they bring him round) or **a shooting** (CPR, the clipboard, the trolley, the back
of the van). A man goes down four metres ahead and a van comes in from about forty metres out,
so the whole scene can be watched in a few seconds rather than after a murder and a two-minute
wait.

---

## Settings

**Press Shift+H.** Every setting is on one screen: arrows to pick and change, Shift+H or Escape
to close. Changes apply the instant you make them and are written back to `CodeThree.ini` when
you close — only the lines you actually touched, so the comments explaining what each one is
*for* survive.

Not a function key, and that was learned rather than assumed. This shipped on F9, chosen by
scanning every ini in the scripts folder for a binding nothing had claimed. F9 came back clean
and was taken anyway — nothing in that folder claims it in a config file even now, so whatever
owns it is hardcoded in another mod's DLL, an ASI with no ini, or something outside the game
entirely. ShadowPlay, Afterburner, Steam and Discord all default to keys in that row.

Which means a scan can't clear F7 or F8 either, and the rest of the row is gone: F1 FranklinRP,
F2 Hoodrich, F3 Street Golf, F5 Vehicle Tweaks, F6 Weapon Tweaks, F10 Five0 Patrol, F11 and F12
Bare Minimum. F4 is ScriptHookVDotNet's own console. So this sits with Bloody Mess (Shift+B) and
Fumes (Shift+F) instead — and `H` is claimed by nothing in the folder, bare *or* chorded, which
matters because a mod bound to a bare key that ignores modifiers will also fire on Shift plus
that key.

Rebind it in the ini if it still clashes — it is the one setting deliberately *not* on the menu,
because a key you rebind from a screen you need that key to open is one wrong press from being
unreachable.

**The `[Fit]` rows are why the screen exists.** A prop's origin is wherever the artist put it,
and there is no way to measure one from outside the running game, so the trolley offsets are
considered guesses. Moving one of those rows **re-attaches the body on the same frame**, so you
hold Right and watch him settle onto the canvas. The corner of the panel shows what the crew are
doing — *compressions*, *calling it*, *fetching the trolley* — because the menu disables the
game's controls, so otherwise the only way to know whether the trolley is out yet is to close it
and go and look.

`CodeThree.ini` is never overwritten by an update: a deploy adds options that are missing, with
their comments, and leaves everything you have changed alone. Deleting a line is safe; deleting
the whole file is safe.

---

## Building

```
.\build.ps1
.\build.ps1 -Deploy
.\build.ps1 -Deploy -HotSwap     # while the game is running; press Insert
.\build.ps1 -Package
```

Roslyn directly rather than `dotnet build` — the machine SDK is a partial install and every
`dotnet` command dies with `hostpolicy.dll not found`. The toolchain is not in this repo; it is
borrowed from `..\hoodrich\tools`, or pass `-Tools` at your own.

Hot swapping is safe here specifically because the `Aborted` handler hands everything back
before the reload: the dispatch service goes back on; the van, the crew, the trolley and the bag
are released; and a patient still ours is put back to dead first.
