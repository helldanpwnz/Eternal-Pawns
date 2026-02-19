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
    [HarmonyPostfix]
    static void Postfix(Rect rect, Pawn pawn)
    {
        // ПРОВЕРКА НАСТРОЕК
        if (FPMod.Settings == null || !FPMod.Settings.showVIPButton) return;
        if (pawn == null || pawn.Faction == null || pawn.Faction.IsPlayer || !pawn.RaceProps.Humanlike) return;

        var manager = Find.World?.GetComponent<WorldPopulationManager>();
        if (manager == null) return;

        // Рисуем кнопку в правом верхнем углу вкладки Социум
        Rect btnRect = new Rect(rect.width - 130f, // позиция по горизонтали (X). Отступает 130 пикселей от правого края.
		45f, // позиция по вертикали (Y). Это отступ сверху. Вот она тебе и нужна!
		100f, // ширина самой кнопки.
		24f); // высота самой кнопки.
        
        bool isVeteran = manager.allVeteranIdsCache.Contains(pawn.thingIDNumber);
        bool isPinned = manager.manualVeteranPins.Contains(pawn.thingIDNumber);
        bool hasNote = manager.pawnNotes.ContainsKey(pawn.thingIDNumber);

        // Цветовая индикация на самой кнопке
        if (isVeteran) GUI.color = Color.cyan;
        else if (isPinned || hasNote) GUI.color = Color.yellow;
        else GUI.color = Color.white;

        if (Widgets.ButtonText(btnRect, "FP_MemoryButton".Translate()))
        {
            Find.WindowStack.Add(new Window_PawnMemory(pawn));
        }
        
        GUI.color = Color.white;
    }
}


}