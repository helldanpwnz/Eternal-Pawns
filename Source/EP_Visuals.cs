using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace FinitePopulationVeterans
{
    public static partial class FPUtility
    {
        // ЕДИНАЯ ЛИНЕЙНАЯ ФУНКЦИЯ ЦВЕТА (ОТ ЭТАЛОНА)
        public static Color GetColorForAge(Pawn pawn, Color baseColor)
        {
            if (FPMod.Settings == null || !FPMod.Settings.enableAgingVisuals) return baseColor;

            float currentAge = pawn.ageTracker.AgeBiologicalYearsFloat;
            float lifeExpectancy = pawn.RaceProps.lifeExpectancy; 
            
            float startAge = lifeExpectancy * (FPMod.Settings?.startGrayingHairRatio ?? 0.45f);
            if (currentAge <= startAge) return baseColor;

            float ratio = FPMod.Settings?.grayingYearlyRatio ?? 0.25f;
            float span = (lifeExpectancy - startAge) * (ratio / 0.25f);
            float progress = Mathf.Clamp01((currentAge - startAge) / span);

            float h, s, v;
            Color.RGBToHSV(baseColor, out h, out s, out v);

            float finalS = Mathf.Lerp(s, 0f, progress);
            float targetV = Mathf.Max(v, 0.96f);
            float finalV = Mathf.Lerp(v, targetV, progress);

            Color result = Color.HSVToRGB(h, finalS, finalV);
            result.a = baseColor.a;
            return result;
        }

        public static void SyncAgingVisuals(Pawn pawn)
        {
            if (pawn == null || !pawn.RaceProps.Humanlike || pawn.story == null) return;
            if (pawn.IsColonyMech || pawn.IsGhoul) return;

            // 1. Проба найти "Якорь" (эталонный цвет)
            Color baseColor = pawn.story.HairColor;
            bool foundBase = false;

            if (pawn.genes != null)
            {
                if (pawn.genes.GenesListForReading.Any(g => g.Active && g.def.neverGrayHair)) return;
                foreach (var g in pawn.genes.GenesListForReading) {
                    if (g.Active && g.def.hairColorOverride.HasValue) {
                        baseColor = g.def.hairColorOverride.Value; 
                        foundBase = true; break; 
                    }
                }
            }

            if (!foundBase)
            {
                var manager = Find.World?.GetComponent<WorldPopulationManager>();
                if (manager != null) {
                    if (manager.originalHairColors.TryGetValue(pawn.thingIDNumber, out Color remembered)) baseColor = remembered;
                    else manager.originalHairColors[pawn.thingIDNumber] = baseColor;
                }
            }

            // 2. Установка цвета
            pawn.story.HairColor = GetColorForAge(pawn, baseColor);

            if (pawn.Spawned && pawn.Drawer?.renderer != null)
            {
                pawn.Drawer.renderer.SetAllGraphicsDirty();
                PortraitsCache.SetDirty(pawn);
            }
        }

        public static void ProcessGrayingHair(Pawn pawn, float years = 1f) => SyncAgingVisuals(pawn);
        public static void ReverseGrayingHair(Pawn pawn, float years = 1f) => SyncAgingVisuals(pawn);
    }

    // --- ПАТЧИ (ТОТАЛЬНЫЙ КОНТРОЛЬ) ---

    [HarmonyPatch(typeof(PawnGenerator), "GeneratePawn", new Type[] { typeof(PawnGenerationRequest) })]
    public static class Patch_GeneratePawn_Visuals
    {
        static void Postfix(Pawn __result)
        {
            if (FPMod.Settings == null || !FPMod.Settings.enableAgingVisuals) return;
            if (__result != null) FPUtility.SyncAgingVisuals(__result);
        }
    }

    [HarmonyPatch(typeof(Pawn_AgeTracker), "BirthdayBiological")]
    public static class Patch_Birthday_Visuals
    {
        static void Postfix(Pawn_AgeTracker __instance)
        {
            Pawn pawn = (Pawn)AccessTools.Field(typeof(Pawn_AgeTracker), "pawn").GetValue(__instance);
            if (pawn != null) FPUtility.SyncAgingVisuals(pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn), "SpawnSetup")]
    public static class Patch_Spawn_Visuals
    {
        static void Postfix(Pawn __instance)
        {
            FPUtility.SyncAgingVisuals(__instance);
        }
    }

    [HarmonyPatch]
    public static class Patch_Biosculpter_Sync {
        public static bool Prepare() => ModsConfig.IdeologyActive && AccessTools.Method("RimWorld.CompBiosculpterPod_AgeReversalCycle:CycleCompleted") != null;
        public static MethodBase TargetMethod() => AccessTools.Method("RimWorld.CompBiosculpterPod_AgeReversalCycle:CycleCompleted");
        static void Postfix(Pawn pawn) => FPUtility.SyncAgingVisuals(pawn);
    }

    [HarmonyPatch]
    public static class Patch_Ritual_Sync {
        public static bool Prepare() => ModsConfig.AnomalyActive && AccessTools.Method("RimWorld.PsychicRitualToil_Chronophagy:AgePawn") != null;
        public static MethodBase TargetMethod() => AccessTools.Method("RimWorld.PsychicRitualToil_Chronophagy:AgePawn");
        static void Postfix(Pawn pawn) => FPUtility.SyncAgingVisuals(pawn);
    }

    [HarmonyPatch]
    public static class Patch_Ritual_Reverse_Sync {
        public static bool Prepare() => ModsConfig.AnomalyActive && AccessTools.Method("RimWorld.PsychicRitualToil_Chronophagy:ReverseAgePawn") != null;
        public static MethodBase TargetMethod() => AccessTools.Method("RimWorld.PsychicRitualToil_Chronophagy:ReverseAgePawn");
        static void Postfix(Pawn pawn) => FPUtility.SyncAgingVisuals(pawn);
    }

    [HarmonyPatch]
    public static class Patch_VPE_Chronopath_Sync {
        public static bool Prepare() => ModLister.HasActiveModWithName("Vanilla Psycasts Expanded") && AccessTools.Method("VanillaPsycastsExpanded.Chronopath.AbilityExtension_Age:Age") != null;
        public static MethodBase TargetMethod() => AccessTools.Method("VanillaPsycastsExpanded.Chronopath.AbilityExtension_Age:Age");
        static void Postfix(Pawn pawn) => FPUtility.SyncAgingVisuals(pawn);
    }
}
