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
            var settings = FPMod.Settings;
            if (settings == null || !settings.enableAgingVisuals) return baseColor;

            float currentAge = pawn.ageTracker.AgeBiologicalYearsFloat;
            float lifeExpectancy = pawn.RaceProps.lifeExpectancy; 
            
            float startAge = lifeExpectancy * settings.startGrayingHairRatio;
            if (currentAge <= startAge) return baseColor;

            float ratio = settings.grayingYearlyRatio;
            float span = (lifeExpectancy - startAge) * (ratio / 0.25f);
            float progress = Mathf.Clamp01((currentAge - startAge) / span);

            // МАТЕМАТИКА "РЕАЛИСТИЧНОГО ВЫЦВЕТАНИЯ"
            // Насыщенность (S) падает СТРЕМИТЕЛЬНО (вымываем пигмент)
            float satProgress = Mathf.Pow(progress, 0.5f); 
            // Яркость (V) следует по кривой "темного вымывания"
            float valProgress = Mathf.Pow(progress, 2.0f);
            // Эффект "пепельного потемнения": в середине пути волосы теряют блеск и кажутся темнее
            float darkeningEffect = 0.15f * Mathf.Sin(Mathf.PI * progress);

            float h, s, v;
            Color.RGBToHSV(baseColor, out h, out s, out v);

            float finalS = Mathf.Lerp(s, 0f, satProgress);
            // Плавно идем к белому, но вычитаем "пепельный эффект" в середине
            float finalV = Mathf.Lerp(v, 0.96f, valProgress) - (darkeningEffect * s);
            finalV = Mathf.Clamp(finalV, 0.1f, 0.98f); // Защита от ухода в черную дыру

            Color result = Color.HSVToRGB(h, finalS, finalV);
            result.a = baseColor.a;
            return result;
        }

        public static void SyncAgingVisuals(Pawn pawn)
        {
            if (pawn == null || !pawn.RaceProps.Humanlike || pawn.story == null) return;
            if (pawn.IsColonyMech || pawn.IsGhoul || (ModsConfig.AnomalyActive && pawn.IsMutant)) return;
            if (pawn.Faction != null && pawn.Faction.def.defName == "Entities") return;

            // 1. Проба найти "Якорь" (эталонный цвет)
            Color baseColor = pawn.story.HairColor;
            bool foundBase = false;

            // Оптимизированный проход по генам (последний активный ген цвета имеет приоритет)
            if (pawn.genes != null)
            {
                foreach (var g in pawn.genes.GenesListForReading) 
                {
                    if (!g.Active) continue;
                    if (g.def.neverGrayHair) return; // Сразу выходим, если есть ген на запрет седины
                    
                    if (g.def.hairColorOverride.HasValue) 
                    {
                        baseColor = g.def.hairColorOverride.Value; 
                        foundBase = true; 
                    }
                }
            }

            if (!foundBase)
            {
                var manager = Find.World?.GetComponent<WorldPopulationManager>();
                if (manager != null) {
                    if (manager.originalHairColors.TryGetValue(pawn.thingIDNumber, out Color remembered)) 
                    {
                        baseColor = remembered;
                        foundBase = true;
                    }
                    else if (FPMod.Settings.enableAgingVisuals)
                    {
                        manager.originalHairColors[pawn.thingIDNumber] = baseColor;
                    }
                }
            }

            // 2. Умная установка цвета
            Color newColor = GetColorForAge(pawn, baseColor);
            Color32 c1 = pawn.story.HairColor;
            Color32 c2 = newColor;
            if (c1.r != c2.r || c1.g != c2.g || c1.b != c2.b)
            {
                pawn.story.HairColor = newColor;

                if (pawn.Spawned && pawn.Drawer?.renderer != null)
                {
                    pawn.Drawer.renderer.SetAllGraphicsDirty();
                    PortraitsCache.SetDirty(pawn);
                }
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
        // Используем прямой проброс приватного поля через ___pawn (быстрее рефлексии)
        static void Postfix(Pawn ___pawn)
        {
            if (FPMod.Settings == null || !FPMod.Settings.enableAgingVisuals) return;
            if (___pawn != null) FPUtility.SyncAgingVisuals(___pawn);
        }
    }

    [HarmonyPatch(typeof(Pawn), "SpawnSetup")]
    public static class Patch_Spawn_Visuals
    {
        static void Postfix(Pawn __instance)
        {
            if (FPMod.Settings == null || !FPMod.Settings.enableAgingVisuals) return;
            FPUtility.SyncAgingVisuals(__instance);
        }
    }

    [HarmonyPatch]
    public static class Patch_Biosculpter_Sync {
        public static bool Prepare() => ModsConfig.IdeologyActive && AccessTools.Method("RimWorld.CompBiosculpterPod_AgeReversalCycle:CycleCompleted") != null;
        public static MethodBase TargetMethod() => AccessTools.Method("RimWorld.CompBiosculpterPod_AgeReversalCycle:CycleCompleted");
        static void Postfix(Pawn pawn) 
        {
            if (FPMod.Settings == null || !FPMod.Settings.enableAgingVisuals) return;
            FPUtility.SyncAgingVisuals(pawn);
        }
    }

    [HarmonyPatch]
    public static class Patch_Ritual_Sync {
        public static bool Prepare() => ModsConfig.AnomalyActive && AccessTools.Method("RimWorld.PsychicRitualToil_Chronophagy:AgePawn") != null;
        public static MethodBase TargetMethod() => AccessTools.Method("RimWorld.PsychicRitualToil_Chronophagy:AgePawn");
        static void Postfix(Pawn pawn) 
        {
            if (FPMod.Settings == null || !FPMod.Settings.enableAgingVisuals) return;
            FPUtility.SyncAgingVisuals(pawn);
        }
    }

    [HarmonyPatch]
    public static class Patch_Ritual_Reverse_Sync {
        public static bool Prepare() => ModsConfig.AnomalyActive && AccessTools.Method("RimWorld.PsychicRitualToil_Chronophagy:ReverseAgePawn") != null;
        public static MethodBase TargetMethod() => AccessTools.Method("RimWorld.PsychicRitualToil_Chronophagy:ReverseAgePawn");
        static void Postfix(Pawn pawn) 
        {
            if (FPMod.Settings == null || !FPMod.Settings.enableAgingVisuals) return;
            FPUtility.SyncAgingVisuals(pawn);
        }
    }

    [HarmonyPatch]
    public static class Patch_VPE_Chronopath_Sync {
        public static bool Prepare() => ModLister.HasActiveModWithName("Vanilla Psycasts Expanded") && AccessTools.Method("VanillaPsycastsExpanded.Chronopath.AbilityExtension_Age:Age") != null;
        public static MethodBase TargetMethod() => AccessTools.Method("VanillaPsycastsExpanded.Chronopath.AbilityExtension_Age:Age");
        static void Postfix(Pawn pawn) 
        {
            if (FPMod.Settings == null || !FPMod.Settings.enableAgingVisuals) return;
            FPUtility.SyncAgingVisuals(pawn);
        }
    }
}
