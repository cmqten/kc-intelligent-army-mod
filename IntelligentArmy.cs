/*
Automatically assigns targets to allied soldier squads.

Author: cmjten10
Mod Version: 0
Date: 2020-05-17
*/
using Harmony;
using System;
using System.Collections.Generic;
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
        private static Dictionary<UnitSystem.Army, int> assignedToViking = new Dictionary<UnitSystem.Army, int>();

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

        private static UnitSystem.Army GetClosestVikingSquad(Vector3 pos, float range)
        {
            IProjectileHitable closestViking = UnitSystem.inst.GetClosestDamageable(pos, 1, range);
            if (closestViking != null && OnSameLandmass(pos, closestViking.GetPosition()))
            {
                if (closestViking is UnitSystem.Unit)
                {
                    return (closestViking as UnitSystem.Unit).army;
                }
                else if (closestViking is UnitSystem.Army)
                {
                    return closestViking as UnitSystem.Army;
                }
            }
            return null;
        }

        private static IMoveTarget GetClosestTarget(Vector3 pos)
        {
            SiegeMonster closestOgre = SiegeMonster.ClosestMonster(pos);
            if (closestOgre != null && OnSameLandmass(pos, closestOgre.GetPos()))
            {
                return closestOgre;
            }
            UnitSystem.Army closestViking = GetClosestVikingSquad(pos, 10f);
            if (closestViking != null)
            {
                return closestViking;
            }
            IProjectileHitable closestWolf = WolfDen.ClosestWolf(pos);
            if (closestWolf != null && OnSameLandmass(pos, closestWolf.GetPosition()))
            {
                Cell wolfDenCell = World.inst.GetCellData(closestWolf.GetPosition());
                return wolfDenCell;
            }
            return null;
        }

        private static IMoveTarget GetClosestTarget(UnitSystem.Army army)
        {
            return GetClosestTarget(army.GetPos());
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
                        // The goal of each army is to patrol an area of radius 5 from its starting point.
                        IMoveTarget target = null;
                        if (originalPos.ContainsKey(__instance.army))
                        {
                            target = GetClosestTarget(originalPos[__instance.army]);
                        }
                        else
                        {
                            target = GetClosestTarget(__instance.army);
                        }

                        if (target != null && !originalPos.ContainsKey(__instance.army))
                        {
                            originalPos[__instance.army] = __instance.army.GetPos();
                        }
                        else if (target == null && originalPos.ContainsKey(__instance.army))
                        {
                            // If there are no more targets, return the army to its original position.
                            target = World.inst.GetCellData(originalPos[__instance.army]);
                            originalPos.Remove(__instance.army);
                        }

                        if (target != null)
                        {
                            OrdersManager.inst.MoveTo(__instance.army, target);
                        }
                    }
                }
                catch {}
            }
        }

        [HarmonyPatch(typeof(UnitSystem), "ReleaseArmy")]
        public static class RemoveArmyFromModStatePatch
        {
            public static void Prefix(UnitSystem.Army army)
            {
                if (originalPos.ContainsKey(army))
                {
                    originalPos.Remove(army);
                }
            }
        }
    }
}
