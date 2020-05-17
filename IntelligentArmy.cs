/*
Automatically assigns targets to allied soldier squads.

Author: cmjten10
Mod Version: 0
Date: 2020-05-17
*/
using Harmony;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace IntelligentArmy
{
    public class ModMain : MonoBehaviour 
    {
        private const string authorName = "cmjten10";
        private const string modName = "Intelligent Army";
        private const string modNameNoSpace = "IntelligentArmy";
        private const string version = "v0";
        private static string modId = $"{authorName}.{modNameNoSpace}";

        // Logging
        public static KCModHelper helper;
        private static UInt64 logId = 0;

        // Allied army information
        private static Dictionary<UnitSystem.Army, Vector3> originalPos = new Dictionary<UnitSystem.Army, Vector3>();

        // Viking army information
        private static Dictionary<IMoveTarget, int> assignedToViking = new Dictionary<IMoveTarget, int>();

        void Preload(KCModHelper __helper) 
        {
            helper = __helper;
            var harmony = HarmonyInstance.Create(modId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }

        // Logger in the log box in game.
        private static void LogInGame(string message, KingdomLog.LogStatus status = KingdomLog.LogStatus.Neutral)
        {
            KingdomLog.TryLog($"{modId}-{logId}", message, status);
            logId++; 
        }


        private static bool ArmyIdle(UnitSystem.Army army)
        {
            for (int i = 0; i < army.units.Count; i++)
            {
                if (army.units.data[i].status != UnitSystem.Unit.Status.Following)
                {
                    return false;
                }
            }
            return true;
        }

        private static bool OnSameLandmass(Vector3 pos1, Vector3 pos2)
        {
            int pos1Landmass = World.inst.GetCellData(pos1).landMassIdx;
            int pos2Landmass = World.inst.GetCellData(pos2).landMassIdx;
            return pos1Landmass == pos2Landmass;
        }

        // Refer to SiegeMonster::ClosestMonster and UnitSystem::GetClosestDamageable.
        private static IMoveTarget GetClosestViking(Vector3 pos, float range)
        {
            float rangeSquared = range * range;
            float currentClosestDistance = float.MaxValue;
            float currentLowestAssigned = float.MaxValue;
            IMoveTarget closestViking = null;

            foreach (IMoveTarget viking in assignedToViking.Keys)
            {
                UnitSystem.Army army = viking as UnitSystem.Army;
                SiegeMonster ogre = viking as SiegeMonster;
                if ((army == null && ogre == null) ||
                    (army != null && (army.IsInvalid() || !OnSameLandmass(pos, army.GetPos()))) ||
                    (ogre != null && (ogre.IsInvalid() || !OnSameLandmass(pos, ogre.GetPos()))))
                {
                    continue;
                }

                // Select the closest viking army or ogre with the lowest number of assigned allied army for more even
                // distribution. Avoids an entire army attacking a single viking army, in theory.
                int assigned = assignedToViking[viking];
                float distanceSquared = Mathff.DistSqrdXZ(pos, viking.GetPos());
                if (distanceSquared > rangeSquared)
                {
                    continue;
                }
                if ((assigned < currentLowestAssigned) ||
                    (assigned == currentLowestAssigned && distanceSquared < currentClosestDistance))
                {
                    closestViking = viking;
                    currentClosestDistance = distanceSquared;
                    currentLowestAssigned = assigned;
                }
            }
            
            if (closestViking != null)
            {
                assignedToViking[closestViking] += 1;
            }
            return closestViking;
        }

        private static IMoveTarget GetClosestTarget(Vector3 pos, float range)
        {
            IMoveTarget closestViking = GetClosestViking(pos, range);
            if (closestViking != null)
            {
                return closestViking;
            }
            return null;
        }

        private static IMoveTarget GetClosestTarget(UnitSystem.Army army, float range)
        {
            return GetClosestTarget(army.GetPos(), range);
        }

        private static void AssignTargetToArmyAndMove(UnitSystem.Army army, float range)
        {
            // The goal of each army is to patrol an area of radius 10 from its starting point, or its current position
            // if there are no enemies within its patrol radius.
            IMoveTarget target = null;
            if (originalPos.ContainsKey(army))
            {
                target = GetClosestTarget(originalPos[army], range);
            }
            if (target == null)
            {
                target = GetClosestTarget(army, range);
            }

            if (target != null && !originalPos.ContainsKey(army))
            {
                originalPos[army] = army.GetPos();
            }
            else if (target == null && originalPos.ContainsKey(army))
            {
                // If there are no more targets, return the army to its original position.
                target = World.inst.GetCellData(originalPos[army]);
                originalPos.Remove(army);
            }

            if (target != null)
            {
                OrdersManager.inst.MoveTo(army, target);
            }
        }
        
        // Reassigns all allied soldiers on the given IMoveTarget's current location's landmass. Useful for events such
        // as specific enemy units arriving on a specific landmass. If IMoveTarget is null, all allied soldiers on all
        // landmasses are reassigned.
        private static void ReassignAllArmyOnMoveTargetLandmass(IMoveTarget unit = null)
        {
            int armiesCount = UnitSystem.inst.ArmyCount();

            foreach (IMoveTarget viking in assignedToViking.Keys.ToList())
            {
                if (unit == null || OnSameLandmass(unit.GetPos(), viking.GetPos()))
                {
                    assignedToViking[viking] = 0;
                }
            }

            for (int i = 0; i < armiesCount; i++)
            {
                UnitSystem.Army army = UnitSystem.inst.GetAmry(i);
                Vector3 armyPos = army.GetPos();

                if (army.TeamID() == 0 && army.armyType == UnitSystem.ArmyType.Default && !army.IsInvalid() 
                    && (unit == null || OnSameLandmass(armyPos, unit.GetPos())))
                {
                    AssignTargetToArmyAndMove(army, 10f);
                }
            }
        }

        [HarmonyPatch(typeof(General), "Tick")]
        public static class FindTargetPatch
        {
            public static void Postfix(General __instance)
            {
                try
                {
                    if (__instance.army.TeamID() != 0 || __instance.army.armyType != UnitSystem.ArmyType.Default)
                    {
                        return;
                    }

                    if (!__instance.army.moving && ArmyIdle(__instance.army))
                    {
                        AssignTargetToArmyAndMove(__instance.army, 10f);
                    }
                }
                catch {}
            }
        }

        [HarmonyPatch(typeof(RaiderSystem), "OnOgreArrived")]
        public static class ReassignOnOgreArrivalPatch
        {
            public static void Postfix(IMoveableUnit unit)
            {
                // When an ogre arrives, redistribute all the allied soldiers.
                SiegeMonster ogre = unit as SiegeMonster;
                if (unit == null)
                {
                    return;
                }
                ReassignAllArmyOnMoveTargetLandmass(ogre);
            }
        }

        [HarmonyPatch(typeof(RaiderSystem), "SpawnOgre")]
        public static class TrackOgrePatch
        {
            public static void Postfix(IMoveableUnit __result)
            {
                SiegeMonster ogre = __result as SiegeMonster;
                if (ogre != null && !assignedToViking.ContainsKey(ogre))
                {
                    assignedToViking[ogre] = 0;
                }
            }
        }

        [HarmonyPatch(typeof(RaiderSystem), "SpawnArmy")]
        public static class TrackVikingArmyPatch
        {
            public static void Postfix(IMoveableUnit __result)
            {
                UnitSystem.Army army = __result as UnitSystem.Army;
                if (army != null && !assignedToViking.ContainsKey(army))
                {
                    assignedToViking[army] = 0;
                }
            }
        }

        [HarmonyPatch(typeof(UnitSystem), "ReleaseArmy")]
        public static class UntrackArmyPatch
        {
            public static void Prefix(UnitSystem.Army army)
            {
                if (originalPos.ContainsKey(army))
                {
                    originalPos.Remove(army);
                }
                if (assignedToViking.ContainsKey(army))
                {
                    assignedToViking.Remove(army);
                }
            }
        }

        [HarmonyPatch(typeof(SiegeMonster), "DestroyUnit")]
        public static class UntrackOgrePatch
        {
            public static void Prefix(SiegeMonster __instance)
            {
                if (assignedToViking.ContainsKey(__instance))
                {
                    assignedToViking.Remove(__instance);
                }
            }
        }
    }
}
