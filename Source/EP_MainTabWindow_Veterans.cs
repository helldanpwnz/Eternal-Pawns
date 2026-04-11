using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;
using HarmonyLib;

namespace FinitePopulationVeterans
{
    public class MainTabWindow_Veterans : MainTabWindow
    {
        private Vector2 scrollPosition = Vector2.zero;
        private static HashSet<int> expandedFactions = new HashSet<int>();

        public override Vector2 InitialSize => new Vector2(450f, 600f);

        public override void DoWindowContents(Rect inRect)
        {
            var manager = Find.World?.GetComponent<WorldPopulationManager>();
            if (manager == null) 
            {
                Widgets.Label(inRect, "FP_WorldManagerNotFound".Translate());
                return;
            }

            // --- ЗАГОЛОВОК ---
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 35f), "FP_VeteransTabHeader".Translate());
            Text.Font = GameFont.Small;

            // --- ОБЛАСТЬ СПИСКА ---
            Rect outRect = new Rect(0, 45f, inRect.width, inRect.height - 55f);
            
            // Считаем высоту контента для скролла
            float viewHeight = 0f;
            var sortedPool = manager.veteranPool.OrderBy(x => GetFactionName(x.Key)).ToList();

            foreach (var kvp in sortedPool)
            {
                if (kvp.Value == null || kvp.Value.pawns.Count == 0) continue;
                viewHeight += 40f; // Высота кнопки фракции
                if (expandedFactions.Contains(kvp.Key))
                {
                    viewHeight += kvp.Value.pawns.Count * 28f; // Высота строк пешек
                    // Добавляем высоту для мертвых записей
                    int deadCountForFaction = manager.deceasedPawns.Values.Count(d => d.factionId == kvp.Key);
                    viewHeight += deadCountForFaction * 28f;
                }
                viewHeight += 10f; // Gap
            }

            Rect viewRect = new Rect(0, 0, outRect.width - 16f, viewHeight + 20f);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            
            float curY = 0f;
            foreach (var kvp in sortedPool)
            {
                int factionId = kvp.Key;
                var group = kvp.Value;
                if (group == null || group.pawns.Count == 0) continue;

                Faction faction = Find.FactionManager.AllFactions.FirstOrDefault(f => f.loadID == factionId);

                // Кнопка-заголовок фракции
                Rect factionRect = new Rect(0, curY, viewRect.width, 40f);
                
                string factionName = faction?.Name ?? "FP_UnknownFaction".Translate();
                string arrow = expandedFactions.Contains(factionId) ? "▼ " : "► ";
                
                if (Widgets.ButtonText(factionRect, "", true, true, true))
                {
                    if (expandedFactions.Contains(factionId)) expandedFactions.Remove(factionId);
                    else expandedFactions.Add(factionId);
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                }

                // Отрисовка названия и иконки поверх пустой кнопки
                Rect contentRect = factionRect.ContractedBy(2f);
                if (faction != null)
                {
                    GUI.color = faction.Color;
                    // Смещаем иконку на 8 пикселей от левого края (включая отступ) и центрируем вертикально
                    Rect iconRect = new Rect(contentRect.x + 8f, contentRect.y + (contentRect.height - 24f) / 2f, 24f, 24f);
                    Widgets.DrawTextureFitted(iconRect, faction.def.FactionIcon, 1f);
                    GUI.color = Color.white;
                }
                
                // Посчитываем живых и мертвых отдельно
                int simDeadCount = group.pawns.Count(p => p != null && p.Dead);
                int recordDeadCount = manager.deceasedPawns.Values.Count(d => d.factionId == factionId);
                int livingCount = group.pawns.Count - simDeadCount;
                string mainLabel = arrow + factionName + $" ({livingCount})";
                
                Text.Anchor = TextAnchor.MiddleLeft;
                // Смещаем текст, чтобы он был на фиксированном расстоянии от иконки
                Rect labelRect = new Rect(contentRect.x + 40f, contentRect.y, contentRect.width - 40f, contentRect.height);
                Widgets.Label(labelRect, mainLabel);

                int totalDead = simDeadCount + recordDeadCount;
                if (totalDead > 0)
                {
                    float widthOffset = Text.CalcSize(mainLabel).x + 5f;
                    Rect deadCountRect = new Rect(labelRect.x + widthOffset, labelRect.y, labelRect.width - widthOffset, labelRect.height);
                    GUI.color = Color.red;
                    Widgets.Label(deadCountRect, $"({totalDead})");
                    GUI.color = Color.white;
                }
                Text.Anchor = TextAnchor.UpperLeft;

                curY += 40f;

                // Список пешек внутри фракции
                if (expandedFactions.Contains(factionId))
                {
                    foreach (var p in group.pawns)
                    {
                        if (p == null) continue;
                        Rect pawnRect = new Rect(10f, curY, viewRect.width - 10f, 26f);
                        DrawPawnRow(pawnRect, p, manager);
                        curY += 28f;
                    }

                    // Отрисовка мертвых (текстовые ссылки)
                    foreach (var kvpDead in manager.deceasedPawns)
                    {
                        if (kvpDead.Value.factionId == factionId)
                        {
                            Rect deadRect = new Rect(10f, curY, viewRect.width - 10f, 26f);
                            DrawDeceasedPawnRow(deadRect, kvpDead.Key, kvpDead.Value, manager);
                            curY += 28f;
                        }
                    }
                }
                curY += 10f;
            }

            Widgets.EndScrollView();
        }

        private string GetFactionName(int loadId)
        {
            var faction = Find.FactionManager.AllFactions.FirstOrDefault(f => f.loadID == loadId);
            return faction?.Name ?? "FP_UnknownFaction".Translate();
        }

        private void DrawPawnRow(Rect rect, Pawn p, WorldPopulationManager manager)
        {
            // Подсветка при наведении
            Widgets.DrawHighlightIfMouseover(rect);

            int id = p.thingIDNumber;
            bool isPinned = manager.manualVeteranPins.Contains(id);
            bool hasNote = manager.pawnNotes.ContainsKey(id);

            // Имя и возраст
            string label = p.LabelShort;
            if (p.ageTracker != null) label += $", {p.ageTracker.AgeBiologicalYears}";
            if (p.Dead) GUI.color = Color.red;

            Text.Anchor = TextAnchor.MiddleLeft;
            float labelWidth = Text.CalcSize(label).x;
            Rect nameRect = new Rect(rect.x, rect.y, labelWidth, rect.height);
            Widgets.Label(nameRect, label);
            GUI.color = Color.white;
            TooltipHandler.TipRegion(nameRect, p.Label);
            Text.Anchor = TextAnchor.UpperLeft;

            // Иконки статуса (теперь после имени)
            float iconX = nameRect.xMax + 5f;
            if (isPinned)
            {
                GUI.color = Color.yellow;
                Widgets.DrawTextureFitted(new Rect(iconX, rect.y + 4f, 18f, 18f), Widgets.CheckboxOnTex, 1f);
                GUI.color = Color.white;
                iconX += 22f;
            }
            if (hasNote)
            {
                Widgets.DrawTextureFitted(new Rect(iconX, rect.y + 4f, 18f, 18f), ContentFinder<Texture2D>.Get("UI/Icons/EP_notepad"), 1f);
                iconX += 22f;
            }

            // Кнопка подробностей (выровнена по правой границе окна)
            Rect btnRect = new Rect(rect.xMax - 110f, rect.y + 1f, 110f, 24f);
            if (Widgets.ButtonText(btnRect, "FP_Details".Translate()))
            {
                Find.WindowStack.Add(new Window_PawnMemory(p));
            }
        }

        private void DrawDeceasedPawnRow(Rect rect, int id, DeceasedPawnRecord record, WorldPopulationManager manager)
        {
            Widgets.DrawHighlightIfMouseover(rect);

            GUI.color = Color.red; // Мертвые в списке теперь красные
            Text.Anchor = TextAnchor.MiddleLeft;
            string label = record.name + ", " + record.bioAge;
            float labelWidth = Text.CalcSize(label).x;
            Rect nameRect = new Rect(rect.x, rect.y, labelWidth, rect.height);
            Widgets.Label(nameRect, label);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;

            Rect btnRect = new Rect(rect.xMax - 110f, rect.y + 1f, 110f, 24f);
            // "Текстовая ссылка" на историю
            if (Widgets.ButtonText(btnRect, "FP_History".Translate()))
            {
                Find.WindowStack.Add(new Window_PawnMemory(id, record));
            }
        }
    }

    // --- ПАТЧ ДЛЯ СКРЫТИЯ ВКЛАДКИ ЧЕРЕЗ НАСТРОЙКИ (Ручная регистрация в ModStartup) ---
    public static class Patch_MainButton_Visibility
    {
        public static void Postfix(MainButtonDef __instance, ref bool __result)
        {
            if (__instance.defName == "FP_VeteransMainTab")
            {
                if (FPMod.Settings == null) return;
                __result = __result && FPMod.Settings.showMainTab;
            }
        }
    }
}
