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

        private static readonly Color[] NaturalHairColors = new Color[] 
        {
            new Color(0.2f, 0.20f, 0.20f), // Black
            new Color(0.35f, 0.23f, 0.15f), // Dark Brown
            new Color(0.55f, 0.40f, 0.25f), // Light Brown
            new Color(0.90f, 0.85f, 0.50f), // Blonde
            new Color(0.70f, 0.40f, 0.20f)  // Reddish
        };

        public static void SyncAgingVisuals(Pawn pawn)
        {
            if (pawn == null || !pawn.RaceProps.Humanlike || pawn.story == null || pawn.ageTracker == null) return;
            if (pawn.IsColonyMech || pawn.IsGhoul || (ModsConfig.AnomalyActive && pawn.IsMutant)) return;
            if (pawn.Faction != null && pawn.Faction.def?.defName == "Entities") return;

            var settings = FPMod.Settings;
            if (settings == null || !settings.enableAgingVisuals) return;

            var manager = Find.World?.GetComponent<WorldPopulationManager>();
            if (manager == null) return;

            Color baseColor = pawn.story.HairColor;
            bool hasSavedAnchor = manager.originalHairColors.TryGetValue(pawn.thingIDNumber, out Color remembered);

            if (hasSavedAnchor)
            {
                baseColor = remembered;
                
                // ДЕТЕКТОР КРАСКИ (Dye Detection)
                // Если текущий цвет СИЛЬНО отличается от того, что МЫ насчитали - значит игрок покрасил пешку
                Color expectedNow = GetColorForAge(pawn, baseColor);
                Color32 cReal = pawn.story.HairColor;
                Color32 cExp = expectedNow;
                
                if (cReal.r != cExp.r || cReal.g != cExp.g || cReal.b != cExp.b)
                {
                    Color.RGBToHSV(pawn.story.HairColor, out float h, out float s, out float v);
                    // Если новый цвет НЕ седой (есть пигмент s > 0.08) - принимаем его как новый эталон
                    if (s > 0.08f)
                    {
                        baseColor = pawn.story.HairColor;
                        manager.originalHairColors[pawn.thingIDNumber] = baseColor;
                    }
                }
            }
            else // Записи в базе нет — ищем отправную точку
            {
                bool gotGeneric = false;
                // А) Смотрим гены (если есть и активны)
                if (pawn.genes != null)
                {
                    foreach (var g in pawn.genes.GenesListForReading) 
                    {
                        if (!g.Active) continue;
                        if (g.def.neverGrayHair) return; // Сразу выходим (запрет седины)
                        if (g.def.hairColorOverride.HasValue) 
                        {
                            baseColor = g.def.hairColorOverride.Value; 
                            gotGeneric = true;
                        }
                    }
                }

                // Б) Проверяем на "уже седой" при первом знакомстве
                Color.RGBToHSV(pawn.story.HairColor, out float h, out float s, out float v);
                float grayStart = pawn.RaceProps.lifeExpectancy * settings.startGrayingHairRatio;
                
                if (s < 0.05f && v > 0.6f && pawn.ageTracker.AgeBiologicalYearsFloat < grayStart)
                {
                    // Если пешка еще молодая, но уже белая — значит игра выдала седину по ошибке, восстанавливаем
                    if (!gotGeneric) 
                    {
                        int colorIdx = Math.Abs(pawn.thingIDNumber) % NaturalHairColors.Length;
                        baseColor = NaturalHairColors[colorIdx];
                    }
                    // Если был ген, baseColor уже установлен из него
                }
                else
                {
                    // Сохраняем текущий цвет как базу (даже если он от генов или Natural)
                    baseColor = pawn.story.HairColor;
                }
                
                manager.originalHairColors[pawn.thingIDNumber] = baseColor;
            }

            // 2. Установка цвета
            Color newColor = GetColorForAge(pawn, baseColor);
            Color32 c1 = pawn.story.HairColor;
            Color32 c2 = newColor;
            
            if (c1.r != c2.r || c1.g != c2.g || c1.b != c2.b)
            {
                pawn.story.HairColor = newColor;

                if (pawn.Spawned && pawn.Drawer?.renderer != null)
                {
                    try 
                    {
                        pawn.Drawer.renderer.SetAllGraphicsDirty();
                        PortraitsCache.SetDirty(pawn);
                    }
                    catch { /* Глушим ошибки инициализации UI */ }
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