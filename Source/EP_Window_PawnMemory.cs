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

public class Window_PawnMemory : Window
{
    private Pawn pawn;
    private DeceasedPawnRecord deceasedRecord;
    private int thingID;
    private string pawnLabel;
    private WorldPopulationManager manager;
    private string currentNote = "";

    public override Vector2 InitialSize => new Vector2(650f, 500f);

    public Window_PawnMemory(Pawn pawn)
    {
        this.pawn = pawn;
        this.thingID = pawn.thingIDNumber;
        this.pawnLabel = pawn.LabelShort;
        Init();
    }

    public Window_PawnMemory(int id, DeceasedPawnRecord record)
    {
        this.deceasedRecord = record;
        this.thingID = id;
        this.pawnLabel = record.name;
        Init();
    }

    private void Init()
    {
        this.manager = Find.World?.GetComponent<WorldPopulationManager>();
        this.doCloseButton = true; 
        this.doCloseX = true;      
        this.absorbInputAroundWindow = false; 

        if (manager != null && manager.pawnNotes.TryGetValue(thingID, out string savedNote))
        {
            currentNote = savedNote;
        }
    }

    public override void DoWindowContents(Rect inRect)
    {
        if (manager == null) return;

        bool isDead = deceasedRecord != null || (pawn != null && pawn.Dead);
        bool isVeteran = manager.allVeteranIdsCache.Contains(thingID);
        bool isPinned = manager.manualVeteranPins.Contains(thingID);

        // --- ЗАГОЛОВОК ---
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0, 0, inRect.width, 35f), "FP_MemoryHeader".Translate(pawnLabel));
        Text.Font = GameFont.Small;

        // --- ДАТА ДОБАВЛЕНИЯ ---
        int addedTicks = (deceasedRecord != null) ? deceasedRecord.addedTick : 0;
        if (addedTicks == 0 && manager.veteranAddTicks.TryGetValue(thingID, out int t)) addedTicks = t;

        if (addedTicks > 0)
        {
            Rect dateRect = new Rect(0, 35f, inRect.width, 24f); 
            GUI.color = Color.gray;
            Widgets.Label(dateRect, "FP_AddedOn".Translate(GenDate.DateFullStringAt((long)addedTicks, Vector2.zero)));
            GUI.color = Color.white;
        }

        // --- СТАТУС (ТОЛЬКО ДЛЯ ЖИВЫХ) ---
        if (!isDead)
        {
            Rect statusRect = new Rect(0, 60f, inRect.width, 40f);
            if (isVeteran)
            {
                GUI.color = Color.cyan;
                Widgets.Label(statusRect, "FP_StatusVeteran".Translate());
            }
            else if (isPinned)
            {
                GUI.color = Color.yellow;
                Widgets.Label(statusRect, "FP_StatusPinned".Translate());
            }
            GUI.color = Color.white;
        }

        // --- КНОПКА ДЕЙСТВИЯ ---
        Rect btnRect = new Rect(0, 105f, 180f, 30f); 
        
        if (isDead)
        {
            // У мертвых нет кнопки действий, просто место
        }
        else if (isVeteran)
        {
            if (Widgets.ButtonText(btnRect, "FP_AlreadyInHistory".Translate()))
            {
                Messages.Message("FP_AlreadyInHistoryMsg".Translate(), MessageTypeDefOf.NeutralEvent, false);
            }
        }
        else if (isPinned)
        {
            if (Widgets.ButtonText(btnRect, "FP_ForgetPawn".Translate()))
            {
                manager.manualVeteranPins.Remove(thingID);
				manager.pawnNotes.Remove(thingID);
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
            }
        }
        else
        {
            if (Widgets.ButtonText(btnRect, "FP_RememberPawn".Translate()))
            {
                manager.manualVeteranPins.Add(thingID);
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }
        }

        // --- НОВАЯ КНОПКА: СОБЫТИЯ ---
        if (FPMod.Settings.enableEventLogging)
        {
            Rect eventsBtnRect = new Rect(isDead ? 0 : 190f, 105f, 140f, 30f);
            if (Widgets.ButtonText(eventsBtnRect, "FP_EventsButton".Translate()))
            {
                if (deceasedRecord != null) Find.WindowStack.Add(new Window_PawnEvents(thingID, pawnLabel));
                else Find.WindowStack.Add(new Window_PawnEvents(pawn));
            }
        }

        // --- ПОЛЕ ДЛЯ ЗАМЕТОК ---
        Rect labelRect = new Rect(0, 155f, inRect.width, 24f);
        Widgets.Label(labelRect, "FP_PersonalNotes".Translate());

        Rect textRect = new Rect(0, 180f, inRect.width, inRect.height - 230f); 
        string newNote = Widgets.TextArea(textRect, currentNote);

        if (newNote != currentNote)
        {
            currentNote = newNote;
            if (string.IsNullOrWhiteSpace(currentNote)) manager.pawnNotes.Remove(thingID);
            else manager.pawnNotes[thingID] = currentNote;
        }
    }
}

[HarmonyPatch(typeof(SocialCardUtility), "DrawSocialCard")]
public static class Patch_DrawSocialCardButton
{
    [HarmonyPrefix] // 1. МЕНЯЕМ Postfix НА Prefix
    static void Prefix(Rect rect, Pawn pawn) // 2. МЕНЯЕМ ИМЯ МЕТОДА НА Prefix
    {
// ПРОВЕРКА НАСТРОЕК
        if (FPMod.Settings == null || !FPMod.Settings.showVIPButton) return;
		
		if (!FPUtility.IsPawnSavable(pawn)) return;

        var manager = Find.World?.GetComponent<WorldPopulationManager>();
        if (manager == null) return;

        // Рисуем кнопку в правом верхнем углу вкладки Социум
        Rect btnRect = new Rect(rect.width - 130f, 45f, 100f, 24f);
        
        bool isVeteran = manager.allVeteranIdsCache.Contains(pawn.thingIDNumber);
        bool isPinned = manager.manualVeteranPins.Contains(pawn.thingIDNumber);
        bool hasNote = manager.pawnNotes.ContainsKey(pawn.thingIDNumber);

        // Цветовая индикация на самой кнопке
        if (isVeteran) GUI.color = Color.cyan;
        else if (isPinned || hasNote) GUI.color = Color.yellow;
        else GUI.color = Color.white;

        // Так как это Prefix, кнопка отрендерится и проверит клик ДО ванильного кода
        if (Widgets.ButtonText(btnRect, "FP_MemoryButton".Translate()))
        {
            Find.WindowStack.Add(new Window_PawnMemory(pawn));
        }
        
        GUI.color = Color.white;
    }
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
public static class Patch_Pawn_GetGizmos
{
    [HarmonyPostfix]
    static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
    {
        // 1. Сначала возвращаем все ванильные гизмо (призыв, и т.д.)
        if (__result != null)
        {
            foreach (var gizmo in __result)
            {
                yield return gizmo;
            }
        }

        // 2. ПРОВЕРКА НАСТРОЕК
		if (FPMod.Settings == null || !FPMod.Settings.showGizmoButton) yield break;
        
        // 3. НОВАЯ ПРОВЕРКА: Пускаем попрошаек и дикарей (Faction может быть null)
		if (!FPUtility.IsPawnSavable(__instance)) yield break;

        var manager = Find.World?.GetComponent<WorldPopulationManager>();
        if (manager == null) yield break;

        // 4. ОПРЕДЕЛЯЕМ СТАТУС (для цвета)
        bool isVeteran = manager.allVeteranIdsCache.Contains(__instance.thingIDNumber);
        bool isPinned = manager.manualVeteranPins.Contains(__instance.thingIDNumber);
        bool hasNote = manager.pawnNotes.ContainsKey(__instance.thingIDNumber);

        // 5. СОЗДАЕМ КНОПКУ-ГИЗМО
        Command_Action memoryGizmo = new Command_Action
        {
            defaultLabel = "FP_MemoryButton".Translate(),
            defaultDesc = "FP_OpenPawnMemoryPanelDesc".Translate(),
            

            icon = ContentFinder<Texture2D>.Get("UI/Icons/EP_MemoryIcon"), 
            
            action = delegate
            {
                // При клике открываем то же самое окно
                Find.WindowStack.Add(new Window_PawnMemory(__instance));
            }
        };

        // 6. КРАСИМ ИКОНКУ В ЗАВИСИМОСТИ ОТ СТАТУСА (Точно как текст в окне Социума)
        if (isVeteran) memoryGizmo.defaultIconColor = Color.cyan;
        else if (isPinned || hasNote) memoryGizmo.defaultIconColor = Color.yellow;
        else memoryGizmo.defaultIconColor = Color.white;

        // Отдаем нашу кнопку в игру
        yield return memoryGizmo;
    }
}


}