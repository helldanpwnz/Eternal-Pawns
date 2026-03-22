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
    private WorldPopulationManager manager;
    private string currentNote = "";

    public override Vector2 InitialSize => new Vector2(650f, // ШИРИНА ОКНА
	500f); // ВЫСОТА ОКНА

    public Window_PawnMemory(Pawn pawn)
    {
        this.pawn = pawn;
        this.manager = Find.World?.GetComponent<WorldPopulationManager>();
        
        this.doCloseButton = true; 
        this.doCloseX = true;      
        this.absorbInputAroundWindow = false; 

        if (manager != null && manager.pawnNotes.TryGetValue(pawn.thingIDNumber, out string savedNote))
        {
            currentNote = savedNote;
        }
    }

    public override void DoWindowContents(Rect inRect)
    {
        if (manager == null || pawn == null) return;

        int id = pawn.thingIDNumber;
        bool isVeteran = manager.allVeteranIdsCache.Contains(id);
        bool isPinned = manager.manualVeteranPins.Contains(id);

        // --- ЗАГОЛОВОК ---
        Text.Font = GameFont.Medium;
        Widgets.Label(new Rect(0, 0, inRect.width, 35f), "FP_MemoryHeader".Translate(pawn.LabelShort));
        Text.Font = GameFont.Small;

        // --- АТМОСФЕРНЫЙ ТЕКСТ СТАТУСА (Из твоего старого тултипа) ---
        Rect statusRect = new Rect(0, 40f, inRect.width, 60f);
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
        else
        {
            GUI.color = Color.gray;
            Widgets.Label(statusRect, "FP_StatusUnknown".Translate());
        }
        GUI.color = Color.white;

        // --- КНОПКА ДЕЙСТВИЯ (Заменяет клик по звездочке) ---
        Rect btnRect = new Rect(0, 105f, 200f, 30f);
        
        if (isVeteran)
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
                manager.manualVeteranPins.Remove(id);
				manager.pawnNotes.Remove(id);
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
            }
        }
        else
        {
            if (Widgets.ButtonText(btnRect, "FP_RememberPawn".Translate()))
            {
                manager.manualVeteranPins.Add(id);
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }
        }

        // --- ПОЛЕ ДЛЯ ЗАМЕТОК ---
        Rect labelRect = new Rect(0, 145f, inRect.width, 24f);
        Widgets.Label(labelRect, "FP_PersonalNotes".Translate());

        Rect textRect = new Rect(0, 170f, inRect.width, inRect.height - 230f); 
        string newNote = Widgets.TextArea(textRect, currentNote);

        if (newNote != currentNote)
        {
            currentNote = newNote;
            if (string.IsNullOrWhiteSpace(currentNote)) manager.pawnNotes.Remove(id);
            else manager.pawnNotes[id] = currentNote;
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