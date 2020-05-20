/*
Automatically assigns targets to allied soldier squads.

Author: cmjten10
Mod Version: 1.1
Date: 2020-05-20
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
        private static Dictionary<IMoveTarget, int> enemyAssignments = new Dictionary<IMoveTarget, int>();
        private const int ogreAssignPoints = 1;
        private const int ogreStartPoints = 0;
        private const int ogreMinSoldiers = 3;
        private static int ogreMinPoints = ogreStartPoints + ogreAssignPoints * ogreMinSoldiers;
        private const int vikingAssignPoints = 3;
        private const int vikingStartPoints = 3;
        private const int vikingMinSoldiers = 1;
        private static int vikingMinPoints = vikingStartPoints + vikingAssignPoints * vikingMinSoldiers;

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
            if (ogre != null)
            {
                return settings.ogres.Value;
            }
            if (army != null)
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

        private static bool TargetTypeInvalid(IMoveTarget target)
        {
            UnitSystem.Army army = target as UnitSystem.Army;
            SiegeMonster ogre = target as SiegeMonster;
            if (ogre != null)
            {
                return ogre.IsInvalid();
            }
            if (army != null)
            {
                return army.IsInvalid();
            }   
            return true;
        }

        private static bool VikingInvasion()
        {
            return enemyAssignments.Count > 0;
        }

        private static IMoveTarget GetNextViking(Vector3 pos, float range)
        {
            // Refer to SiegeMonster::ClosestMonster and UnitSystem::GetClosestDamageable.
            float rangeSquared = range * range;
            float currentClosestDistance = float.MaxValue;
            int currentLowestAssigned = int.MaxValue;
            IMoveTarget nextViking = null;

            foreach (IMoveTarget viking in enemyAssignments.Keys)
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

                // Select the closest viking squad or ogre with the least points to obey the assignment and distribution
                // rules.
                int assigned = enemyAssignments[viking];
                float distanceSquared = Mathff.DistSqrdXZ(pos, viking.GetPos());

                if (distanceSquared > rangeSquared || assigned > currentLowestAssigned)
                {
                    continue;
                }

                if (nextViking == null || assigned < currentLowestAssigned || 
                    (distanceSquared < currentClosestDistance && assigned == currentLowestAssigned))
                {
                    nextViking = viking;
                    currentClosestDistance = distanceSquared;
                    currentLowestAssigned = assigned;
                }
            }
            return nextViking;
        }

        private static IMoveTarget GetNextTarget(Vector3 pos, float range)
        {
            IMoveTarget nextViking = GetNextViking(pos, range);
            if (nextViking != null)
            {
                return nextViking;
            }
            return null;
        }

        private static int PointsToSoldiers(IMoveTarget target)
        {
            if (target != null && enemyAssignments.ContainsKey(target))
            {
                int points = enemyAssignments[target];
                if (target is SiegeMonster)
                {
                    return (points - ogreStartPoints) / ogreAssignPoints;
                }
                else if (target is UnitSystem.Army)
                {
                    return (points - vikingStartPoints) / vikingAssignPoints;
                }
            }
            return -1;
        }

        private static bool AtMinimumAssignment(IMoveTarget target)
        {
            if (target != null && enemyAssignments.ContainsKey(target))
            { 
                int points = enemyAssignments[target];
                if ((target is SiegeMonster && points <= ogreMinPoints) ||
                    (target is UnitSystem.Army && points <= vikingMinPoints))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool LessThanMinimumAssignment(IMoveTarget target)
        {
            if (target != null && enemyAssignments.ContainsKey(target))
            { 
                int points = enemyAssignments[target];
                if ((target is SiegeMonster && points < ogreMinPoints) ||
                    (target is UnitSystem.Army && points < vikingMinPoints))
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
            if (target != null && enemyAssignments.ContainsKey(target))
            {
                if (target is SiegeMonster)
                {
                    enemyAssignments[target] += ogreAssignPoints;
                }
                else if (target is UnitSystem.Army)
                {
                    enemyAssignments[target] += vikingAssignPoints;
                }
            }
        }

        private static void RemoveAssignment(IMoveTarget target)
        {
            if (target != null && enemyAssignments.ContainsKey(target))
            {
                if (target is SiegeMonster)
                {
                    enemyAssignments[target] -= ogreAssignPoints;
                }
                else if (target is UnitSystem.Army)
                {
                    enemyAssignments[target] -= vikingAssignPoints;
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
            target = GetNextTarget(originalPos[army], range);

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

                // Only valid allied soldiers can be reassigned.
                if (!alliedSoldier || !valid)
                {
                    continue;
                }

                IMoveTarget newTarget = GetTargetForArmy(army, settings.patrolRadius.Value);

                // If the current target enemy is not a viking, disabled through settings, or invalid (dying or getting 
                // on a boat), always reassign.
                bool targetIsViking = target != null && enemyAssignments.ContainsKey(target);
                if (!targetIsViking || !TargetTypeEnabled(target) || TargetTypeInvalid(target))
                {
                    MoveArmyToTarget(army, newTarget);
                }
                else if (target != null && target is UnitSystem.Army)
                {
                    if (newTarget is UnitSystem.Army && !AtMinimumAssignment(target) && 
                        LessThanMinimumAssignment(newTarget))
                    {
                        MoveArmyToTarget(army, newTarget);
                    }
                    else if (newTarget is SiegeMonster && LessThanMinimumAssignment(newTarget))
                    {
                        // Ogres are always prioritized by always pulling soldiers away from viking squads.
                        MoveArmyToTarget(army, newTarget);
                    }
                }
                else if (target != null && target is SiegeMonster)
                {
                    if (!AtMinimumAssignment(target) && LessThanMinimumAssignment(newTarget))
                    {
                        MoveArmyToTarget(army, newTarget);
                    }
                }
            }
        }

        private static void ResetModState()
        {
            originalPos.Clear();
            enemyAssignments.Clear();
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

        [HarmonyPatch(typeof(OrdersManager), "MoveTo")]
        public static class TrackTargetAssignmentPatch
        {
            public static void Prefix(IMoveableUnit moveableUnit, IMoveTarget moveTarget)
            {
                UnitSystem.Army army = moveableUnit as UnitSystem.Army;
                if (army != null && army.TeamID() == 0 && army.armyType == UnitSystem.ArmyType.Default &&
                    !army.IsInvalid())
                {
                    if (moveTarget != null && !(moveTarget is SiegeMonster) && !(moveTarget is UnitSystem.Army) &&
                        VikingInvasion())
                    {
                        originalPos[army] = moveTarget.GetPos();
                    }
                    string armyType = army.armyType.ToString();

                    RemoveAssignment(army.moveTarget);
                    RecordAssignment(moveTarget);
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
                if (ogre != null && !enemyAssignments.ContainsKey(ogre))
                {
                    enemyAssignments[ogre] = ogreStartPoints;
                }
            }
        }

        // Patch for untracking ogre on despawn.
        [HarmonyPatch(typeof(SiegeMonster), "DestroyUnit")]
        public static class UntrackOgrePatch
        {
            public static void Prefix(SiegeMonster __instance)
            {
                if (enemyAssignments.ContainsKey(__instance))
                {
                    enemyAssignments.Remove(__instance);
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
                if (army != null && !enemyAssignments.ContainsKey(army))
                {
                    enemyAssignments[army] = vikingStartPoints;
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
                if (enemyAssignments.ContainsKey(army))
                {
                    enemyAssignments.Remove(army);
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

                    // Don't clear enemyAssignments because in the case where the mod is deactivated, then activated
                    // during a viking invasion, no vikings will be tracked, so the soldier squads will not move.
                    foreach (IMoveTarget viking in enemyAssignments.Keys.ToList())
                    {
                        enemyAssignments[viking] = 0;
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
