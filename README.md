# Flatline

Paramedics who actually do something, for GTA V.

The game already sends an ambulance to a body. It arrives with its lights on, two paramedics
get out, they walk over, they stand there, they get back in, and they drive away — and the body
is still lying in the road behind them. Whatever those crews were meant to be for, they are not
for that, and the effect of watching it twice is that you stop looking at ambulances at all.

Flatline is the rest of that call-out. They kneel and they work on him, and then one of two
things happens, decided by what actually killed him:

- **A beating, a bat, a knife, a taser.** They get him back. He comes round, gets up, and walks
  off with a third of his health.
- **A gun, a car, a fire, an explosion, a long drop.** They stop. One of them fetches the
  trolley, they load him onto it, wheel him back to the van, put him in the back, shut the
  doors and drive him to the nearest hospital.

Pure ScriptHookVDotNet: one dll, one ini, two PNGs. **No game file is modified**, no RPF edits,
no asset replacement, no dependencies beyond ScriptHookVDotNet itself. One build runs on both
GTA V editions.

---

## What it does

**Decides on the death, not on a dice roll.** [Cause.cs](src/Flatline/Core/Cause.cs) reads
`GET_PED_CAUSE_OF_DEATH`, which the engine fills in the moment a ped dies. Fists and melee
weapons are workable; guns, vehicles, fire, explosions, drowning and falls are not. A mod that
resuscitates a headshot and a fist fight at the same rate has not said anything — it has put an
animation in front of a coin toss.

**Uses the game's own CPR animations.** `mini@cpr@char_a@cpr_str` — `cpr_pumpchest`,
`cpr_success`, `cpr_fail`. Every dictionary and clip pair was checked against the game's own
dump of what is in this build before it was written down, per the rule in
Hoodrich's `ANIMS.md`. `TASK_PLAY_ANIM` returns void: a clip name that is
not in its dictionary is accepted and nothing moves, with no error and no log line.

**Uses a real gurney.** `m25_2_prop_m52_gurney_01a`, with
`m23_2_prop_m32_lgstretcher_01a` as the fallback. Both are genuine props that shipped with
recent updates, which is why nobody uses them — every other EMS mod for this game ships a
*replacement* model, usually by overwriting an unrelated prop (the popular pack turns
`prop_ld_binbag_01`, a bin bag, into a stretcher). That needs an RPF edit. This does not. On an
install with neither prop, the crew carry him instead.

**Finds the hospital by asking the map.** [Hospitals.cs](src/Flatline/Scene/Hospitals.cs)
iterates the blips the game has already placed — sprite 61, `radar_hospital` — rather than
carrying a table of coordinates that cannot know about a hospital added in a DLC. Central Los
Santos Medical is the backstop if the iteration finds nothing.

**Only answers deaths somebody saw.** [Watch.cs](src/Flatline/Scene/Watch.cs) watches the
*living*: a ped has to have been seen standing before its death counts. Sweeping for corpses
instead would dispatch an ambulance to every body in a street where a gang fight finished ten
minutes before you arrived, and then another, and another.

**Turns off the game's own ambulance dispatch** so two do not turn up at one junction — and
turns it back on when the mod unloads, so this can never leave you with a city that has no
ambulances at all.

---

## Working with the other mods

**Five0 Patrol** already had an ambulance of its own: an officer stands over a body, calls it
in, a van turns up, the crew kneel for twenty-six seconds and leave him where he is. That is
exactly what this mod replaces, so the two would otherwise put two vans at one junction.

Flatline publishes [`Flatline.Api.Medics`](src/Flatline/Api/Medics.cs) and Five0 Patrol's
[`Core/Ambo.cs`](https://github.com/defthrets/five0patrol/blob/main/src/Five0Patrol/Core/Ambo.cs)
reads it by reflection — the same
late-bound pattern that mod already uses against `Hoodrich.Api.Block` and `Hoodrich.Api.Corpse`.
When Flatline is installed **and switched on**, Five0 Patrol stands its own ambulance down.

The one beat worth keeping is kept: Five0 Patrol's officers wait at a body until the ambulance
has left, and rather than losing that, `Ambo.Still()` asks Flatline the same question its own
`Medics.At()` used to answer. The police behave exactly as they did.

It fails open in both directions. Without Flatline, Five0 Patrol keeps its own ambulance and
nothing changes. Without Five0 Patrol, Flatline neither knows nor cares.

**Bloody Mess** is untouched and complementary — it is what makes the body worth sending
somebody to. **Hoodrich** and **Bare Minimum** do not overlap with this at all.

The one thing to know: `TakeToHospital` is the half of Flatline that *moves other people's
corpses*. If another mod wants a body where it fell, turn it off in the ini and keep the
resuscitation, which is most of the value.

---

## The player

Flatline does not touch your own death, deliberately. The vanilla Wasted flow and Hoodrich's
hospital bill both already own that moment, and three systems answering one death is how you
get a mod that argues with the game about what just happened to you.

---

## Settings

**Press Shift+H.** Every setting is on one screen: arrows to pick and change, Shift+H or Escape
to close. Changes apply the instant you make them and are written back to `Flatline.ini` when
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

**The `[Fit]` rows are why the screen exists.** Everything else in this mod was verified before
it shipped — the clips exist, the props exist, the damage hashes were computed and
cross-checked. But a prop's origin is wherever the artist put it, and there is no way to
measure one from outside the running game, so those offsets are considered guesses. The only
way to improve a guess is to look at it.

Moving one of those rows **re-attaches the body on the same frame**, so you hold Right and watch
him settle onto the canvas. Without that they would only affect the *next* call-out, which is
the alt-tab-and-restart loop again wearing a different hat. The corner of the panel shows what
the crew are currently doing — *fetching the trolley*, *loading him*, *driving to the hospital* —
because the menu disables the game's controls, so otherwise the only way to know whether the
trolley is out yet is to close it and go and look.

`Flatline.ini` is never overwritten by an update: a deploy adds options that are missing, with
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
borrowed from `..\hoodrich\tools`.

Hot swapping is safe here specifically because the `Aborted` handler hands everything back
before the reload: the dispatch service goes back on, and the van, the crew, the trolley and any
body welded to it are all released.
