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

    public class VeteranGroup : IExposable
    {
        public List<Pawn> pawns = new List<Pawn>();
        public void ExposeData() => Scribe_Collections.Look(ref pawns, "pawns", LookMode.Reference);
    }

    // --- ОЧЕРЕДЬ ---
    public static class VeteranInputQueue
    {
        public static HashSet<int> pendingPawnIDs = new HashSet<int>();
        private static Dictionary<int, Pawn> pendingPawns = new Dictionary<int, Pawn>();
        private static object _lock = new object();

        public static void Enqueue(Pawn p)
        {
            if (p == null || p.Dead || p.Destroyed || p.Discarded) return;
            int id = p.thingIDNumber;
            
            lock (_lock)
            {
                if (!pendingPawnIDs.Contains(id))
                {
                    pendingPawnIDs.Add(id);
                    pendingPawns[id] = p;
                }
            }
        }

        public static void ProcessQueue(WorldPopulationManager manager)
        {
            if (pendingPawns.Count == 0) return;

            Dictionary<int, Pawn> toProcess;
            lock (_lock)
            {
                toProcess = new Dictionary<int, Pawn>(pendingPawns);
                pendingPawns.Clear();
                pendingPawnIDs.Clear();
            }

            foreach (var kvp in toProcess)
            {
                Pawn pawn = kvp.Value;
                if (pawn != null && !pawn.Dead && !pawn.Discarded)
                {
                    try
                    {
                        manager.AddVeteran(pawn);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[FP] Ошибка сохранения ветерана {pawn.LabelShort}: {ex.Message}");
                    }
                }
            }
        }
		public static void Clear()
        {
            lock (_lock)
            {
                pendingPawnIDs.Clear();
                pendingPawns.Clear();
            }
        }
		
    }

    // --- МЕНЕДЖЕР ---
    public class WorldPopulationManager : WorldComponent
    {
		
        public Dictionary<int, VeteranGroup> veteranPool = new Dictionary<int, VeteranGroup>();
		public Dictionary<int, int> veteranAddTicks = new Dictionary<int, int>(); // Время добавления
		public Dictionary<int, string> pawnNotes = new Dictionary<int, string>();
		public Dictionary<int, long> savedBioAges = new Dictionary<int, long>();
private List<long> tmpBioValues; // Для сохранения
private List<int> tmpTicksKeys;   // Для сохранения
private List<int> tmpTicksValues; // Для сохранения
private int ticksToNextUpdate = -1; // По умолчанию 1 год
        
        // КЭШ ID
        public HashSet<int> allVeteranIdsCache = new HashSet<int>(); 
		// НОВЫЙ СПИСОК: VIP-бронь для тех, кто сейчас на карте
        public HashSet<int> veteransOnMission = new HashSet<int>();
		public HashSet<int> manualVeteranPins = new HashSet<int>();

        public static bool IsManuallyAdding = false;
        private List<int> tmpVeteranKeys;
        private List<VeteranGroup> tmpVeteranValues;
        
        private HashSet<int> pawnsIssuedThisTickIDs = new HashSet<int>();
        private int lastTickIssued = -1;
		private int ticksToNextYearUpdate = 0;

public WorldPopulationManager(World world) : base(world) { }

private void CleanPawnHealth(Pawn p, bool fullHeal)
{
    if (p.health?.hediffSet == null) return;
    var toRemove = p.health.hediffSet.hediffs.Where(h => 
        (h is Hediff_Injury inj && !inj.IsPermanent()) || 
        (fullHeal && (h.def.makesSickThought || h.def.tendable || h is Hediff_High)) ||
        h.def == HediffDefOf.BloodLoss
    ).ToList();
    foreach (var h in toRemove) p.health.RemoveHediff(h);
    if (p.needs != null) { 
        if (p.needs.food != null) p.needs.food.CurLevelPercentage = 1f;
        if (p.needs.rest != null) p.needs.rest.CurLevelPercentage = 1f;
    }
}

		

        public override void ExposeData()
        {
			// ДОБАВИТЬ ЭТО В НАЧАЛО: Убираем стертых пешек до записи в сейв
    if (Scribe.mode == LoadSaveMode.Saving)
    {
        foreach (var group in veteranPool.Values)
        {
            group.pawns.RemoveAll(p => p == null || p.Discarded);
        }
    }
			
            base.ExposeData();
			if (Scribe.mode == LoadSaveMode.LoadingVars)
        {
            VeteranInputQueue.Clear();
			FPSeenTracker.Clear();
        }
            Scribe_Collections.Look(ref veteranPool, "veteranPool", LookMode.Value, LookMode.Deep, ref tmpVeteranKeys, ref tmpVeteranValues);
            if (veteranPool == null) veteranPool = new Dictionary<int, VeteranGroup>();
			Scribe_Collections.Look(ref veteransOnMission, "veteransOnMission", LookMode.Value);
            if (veteransOnMission == null) veteransOnMission = new HashSet<int>();
			Scribe_Values.Look(ref ticksToNextYearUpdate, "ticksToNextYearUpdate", GenDate.TicksPerYear);
			Scribe_Collections.Look(ref veteranAddTicks, "veteranAddTicks", LookMode.Value, LookMode.Value, ref tmpTicksKeys, ref tmpTicksValues);
			Scribe_Collections.Look(ref manualVeteranPins, "manualVeteranPins", LookMode.Value);
			Scribe_Collections.Look(ref pawnNotes, "pawnNotes", LookMode.Value, LookMode.Value);
			Scribe_Values.Look(ref ticksToNextUpdate, "ticksToNextUpdate", -1);
			Scribe_Collections.Look(ref savedBioAges, "savedBioAges", LookMode.Value, LookMode.Value, ref tmpTicksKeys, ref tmpBioValues);
if (savedBioAges == null) savedBioAges = new Dictionary<int, long>();
if (pawnNotes == null) pawnNotes = new Dictionary<int, string>();
if (manualVeteranPins == null) manualVeteranPins = new HashSet<int>();

            // ЧИСТКА ДУБЛИКАТОВ
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                allVeteranIdsCache.Clear();
                foreach (var group in veteranPool.Values)
                {
                    if (group.pawns != null)
                    {
                        group.pawns.RemoveAll(x => x == null);
                        HashSet<int> seenIds = new HashSet<int>();
                        List<Pawn> uniquePawns = new List<Pawn>();
                        
                        foreach (var p in group.pawns)
                        {
                            if (!seenIds.Contains(p.thingIDNumber))
                            {
                                seenIds.Add(p.thingIDNumber);
                                uniquePawns.Add(p);
                                allVeteranIdsCache.Add(p.thingIDNumber);
                            }
                            else
                            {
                                if (!p.Spawned && !p.Dead) p.Discard();
                            }
                        }
                        group.pawns = uniquePawns;
                    }
                }
				
				var activeIds = new HashSet<int>(allVeteranIdsCache);
var keys = veteranAddTicks.Keys.ToList();
foreach (var k in keys) if (!activeIds.Contains(k)) veteranAddTicks.Remove(k);

// --- ОЧИСТКА РУЧНЫХ МЕТОК ---
if (manualVeteranPins != null)
{
    // 1. Убираем тех, кто уже и так ветеран (на всякий случай)
    manualVeteranPins.RemoveWhere(id => allVeteranIdsCache.Contains(id));

    // 2. Убираем "призраков", которых игра уже стерла сборщиком мусора (GC)
    manualVeteranPins.RemoveWhere(id => 
        !Find.WorldPawns.AllPawnsAliveOrDead.Any(p => p.thingIDNumber == id) && 
        !Find.Maps.Any(m => m.mapPawns.AllPawns.Any(p => p.thingIDNumber == id))
    );
}
				
            }
        }

public override void WorldComponentTick()
{
    base.WorldComponentTick();
    VeteranInputQueue.ProcessQueue(this);

    // Если это первый тик новой игры — сразу считаем таймер по настройкам
    if (ticksToNextUpdate < 0)
    {
        float startRate = Mathf.Max(0.01f, Find.Storyteller.difficulty.adultAgingRate);
        ticksToNextUpdate = (int)(3600000 / startRate);
    }

    ticksToNextUpdate--;
    if (ticksToNextUpdate <= 0)
    {
        // Читаем настройку и заводим таймер на следующий круг
        float rate = Mathf.Max(0.01f, Find.Storyteller.difficulty.adultAgingRate);
        ticksToNextUpdate = (int)(3600000 / rate);

        if (FPMod.Settings.enableDebugLogs)
        {
            Log.Message($"<color=green>[FP-Timer]</color> Цикл запущен! След. через {ticksToNextUpdate / 60000} дней.");
        }

        ProcessYearlyVeteranAging();
    }
}

public void AddVeteran(Pawn p)
{
    // 1. Базовые проверки (отсекаем мусор сразу)
    if (p == null || p.Faction == null || p.Faction.def.hidden || p.Dead || p.Discarded) return;

    int currentFid = p.Faction.loadID;
    if (!veteranPool.ContainsKey(currentFid)) veteranPool[currentFid] = new VeteranGroup();
    VeteranGroup group = veteranPool[currentFid];

// Узнаем: это наш старый дед вернулся с задания, или это новичок?
            bool isReturningVeteran = allVeteranIdsCache.Contains(p.thingIDNumber) || veteransOnMission.Contains(p.thingIDNumber);
			bool isPinned = manualVeteranPins.Contains(p.thingIDNumber);

// Проверка лимита
if (FPMod.Settings.enableFactionLimit && group.pawns.Count >= FPMod.Settings.factionVeteranLimit)
{
    // Если это не старый ветеран и не помеченный вручную VIP — выкидываем
    if (!isReturningVeteran && !isPinned) 
    {
        return; 
    }
}

// Снимаем метки (он вернулся / добавлен)
veteransOnMission.Remove(p.thingIDNumber);
if (isPinned) manualVeteranPins.Remove(p.thingIDNumber); // Одноразовый билет использован

// ДОБАВИТЬ ЭТО: Отвязываем от ИИ карты, чтобы игра не пыталась сохранить старый рейд
if (p.mindState != null) p.mindState.duty = null;
if (p.jobs != null)
{
    p.jobs.ClearQueuedJobs();
    p.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced, false);
}

// 3. ПОДГОТОВКА (Чистим здоровье)
CleanPawnHealth(p, false);

    // 3. ПОДГОТОВКА (Чистим здоровье)
    CleanPawnHealth(p, false);

    // 4. ПЕРЕДАЧА В МИР (Теперь это безопасно, так как мы знаем, что место в пуле есть)
    if (!Find.WorldPawns.Contains(p))
    {
        try
        {
            IsManuallyAdding = true;
            // Ставим KeepForever только тем, кого реально берем в ветераны
            Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.KeepForever);
        }
        catch (Exception ex)
        {
            Log.Warning($"[FP] Ошибка сохранения ветерана в мир: {ex.Message}");
            return;
        }
        finally { IsManuallyAdding = false; }
    }

// 5. ОБНОВЛЕНИЕ СПИСКОВ (Убираем дубликат если был, и записываем свежую версию)
    if (allVeteranIdsCache.Contains(p.thingIDNumber))
    {
        // Если он уже был ветераном, просто удаляем старую ссылку из списка фракции,
        // чтобы заменить её на свежую (с новыми ранами/опытом)
        group.pawns.RemoveAll(x => x.thingIDNumber == p.thingIDNumber);
    }
    else
    {
        // Если это новый человек — регистрируем его ID в общем кэше
        allVeteranIdsCache.Add(p.thingIDNumber);
    }

    group.pawns.Add(p);
    veteranAddTicks[p.thingIDNumber] = Find.TickManager.TicksGame;
	savedBioAges[p.thingIDNumber] = p.ageTracker.AgeBiologicalTicks;

    // 6. ЛОГИ
    if (FPMod.Settings.enableDebugLogs) 
    {
        Log.Message($"<color=orange>[FP]</color> Ветеран {p.LabelShort} (ID: {p.thingIDNumber}) успешно сохранен в пул.");
    }
}

        private void ProcessYearlyVeteranAging()
        {
			
			
            int deathCount = 0;
            int levelUpCount = 0;

            foreach (var group in veteranPool.Values)
            {
for (int i = group.pawns.Count - 1; i >= 0; i--)
                {
                    Pawn p = group.pawns[i];

                    // 1. СНАЧАЛА проверяем на смерть (и вычищаем из всех списков, включая миссии)
                    if (p == null || p.Dead || p.Discarded) 
                    { 
                        if (p != null) 
                        {
                            allVeteranIdsCache.Remove(p.thingIDNumber);
                            veteranAddTicks.Remove(p.thingIDNumber); 
                            veteransOnMission.Remove(p.thingIDNumber); // <--- ВАЖНО: забираем пропуск у трупа
                        }
                        group.pawns.RemoveAt(i); 
                        continue; 
                    }

                    // 2. И ТОЛЬКО ПОТОМ пропускаем живых, которые сейчас на карте
                    // Если ветеран жив, но на миссии — пропускаем его старение и мутации в пуле
                    if (veteransOnMission.Contains(p.thingIDNumber) || p.Spawned) continue;					

                    CleanPawnHealth(p, true);
					
					
// 2. И ТОЛЬКО ПОТОМ пропускаем живых, которые сейчас на карте
if (veteransOnMission.Contains(p.thingIDNumber) || p.Spawned) continue;					

CleanPawnHealth(p, true);

// === УМНОЕ СТАРЕНИЕ ===
if (savedBioAges.TryGetValue(p.thingIDNumber, out long lastKnownAge))
{
    // Если возраст за весь цикл почти не изменился (ванилла заблокирована модами)
    if (p.ageTracker.AgeBiologicalTicks <= lastKnownAge + 60000) 
    {
        float rate = Mathf.Max(0.01f, Find.Storyteller.difficulty.adultAgingRate);
        p.ageTracker.AgeChronologicalTicks += 3600000;
        p.ageTracker.AgeBiologicalTicks += (long)(3600000 * rate);
        
        if (FPMod.Settings.enableDebugLogs)
            Log.Message($"[FP] Ветеран {p.LabelShort} состарен вручную (оптимизатор блокирует ваниллу).");
    }
}
// Обновляем запись возраста для следующего года (неважно, ванилла состарила или мы)
savedBioAges[p.thingIDNumber] = p.ageTracker.AgeBiologicalTicks;

          //         p.ageTracker.AgeBiologicalTicks += 3600000;  //ПОКА ОСТАВЛЮ Я НЕ ПОМНЮ ЕСТЬ ЛИ СТАРЕНИЕ В ВАНИЛЕ

           //        p.ageTracker.AgeChronologicalTicks += 3600000;  //ПОКА ОСТАВЛЮ Я НЕ ПОМНЮ ЕСТЬ ЛИ СТАРЕНИЕ В ВАНИЛЕ

                    if (p.skills != null)
                    {
                        var learnable = p.skills.skills.Where(s => !s.TotallyDisabled && s.Level < 20).ToList();
                        if (learnable.Any())
                        {
                            var skill = learnable.RandomElementByWeight(s => s.passion == Passion.Major ? 3f : (s.passion == Passion.Minor ? 2f : 1f));
                            skill.Level++;
                            skill.xpSinceLastLevel = skill.XpRequiredForLevelUp / 2f;
                            levelUpCount++;
                        }
                    }
					
try { ProcessVeteranImplants(p); } 
catch (Exception ex) { Log.Warning($"[FP] Ошибка обработки имплантов для {p.LabelShort}: {ex.Message}"); }

try { ProcessVeteranGenes(p); } 
catch (Exception ex) { Log.Warning($"[FP] Ошибка обработки генов для {p.LabelShort}: {ex.Message}"); }

try { ProcessVeteranAnomaly(p); } 
catch (Exception ex) { Log.Warning($"[FP] Ошибка обработки аномалий для {p.LabelShort}: {ex.Message}"); }

try { ProcessVeteranAgeDiseases(p); } 
catch (Exception ex) { Log.Warning($"[FP] Ошибка обработки болезней для {p.LabelShort}: {ex.Message}"); }
int age = p.ageTracker.AgeBiologicalYears;
// Упрощенный шанс: до 60 лет — 0%, после 60 лет — база 5% в год
float deathChance = (age >= 60) ? 0.05f : 0f;

if (deathChance > 0f && p.Faction != null)
            {
                // Быстрый переключатель тех-уровня (Switch expression)
                float techMult = p.Faction.def.techLevel switch
                {
                    TechLevel.Animal => 2.0f,
                    TechLevel.Neolithic => 1.5f,
                    TechLevel.Medieval => 1.0f,
                    TechLevel.Industrial => 0.5f,
                    TechLevel.Spacer => 0.3f,
                    TechLevel.Ultra => 0.1f,
                    TechLevel.Archotech => 0.01f,
                    _ => 1.0f
                };

                deathChance *= techMult;
                deathChance *= FPMod.Settings.deathChanceMultiplier;
            }

if (Rand.Value < deathChance)
{
    // Запоминаем имя ДО того, как сотрем пешку из реальности
    string deadName = p.LabelShort; 

if (!p.Dead)
{
    p.Kill(null); // Убиваем пешку естественной смертью.
}

    allVeteranIdsCache.Remove(p.thingIDNumber);
    if (veteranAddTicks.ContainsKey(p.thingIDNumber)) 
        veteranAddTicks.Remove(p.thingIDNumber);
    
    group.pawns.RemoveAt(i);
    deathCount++; // Вернули счетчик смертей!

    if (FPMod.Settings != null && FPMod.Settings.enableDebugLogs)
        Log.Message($"<color=red>[FP-Death]</color> Ветеран {deadName} скончался от старости в мире.");
}
                }
            }
            
            if (deathCount > 0 || levelUpCount > 0)
				if (FPMod.Settings.enableDebugLogs) 
{
                Log.Message($"[FP] ГОДОВОЙ ОТЧЕТ: {levelUpCount} ветеранов получили LevelUp. {deathCount} скончались от старости.");
				}
        }
		
private void ProcessVeteranImplants(Pawn p)
        {
			if (!allVeteranIdsCache.Contains(p.thingIDNumber)) return;
			if (p.Faction == null || p.Faction.def.hidden) return;
			
			
            if (p.Faction == null || p.health == null || p.health.hediffSet == null) return;

            TechLevel tech = p.Faction.def.techLevel;
            bool changed = false;

            // 1. ЛЕЧИМ ИНВАЛИДОВ (Заменяем оторванные органы и конечности)
            var missingParts = p.health.hediffSet.GetMissingPartsCommonAncestors().ToList();
            foreach (var missing in missingParts)
            {
                HediffDef prosth = GetDynamicProstheticFor(missing.Part, tech);
                if (prosth != null)
                {
                    p.health.RestorePart(missing.Part);
                    p.health.AddHediff(prosth, missing.Part);
                    changed = true;
                }
            }

            // 2. АПГРЕЙД (10% шанс прокачать ЛЮБУЮ здоровую часть тела)
            if (tech >= TechLevel.Industrial && Rand.Value < (0.10f * FPMod.Settings.implantChanceMultiplier))
            {
                // Берем вообще ВСЕ части тела (сердце, позвоночник, мозг, хвост из мода и т.д.), 
                // которые целы и на которых еще нет протезов
                var validParts = p.RaceProps.body.AllParts.Where(x => 
                    !p.health.hediffSet.PartIsMissing(x) && 
                    !p.health.hediffSet.HasDirectlyAddedPartFor(x)
                ).InRandomOrder().ToList(); // Сортируем случайно

                foreach (var partToUpgrade in validParts)
                {
                    HediffDef upgrade = GetDynamicProstheticFor(partToUpgrade, tech);
                    if (upgrade != null)
                    {
                        p.health.AddHediff(upgrade, partToUpgrade);
                        changed = true;
                        break; // Ставим только 1 апгрейд в год, чтобы он не превратился в киборга за секунду
                    }
                }
            }

            if (changed && FPMod.Settings.enableDebugLogs)
            {
                Log.Message($"<color=cyan>[FP-Surgery]</color> Ветеран {p.LabelShort} ({p.Faction.Name}) получил импланты за год отсутствия!");
            }
        }
		
private void ProcessVeteranGenes(Pawn p)
        {
            // 1. БЕЗОПАСНОСТЬ
            if (!ModsConfig.BiotechActive || p.genes == null) return;
            if (!allVeteranIdsCache.Contains(p.thingIDNumber)) return;
            if (p.Faction == null || p.Faction.def.hidden) return;

            TechLevel tech = p.Faction.def.techLevel;
            
            // Если фракция ниже Космоса (Spacer) — генов не будет вообще
            if (tech < TechLevel.Spacer) return;

            // 2. БРОСАЕМ КУБИКИ
            // 5% шанс на Архит-ген (Только для Ultra и выше - например, Империя)
            bool getsArchite = tech >= TechLevel.Ultra && Rand.Value < (0.05f * FPMod.Settings.geneChanceMultiplier);
            
            // 10% шанс на обычный ген (если Архит не выпал)
            bool getsNormal = !getsArchite && Rand.Value < (0.10f * FPMod.Settings.geneChanceMultiplier);

            // 3. ЕСЛИ ПРОКНУЛА ХОТЯ БЫ ОДНА МУТАЦИЯ
            if (getsArchite || getsNormal)
            {
                // Собираем список доступных генов
                var availableGenes = DefDatabase<GeneDef>.AllDefsListForReading.Where(g =>
                    !p.genes.HasActiveGene(g) && 
                    // Если прокнул Архит — ищем только гены с архо-капсулами (biostatArc > 0)
                    // Если обычный — ищем гены без архо-капсул (biostatArc == 0)
                    (getsArchite ? g.biostatArc > 0 : g.biostatArc == 0) 
                ).ToList();

                if (availableGenes.Count > 0)
                {
                    GeneDef newGene = availableGenes.RandomElement();
                    p.genes.AddGene(newGene, xenogene: true);

                    if (FPMod.Settings.enableDebugLogs)
                    {
                        // Делаем разные цвета для обычных генов и для легендарных архо-генов!
                        string prefix = getsArchite ? "<color=red>[FP-Archite]</color>" : "<color=magenta>[FP-Genetics]</color>";
                        Log.Message($"{prefix} Ветеран {p.LabelShort} ({p.Faction.Name}) получил ген: {newGene.label}!");
                    }
                }
            }
        }
		
private void ProcessVeteranAgeDiseases(Pawn p)
{
    // 1. Быстрая проверка возраста
    if (p.ageTracker.AgeBiologicalYears < 60) return;

    // 2. Определяем тех-уровень (безопасно достаем через ?. или используем Industrial по умолчанию)
    TechLevel tech = p.Faction?.def.techLevel ?? TechLevel.Industrial;

    // 3. Оптимизированный расчет множителя через switch (совет Dusk)
    float techMult = tech switch
    {
        TechLevel.Animal     => 3.0f,
        TechLevel.Neolithic  => 2.0f,
        TechLevel.Medieval   => 1.5f,
        TechLevel.Industrial => 1.0f,
        TechLevel.Spacer     => 0.5f,
        TechLevel.Ultra      => 0.1f,
        TechLevel.Archotech  => 0.05f,
        _                    => 1.0f
    };

    // 4. Проверка шанса
    if (Rand.Value < (0.05f * techMult * FPMod.Settings.diseaseChanceMultiplier))
    {
        // УНИВЕРСАЛЬНЫЙ ПОИСК: берем все болезни, которые прописаны расе как "возрастные"
        // Это подхватит и ванильные болезни, и любые болезни из модов.
        var potentialDiseases = p.RaceProps.hediffGiverSets?
            .SelectMany(set => set.hediffGivers)
            .OfType<HediffGiver_Birthday>()
            .ToList();

        if (potentialDiseases == null || potentialDiseases.Count == 0) return;

        // Выбираем случайного "дарителя" болезни
        var giver = potentialDiseases.RandomElement();

        if (giver?.hediff != null)
        {
            // TryApply — это стандартный метод игры. Он сам найдет нужную часть тела 
            // (глаз для катаракты, позвоночник для спины) и наложит эффект.
            giver.TryApply(p);

            if (FPMod.Settings.enableDebugLogs)
                Log.Message($"<color=red>[FP-Disease]</color> {p.LabelShort} получил возрастную болезнь: {giver.hediff.label} (из пула {p.def.label})");
        }
    }
}		
		
		
private void ProcessVeteranAnomaly(Pawn p)
        {
            // 1. БЕЗОПАСНОСТЬ
            if (!ModsConfig.AnomalyActive) return;
            if (!allVeteranIdsCache.Contains(p.thingIDNumber)) return;
            if (p.Faction == null || p.Faction.def.hidden) return;

            // Только для Племен и Средневековья
            if (p.Faction.def.techLevel >= TechLevel.Industrial) return;

            // ШАНС: 5% на контакт с Пустотой
            if (Rand.Value < (0.05f * FPMod.Settings.anomalyChanceMultiplier))
            {
                // Ищем все медицинские рецепты из Anomaly (Позвонок ревенанта и т.д.)
                var anomalyRecipes = DefDatabase<RecipeDef>.AllDefsListForReading.Where(r =>
                    r.addsHediff != null &&
                    r.modContentPack != null &&
                    r.modContentPack.PackageId.ToLower() == "ludeon.rimworld.anomaly" &&
                    (typeof(Recipe_InstallArtificialBodyPart).IsAssignableFrom(r.workerClass) || 
                     typeof(Recipe_InstallImplant).IsAssignableFrom(r.workerClass))
                ).ToList();

                // Выбираем, что дадут боги Пустоты: 
                // 50% на ритуальную мутацию (щупальца), 50% на хирургический артефакт (позвонок)
                bool useRitual = Rand.Bool || anomalyRecipes.Count == 0;

                if (useRitual)
                {
                    // Вручную прописанные ритуальные мутации (у которых нет рецептов)
                    string[] ritualMutations = { "FleshTentacle", "FleshWhip", "DeathRefusal" };
                    string mut = ritualMutations.RandomElement();
                    HediffDef hediff = DefDatabase<HediffDef>.GetNamedSilentFail(mut);

                    if (hediff != null)
                    {
                        if (mut == "DeathRefusal")
                        {
                            if (p.health.hediffSet.GetFirstHediffOfDef(hediff) == null)
                            {
                                p.health.AddHediff(hediff);
                                if (FPMod.Settings.enableDebugLogs) Log.Message($"<color=#800080>[FP-Anomaly]</color> Ветеран {p.LabelShort} получил ритуал: {hediff.label}!");
                            }
                        }
                        else // Щупальце или хлыст
                        {
                            var shoulder = p.RaceProps.body.AllParts.FirstOrDefault(x => 
                                x.def.defName.Contains("Shoulder") && 
                                !p.health.hediffSet.PartIsMissing(x) && 
                                !p.health.hediffSet.HasDirectlyAddedPartFor(x));

                            if (shoulder != null)
                            {
                                p.health.AddHediff(hediff, shoulder);
                                if (FPMod.Settings.enableDebugLogs) Log.Message($"<color=#800080>[FP-Anomaly]</color> У дикаря {p.LabelShort} отросло {hediff.label}!");
                            }
                        }
                    }
                }
                else
                {
                    // Применяем динамический рецепт (Позвонки ревенанта, импланты из модов на Аномалию)
                    var recipe = anomalyRecipes.RandomElement();
                    if (recipe.appliedOnFixedBodyParts != null && recipe.appliedOnFixedBodyParts.Count > 0)
                    {
                        var validPart = p.RaceProps.body.AllParts.FirstOrDefault(part =>
                            recipe.appliedOnFixedBodyParts.Contains(part.def) &&
                            !p.health.hediffSet.PartIsMissing(part) &&
                            !p.health.hediffSet.HasDirectlyAddedPartFor(part)
                        );

                        if (validPart != null)
                        {
                            p.health.AddHediff(recipe.addsHediff, validPart);
                            if (FPMod.Settings.enableDebugLogs) Log.Message($"<color=#800080>[FP-Anomaly]</color> {p.LabelShort} вживил себе артефакт: {recipe.addsHediff.label}!");
                        }
                    }
                }
            }
        }


        // --- УМНЫЙ ПОИСК ПРОТЕЗОВ ПО БАЗЕ ДАННЫХ ---
        private HediffDef GetDynamicProstheticFor(BodyPartRecord part, TechLevel factionTech)
        {
            // Ищем в игре ВСЕ рецепты хирургии (включая из модов)
            var validRecipes = DefDatabase<RecipeDef>.AllDefsListForReading.Where(r =>
                r.addsHediff != null && // Рецепт дает хедифф (протез/имплант)
                r.appliedOnFixedBodyParts != null && 
                r.appliedOnFixedBodyParts.Contains(part.def) && // Подходит именно для этой части тела
                (typeof(Recipe_InstallArtificialBodyPart).IsAssignableFrom(r.workerClass) || 
                 typeof(Recipe_InstallImplant).IsAssignableFrom(r.workerClass)) // Это именно установка импланта
            );

            // Фильтруем по уровню развития фракции
            var available = validRecipes.Where(r => 
            {
                // Проверяем предмет (коробку с протезом), который нужен для операции
                var itemDef = r.ingredients.FirstOrDefault()?.filter?.AnyAllowedDef;
                if (itemDef == null) return false;
                
                // Главное условие: технологический уровень импланта не должен превышать уровень фракции
                return itemDef.techLevel <= factionTech; 
            }).ToList();

            if (available.Count == 0) return null; // Подходящих протезов нет

            // Выдаем случайный протез из тех, что подошли по развитию
            return available.RandomElement().addsHediff;
        }

        // Вспомогательный метод: подбирает протез по уровню фракции
        private HediffDef GetProstheticDefFor(BodyPartRecord part, TechLevel tech)
        {
            bool isLeg = part.def.tags.Contains(BodyPartTagDefOf.MovingLimbCore);
            bool isArm = part.def.tags.Contains(BodyPartTagDefOf.ManipulationLimbCore);
            bool isEye = part.def == BodyPartDefOf.Eye;

            // Космос (Пираты, Империя) -> Бионика
            if (tech >= TechLevel.Spacer)
            {
                if (isLeg) return DefDatabase<HediffDef>.GetNamedSilentFail("BionicLeg");
                if (isArm) return DefDatabase<HediffDef>.GetNamedSilentFail("BionicArm");
                if (isEye) return DefDatabase<HediffDef>.GetNamedSilentFail("BionicEye");
            }
            // Индустриальная эра (Союзники) -> Простые протезы
            else if (tech >= TechLevel.Industrial)
            {
                if (isLeg) return DefDatabase<HediffDef>.GetNamedSilentFail("SimpleProstheticLeg");
                if (isArm) return DefDatabase<HediffDef>.GetNamedSilentFail("SimpleProstheticArm");
            }
            // Племена и Средневековье -> Деревяшки
            else
            {
                if (isLeg) return DefDatabase<HediffDef>.GetNamedSilentFail("PegLeg");
                if (isArm) return DefDatabase<HediffDef>.GetNamedSilentFail("WoodenHand");
            }

            return null; // Если часть тела неизвестна или нет подходящего протеза
        }
		
		
		

        // === SMART MATCHING ===
        public Pawn TryGetVeteran(PawnGenerationRequest request, bool silent = false)
        {
            Faction f = request.Faction;
            if (f == null || !veteranPool.TryGetValue(f.loadID, out var group) || group.pawns.Count == 0) return null;

if (Find.TickManager.TicksGame != lastTickIssued)
{
    pawnsIssuedThisTickIDs.Clear();
    lastTickIssued = Find.TickManager.TicksGame;
}

// Достаем валидатор ОДИН РАЗ перед циклом (Оптимизация!)
Predicate<Pawn> validator = null;
try {
    var trav = Traverse.Create(request);
    validator = trav.Property<Predicate<Pawn>>("Validator").Value ?? trav.Field<Predicate<Pawn>>("validator").Value;
} catch { }
				 
				 

int index = group.pawns.FindIndex(p => 
    p != null && !p.Dead && !p.Discarded && !p.Spawned && p.Map == null && 
    !pawnsIssuedThisTickIDs.Contains(p.thingIDNumber) && 
    !veteransOnMission.Contains(p.thingIDNumber) && 
    // ПРОВЕРКА КУЛДАУНА:
    (!veteranAddTicks.TryGetValue(p.thingIDNumber, out int addedTick) || 
     Find.TickManager.TicksGame >= addedTick + (FPMod.Settings.veteranRecallCooldownDays * 60000)) &&
    IsPawnAvailableForDispatch(p) && 
    PawnMatchesRequest(p, request, validator)
);

            if (index == -1) return null;

            Pawn candidate = group.pawns[index];     
if (Find.WorldPawns.Contains(candidate))
{
    Find.WorldPawns.RemovePawn(candidate);
}           
pawnsIssuedThisTickIDs.Add(candidate.thingIDNumber);
veteranAddTicks.Remove(candidate.thingIDNumber);
veteransOnMission.Add(candidate.thingIDNumber);

// Найти в методе TryGetVeteran этот блок и заменить:
if (FPMod.Settings.enableDebugLogs) // Убрано !silent
{
    bool isMothballed = Traverse.Create(Find.WorldPawns).Field<HashSet<Pawn>>("pawnsMothballed").Value?.Contains(candidate) ?? false;
    string state = isMothballed ? "из глубокой заморозки" : "из активного пула";

    Log.Message($"<color=cyan>[FP-Wakeup]</color> {candidate.LabelShort} (ID:{candidate.thingIDNumber}) выдан {state}. " +
                $"В миссии сейчас: {veteransOnMission.Count} чел. " + 
                $"Выдано за тик: {pawnsIssuedThisTickIDs.Count}");
}
            
            return candidate;
        }

// === ЛОГИКА СОВМЕСТИМОСТИ (ПОЛНАЯ ВЕРСИЯ) ===
private bool PawnMatchesRequest(Pawn p, PawnGenerationRequest req, Predicate<Pawn> validator)
{
    // 1. Раса (Alien Races / Androids)
    if (req.KindDef != null && p.def != req.KindDef.race) return false;

    // 2. Пол
    if (req.FixedGender.HasValue && p.gender != req.FixedGender.Value) return false;

    // 3. Возраст
    if (req.FixedBiologicalAge.HasValue)
    {
        if (Math.Abs(p.ageTracker.AgeBiologicalYears - req.FixedBiologicalAge.Value) > 1) return false;
    }

    // 4. Имя (Сценарные персонажи)
    if (req.FixedLastName != null || req.FixedBirthName != null) return false;

    // 5. Стадия развития (Biotech: чтобы ребенок не пришел вместо деда)
    if (!req.AllowedDevelopmentalStages.HasFlag(p.DevelopmentalStage)) return false;

    // 6. Запрещенные черты (Для квестов "Рейд без пироманов")
    if (req.ProhibitedTraits != null && p.story != null && p.story.traits != null)
    {
        foreach (var traitDef in req.ProhibitedTraits)
        {
            if (p.story.traits.HasTrait(traitDef)) return false;
        }
    }

    // 7. Ксенотип (Biotech)
    if (req.ForcedXenotype != null)
    {
        if (p.genes == null || p.genes.Xenotype != req.ForcedXenotype) return false;
    }
    
    // 8. Мутанты (Anomaly DLC - Гули, Шамблеры)
    if (req.ForcedMutant != null)
    {
        if (p.mutant == null || p.mutant.Def != req.ForcedMutant) return false;
    }

    // 9. Внешний валидатор от других модов (Тот самый, что мы вынесли)
// Просто используем то, что передали. Никакой рефлексии в цикле!
if (validator != null && !validator(p)) return false;

    return true;
}

private bool IsPawnAvailableForDispatch(Pawn p)
{
    // 1. Базовая проверка (самая дешевая)
    if (p == null || !Find.WorldPawns.Contains(p)) return false;

    // 2. Цепочка проверок от легких к тяжелым (ленивые вычисления)
    string r = null;
    if (p.holdingOwner != null) 
        r = "контейнер";
    else if (p.IsCaravanMember()) 
        r = "караван";
    else if (PawnUtility.IsTravelingInTransportPodWorldObject(p)) 
        r = "капсула";
    else if (QuestUtility.IsReservedByQuestOrQuestBeingGenerated(p)) 
        r = "квест";

    // 3. Если найдена причина блокировки
    if (r != null)
    {
        if (FPMod.Settings.enableDebugLogs)
            Log.Message($"<color=yellow>[FP-Filter]</color> {p.LabelShort} пропущен: {r}");
        return false;
    }

    return true;
}
    }

    // === ПАТЧИ ===

// === УМНЫЙ ТРЕКЕР АНТИ-ФАНТОМОВ ===
// === УМНЫЙ ТРЕКЕР АНТИ-ФАНТОМОВ ===
    public static class FPSeenTracker
    {
        private static readonly HashSet<int> seenIDs = new HashSet<int>();

        public static void Mark(Pawn p)
        {
            if (p?.RaceProps?.Humanlike == true && p.Faction != null && !p.Faction.IsPlayer)
            {
                seenIDs.Add(p.thingIDNumber);
            }
        }

        public static bool Contains(int id) => seenIDs.Contains(id);
        public static void Remove(int id) => seenIDs.Remove(id);
        public static void Clear() => seenIDs.Clear();
    }

    // === ПАТЧ 1: ПЕШКА КОСНУЛАСЬ ЗЕМЛИ (РЕГИСТРАЦИЯ) ===
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
    public static class Patch_Pawn_SpawnSetup
    {
        [HarmonyPostfix]
        static void Postfix(Pawn __instance, Map map)
        {
            if (map != null) 
            {
                FPSeenTracker.Mark(__instance);
            }
        }
    }

    // === ПАТЧ 2: ПЕШКА УХОДИТ В МИР (ПРОВЕРКА) ===
[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.PassToWorld), new[] { typeof(Pawn), typeof(PawnDiscardDecideMode) })]
public static class Patch_PassToWorld
{
    [HarmonyPrefix]
    static void Prefix(Pawn pawn, PawnDiscardDecideMode discardMode)
    {
        if (Current.ProgramState != ProgramState.Playing || pawn == null) return;
        if (WorldPopulationManager.IsManuallyAdding) return;
        if (discardMode != PawnDiscardDecideMode.Decide) return;

        if (!FPSeenTracker.Contains(pawn.thingIDNumber)) return; 

        // Базовые проверки (трупы, животные и свои колонисты нам точно не нужны)
        if (!pawn.RaceProps.Humanlike || pawn.Faction == null || pawn.Faction.IsPlayer || pawn.Dead) return;
        if (pawn.ParentHolder is Building) return; 
        if (pawn.Faction.def.hidden) return;

        // ПРОВЕРЯЕМ НАЛИЧИЕ ЗВЕЗДЫ
        var manager = Find.World?.GetComponent<WorldPopulationManager>();
        bool isPinned = manager != null && manager.manualVeteranPins.Contains(pawn.thingIDNumber);

        // ЕСЛИ ЗВЕЗДЫ НЕТ - проводим строгую проверку на квесты
        if (!isPinned)
        {
            string dn = pawn.Faction.def.defName;
            // Исключаем временных квестовых пешек
            if (dn.Contains("Refugee") || dn.Contains("Beggar") || dn.Contains("Ancient") || dn.Contains("Sleeper")) return;
            if (PawnUtility.IsKidnappedPawn(pawn)) return;
        }

        // Если пешка дошла сюда (либо она нормальная, либо на ней Звезда) - сохраняем!
        FPSeenTracker.Remove(pawn.thingIDNumber);
        VeteranInputQueue.Enqueue(pawn);
    }
}

    [HarmonyPatch(typeof(WorldPawnGC), "GetCriticalPawnReason")]
    public static class Patch_GC
    {
        [HarmonyPostfix]
        static void Postfix(Pawn pawn, ref string __result)
        {
            if (__result != null) return;
            if (pawn != null && pawn.Faction != null && !pawn.Faction.IsPlayer)
            {
                var manager = Find.World?.GetComponent<WorldPopulationManager>();
                if (manager != null && manager.allVeteranIdsCache.Contains(pawn.thingIDNumber))
                {
                    __result = "FinitePopulation_Veteran";
                }
            }
        }
    }
    
[HarmonyPatch(typeof(PawnGenerator), "GeneratePawn", new Type[] { typeof(PawnGenerationRequest) })]
    public static class Patch_PawnGenerator
    {
        // [ВАЖНО] Ставим приоритет ОЧЕНЬ ВЫСОКИЙ (выше, чем у основного мода).
        // Это гарантирует, что мы сначала попытаемся достать Ветерана (пока пол еще не подменен),
        // и только если ветерана нет, основной мод потом выставит пол для случайной пешки.
        [HarmonyPriority(2000)] 
        [HarmonyPrefix]
        static bool Prefix(ref PawnGenerationRequest request, ref Pawn __result)
        {
            if (request.Faction == null || request.Faction.IsPlayer || !request.Faction.def.humanlikeFaction) return true;
            if (request.ForceGenerateNewPawn) return true;
            if (!request.CanGeneratePawnRelations) return true;

            var manager = Find.World?.GetComponent<WorldPopulationManager>();
            
            // Шанс 80% на призыв ветерана
            if (manager != null && Rand.Value < FPMod.Settings.veteranRecallChance)
            {
                bool silent = Scribe.mode != LoadSaveMode.Inactive;
                
                // Пробуем достать ветерана
                Pawn v = manager.TryGetVeteran(request, silent);
                
                if (v != null) 
                { 
                    if (request.KindDef != null) v.kindDef = request.KindDef; 
                    __result = v; 
                    return false; // Прерываем генерацию, ветеран найден!
                }
            }
            
            // Если ветеран не найден — возвращаем true. 
            // Дальше управление перейдет к Основному моду (у него приоритет ниже), 
            // и он уже настроит пол для создания НОВОЙ случайной пешки.
            return true;
        }
    }
	
//ЛОГИКА ЗАМОРОЗКИ
	
[HarmonyPatch(typeof(WorldPawns), "DefPreventingMothball")]
public static class Patch_Mothball
{
    // 1. Быстрая ссылка на список замороженных пешек (чтобы не было спама логов)
    private static readonly AccessTools.FieldRef<WorldPawns, HashSet<Pawn>> PawnsMothballedRef = 
        AccessTools.FieldRefAccess<WorldPawns, HashSet<Pawn>>("pawnsMothballed");

    // 2. Кэш всех зависимостей. Мы заполним его один раз и будем мгновенно проверять.
    private static HashSet<HediffDef> addictionDefsCache;

[HarmonyPostfix]
static void Postfix(Pawn p, ref HediffDef __result)
{
    if (p == null) return;

    var manager = Find.World?.GetComponent<WorldPopulationManager>();
    if (manager == null) return;

    // 1. ПРОВЕРКА: Наш ли это ветеран?
    bool isVeteran = manager.allVeteranIdsCache.Contains(p.thingIDNumber) || 
                     VeteranInputQueue.pendingPawnIDs.Contains(p.thingIDNumber) || 
                     WorldPopulationManager.IsManuallyAdding;

    if (!isVeteran) return;

    bool isAlreadyMothballed = PawnsMothballedRef(Find.WorldPawns)?.Contains(p) ?? false;

    // 2. ЛОГИКА 1: Принудительная заморозка (Теперь ПЕРВАЯ и с проверкой >=)
    // Если стоит 0 дней, сработает мгновенно в момент добавления
    if (manager.veteranAddTicks.TryGetValue(p.thingIDNumber, out int addedAt) && 
        Find.TickManager.TicksGame >= addedAt + (FPMod.Settings.forcedFreezeDays * 60000)) 
    {
        if (!isAlreadyMothballed && FPMod.Settings.enableDebugLogs)
            Log.Message($"<color=orange>[FP-Freeze]</color> {p.LabelShort} принудительно заморожен (настройка: {FPMod.Settings.forcedFreezeDays} дн, игнорируя {(__result?.defName ?? "ничего")}).");
        
        __result = null; // Разрешаем заморозку, стирая причину отказа (зависимость и т.д.)
        return;
    }

    // 3. ЛОГИКА ДЛЯ ЗДОРОВЫХ (Если принудительная выше не сработала по времени)
    if (__result == null) 
    {
        if (FPMod.Settings.enableDebugLogs && !isAlreadyMothballed)
            Log.Message($"<color=orange>[FP-Freeze]</color> {p.LabelShort} заморожен (здоров/естественно).");
        return;
    }

    // 4. ЛОГИКА 2: Разрешение сна при зависимостях (если время принудительной еще не пришло)
    if (IsDependencyOptimized(__result))
    {
        // ... твой старый код проверки зависимостей ...
            if (!isAlreadyMothballed && FPMod.Settings.enableDebugLogs)
                Log.Message($"<color=orange>[FP-Freeze]</color> {p.LabelShort} засыпает (Разрешена зависимость: {__result.defName}).");

            __result = null;

            // Проверяем, нет ли других БЛОКИРУЮЩИХ болезней (раны, инфекции)
            var hediffs = p.health.hediffSet.hediffs;
            for (int i = 0; i < hediffs.Count; i++)
            {
                var h = hediffs[i];
                if (!h.def.AlwaysAllowMothball && !h.IsPermanent() && !IsDependencyOptimized(h.def))
                {
                    __result = h.def; // Нашли реальную болезнь — она запрещает сон
                    break;
                }
            }
        }
    }

    // Сверхбыстрая проверка через кэш
    private static bool IsDependencyOptimized(HediffDef def)
    {
        if (def == null) return false;

        // Если кэш еще не создан (первый запуск), создаем его
        if (addictionDefsCache == null)
        {
            addictionDefsCache = new HashSet<HediffDef>();
            foreach (var d in DefDatabase<HediffDef>.AllDefs)
            {
		if (d.hediffClass != null && 
			(typeof(Hediff_Addiction).IsAssignableFrom(d.hediffClass) || 
			typeof(Hediff_High).IsAssignableFrom(d.hediffClass) ||     
			typeof(Hediff_Hangover).IsAssignableFrom(d.hediffClass) ||  
			d.defName.Contains("Dependency") || d.defName.Contains("Addiction")))
	{
                    addictionDefsCache.Add(d);
                }
            }
        }
        return addictionDefsCache.Contains(def);
    }
}


//КОНЕЦ ЛОГИКИ ЗАМОРОЗКИ 

// --- ПАТЧ 3: ОЧИСТКА ПАМЯТИ ПРИ СМЕРТИ НА КАРТЕ ---
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Patch_Pawn_Kill_SeenCleanup
    {
        [HarmonyPrefix]
        static void Prefix(Pawn __instance)
        {
            if (__instance != null) 
            {
                FPSeenTracker.Remove(__instance.thingIDNumber);
            }
        }
    }

// --- ПАТЧ 4: ОЧИСТКА ПАМЯТИ ПРИ ПОЛНОМ СТИРАНИИ (ГАРАНТИЯ ОТ УТЕЧЕК) ---
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Discard), new[] { typeof(bool) })]
    public static class Patch_Pawn_Discard_SeenCleanup
    {
        [HarmonyPrefix]
        static void Prefix(Pawn __instance)
        {
            if (__instance != null) 
            {
                FPSeenTracker.Remove(__instance.thingIDNumber);
            }
        }
    }

	
}
