/*
Automatically assigns targets to allied soldier squads.

Author: cmjten10
Mod Version: 1
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
    public class ModMain : Tickable
    {
        private const string authorName = "cmjten10";
        private const string modName = "Intelligent Army";
        private const string modNameNoSpace = "IntelligentArmy";
        private const string version = "v1";
        private static string modId = $"{authorName}.{modNameNoSpace}";

        private static System.Random random = new System.Random();

        // Logging
        public static KCModHelper helper;
        private static UInt64 logId = 0;

        // Timer
        private static Timer timer = new Timer(1f);

        // Allied army information
        private static float patrolRadius = 10f;
        private static Dictionary<UnitSystem.Army, Vector3> originalPos = new Dictionary<UnitSystem.Army, Vector3>();

        // Viking information
        private static Dictionary<IMoveTarget, int> assignedToViking = new Dictionary<IMoveTarget, int>();

        void Preload(KCModHelper __helper) 
        {
            helper = __helper;
            var harmony = HarmonyInstance.Create(modId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }

        public override void Tick(float dt)
        {
            base.Tick(dt);

            // Refer to Cemetery::Tick.
            if (timer == null || !timer.Update(dt))
            {
                return;
            }
            LogInGame($"{originalPos.Count} - {assignedToViking.Count}");
            // Reassign all soldiers every second.
            if (VikingInvasion())
            {
                ReassignAllArmy();
            }
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

        private static bool VikingInvasion()
        {
            return assignedToViking.Count > 0;
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

                // Select the closest viking squad or ogre with the lowest number of assigned allied soldiers for more 
                // even distribution. Avoids an entire army attacking a single viking squad, in theory.
                int assigned = assignedToViking[viking];
                float distanceSquared = Mathff.DistSqrdXZ(pos, viking.GetPos());

                if (distanceSquared > rangeSquared || assigned > currentLowestAssigned)
                {
                    continue;
                }

                // ogre != null gives ogres priority by disregarding their distance compared to the current closest.
                if (closestViking == null || ogre != null ||
                    (closestViking is UnitSystem.Army && army != null && distanceSquared < currentClosestDistance))
                {
                    closestViking = viking;
                    currentClosestDistance = distanceSquared;
                    currentLowestAssigned = assigned;
                }
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

        private static void AssignTargetToArmyAndMove(UnitSystem.Army army, float range)
        {
            if (!originalPos.ContainsKey(army))
            {
                return;
            }

            // Soldiers will patrol a radius around its starting point specified by range.
            IMoveTarget target = null;
            target = GetClosestTarget(originalPos[army], range);

            if (target == null)
            {
                // If there are no targets within the soldier squads's patrol range, it will try to help out in a radius
                // half of specified range from its current position, as long as it is still within 1.5x its patrol 
                // radius. Of course, this doesn't work if the soldier squad is standing at its original position.
                if (Mathff.DistSqrdXZ(army.GetPos(), originalPos[army]) < range * range * 1.5f * 1.5f)
                {
                    target = GetClosestTarget(army.GetPos(), range * 0.5f);
                }
                else
                {
                    target = GetClosestTarget(originalPos[army], range * 1.5f);
                }
            }

            if (target == null)
            {
                // If there are no more targets, return the soldier squad to its original position.
                target = World.inst.GetCellData(originalPos[army]);
            }
            else
            {
                // By adding a smaller number to every assignment to an ogre, the mod will try to assign more soldiers
                // to it.
                if (target is SiegeMonster)
                {
                    assignedToViking[target] += 1;
                }
                else if (target is UnitSystem.Army)
                {
                    assignedToViking[target] += 2;
                }
            }
            OrdersManager.inst.MoveTo(army, target);
        }
        
        // Reassigns some allied soldiers.
        private static void ReassignAllArmy()
        {
            int armiesCount = UnitSystem.inst.ArmyCount();

            for (int i = 0; i < armiesCount; i++)
            {
                UnitSystem.Army army = UnitSystem.inst.GetAmry(i);
                Vector3 armyPos = army.GetPos();

                bool alliedSoldier = army.TeamID() == 0 && army.armyType == UnitSystem.ArmyType.Default;
                bool valid = !army.IsInvalid();
                bool idle = !army.moving && ArmyIdle(army);

                // 20% chance of being reassigned regardless of idle or not if target is not an ogre.
                bool targetIsOgre = (army.moveTarget != null) && (army.moveTarget is SiegeMonster);
                bool forceReassign = (random.Next(0, 100) < 20) && !targetIsOgre;

                if (alliedSoldier && valid && (idle || forceReassign))
                {
                    IMoveTarget currentTarget = army.moveTarget;
                    if (currentTarget != null && assignedToViking.ContainsKey(currentTarget))
                    {
                        assignedToViking[currentTarget] -= 1;
                    }
                    AssignTargetToArmyAndMove(army, patrolRadius);
                }
            }
        }

        private static void ResetModState()
        {
            originalPos.Clear();
            assignedToViking.Clear();
        }

        // General::Tick patch for assigning a target.
        [HarmonyPatch(typeof(General), "Tick")]
        public static class FindTargetPatch
        {
            public static void Postfix(General __instance)
            {
                UnitSystem.Army army = __instance.army;

                if (VikingInvasion())
                {
                    try
                    {
                        if (army.TeamID() != 0 || army.armyType != UnitSystem.ArmyType.Default)
                        {
                            return;
                        }

                        if (!army.moving && ArmyIdle(army))
                        {
                            if (!originalPos.ContainsKey(army))
                            {
                                // At the beginning of an invasion, record the soldier squad's original position so it
                                // can be returned at the end.
                                originalPos[army] = army.GetPos();
                            }
                            AssignTargetToArmyAndMove(army, patrolRadius);
                        }
                    }
                    catch {}
                }
                else
                {
                    if (originalPos.ContainsKey(army))
                    {
                        // At the end of a viking invasion, move all soldiers back to their original position.
                        IMoveTarget target = World.inst.GetCellData(originalPos[army]);
                        if (target != null)
                        {
                            OrdersManager.inst.MoveTo(army, target);
                        }
                        originalPos.Remove(army);
                    }
                }
            }
        }

        // Patch for tracking ogre on spawn.
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

        // Patch for untracking ogre on despawn.
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

        // Patch for tracking viking and soldier squad on spawn.
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

        // Patch for untracking viking squad on despawn.
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

        // Patch for resetting mod state when loading a new game.
        [HarmonyPatch(typeof(Player), "Reset")]
        public static class ResetModStatePatch
        {
            public static void Postfix()
            {
                ResetModState();
            }
        }
    }
}
