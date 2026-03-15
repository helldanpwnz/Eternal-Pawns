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
        public Dictionary<int, Color> originalHairColors = new Dictionary<int, Color>();
private List<long> tmpBioValues; // Для сохранения
private List<int> tmpTicksKeys;   // Для сохранения
private List<int> tmpTicksValues; // Для сохранения
private int ticksToNextUpdate = -1; // По умолчанию 1 год


// КЭШ БАЗЫ ДАННЫХ ДЛЯ ОПТИМИЗАЦИИ СТАРЕНИЯ
        private static List<RecipeDef> cachedAnomalyRecipes = null;
        private static List<GeneDef> cachedArchiteGenes = null;
        private static List<GeneDef> cachedNormalGenes = null;
        private static List<RecipeDef> cachedProstheticRecipes = null;
        
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
		private int ticksToNextCleanup = -1; // НОВЫЙ ТАЙМЕР: 10 дней (60,000 тиков * 10)

public WorldPopulationManager(World world) : base(world) { FPSeenTracker.Clear(); }

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

    // --- ВОССТАНОВЛЕНИЕ ПРОЧНОСТИ ВЕЩЕЙ ---
    if (p.apparel != null)
    {
        foreach (var ap in p.apparel.WornApparel)
        {
            if (ap.def.useHitPoints) ap.HitPoints = ap.MaxHitPoints;
        }
    }
    if (p.equipment != null)
    {
        foreach (var eq in p.equipment.AllEquipmentListForReading)
        {
            if (eq.def.useHitPoints) eq.HitPoints = eq.MaxHitPoints;
        }
    }
    if (p.inventory != null)
    {
        foreach (var item in p.inventory.innerContainer)
        {
            if (item.def.useHitPoints) item.HitPoints = item.MaxHitPoints;
        }
    }

    // Перезарядка оружия (CE, Yayo и т.d.)
    FPUtility.ReloadWeapons(p);
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
			Scribe_Values.Look(ref ticksToNextCleanup, "ticksToNextCleanup", -1);
			Scribe_Collections.Look(ref savedBioAges, "savedBioAges", LookMode.Value, LookMode.Value, ref tmpTicksKeys, ref tmpBioValues);
            Scribe_Collections.Look(ref originalHairColors, "originalHairColors", LookMode.Value, LookMode.Value);
if (savedBioAges == null) savedBioAges = new Dictionary<int, long>();
if (pawnNotes == null) pawnNotes = new Dictionary<int, string>();
if (manualVeteranPins == null) manualVeteranPins = new HashSet<int>();
if (originalHairColors == null) originalHairColors = new Dictionary<int, Color>();

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

				// --- ОЧИСТКА ОТ УТЕЧЕК ПАМЯТИ ---
				if (savedBioAges != null)
				{
					var keysBio = savedBioAges.Keys.ToList();
					foreach (var k in keysBio) if (!activeIds.Contains(k)) savedBioAges.Remove(k);
				}

				if (pawnNotes != null)
				{
					var keysNotes = pawnNotes.Keys.ToList();
					foreach (var k in keysNotes) if (!activeIds.Contains(k)) pawnNotes.Remove(k);
				}

                if (originalHairColors != null)
                {
                    var keysHair = originalHairColors.Keys.ToList();
                    foreach (var k in keysHair) if (!activeIds.Contains(k)) originalHairColors.Remove(k);
                }
                // --- КОНЕЦ ОЧИСТКИ ---

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
	
	// --- НОВЫЙ ЦИКЛ: Очистка зависших миссий (Раз в 10 дней) ---
    ticksToNextCleanup--;
    if (ticksToNextCleanup <= 0)
    {
        ticksToNextCleanup = 600000; // Заводим таймер заново на 10 дней
        ProcessMissionCleanup();
    }

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

private void ProcessMissionCleanup()
{
    // Если на миссиях никого нет, даже не тратим время
    if (veteransOnMission.Count == 0) return;

    List<Pawn> toRescue = new List<Pawn>();

    foreach (var group in veteranPool.Values)
    {
        foreach (Pawn p in group.pawns)
        {
            if (p != null && !p.Dead && veteransOnMission.Contains(p.thingIDNumber))
            {
                // Если пешки нет на карте И она не в караване ИГРОКА
                if (p.MapHeld == null && !p.IsCaravanMember())
                {
                    toRescue.Add(p);
                    if (FPMod.Settings.enableDebugLogs)
                    {
                        Log.Message($"<color=gray>[FP-Rescue]</color> {p.LabelShort} спасен из пустоты (санитарный цикл). Возвращен в пул!");
                    }
                }
            }
        }
    }

    // Безопасно снимаем статус миссии и ВОЗВРАЩАЕМ В МИР
    foreach (Pawn p in toRescue)
    {
        veteransOnMission.Remove(p.thingIDNumber);
        
        // ВОТ ОНА, РАЗГАДКА: Если игра потеряла пешку, закидываем её обратно в WorldPawns
        if (!Find.WorldPawns.Contains(p))
        {
            IsManuallyAdding = true; // Блокируем наш перехватчик сохранения
            try
            {
                Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.KeepForever);
            }
            catch (Exception ex)
            {
                Log.Warning($"[FP] Ошибка возврата спасенного ветерана {p.LabelShort} в мир: {ex.Message}");
            }
            finally
            {
                IsManuallyAdding = false;
            }
        }
    }
}

public void AddVeteran(Pawn p)
{
    // 1. Базовые проверки (отсекаем мусор сразу)
    if (p == null || p.Faction == null || p.Faction.def.hidden || p.Dead || p.Discarded) return;
	if (p.Spawned) return;

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
	
// 5. ФИКС ДУБЛИКАТОВ И ОБНОВЛЕНИЕ СПИСКОВ
    savedBioAges[p.thingIDNumber] = p.ageTracker.AgeBiologicalTicks;
    
    // Жестко вычищаем старые копии этой пешки из ВСЕХ фракций (включая текущую)
    foreach (var otherGroup in veteranPool.Values)
    {
        otherGroup.pawns.RemoveAll(x => x.thingIDNumber == p.thingIDNumber);
    }

    // Если это совершенно новый человек — регистрируем его ID в общем кэше
    if (!allVeteranIdsCache.Contains(p.thingIDNumber))
    {
        allVeteranIdsCache.Add(p.thingIDNumber);
    }

    // Записываем свежую версию пешки в нужную фракцию
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
                            savedBioAges.Remove(p.thingIDNumber); 
                            pawnNotes.Remove(p.thingIDNumber);
                            originalHairColors.Remove(p.thingIDNumber);
                        }

                        group.pawns.RemoveAt(i); 

                        continue; 

                    }



                    // 2. И ТОЛЬКО ПОТОМ пропускаем живых, которые сейчас на карте

                    // Если ветеран жив, но на миссии — пропускаем его старение и мутации в пуле

                    if (veteransOnMission.Contains(p.thingIDNumber) || p.Spawned) continue;



                    CleanPawnHealth(p, true);


// === УМНОЕ СТАРЕНИЕ (Система точной компенсации) ===
if (savedBioAges.TryGetValue(p.thingIDNumber, out long lastKnownAge))
{
    float rate = Mathf.Max(0.01f, Find.Storyteller.difficulty.adultAgingRate);
    long actualTicksPassed = (long)(3600000 / rate); // Время, прошедшее в мире от звонка до звонка таймера
    
    // Хронологический возраст (время в мире) догоняем жестко
    p.ageTracker.AgeChronologicalTicks += actualTicksPassed;

    float pawnAgeFactor = p.ageTracker.BiologicalTicksPerTick; 
    
    if (pawnAgeFactor > 0f)
    {
        // 1. Сколько пешка ДОЛЖНА была состариться за этот цикл (с учетом ее генов)?
        long expectedBioGrowth = (long)(3600000 * pawnAgeFactor); 

        // 2. Сколько она РЕАЛЬНО состарилась силами самой игры?
        long actualBioGrowth = p.ageTracker.AgeBiologicalTicks - lastKnownAge;

        // 3. Защита от старения вспять (мало ли какие баги в других модах)
        if (actualBioGrowth < 0) actualBioGrowth = 0;

        // 4. Если ванилла недодала возраст (например, дала 6 дней вместо 30) - компенсируем разницу!
        if (actualBioGrowth < expectedBioGrowth)
        {
            long catchUpTicks = expectedBioGrowth - actualBioGrowth;
            p.ageTracker.AgeBiologicalTicks += catchUpTicks;

            if (FPMod.Settings.enableDebugLogs)
                Log.Message($"[FP-Aging] {p.LabelShort}: Ванилла дала +{actualBioGrowth / 60000f:F1} дн. Мод компенсировал нехватку: +{catchUpTicks / 60000f:F1} дн.");
        }
        else
        {
            // Если actualBioGrowth >= expectedBioGrowth, значит ванилла честно старила пешку весь год сама.
            // Мод ничего не делает, чтобы не состарить ее дважды.
            if (FPMod.Settings.enableDebugLogs)
                Log.Message($"[FP-Aging] {p.LabelShort}: Ванилла сама состарила пешку штатно. Вмешательство не требуется.");
        }
    }
}

// В самом конце обязательно обновляем слепок возраста на следующий год
savedBioAges[p.thingIDNumber] = p.ageTracker.AgeBiologicalTicks;



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
					
try { ProcessVeteranRegeneration(p); } 
catch (Exception ex) { Log.Warning($"[FP] Ошибка регенерации для {p.LabelShort}: {ex.Message}"); }

try { ProcessVeteranImplants(p); } 
catch (Exception ex) { Log.Warning($"[FP] Ошибка обработки имплантов для {p.LabelShort}: {ex.Message}"); }

try { ProcessVeteranGenes(p); } 
catch (Exception ex) { Log.Warning($"[FP] Ошибка обработки генов для {p.LabelShort}: {ex.Message}"); }

try { ProcessVeteranAnomaly(p); } 
catch (Exception ex) { Log.Warning($"[FP] Ошибка обработки аномалий для {p.LabelShort}: {ex.Message}"); }

try { ProcessVeteranAgeDiseases(p); } 
catch (Exception ex) { Log.Warning($"[FP] Ошибка обработки болезней для {p.LabelShort}: {ex.Message}"); }

// --- ВИЗУАЛЬНОЕ СТАРЕНИЕ (СЕДИНА) ---
if (FPMod.Settings.enableAgingVisuals)
{
    try 
    { 
        // Вычисляем, на сколько реально состарилась пешка за этот цикл
        float yearsAgeed = (p.ageTracker.AgeBiologicalTicks - lastKnownAge) / (float)GenDate.TicksPerYear;
        if (yearsAgeed > 0.01f) 
        {
            FPUtility.ProcessGrayingHair(p, yearsAgeed);
        }
    } 
    catch (Exception ex) { Log.Warning($"[FP] Ошибка визуального старения для {p.LabelShort}: {ex.Message}"); }
}
int age = p.ageTracker.AgeBiologicalYears;
// Упрощенный шанс: до 60 лет — 0%, после 60 лет — база 5% в год
float deathChance = (age >= 60) ? 0.05f : 0f;

// --- УМНАЯ ЗАЩИТА ГЕНАМИ ОТ СМЕРТИ ПО СТАРОСТИ ---
if (deathChance > 0f)
{
    // 1. Нативная проверка: если биологические часы остановлены (ageless и его аналоги из модов)
    bool isAgeless = p.ageTracker.BiologicalTicksPerTick == 0f;

    // 2. Поиск генов на неуязвимость
    bool isImmortal = false;
    
    // Ищем гены только если пешка все-таки стареет (иначе зачем тратить ресурсы процессора?)
    if (!isAgeless && ModsConfig.BiotechActive && p.genes != null)
    {
        isImmortal = p.genes.GenesListForReading.Any(g => 
            g.Active && (
                g.def.defName.IndexOf("deathless", StringComparison.OrdinalIgnoreCase) >= 0 || 
                g.def.defName.IndexOf("immortal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                g.def.defName.IndexOf("nonsenescent", StringComparison.OrdinalIgnoreCase) >= 0
            )
        );
    }

    // Если пешка не стареет или физически не может умереть
    if (isAgeless || isImmortal)
    {
        deathChance = 0f; // Смерть отменяется
    }
}

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
    
    savedBioAges.Remove(p.thingIDNumber); 
    pawnNotes.Remove(p.thingIDNumber);
    veteransOnMission.Remove(p.thingIDNumber);
    originalHairColors.Remove(p.thingIDNumber);
    
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
    if (p.Faction == null || p.Faction.def.hidden || p.health?.hediffSet == null) return;

    TechLevel tech = p.Faction.def.techLevel;
    bool changed = false;

    // --- КЭШИРОВАНИЕ БАЗЫ (если еще не создана) ---
    // Это гарантия, что кэш протезов точно загружен до того, как мы начнем апгрейд
    if (cachedProstheticRecipes == null)
    {
        cachedProstheticRecipes = DefDatabase<RecipeDef>.AllDefsListForReading.Where(r =>
            r.addsHediff != null && 
            r.appliedOnFixedBodyParts != null && 
            (typeof(Recipe_InstallArtificialBodyPart).IsAssignableFrom(r.workerClass) || 
             typeof(Recipe_InstallImplant).IsAssignableFrom(r.workerClass))
        ).ToList();
    }

    // 1. ЛЕЧИМ ИНВАЛИДОВ
    // Здесь ToList() нужен ОБЯЗАТЕЛЬНО, так как RestorePart модифицирует коллекцию прямо во время цикла
    var missingParts = p.health.hediffSet.GetMissingPartsCommonAncestors().ToList();
    foreach (var missing in missingParts)
    {
        HediffDef prosth = GetDynamicProstheticFor(p, missing.Part, tech);
        if (prosth != null)
        {
            p.health.RestorePart(missing.Part);
            p.health.AddHediff(prosth, missing.Part);
            changed = true;
        }
    }

    // 2. АПГРЕЙД (Оптимизированный поиск)
    if (tech >= TechLevel.Industrial && Rand.Value < (0.10f * FPMod.Settings.implantChanceMultiplier))
    {
        // Выбираем из нашего кэша ТОЛЬКО те протезы, до которых фракция доросла по технологиям
        var availableUpgrades = cachedProstheticRecipes.Where(r => 
        {
            // ФИКС ОТ СОБАЧЬИХ ЛАП: Проверяем, разрешен ли этот рецепт для расы этой пешки
            if (!p.def.AllRecipes.Contains(r)) return false;

            var itemDef = r.ingredients.FirstOrDefault()?.filter?.AnyAllowedDef;
            return itemDef != null && itemDef.techLevel <= tech;
        }).ToList();

        // Перемешиваем доступные рецепты протезов (их всего пара десятков, это очень быстро!)
        availableUpgrades.Shuffle(); 

        foreach (var recipe in availableUpgrades)
        {
            // Смотрим, есть ли у пешки свободное место под этот конкретный протез
            var partToUpgrade = p.RaceProps.body.AllParts.FirstOrDefault(part => 
                recipe.appliedOnFixedBodyParts.Contains(part.def) &&
                !p.health.hediffSet.PartIsMissing(part) &&
                !p.health.hediffSet.HasDirectlyAddedPartFor(part)
            );

            // Если место нашлось — ставим имплант и сразу выходим
            if (partToUpgrade != null)
            {
                p.health.AddHediff(recipe.addsHediff, partToUpgrade);
                changed = true;
                break; 
            }
        }
    }

    if (changed && FPMod.Settings.enableDebugLogs)
    {
        Log.Message($"<color=cyan>[FP-Surgery]</color> Ветеран {p.LabelShort} ({p.Faction.Name}) получил импланты за год отсутствия!");
    }
}

private void ProcessVeteranRegeneration(Pawn p)
{
    if (!ModsConfig.BiotechActive || p.genes == null) return;

    // ОПТИМИЗАЦИЯ: Если пешка и так полностью здорова, ничего не делаем
    if (!p.health.hediffSet.GetMissingPartsCommonAncestors().Any() && 
        !p.health.hediffSet.hediffs.Any(h => h.IsPermanent() && h is Hediff_Injury)) return;

    // Проверка генов регенерации (всё в одном месте для удобства правки)
    bool hasRegenGene = p.genes.GenesListForReading.Any(g => 
    {
        if (!g.Active) return false;
        switch (g.def.defName)
        {
            case "AG_LimbRegeneration":
            case "VRE_OrganRegeneration":
            case "VQEA_SelfRepairingTissue":
            case "BS_Fast_TotalHealing":
            case "Cinder_Revive":
            case "ArchoGenes_ArchoRegeneration":
            case "WVC_MechaHidden_ArchiteForge":
            case "WVC_WoundHealing_SelfRepair":
            case "WVC_WoundHealing_Unnatural":
            case "WVC_MecaBodyParts_Kidney":
            case "Turn_Gene_FleshbeastRegeneration":
            case "Outland_Regeneration":
            case "ImmortalRegenerant":
            case "SHGE_EternalDivineBless":
            case "SHGE_SelfRegeneration":
            case "SHGE_SuperRegeneration":
            case "SHGE_ExtremeSpeedRegeneration":
                return true;
            default:
                return false;
        }
    });

    if (!hasRegenGene) return;

    // 1. Приоритет: Отращивание утерянных частей тела
    var missing = p.health.hediffSet.GetMissingPartsCommonAncestors().ToList();
    if (missing.Any())
    {
        var partToRegrow = missing.RandomElement();
        p.health.RestorePart(partToRegrow.Part);
        if (FPMod.Settings.enableDebugLogs)
            Log.Message($"<color=green>[FP-Regen]</color> {p.LabelShort}: Отращена часть тела ({partToRegrow.Part.Label}) благодаря генам.");
        return; 
    }

    // 2. Вторично: Лечение шрамов
    var scar = p.health.hediffSet.hediffs.Where(h => h.IsPermanent() && h is Hediff_Injury).ToList();
    if (scar.Any())
    {
        var targetScar = scar.RandomElement();
        p.health.RemoveHediff(targetScar);
        if (FPMod.Settings.enableDebugLogs)
            Log.Message($"<color=green>[FP-Regen]</color> {p.LabelShort}: Заживлен шрам ({targetScar.Label}) благодаря генам.");
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
                // Заполняем кэш генов ОДИН РАЗ за игру
                if (cachedArchiteGenes == null || cachedNormalGenes == null)
                {
                    cachedArchiteGenes = DefDatabase<GeneDef>.AllDefsListForReading.Where(g => g.biostatArc > 0).ToList();
                    cachedNormalGenes = DefDatabase<GeneDef>.AllDefsListForReading.Where(g => g.biostatArc == 0).ToList();
                }

                // Быстро берем нужный список из кэша и отсеиваем только те гены, которых еще нет у пешки
                var baseGenesList = getsArchite ? cachedArchiteGenes : cachedNormalGenes;
                var availableGenes = baseGenesList.Where(g => !p.genes.HasActiveGene(g)).ToList();

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
    // 1. Быстрая проверка возраста (до 60 лет старческих болезней не бывает)
    if (p.ageTracker.AgeBiologicalYears < 60) return;

    // 2. УНИВЕРСАЛЬНАЯ ПРОВЕРКА ДВИЖКА: 
    // Если биологические часы стоят (ванильный Ageless и 99% модов), 
    // пешка не стареет, а значит, и новые старческие болезни получать не должна!
    if (p.ageTracker.BiologicalTicksPerTick == 0f) return;
    
    // 3. УМНАЯ ЗАЩИТА ГЕНАМИ ИЗ МОДОВ (Оптимизированный поиск)
    if (ModsConfig.BiotechActive && p.genes != null)
    {
        // Ищем гены "бога", игнорируя регистр букв и не создавая мусор в памяти
        bool immuneToDisease = p.genes.GenesListForReading.Any(g => 
            g.Active && (
                g.def.defName.IndexOf("diseasefree", StringComparison.OrdinalIgnoreCase) >= 0 || 
                g.def.defName.IndexOf("perfectimmunity", StringComparison.OrdinalIgnoreCase) >= 0 || 
                g.def.defName.IndexOf("deathless", StringComparison.OrdinalIgnoreCase) >= 0 || 
                g.def.defName.IndexOf("immortal", StringComparison.OrdinalIgnoreCase) >= 0 ||
                g.def.defName.IndexOf("nonsenescent", StringComparison.OrdinalIgnoreCase) >= 0
            )
        );

        if (immuneToDisease) return; // У пешки абсолютный иммунитет, выходим
    }

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
// Ищем все рецепты из Anomaly ТОЛЬКО ОДИН РАЗ
                if (cachedAnomalyRecipes == null)
                {
                    cachedAnomalyRecipes = DefDatabase<RecipeDef>.AllDefsListForReading.Where(r =>
                        r.addsHediff != null &&
                        r.modContentPack != null &&
                        r.modContentPack.PackageId.ToLower() == "ludeon.rimworld.anomaly" &&
                        (typeof(Recipe_InstallArtificialBodyPart).IsAssignableFrom(r.workerClass) || 
                         typeof(Recipe_InstallImplant).IsAssignableFrom(r.workerClass))
                    ).ToList();
                }
                var anomalyRecipes = cachedAnomalyRecipes;

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


// --- УМНЫЙ ПОИСК ПРОТЕЗОВ ПО БАЗЕ ДАННЫХ (С КЭШЕМ) ---
        private HediffDef GetDynamicProstheticFor(Pawn p, BodyPartRecord part, TechLevel factionTech)
        {
            // 1. Создаем глобальный кэш всех протезов игры ТОЛЬКО ОДИН РАЗ
            if (cachedProstheticRecipes == null)
            {
                cachedProstheticRecipes = DefDatabase<RecipeDef>.AllDefsListForReading.Where(r =>
                    r.addsHediff != null && 
                    r.appliedOnFixedBodyParts != null && 
                    (typeof(Recipe_InstallArtificialBodyPart).IsAssignableFrom(r.workerClass) || 
                     typeof(Recipe_InstallImplant).IsAssignableFrom(r.workerClass))
                ).ToList();
            }

            // 2. Быстро фильтруем готовый кэш под конкретную часть тела и тех-уровень
            var available = cachedProstheticRecipes.Where(r => 
            {
                if (!r.appliedOnFixedBodyParts.Contains(part.def)) return false;
                
                // Проверяем совместимость с расой (A Dog Said и др.)
                if (!p.def.AllRecipes.Contains(r)) return false;

                var itemDef = r.ingredients.FirstOrDefault()?.filter?.AnyAllowedDef;
                if (itemDef == null) return false;
                
                return itemDef.techLevel <= factionTech; 
            }).ToList();

            if (available.Count == 0) return null;

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

				 
				 

int index = group.pawns.FindIndex(p => 
    p != null && !p.Dead && !p.Discarded && !p.Spawned && p.Map == null && 
    !pawnsIssuedThisTickIDs.Contains(p.thingIDNumber) && 
    !veteransOnMission.Contains(p.thingIDNumber) && 
    // ПРОВЕРКА КУЛДАУНА:
    (!veteranAddTicks.TryGetValue(p.thingIDNumber, out int addedTick) || 
     Find.TickManager.TicksGame >= addedTick + (FPMod.Settings.veteranRecallCooldownDays * 60000)) &&
IsPawnAvailableForDispatch(p) && 
    PawnMatchesRequest(p, request)
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
private bool PawnMatchesRequest(Pawn p, PawnGenerationRequest req)
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


// 9. Внешние валидаторы от других модов (Новый API RimWorld 1.5)
    if (req.ValidatorPreGear != null && !req.ValidatorPreGear(p))
    {
        if (FPMod.Settings.enableDebugLogs) 
            Log.Message($"<color=yellow>[FP-Validator]</color> Ветеран {p.LabelShort} забракован (не прошел PreGear проверку).");
        return false;
    }
    
    if (req.ValidatorPostGear != null && !req.ValidatorPostGear(p))
    {
        if (FPMod.Settings.enableDebugLogs) 
            Log.Message($"<color=yellow>[FP-Validator]</color> Ветеран {p.LabelShort} забракован (не прошел PostGear проверку).");
        return false;
    }

    return true;
}
private bool IsPawnAvailableForDispatch(Pawn p)
{
// 1. Базовая проверка
    if (p == null || !Find.WorldPawns.Contains(p)) return false;

    // --- НОВАЯ ЗАЩИТА: ПЕШКА ДОЛЖНА СТОЯТЬ НА НОГАХ И УМЕТЬ ХОДИТЬ ---
    if (p.Downed || p.health == null || !p.health.capacities.CapableOf(PawnCapacityDefOf.Moving)) 
    {
        if (FPMod.Settings.enableDebugLogs)
            Log.Message($"<color=yellow>[FP-Filter]</color> {p.LabelShort} пропущен: Не может ходить (Downed).");
        return false;
    }

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
            if (p?.RaceProps?.Humanlike == true)
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
// === ПАТЧ 2: ПЕШКА УХОДИТ В МИР (ПРОВЕРКА) ===
[HarmonyPatch(typeof(WorldPawns), nameof(WorldPawns.PassToWorld), new[] { typeof(Pawn), typeof(PawnDiscardDecideMode) })]
public static class Patch_PassToWorld
{
    private static RecordDef timeAsColonistRecord;

    [HarmonyPrefix]
    static void Prefix(Pawn pawn, PawnDiscardDecideMode discardMode)
    {
        if (Current.ProgramState != ProgramState.Playing || pawn == null) return;
        if (WorldPopulationManager.IsManuallyAdding) return;
        if (discardMode != PawnDiscardDecideMode.Decide) return;
        if (pawn.Spawned) return;

        if (!FPSeenTracker.Contains(pawn.thingIDNumber)) return; 

        // 1. ЕДИНЫЙ ФИЛЬТР: Базовые проверки, мутанты, СТРОГАЯ защита квестов
        if (!FPUtility.IsPawnSavable(pawn)) return;

        // 2. ДОПОЛНИТЕЛЬНЫЕ ПРЕДОХРАНИТЕЛИ:
        if (pawn.ParentHolder is Building) return; 
        
        // Отсекаем младенцев (Biotech) - пускаем только от Child (3+ лет) и старше
        if (pawn.DevelopmentalStage == DevelopmentalStage.Baby || pawn.DevelopmentalStage == DevelopmentalStage.Newborn) return;
        
        // Отсекаем зомби/голограммы (дополнительная страховка от модов)
        if (pawn.health != null && pawn.health.Dead) return; 
        // -----------------------------

        var manager = Find.World?.GetComponent<WorldPopulationManager>();
        bool isPinned = manager != null && manager.manualVeteranPins.Contains(pawn.thingIDNumber);

        bool isTemporaryOrHidden = false;
        string dn = "None";
        
        // 3. БЕЗОПАСНОЕ определение проблемных фракций (учитываем безфракционных)
        if (pawn.Faction == null)
        {
            isTemporaryOrHidden = true; // Считаем безфракционных скитальцев временными/проблемными
        }
        else
        {
            dn = pawn.Faction.def.defName;
            isTemporaryOrHidden = pawn.Faction.def.hidden || pawn.Faction.temporary || pawn.Faction.IsPlayer ||
                                  dn.Contains("Refugee") || dn.Contains("Beggar") || 
                                  dn.Contains("Ancient") || dn.Contains("Sleeper");
        }

        if (isTemporaryOrHidden)
        {
            // Кэшируем определение рекорда один раз для макс. производительности
            if (timeAsColonistRecord == null) 
                timeAsColonistRecord = DefDatabase<RecordDef>.GetNamed("TimeAsColonistOrSlave", false);

            // Проверка: был ли он колонистом?
            bool wasColonist = pawn.records != null && timeAsColonistRecord != null && pawn.records.GetValue(timeAsColonistRecord) > 0.1f;

            // Если игрок НЕ нажал "Память", он НЕ бывший колонист и НЕ включена галочка автосохранения — пропускаем
            if (!isPinned && !wasColonist && (FPMod.Settings == null || !FPMod.Settings.autoSaveWanderers)) return;

            // --- ЛОГИКА СМЕНЫ ФРАКЦИИ ---
            
            // ТЕПЕРЬ БЕРЕМ ЗНАЧЕНИЕ ИЗ ПОЛЗУНКА В НАСТРОЙКАХ (по умолчанию 1)
            int allowedRange = FPMod.Settings != null ? FPMod.Settings.techLevelRange : 1;
            
            // Безопасно вытаскиваем тех-уровень (спасает от крашей с попрошайками)
            TechLevel pawnTech = TechLevel.Industrial; // Дефолт на крайний случай
            if (pawn.Faction != null)
            {
                pawnTech = pawn.Faction.def.techLevel;
            }
            
            // Ищем все постоянные фракции (враги, нейтралы, союзники), подходящие по тех-уровню (+/- allowedRange)
            var validFactions = Find.FactionManager.AllFactionsListForReading.Where(f => 
                !f.def.hidden && 
                !f.IsPlayer && 
                !f.temporary && 
                f.def.humanlikeFaction && 
                Math.Abs((int)f.def.techLevel - (int)pawnTech) <= allowedRange && 
                IsXenotypeCompatible(pawn, f) // Наша новая проверка
            ).ToList();
            
            // СПАСАТЕЛЬНЫЙ КРУГ ДЛЯ ПОПРОШАЕК: 
            // Если список пуст (ничего не подошло по технологиям), расширяем поиск
            if (validFactions.Count == 0)
            {
                validFactions = Find.FactionManager.AllFactionsListForReading.Where(f => 
                    !f.def.hidden && 
                    !f.IsPlayer && 
                    !f.temporary && 
                    f.def.humanlikeFaction &&
                    IsXenotypeCompatible(pawn, f)
                ).ToList();
            }

            if (validFactions.Count > 0)
            {
                Faction newFaction = validFactions.RandomElement();
                pawn.SetFaction(newFaction);
				
				// --- ВЕРНУТЬ ЭТОТ ФИКС ---
                foreach (var otherFaction in Find.FactionManager.AllFactionsListForReading)
                {
                    if (otherFaction != newFaction && newFaction.RelationWith(otherFaction, true) == null)
                    {
                        newFaction.TryMakeInitialRelationsWith(otherFaction);
                    }
                }
                // -------------------------
                
                if (FPMod.Settings != null && FPMod.Settings.enableDebugLogs)
                {
                    Log.Message($"<color=cyan>[FP-Wanderer]</color> Скиталец {pawn.LabelShort} (бывш. {dn}) примкнул к постоянной фракции {newFaction.Name}!");
                }
            }
            else
            {
                // Защита от ошибок: если подходящей фракции в мире ВООБЩЕ нет, не сохраняем
                if (FPMod.Settings != null && FPMod.Settings.enableDebugLogs)
                {
                    Log.Warning($"<color=yellow>[FP-Wanderer]</color> Не найдено подходящей фракции для {pawn.LabelShort} (Тех: {pawnTech}). Пешка стерта, чтобы не стать призраком.");
                }
                return;
            }
        }
        else
        {
            // Для обычных (постоянных) фракций всё по-старому:
            // Если звезды нет, отсеиваем тех, кого просто утащили с карты (похищенных)
            if (!isPinned && PawnUtility.IsKidnappedPawn(pawn)) return;
        }

        // Если дошли сюда — пешка легальна, фракция правильная, можно сохранять!
        FPSeenTracker.Remove(pawn.thingIDNumber);
        VeteranInputQueue.Enqueue(pawn);
    }
    // ... дальше метод IsXenotypeCompatible ...
	
private static bool IsXenotypeCompatible(Pawn pawn, Faction f)
{
    // Если DLC Biotech выключено или у пешки нет генов — все совместимо
    if (!ModsConfig.BiotechActive || pawn.genes == null) return true; 

    XenotypeDef pawnXeno = pawn.genes.Xenotype ?? XenotypeDefOf.Baseliner;
    if (f.def.xenotypeSet == null) return true; // Фракция без жестких рамок принимает всех

    // Используем Traverse (HarmonyLib) для динамического доступа, 
    // чтобы обойти различия API и ошибки компилятора
    var traverseSet = Traverse.Create(f.def.xenotypeSet);
    
    // Пытаемся достать список шансов под всеми возможными именами
    var chancesList = traverseSet.Field("xenotypeChances").GetValue() 
                   ?? traverseSet.Field("chances").GetValue()
                   ?? traverseSet.Property("XenotypeChances").GetValue();

    // Если список найден (будь то массив или List)
    if (chancesList is System.Collections.IEnumerable enumerable)
    {
        float totalMutantChance = 0f;
        
        foreach (var item in enumerable)
        {
            var traverseItem = Traverse.Create(item);
            XenotypeDef xDef = traverseItem.Field("xenotype").GetValue<XenotypeDef>();
            float chance = traverseItem.Field("chance").GetValue<float>();
            
            if (xDef == pawnXeno && chance > 0f) return true;
            totalMutantChance += chance;
        }
        
        // ВАЖНЫЙ НЮАНС ВАНИЛЛЫ: Шанс спавна обычных людей (Baseliner) — это остаток от 100%.
        // Если сумма шансов мутантов < 1f (например, 0.8f), значит остальные 20% - это люди, и нам туда можно!
        if (pawnXeno == XenotypeDefOf.Baseliner && totalMutantChance < 1f) return true; 
        
        return false; // Ксенотип строго не подходит для этой фракции!
    }

    // Если игра не позволила прочитать список (страховка), разрешаем по умолчанию
    return true; 
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
if (manager != null && (manager.allVeteranIdsCache.Contains(pawn.thingIDNumber) || 
                        VeteranInputQueue.pendingPawnIDs.Contains(pawn.thingIDNumber)))
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

                    // --- ВОССТАНОВЛЕНИЕ ОРУЖИЯ И ОДЕЖДЫ ДЛЯ ГОЛЫХ ВЕТЕРАНОВ ---
                    try
                    {
                        if (v.apparel != null && v.apparel.WornApparel.Count < 3)
                        {
                            var worn = v.apparel.WornApparel.ToList();
                            foreach (var item in worn)
                            {
                                v.apparel.Remove(item);
                                item.Destroy();
                            }
                            PawnApparelGenerator.GenerateStartingApparelFor(v, request);
                        }

                        if (v.inventory != null && v.inventory.innerContainer != null)
                        {
                            // --- ЭЛЕГАНТНАЯ ОЧИСТКА ИНВЕНТАРЯ И ЗАЩИТА ЛУТА ---
                            var savedLoot = new List<Thing>();
                            var toDestroy = new List<Thing>();
                            int randomJunkCount = 0; // Счетчик сувениров

                            // Разделяем карманы на "сохранить" и "удалить"
                            foreach (Thing t in v.inventory.innerContainer)
                            {
                                // 1. Еда и наркотики -> в мусор (игра выдаст новые)
                                if (t.def.IsIngestible || t.def.IsMedicine || t.def.IsDrug)
                                {
                                    toDestroy.Add(t);
                                    continue; // Предмет обработан, переходим к следующему
                                }

                                // 2. Умный поиск патронов (Combat Extended и др.) -> ВСЕГДА сохраняем
                                bool isAmmo = (t.def.thingCategories != null && t.def.thingCategories.Any(c => c.defName.IndexOf("Ammo", StringComparison.OrdinalIgnoreCase) >= 0)) ||
                                              t.def.defName.IndexOf("Ammo", StringComparison.OrdinalIgnoreCase) >= 0;

                                if (isAmmo)
                                {
                                    savedLoot.Add(t);
                                    continue; // Переходим к следующему, патроны не занимают лимит сувениров
                                }

                                // 3. Серебро -> режем стак и ВСЕГДА сохраняем
                                if (t.def == ThingDefOf.Silver)
                                {
                                    int randomCap = Rand.RangeInclusive(100, 1000);
                                    t.stackCount = Mathf.Min(t.stackCount, randomCap);
                                    savedLoot.Add(t);
                                    continue; // Серебро обработано
                                }

                                // 4. ОСТАЛЬНОЙ ЛУТ (Статуи, шкуры, запасное оружие) -> применяем лимит в 4 предмета
                                if (randomJunkCount < 4)
                                {
                                    savedLoot.Add(t);
                                    randomJunkCount++;
                                }
                                else
                                {
                                    toDestroy.Add(t); // Если сувениров уже 4, остальное выбрасываем
                                }
                            }

                            // Физически достаем ценный лут (сталь, ресурсы, ключи, ПАТРОНЫ) из карманов пешки
                            // Это спасет их, если базовый генератор попытается удалить всё!
                            foreach (Thing t in savedLoot)
                            {
                                v.inventory.innerContainer.Remove(t);
                            }

                            // Уничтожаем старый мусор (старую еду и лишние товары)
                            foreach (Thing t in toDestroy)
                            {
                                t.Destroy();
                            }

                            // Выдаем свежий паек (ванильный метод иногда стирает всё)
                            PawnInventoryGenerator.GenerateInventoryFor(v, request);

                            // Возвращаем ценный спасенный лут обратно в карманы!
                            foreach (Thing t in savedLoot)
                            {
                                v.inventory.innerContainer.TryAdd(t);
                            }
                        }

                        // --- ГЕНЕРАЦИЯ ОРУЖИЯ ---
                        if (v.equipment != null && !v.equipment.AllEquipmentListForReading.Any(eq => eq.def.IsWeapon))
                            PawnWeaponGenerator.TryGenerateWeaponFor(v, request);

                        // --- КРИТИЧЕСКАЯ СОВМЕСТИМОСТЬ ПАТРОНОВ (Combat Extended, Yayo's, etc.) ---
                        // Если ветеран вернулся с пушкой, к ней НУЖНЫ патроны. 
                        // Многие моды выдают их только при создании НОВОЙ пушки, поэтому мы делаем это сами.
                        if (v.equipment != null && v.equipment.Primary != null && v.inventory != null)
                        {
                            var weapon = v.equipment.Primary;
                            // Ищем ЛЮБОЙ компонент, связанный с патронами (AmmoUser, CompAmmo, и т.д.)
                            var ammoComp = weapon.AllComps.FirstOrDefault(c => 
                                c.GetType().Name.Contains("Ammo") || 
                                c.GetType().Name.Contains("Reload") ||
                                c.GetType().Name.Contains("Yayo"));

                            if (ammoComp != null)
                            {
                                try
                                {
                                    var trComp = Traverse.Create(ammoComp);
                                    ThingDef ammoDef = null;

                                    // 1. Пытаемся достать текущий тип патрона (CE стиль)
                                    var currentAmmo = trComp.Property("CurrentAmmo").GetValue();
                                    if (currentAmmo != null) ammoDef = currentAmmo as ThingDef;

                                    // 2. Если пусто — лезем в список доступных патронов (Yayo / CE fallback)
                                    if (ammoDef == null)
                                    {
                                         // Пробуем разные названия полей, где моды хранят списки патронов
                                         var ammoSet = trComp.Field("ammoSet").GetValue() ?? trComp.Property("AmmoSet").GetValue();
                                         if (ammoSet != null)
                                         {
                                             var trSet = Traverse.Create(ammoSet);
                                             var ammoTypes = trSet.Field("ammoTypes").GetValue() as System.Collections.IEnumerable 
                                                          ?? trSet.Property("AmmoTypes").GetValue() as System.Collections.IEnumerable;
                                             
                                             if (ammoTypes != null)
                                             {
                                                 foreach (var at in ammoTypes)
                                                 {
                                                     var trAt = Traverse.Create(at);
                                                     ammoDef = trAt.Field("ammo").GetValue<ThingDef>() 
                                                            ?? trAt.Property("Ammo").GetValue<ThingDef>()
                                                            ?? trAt.Field("ammoDef").GetValue<ThingDef>();
                                                     if (ammoDef != null) break;
                                                 }
                                             }
                                         }
                                    }

                                    // 3. Совместимость конкретно с Yayo's Combat (если ничего не помогло)
                                    if (ammoDef == null)
                                    {
                                        ammoDef = trComp.Field("ammoDef").GetValue<ThingDef>() 
                                               ?? trComp.Property("AmmoDef").GetValue<ThingDef>();
                                    }

                                    // Если патрон найден — выдаем боезапас!
                                    if (ammoDef != null)
                                    {
                                        // Определяем количество: 3 магазина или стандартные 60 штук
                                        int magSize = 0;
                                        try { magSize = trComp.Property("MagSize").GetValue<int>(); } catch { }
                                        if (magSize <= 0) try { magSize = trComp.Field("magSize").GetValue<int>(); } catch { }
                                        
                                        int ammoToGive = (magSize > 0) ? magSize * 3 : 60;
                                        
                                        // Проверяем текущее наличие, чтобы не спамить патронами
                                        int currentCount = v.inventory.innerContainer.Where(t => t.def == ammoDef).Sum(t => t.stackCount);
                                        
                                        if (currentCount < ammoToGive)
                                        {
                                            Thing ammoThing = ThingMaker.MakeThing(ammoDef);
                                            ammoThing.stackCount = Mathf.Min(ammoToGive - currentCount, ammoDef.stackLimit);
                                            v.inventory.innerContainer.TryAdd(ammoThing);
                                        }
                                    }
                                }
                                catch { /* Безопасный пропуск ошибок рефлексии */ }
                            }
                        }

                        // ПЕРЕЗАРИЖАЕМ ОРУЖИЕ ПРЯМО ПРИ СПАВНЕ
                        FPUtility.ReloadWeapons(v);

                        if (FPMod.Settings.enableAgingVisuals && manager.savedBioAges.TryGetValue(v.thingIDNumber, out long lastAge))
                        {
                            float diffYears = (v.ageTracker.AgeBiologicalTicks - lastAge) / (float)GenDate.TicksPerYear;
                            if (diffYears > 0.01f) 
                            {
                                FPUtility.ProcessGrayingHair(v, diffYears);
                                
                                // ФИКС CS-280: Синхронизируем внутренний счетчик ваниллы, 
                                // чтобы игра не запускала BirthdayBiological за пропущенные в пуле годы.
                                Traverse.Create(v.ageTracker).Field("lastBirthdayBiologicalYear").SetValue(v.ageTracker.AgeBiologicalYears);
                            }
                            // Обновляем метку, чтобы стационарный цикл в пуле не считал это время еще раз
                            manager.savedBioAges[v.thingIDNumber] = v.ageTracker.AgeBiologicalTicks;
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warning($"[FP] Не удалось сгенерировать экипировку для {v.LabelShort}: {ex.Message}");
                    }

                    // --- ВЫДАЧА ТИТУЛОВ ДЛЯ КВЕСТОВЫХ ПЕШЕК (ROYALTY) ---
                    if (ModsConfig.RoyaltyActive && request.KindDef != null && request.Faction != null)
                    {
                        try
                        {
                            if (request.KindDef.titleRequired != null)
                            {
                                if (v.royalty != null && !v.royalty.HasTitle(request.KindDef.titleRequired))
                                    v.royalty.SetTitle(request.Faction, request.KindDef.titleRequired, true, false, true);
                            }
                            else if (request.KindDef.titleSelectOne != null && request.KindDef.titleSelectOne.Count > 0)
                            {
                                var randomTitle = request.KindDef.titleSelectOne.RandomElement();
                                if (v.royalty != null && !v.royalty.HasTitle(randomTitle))
                                    v.royalty.SetTitle(request.Faction, randomTitle, true, false, true);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warning($"[FP] Ошибка выдачи титула для {v.LabelShort}: {ex.Message}");
                        }
                    }

                    // --- ОЧИСТКА ЗАВИСИМОСТЕЙ ЕСЛИ НУЖНО (в данном случае мы не чистим перманентные болезни, но в будущем можно)
                    
                    // --- ОЧИСТКА СТАТУСА ПЛЕННИКА / ГОСТЯ И ДРУГИХ ХВОСТОВ КОЛОНИИ ---
                    // Это исправит баг, когда отпущенный пленник, вернувшийся в виде рейдера, 
                    // все еще считался вашим узником, хотя физически нападал на вас.
                    if (v.guest != null) v.guest.SetGuestStatus(null);
                    
                    // Полностью удаляем настройки игрока (политки атаки, зоны, медикаменты), 
                    // так как пешка теперь принадлежит другой фракции.
                    v.playerSettings = null;
                    
                    if (v.ownership != null) v.ownership.UnclaimAll(); // Отвязываем от кроватей колонии
                    
                    if (v.timetable != null) v.timetable.times = null; // Сбрасываем расписание колонии
                    
                    if (v.drafter != null) v.drafter.Drafted = false; // Снимаем боевой режим, если он багом остался

                    // --- НОВАЯ ОЧИСТКА РАЗУМА (ЧТОБЫ ИИ НЕ СХОДИЛ С УМА) ---
                    if (v.mindState != null)
                    {
                        v.mindState.duty = null;
                        v.mindState.mentalStateHandler?.Reset();
                        v.mindState.enemyTarget = null;
                    }
                    if (v.jobs != null)
                    {
                        v.jobs.ClearQueuedJobs();
                        v.jobs.StopAll();
                    }
                    // -------------------------------------------------------

                    __result = v; 
                    return false;
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
    if (FPMod.Settings != null && !FPMod.Settings.enableMothball) return;

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
        HediffDef addictionDef = __result; // Запомним, что это была за зависимость, для лога
        __result = null;

        // Проверяем, нет ли других БЛОКИРУЮЩИХ болезней (раны, инфекции, недоедание)
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

        // ПИШЕМ ЛОГ ТОЛЬКО ЗДЕСЬ: если других болезней не нашлось и пешка РЕАЛЬНО уснула
        if (__result == null && !isAlreadyMothballed && FPMod.Settings.enableDebugLogs)
        {
            Log.Message($"<color=orange>[FP-Freeze]</color> {p.LabelShort} засыпает (Разрешена зависимость: {addictionDef.defName}).");
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

public static partial class FPUtility
{
    public static void ReloadWeapons(Pawn p)
    {
        if (p == null || p.equipment == null) return;
        
        foreach (var eq in p.equipment.AllEquipmentListForReading)
        {
            if (eq == null) continue;
            
            foreach (var comp in eq.AllComps)
            {
                string compName = comp.GetType().Name;
                if (!compName.Contains("Ammo") && !compName.Contains("Reload") && !compName.Contains("Charged")) continue;

                try
                {
                    var tr = Traverse.Create(comp);
                    
                    // 1. ИНИЦИАЛИЗАЦИЯ ТИПА ПАТРОНА
                    // ДОБАВЛЕНО: CurrentAmmo и SelectedAmmo (с большой буквы для CE)
                    string[] ammoDefFields = { "ammoDef", "CurrentAmmo", "SelectedAmmo", "currentAmmo", "curAmmo", "selectedAmmo" };
                    foreach (var adf in ammoDefFields)
                    {
                        var f = tr.Field(adf);
                        var prop = tr.Property(adf); // ДОБАВЛЕНО: Поиск по Property
                        
                        bool isField = f.FieldExists();
                        bool isProp = prop.PropertyExists();

                        // Проверяем и Field, и Property
                        if ((isField && f.GetValue() == null) || (isProp && prop.GetValue() == null))
                        {
                            // Пытаемся найти список доступных патронов и взять первый
                            var props = tr.Property("Props").GetValue() ?? tr.Field("props").GetValue();
                            if (props != null)
                            {
                                var trP = Traverse.Create(props);
                                var ammoSet = trP.Field("ammoSet").GetValue() ?? trP.Field("ammoTypes").GetValue();
                                if (ammoSet is System.Collections.IEnumerable list)
                                {
                                    foreach (var item in list)
                                    {
                                        var itemDef = Traverse.Create(item).Field("ammo").GetValue<ThingDef>() 
                                                   ?? Traverse.Create(item).Field("ammoDef").GetValue<ThingDef>();
                                        
                                        if (itemDef != null) 
                                        { 
                                            // Устанавливаем туда, что удалось найти
                                            if (isField) f.SetValue(itemDef);
                                            if (isProp) prop.SetValue(itemDef);
                                            break; 
                                        }
                                    }
                                }
                            }
                        }
                    }

                    int magSize = 0;
                    // 2. Ищем МАКСИМУМ
                    magSize = tr.Property("MaxCharges").GetValue<int>();
                    if (magSize <= 0) magSize = tr.Method("MaxAmmoAmount").GetValue<int>();
                    if (magSize <= 0) magSize = tr.Property("MagSize").GetValue<int>();
                    if (magSize <= 0) magSize = tr.Field("magSize").GetValue<int>();
                    if (magSize <= 0) magSize = tr.Property("AmmoCountMax").GetValue<int>();

                    if (magSize <= 0)
                    {
                        var props = tr.Property("Props").GetValue() ?? tr.Field("props").GetValue();
                        if (props != null)
                        {
                            var trP = Traverse.Create(props);
                            magSize = trP.Field("maxCharges").GetValue<int>();
                            if (magSize <= 0) magSize = trP.Field("magSize").GetValue<int>();
                            if (magSize <= 0) magSize = trP.Field("magazineSize").GetValue<int>(); // ДОБАВЛЕНО: для CE
                            if (magSize <= 0) magSize = trP.Field("ammoCountMax").GetValue<int>();
                            if (magSize <= 0) magSize = trP.Property("Capacity").GetValue<int>();
                        }
                    }

                    if (magSize > 0)
                    {
                        // 3. Устанавливаем ТЕКУЩЕЕ количество
                        bool set = false;
                        // ДОБАВЛЕНО: CurMagCount с большой буквы
                        string[] countFields = { "remainingCharges", "CurMagCount", "curMagCount", "curAmmoCount", "ammoCount", "curAmmo", "count", "ammo" };
                        
                        foreach (var fName in countFields)
                        {
                            var f = tr.Field(fName);
                            if (f.FieldExists()) { f.SetValue(magSize); set = true; }
                            
                            var pField = tr.Property(fName);
                            if (pField.PropertyExists()) { pField.SetValue(magSize); set = true; }
                        }

                        // 4. Сброс состояния (вызов родных методов мода)
                        // ДОБАВЛЕНО: ResetAmmoCount (стандартный метод CE для полной перезарядки оружия без анимации)
                        string[] resetMethods = { "ResetAmmoCount", "ResetAmmo", "FullReload", "Reload", "FillMagazine", "UpdateVerbs" };
                        foreach (var mName in resetMethods)
                        {
                            var method = tr.Method(mName);
                            if (method.MethodExists()) 
                            { 
                                method.GetValue(); 
                                set = true; 
                            }
                        }

                        if (set && FPMod.Settings != null && FPMod.Settings.enableDebugLogs)
                            Log.Message($"<color=orange>[FP-Reload]</color> {p.LabelShort}: {eq.LabelShort} заряжено: {magSize}");
                    }
                }
                catch (Exception) { }
            }
        }
    }

    public static bool IsPawnSavable(Pawn pawn)
    {
        // 1. Базовые проверки
        if (pawn == null || !pawn.RaceProps.Humanlike || pawn.Dead) return false;
        if (pawn.Faction != null && pawn.Faction.IsPlayer) return false;

        // 2. Мутанты (Anomaly)
        if (ModsConfig.AnomalyActive && pawn.IsMutant) return false;

        // 3. СТРОГАЯ ЗАЩИТА КВЕСТОВ (Никаких исключений!)
        // Отсекаем и гостей (Стелларх), и зарезервированных (попрошайки, беженцы)
        if (pawn.IsQuestLodger() || QuestUtility.IsReservedByQuestOrQuestBeingGenerated(pawn))
        {
            return false;
        }

        // 4. ЗАЩИТА ОТ ИСЦЕЛЕНИЯ В КАРАВАНАХ/КАПСУЛАХ И ТЮРЬМАХ
        if (pawn.IsCaravanMember() || PawnUtility.IsTravelingInTransportPodWorldObject(pawn)) return false;
        
        // Отсекаем ваших текущих пленников (чтобы не лутали лечение при переноске),
        // НО разрешаем тех, кого ВЫПУСТИЛИ на волю (Released = true)
        if ((pawn.IsPrisoner || pawn.IsPrisonerOfColony) && (pawn.guest == null || !pawn.guest.Released)) return false;

        return true; // Если дошли сюда — пешка полностью свободна
    }
}
	
}