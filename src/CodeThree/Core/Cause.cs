using System;
using GTA;
using GTA.Native;

namespace CodeThree.Core
{
    /// <summary>What the crew decide when they have had a proper look at him.</summary>
    internal enum Verdict
    {
        /// <summary>Worth working on. He comes round.</summary>
        Workable,

        /// <summary>Nothing to be done here. He goes in the back.</summary>
        Gone,
    }

    /// <summary>
    /// What killed him, and whether that is the sort of thing anybody comes back from.
    ///
    /// THIS IS THE ONE DECISION THE WHOLE MOD TURNS ON, so it is made from the engine's own
    /// record rather than from a dice roll. A man beaten down in a fist fight and a man shot
    /// through the head are not the same event, and a mod that resuscitates them at the same
    /// rate has not actually said anything -- it has put an animation in front of a coin toss.
    /// GET_PED_CAUSE_OF_DEATH is filled in the moment a ped dies and names the weapon, so the
    /// answer is sitting there for the asking.
    ///
    /// FISTS AND BLUNT THINGS ARE SURVIVABLE. Guns, cars, fire, explosions and long drops are
    /// not. That is the rule in one line, and everything below is the care needed to apply it
    /// without getting it wrong in the cases where the game does not hand back a weapon at all.
    ///
    /// READ AS UINT, AND VALIDATED. This is Bloody Mess's lesson, paid for once already and not
    /// paid for again: GET_WEAPONTYPE_GROUP returns a joaat HASH, SHVDN's WeaponGroup enum is
    /// UInt32-backed with those same hashes, and several of them are above int.MaxValue. Read
    /// as an int they come back NEGATIVE, and a negative cast straight to the enum is a value
    /// that is not any real weapon group -- which then matches whatever happens to be nearest.
    /// In that mod it handed rifles the stun gun's gore profile. Here it would quietly make
    /// every shotgun death revivable.
    ///
    /// THE DAMAGE CAUSES ARE NOT WEAPONS AND ARE NOT IN THE ENUM. Falling, drowning, being run
    /// over and burning all come back through the same native as things named WEAPON_*, but
    /// they belong to no weapon group -- so they are matched by hash, by name, from the list
    /// below. Every hash there was computed from its own name with joaat and checked against
    /// the two everybody publishes: WEAPON_UNARMED is 0xA2719263 and WEAPON_KNIFE is
    /// 0x99B507EA. Both came out right, so the rest of the column is trustworthy.
    /// </summary>
    internal static class Cause
    {
        // ---- the damage causes that are not weapons ----------------------------
        //
        // Names joaat'd rather than copied off a forum post, so any one of them can be
        // re-derived by somebody who doubts it. See the class note for how they were checked.

        private const uint Unarmed       = 0xA2719263;
        private const uint Fall          = 0xCDC174B0;
        private const uint Drowning      = 0xFF58C4FB;
        private const uint DrowningInCar = 0x736F5990;
        private const uint RammedByCar   = 0x07FC7D7A;
        private const uint RunOverByCar  = 0xA36D413E;
        private const uint Explosion     = 0x2024F4E8;
        private const uint Fire          = 0xDF8E89EB;
        private const uint ElectricFence = 0x92BD4EBB;
        private const uint Exhaustion    = 0x364A29EC;
        private const uint Bleeding      = 0x8B7333FB;
        private const uint Animal        = 0xF9FBAEBE;
        private const uint Cougar        = 0x08D4BE52;
        private const uint BarbedWire    = 0x48E7B178;
        private const uint HeliCrash     = 0x145F1012;
        private const uint WaterCannon   = 0xCC34325E;
        private const uint StunGun       = 0x3656C8C1;

        /// <summary>
        /// Whether this death is one the crew can do anything about.
        ///
        /// ORDER MATTERS AND THE HASHES GO FIRST. A ped run over by a car has a cause that is
        /// in no weapon group at all, so asking the group first gets Unarmed back for it -- and
        /// Unarmed is the most survivable answer there is. Getting that the wrong way round
        /// would mean every hit-and-run in the city ended with the victim standing up, which is
        /// the single most visible way this mod could be wrong.
        /// </summary>
        public static Verdict Read(Ped body, out uint weapon)
        {
            weapon = 0;

            try
            {
                if (body == null || !body.Exists()) return Verdict.Gone;

                weapon = Function.Call<uint>(Hash.GET_PED_CAUSE_OF_DEATH, body.Handle);

                // NOTHING NAMED AT ALL. A body that was already lying there when the mod
                // loaded, or one the engine never attributed. Treated as gone, because making
                // an unknown death revivable is the failure that shows: it turns every corpse
                // in a street the player has not visited into somebody who gets up.
                if (weapon == 0) return Verdict.Gone;

                switch (weapon)
                {
                    // A beating, a shove, a tasering. He is not marked.
                    case Unarmed:
                    case StunGun:
                    case WaterCannon:
                    case Exhaustion:
                        return Verdict.Workable;

                    // And these are not survivable however keen the crew are.
                    case Fall:
                    case Drowning:
                    case DrowningInCar:
                    case RammedByCar:
                    case RunOverByCar:
                    case Explosion:
                    case Fire:
                    case ElectricFence:
                    case Bleeding:
                    case Animal:
                    case Cougar:
                    case BarbedWire:
                    case HeliCrash:
                        return Verdict.Gone;
                }

                // A real weapon, then. The group is the honest way to ask "was this a beating
                // or a shooting" without listing every melee weapon in the game and missing
                // whichever one was added in the DLC that shipped last month.
                var group = Function.Call<uint>(Hash.GET_WEAPONTYPE_GROUP, weapon);

                if (group != 0 && Enum.IsDefined(typeof(WeaponGroup), group))
                {
                    switch ((WeaponGroup)group)
                    {
                        case WeaponGroup.Unarmed:
                        case WeaponGroup.Melee:
                            // A BAT AND A KNIFE ARE BOTH MELEE AND THAT IS FINE. The point is
                            // not that a stabbing is harmless; it is that somebody stabbed in
                            // the street is somebody a crew has a real chance with, where
                            // somebody shot with a carbine is not. That is the distinction a
                            // player reads off the screen, and it is the one worth having.
                            return Verdict.Workable;
                    }
                }

                return Verdict.Gone;
            }
            catch (Exception ex)
            {
                Log.Debug("Could not read a cause of death: " + ex.Message);
                return Verdict.Gone;
            }
        }

        /// <summary>
        /// The weapon as a word, for the log.
        ///
        /// Best effort and nothing more. This exists so a line in the log reads "a bat" rather
        /// than "0x958A4A8F", which is the difference between a log somebody can tune the mod
        /// from and a log they cannot.
        /// </summary>
        public static string Word(uint weapon)
        {
            switch (weapon)
            {
                case 0:             return "nothing the game would name";
                case Unarmed:       return "a beating";
                case StunGun:       return "a taser";
                case Fall:          return "a fall";
                case Drowning:
                case DrowningInCar: return "drowning";
                case RammedByCar:
                case RunOverByCar:  return "a car";
                case Explosion:     return "an explosion";
                case Fire:          return "a fire";
                case HeliCrash:     return "a crash";
                case Animal:
                case Cougar:        return "an animal";
            }

            try
            {
                var group = Function.Call<uint>(Hash.GET_WEAPONTYPE_GROUP, weapon);

                if (group != 0 && Enum.IsDefined(typeof(WeaponGroup), group))
                {
                    var g = (WeaponGroup)group;

                    if (g == WeaponGroup.Melee) return "a melee weapon";
                    if (g == WeaponGroup.Unarmed) return "a beating";

                    return "a " + g.ToString().ToLowerInvariant();
                }
            }
            catch
            {
                // The number will have to do.
            }

            return "weapon 0x" + weapon.ToString("X8");
        }
    }
}
