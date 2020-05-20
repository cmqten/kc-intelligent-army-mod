/*
Automatically assigns targets to allied soldier squads.

Author: cmjten10
Mod Version: 1.1
Date: 2020-05-19
*/
using Harmony;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using Zat.Shared.ModMenu.API;
using Zat.Shared.ModMenu.Interactive;

namespace IntelligentArmy
{
    public class ModMain : Tickable
    {
        private const string authorName = "cmjten10";
        private const string modName = "Intelligent Army";
        private const string modNameNoSpace = "IntelligentArmy";
        private const string version = "v1.1";
        private static string modId = $"{authorName}.{modNameNoSpace}";

        // Logging
        public static KCModHelper helper;
        private static UInt64 logId = 0;

        // Settings
        public static ModSettingsProxy proxy;
        public static IntelligentArmySettings settings;

        // Timer
        private static Timer timer = new Timer(1f);

        // Allied army information
        private static Dictionary<UnitSystem.Army, Vector3> originalPos = new Dictionary<UnitSystem.Army, Vector3>();

        // Viking information
        private const int ogreAssignPoints = 1;
        private const int minOgreAssignPoints = 2;
        private const int vikingSquadAssignPoints = 2;
        private const int minVikingSquadAssignPoints = 2;
        private static Dictionary<IMoveTarget, int> assignedToViking = new Dictionary<IMoveTarget, int>();

        void Preload(KCModHelper __helper) 
        {
            helper = __helper;
            var harmony = HarmonyInstance.Create(modId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }

        void SceneLoaded(KCModHelper __helper)
        {
            if (!proxy)
            {
                var config = new InteractiveConfiguration<IntelligentArmySettings>();
                settings = config.Settings;
                ModSettingsBootstrapper.Register(config.ModConfig, (_proxy, saved) =>
                {
                    config.Install(_proxy, saved);
                    proxy = _proxy;
                    settings.Setup();
                }, (ex) =>
                {
                    helper.Log($"ERROR: Failed to register proxy for {modName} Mod config: {ex.Message}");
                    helper.Log(ex.StackTrace);
                });
            }
        }

        // =====================================================================
        // Utility Functions
        // =====================================================================

        // Logger in the log box in game.
        private static void LogInGame(string message, KingdomLog.LogStatus status = KingdomLog.LogStatus.Neutral)
        {
            KingdomLog.TryLog($"{modId}-{logId}", message, status);
            logId++; 
        }

        private static bool ArmyIdle(UnitSystem.Army army)
        {
            IMoveTarget target = army.moveTarget;
            return target == null || (!(target is SiegeMonster) && !(target is UnitSystem.Army));
        }

        private static bool OnSameLandmass(Vector3 pos1, Vector3 pos2)
        {
            int pos1Landmass = World.inst.GetCellData(pos1).landMassIdx;
            int pos2Landmass = World.inst.GetCellData(pos2).landMassIdx;
            return pos1Landmass == pos2Landmass;
        }

        private static bool TargetTypeEnabled(IMoveTarget target)
        {
            UnitSystem.Army army = target as UnitSystem.Army;
            SiegeMonster ogre = target as SiegeMonster;
            if (ogre != null && !ogre.IsInvalid())
            {
                return settings.ogres.Value;
            }
            if (army != null && !army.IsInvalid())
            {
                if (army.armyType == UnitSystem.ArmyType.Default)
                {
                    return settings.defaultViking.Value;
                }
                if (army.armyType == UnitSystem.ArmyType.Thief)
                {
                    return settings.thieves.Value;
                }
            }   
            return false;
        }

        private static bool VikingInvasion()
        {
            return assignedToViking.Count > 0;
        }

        // Get the closest viking unit that also has the fewest number of assigned soldiers.
        private static IMoveTarget GetClosestViking(Vector3 pos, float range)
        {
            // Refer to SiegeMonster::ClosestMonster and UnitSystem::GetClosestDamageable.
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
                    (ogre != null && (ogre.IsInvalid() || !OnSameLandmass(pos, ogre.GetPos()))) ||
                    !TargetTypeEnabled(viking))
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

        private static bool AtMinimumAssignment(IMoveTarget target)
        {
            if (target != null && assignedToViking.ContainsKey(target))
            { 
                int numAssigned = assignedToViking[target];
                if ((target is SiegeMonster && numAssigned <= minOgreAssignPoints) ||
                    (target is UnitSystem.Army && numAssigned <= minVikingSquadAssignPoints))
                {
                    return true;
                }
            }
            return false;
        }

        private static void RecordAssignment(IMoveTarget target)
        {
            // By adding a smaller number to every assignment to an ogre, the mod will try to assign more soldiers to 
            // it.
            if (target != null && assignedToViking.ContainsKey(target))
            {
                if (target is SiegeMonster)
                {
                    assignedToViking[target] += ogreAssignPoints;
                }
                else if (target is UnitSystem.Army)
                {
                    assignedToViking[target] += vikingSquadAssignPoints;
                }
            }
        }

        private static void RemoveAssignment(IMoveTarget target)
        {
            if (target != null && assignedToViking.ContainsKey(target))
            {
                if (target is SiegeMonster)
                {
                    assignedToViking[target] -= ogreAssignPoints;
                }
                else if (target is UnitSystem.Army)
                {
                    assignedToViking[target] -= vikingSquadAssignPoints;
                }
            }
        }

        private static IMoveTarget GetTargetForArmy(UnitSystem.Army army, float range)
        {
            if (!originalPos.ContainsKey(army))
            {
                return null;
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
                // If there are no more targets, return the soldier squad to its original position if it's not already
                // there.
                if (originalPos[army] != army.GetPos())
                {
                    target = World.inst.GetCellData(originalPos[army]);
                }
            }
            return target;
        }

        public static void MoveArmyToTarget(UnitSystem.Army army, IMoveTarget target)
        {
            if (target != null)
            {
                RecordAssignment(target);
                OrdersManager.inst.MoveTo(army, target);
            }
        }

        private static void AssignTargetToArmyAndMove(UnitSystem.Army army, float range)
        {
            MoveArmyToTarget(army, GetTargetForArmy(army, range));
        }
        
        // Reassigns some allied soldiers in order to redistribute them.
        private static void TryReassignAllArmy()
        {
            int armiesCount = UnitSystem.inst.ArmyCount();

            for (int i = 0; i < armiesCount; i++)
            {
                UnitSystem.Army army = UnitSystem.inst.GetAmry(i);
                IMoveTarget target = army.moveTarget;
                Vector3 armyPos = army.GetPos();

                bool alliedSoldier = army.TeamID() == 0 && army.armyType == UnitSystem.ArmyType.Default;
                bool valid = !army.IsInvalid();

                // Re-assignment heuristic
                bool targetAtMinimumAssignment = AtMinimumAssignment(target);
                bool forceReassign = !TargetTypeEnabled(target) || !targetAtMinimumAssignment;

                if (alliedSoldier && valid && forceReassign)
                {
                    if (target != null && assignedToViking.ContainsKey(target))
                    {
                        RemoveAssignment(target);
                    }
                    AssignTargetToArmyAndMove(army, settings.patrolRadius.Value);
                }
            }
        }

        private static void ResetModState()
        {
            originalPos.Clear();
            assignedToViking.Clear();
        }

        // =====================================================================
        // Patches
        // =====================================================================

        public override void Tick(float dt)
        {
            base.Tick(dt);

            // Refer to Cemetery::Tick.
            if (timer == null || !timer.Update(dt))
            {
                return;
            }

            // Reassign all soldiers every second.
            if (VikingInvasion() && settings.enabled.Value)
            {
                TryReassignAllArmy();
            }
        }

        // General::Tick patch for assigning a target.
        [HarmonyPatch(typeof(General), "Tick")]
        public static class FindTargetPatch
        {
            public static void Postfix(General __instance)
            {
        
                UnitSystem.Army army = __instance.army;
                if (!settings.enabled.Value || army.TeamID() != 0 || army.armyType != UnitSystem.ArmyType.Default ||
                    army.IsInvalid())
                {
                    return;
                }

                if (VikingInvasion())
                {
                    try
                    {
                        if (!army.moving && ArmyIdle(army))
                        {
                            if (!originalPos.ContainsKey(army))
                            {
                                // At the beginning of an invasion, record the soldier squad's original position so it
                                // can be returned at the end.
                                originalPos[army] = army.GetPos();
                            }
                            AssignTargetToArmyAndMove(army, settings.patrolRadius.Value);
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

        // Patch for tracking viking on spawn.
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

        // Patch for untracking viking and soldier squad on despawn.
        [HarmonyPatch(typeof(UnitSystem), "ReleaseArmy")]
        public static class UntrackArmyPatch
        {
            public static void Prefix(UnitSystem.Army army)
            {
                if (originalPos.ContainsKey(army))
                {
                    IMoveTarget target = army.moveTarget;
                    RemoveAssignment(target);
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

        // =====================================================================
        // Settings
        // =====================================================================

        [Mod(modName, version, authorName)]
        public class IntelligentArmySettings
        {
            [Setting("Enabled", "Use AI to control your soldier squads.")]
            [Toggle(true, "")]
            public InteractiveToggleSetting enabled { get; private set; }

            [Setting("Ogres", "Allow targeting of ogres.")]
            [Toggle(true, "")]
            public InteractiveToggleSetting ogres { get; private set; }

            [Setting("Kidnappers and Torchers", "Allow targetting of kidnappers and torchers.")]
            [Toggle(true, "")]
            public InteractiveToggleSetting defaultViking { get; private set; }

            [Setting("Thieves", "Allow targetting of thieves.")]
            [Toggle(true, "")]
            public InteractiveToggleSetting thieves { get; private set; }

            [Setting("Patrol Radius", "Patrol radius of each soldier squad from its original position.")]
            [Slider(0, 50, 10, "10", true)]
            public InteractiveSliderSetting patrolRadius { get; private set; }

            public void Setup()
            {
                enabled.OnUpdate.AddListener((setting) =>
                {
                    originalPos.Clear();

                    // Don't clear assignedToViking because in the case where the mod is deactivated, then activated
                    // during a viking invasion, no vikings will be tracked, so the soldier squads will not move.
                    foreach (IMoveTarget viking in assignedToViking.Keys.ToList())
                    {
                        assignedToViking[viking] = 0;
                    }
                });
                patrolRadius.OnUpdate.AddListener((setting) =>
                {
                    patrolRadius.Label = ((int)setting.slider.value).ToString();
                });
            }
        }
    }
}
