using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.Sound;
using UnityEngine;

namespace FinitePopulationVeterans
{
	
		// --- НАСТРОЙКИ МОДА ---
    public class FPSettings : ModSettings
    {
        public bool enableFactionLimit = true;
		public bool showVIPButton = true;
        public int factionVeteranLimit = 100;
		public int veteranRecallCooldownDays = 10;
        public int forcedFreezeDays = 0;
        public float veteranRecallChance = 0.5f;
        public bool enableDebugLogs = false;
		public float deathChanceMultiplier = 1f;
		public float diseaseChanceMultiplier = 1f;
		public float implantChanceMultiplier = 1f;
		public float geneChanceMultiplier = 1f;
		public float anomalyChanceMultiplier = 1f;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref enableFactionLimit, "enableFactionLimit", true);
			Scribe_Values.Look(ref showVIPButton, "showVIPButton", true);
            Scribe_Values.Look(ref factionVeteranLimit, "factionVeteranLimit", 100);
            Scribe_Values.Look(ref forcedFreezeDays, "forcedFreezeDays", 0);
            Scribe_Values.Look(ref veteranRecallChance, "veteranRecallChance", 0.5f);
            Scribe_Values.Look(ref enableDebugLogs, "enableDebugLogs", true);
			Scribe_Values.Look(ref deathChanceMultiplier, "deathChanceMultiplier", 1f);
			Scribe_Values.Look(ref diseaseChanceMultiplier, "diseaseChanceMultiplier", 1f);
			Scribe_Values.Look(ref implantChanceMultiplier, "implantChanceMultiplier", 1f);
			Scribe_Values.Look(ref geneChanceMultiplier, "geneChanceMultiplier", 1f);
			Scribe_Values.Look(ref anomalyChanceMultiplier, "anomalyChanceMultiplier", 1f);
			Scribe_Values.Look(ref veteranRecallCooldownDays, "veteranRecallCooldownDays", 10);
        }
    }

    public class FPMod : Mod
    {
		
        public static FPSettings settings;
		public static FPSettings Settings => settings;

        public FPMod(ModContentPack content) : base(content)
        {
            settings = GetSettings<FPSettings>();
        }
		private static Vector2 scrollPosition = Vector2.zero;
public override void DoSettingsWindowContents(Rect inRect)
{
    // 1. Область прокрутки
    Rect viewRect = new Rect(0f, 0f, inRect.width - 16f, 1000f);
    Widgets.BeginScrollView(inRect, ref scrollPosition, viewRect);

    Listing_Standard listing = new Listing_Standard();
    listing.Begin(viewRect);

    // --- Лимит ветеранов ---
    listing.CheckboxLabeled("FP_EnableFactionLimit".Translate(), ref settings.enableFactionLimit, 
        "FP_EnableFactionLimitTooltip".Translate());
    
    if (settings.enableFactionLimit)
    {
        // Ипользуем именованные аргументы для устранения ошибки CS0121
        listing.Label(label: "FP_FactionVeteranLimit".Translate(settings.factionVeteranLimit), 
        tooltip: "FP_FactionVeteranLimitTooltip".Translate());
        settings.factionVeteranLimit = (int)listing.Slider(settings.factionVeteranLimit, 0f, 500f);
    }
	// --- НОВАЯ ГАЛОЧКА ДЛЯ КНОПКИ ---
    listing.CheckboxLabeled("FP_ShowVIPButton".Translate(), ref settings.showVIPButton, 
        "FP_ShowVIPButtonTooltip".Translate());
    listing.Gap(12f);
	

    // --- Отдых ---
    listing.Label(label: "FP_VeteranRecallCooldownDays".Translate(settings.veteranRecallCooldownDays), tooltip: "FP_VeteranRecallCooldownDaysTooltip".Translate());
    settings.veteranRecallCooldownDays = (int)listing.Slider(settings.veteranRecallCooldownDays, 0f, 60f);

    // --- Заморозка ---
    listing.Label(label: "FP_ForcedFreezeDays".Translate(settings.forcedFreezeDays), tooltip: "FP_ForcedFreezeDaysTooltip".Translate());
    settings.forcedFreezeDays = (int)listing.Slider(settings.forcedFreezeDays, 0f, 100f);

    listing.GapLine();

    // --- Шанс призыва ---
    listing.Label(label: "FP_VeteranRecallChance".Translate(Math.Round(settings.veteranRecallChance * 100)), tooltip: "FP_VeteranRecallChanceTooltip".Translate());
    settings.veteranRecallChance = listing.Slider(settings.veteranRecallChance, 0f, 1f);

// --- Смертность (теперь только 60+) ---
    string deathDesc = "FP_DeathDesc".Translate();
    
    listing.Label(label: "FP_DeathChanceMultiplier".Translate(Math.Round(settings.deathChanceMultiplier * 100)), tooltip: deathDesc);
    settings.deathChanceMultiplier = listing.Slider(settings.deathChanceMultiplier, 0f, 5f);
	

    // --- Болезни ---
    string diseaseDesc = "FP_DiseaseDesc".Translate();

    listing.Label(label: "FP_DiseaseChanceMultiplier".Translate(Math.Round(settings.diseaseChanceMultiplier * 100)), tooltip: diseaseDesc);
    settings.diseaseChanceMultiplier = listing.Slider(settings.diseaseChanceMultiplier, 0f, 5f);

    listing.GapLine();

    // --- Эволюция ---
	// --- Импланты ---
    string implantDesc = "FP_ImplantDesc".Translate();
    
    listing.Label(label: "FP_ImplantChanceMultiplier".Translate(Math.Round(settings.implantChanceMultiplier * 100)), tooltip: implantDesc);
    settings.implantChanceMultiplier = listing.Slider(settings.implantChanceMultiplier, 0f, 5f);

    listing.Label(label: "FP_GeneChanceMultiplier".Translate(Math.Round(settings.geneChanceMultiplier * 100)), tooltip: "FP_GeneChanceTooltip".Translate());
    settings.geneChanceMultiplier = listing.Slider(settings.geneChanceMultiplier, 0f, 5f);

    listing.Label(label: "FP_AnomalyChanceMultiplier".Translate(Math.Round(settings.anomalyChanceMultiplier * 100)), tooltip: "FP_AnomalyChanceTooltip".Translate());
    settings.anomalyChanceMultiplier = listing.Slider(settings.anomalyChanceMultiplier, 0f, 5f);

    listing.GapLine();

    listing.CheckboxLabeled("FP_EnableDebugLogs".Translate(), ref settings.enableDebugLogs, "FP_EnableDebugLogsTooltip".Translate());
    
    listing.Gap(15f);

if (listing.ButtonText("FP_ClearDatabaseButton".Translate()))
    {
        if (Current.ProgramState == ProgramState.Playing && Find.World != null)
        {
            // Вызываем стандартное окно подтверждения (true делает кнопку "Да" красной)
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("FP_ClearDatabaseConfirm".Translate(), delegate
            {
                var manager = Find.World.GetComponent<WorldPopulationManager>();
                if (manager != null)
                {
                    manager.veteranPool.Clear();
                    manager.allVeteranIdsCache.Clear();
                    manager.veteranAddTicks.Clear();
                    manager.veteransOnMission.Clear();
                    Messages.Message("FP_DatabaseClearedMessage".Translate(), MessageTypeDefOf.TaskCompletion, false);
                }
            }, true));
        }
    }

    listing.End();
    Widgets.EndScrollView();
    base.DoSettingsWindowContents(inRect);
}

        public override string SettingsCategory()
        {
            return "FP_SettingsCategory".Translate(); // Название мода в меню настроек
        }
    }
	
	
	
	
	
[StaticConstructorOnStartup]
    public static class ModStartup
    {
        static ModStartup()
        {
            new Harmony("helldan.finitepopulation.veterans").PatchAll();
            Log.Message("<color=green>[Finite Population]</color> Veterans Module Loaded: Smart Compatibility Active.");
        }
    }


}