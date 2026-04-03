using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using UnityEngine;

namespace FinitePopulationVeterans
{
    public class Window_PawnEvents : Window
    {
        private Pawn pawn;
        private int thingID;
        private string pawnLabel;
        private Vector2 scrollPosition = Vector2.zero;
        private WorldPopulationManager manager;

        public override Vector2 InitialSize => new Vector2(500f, 600f);

        public Window_PawnEvents(Pawn pawn)
        {
            this.pawn = pawn;
            this.thingID = pawn.thingIDNumber;
            this.pawnLabel = pawn.LabelShort;
            Init();
        }

        public Window_PawnEvents(int id, string label)
        {
            this.thingID = id;
            this.pawnLabel = label;
            Init();
        }

        private void Init()
        {
            this.doCloseButton = true;
            this.doCloseX = true;
            this.closeOnClickedOutside = true;
            this.absorbInputAroundWindow = true;
            this.manager = Find.World.GetComponent<WorldPopulationManager>();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0, 0, inRect.width, 35f), "FP_EventsHeader".Translate(pawnLabel));
            Text.Font = GameFont.Small;

            Rect outRect = new Rect(0, 45f, inRect.width, inRect.height - 100f);
            
            if (manager == null || !manager.pawnEvents.TryGetValue(thingID, out List<PawnEvent> events) || events.Count == 0)
            {
                Widgets.Label(outRect, "FP_NoEventsCaught".Translate());
                return;
            }

            // Группируем по годам для красоты (хронологический порядок)
            var groupedEvents = events.OrderBy(e => e.ticks)
                                      .GroupBy(e => GenDate.Year(e.ticks, 0f));

            float viewHeight = 0f;
            foreach (var group in groupedEvents)
            {
                viewHeight += 30f; // Заголовок года
                viewHeight += group.Count() * 25f; // Сами события
                viewHeight += 10f; // Разделитель
            }

            Rect viewRect = new Rect(0, 0, inRect.width - 16f, viewHeight);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);

            float currentY = 0f;
            foreach (var group in groupedEvents)
            {
                // Год
                GUI.color = Color.gray;
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(0, currentY, viewRect.width, 30f), "FP_YearHeader".Translate(group.Key));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                currentY += 30f;

                foreach (var ev in group)
                {
                    string dateStr = GenDate.DateReadoutStringAt(ev.ticks, Vector2.zero);
                    string fullText = $"• {dateStr}: {ev.text}";
                    
                    float height = Text.CalcHeight(fullText, viewRect.width - 10f);
                    Widgets.Label(new Rect(10f, currentY, viewRect.width - 10f, height), fullText);
                    currentY += height + 2f;
                }
                currentY += 10f;
            }

            Widgets.EndScrollView();
        }
    }
}
