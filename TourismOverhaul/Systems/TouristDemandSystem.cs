using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Companies;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Fix B — population-scaled tourist demand.
    ///
    /// Vanilla defect (see docs/TOURISM-DIAGNOSIS.md, Findings 1 and 2):
    /// TourismSystem.GetTargetTourists returns attractiveness * 15 below 100, and city
    /// attractiveness is itself logistic-capped at 100, so the target is a flat 1500 tourist
    /// citizens for any city. Above 100 it grows as 100 * log10, i.e. +200 for doubling
    /// attractiveness. Tourism can therefore never be more than a rounding error in a large city.
    ///
    /// This system does not disable or patch anything. It complements the native spawner by
    /// creating additional tourist households — using the same archetype, the same Household
    /// flags and the same outside-connection selection — whenever the city is below a target that
    /// scales with population. Everything downstream (HouseholdInitializeSystem, hotels,
    /// pathfinding, the economy) treats them exactly like native arrivals.
    ///
    /// Unlike the native spawner it resolves the outside connection FIRST and skips the arrival
    /// entirely if none is available, so it can never create the citizen-less zombie households
    /// described in Finding 3.
    /// </summary>
    public partial class TouristDemandSystem : GameSystemBase
    {
        private EntityQuery m_HouseholdPrefabQuery;
        private EntityQuery m_OutsideConnectionQuery;
        private EntityQuery m_DemandParameterQuery;
        private EntityQuery m_TouristHouseholdQuery;

        private EntityQuery m_LeakedHouseholdQuery;
        /// <summary>
        /// Citizens per travel party, used to turn a shortfall in people into a number of
        /// households to create. Measured at roughly 2.3 across a session with the group-size
        /// preference at its default; the exact value only affects how briskly the gap closes.
        /// </summary>
        private const float kAveragePartySize = 2.3f;

        /// <summary>
        /// Updates over which to close the shortfall. This system runs 1,024 times per in-game
        /// month, so 256 fills a gap over roughly a quarter of a month — brisk enough to feel
        /// responsive after building hotels, gentle enough not to overshoot and evict.
        /// </summary>
        private const int kFillUpdates = 256;

        private EntityQuery m_HotelQuery;
        private EntityQuery m_ArrivedQuery;

        private CitySystem m_CitySystem;
        private SimulationSystem m_SimulationSystem;
        private TimeSystem m_TimeSystem;
        private EndFrameBarrier m_EndFrameBarrier;
        private TouristSpawnSystem m_NativeSpawnSystem;
        private HotelWelcomeSystem m_WelcomeSystem;

        private bool m_NativeSpawnerDisabled;

        /// <summary>Leaked empty households cleaned up since load.</summary>
        public int LeakedHouseholdsRemoved { get; private set; }

        /// <summary>Whether the native tourist spawner is currently stood down.</summary>
        public bool NativeSpawnerDisabled => m_NativeSpawnerDisabled;

        /// <summary>Accurate tourist citizen count from the most recent update.</summary>
        public int CurrentTourists { get; private set; }

        /// <summary>Target tourist citizen count from the most recent update.</summary>
        public int TargetTourists { get; private set; }

        /// <summary>
        /// Tourist demand from the city itself — population and attractiveness only, with no
        /// contribution from hotel rooms or opening boosts.
        ///
        /// This is what tells you whether more hotels are warranted. Using the full target would
        /// be circular: rooms raise the target, which would then justify more rooms indefinitely.
        /// </summary>
        public int IntrinsicTarget { get; private set; }

        /// <summary>Households this system tried to create since load.</summary>
        public int SpawnAttempts { get; private set; }

        /// <summary>
        /// Tourists dispatched through each connection type, as (road, train, air, ship).
        ///
        /// Counted in citizens rather than households, because a household is one to four people
        /// and the interesting question is how many visitors each mode delivers.
        ///
        /// This is a flow, not a population: it counts everyone who came through the door over a
        /// month, where CurrentTourists counts who is standing in the city right now. The two
        /// differ by the average length of stay and are not expected to be close.
        ///
        /// Counted at dispatch, immediately after the outside connection is resolved. Anything the
        /// game later discards — a household that never gets initialised — is still counted here,
        /// so treat this as arrivals sent rather than visitors confirmed.
        ///
        /// Reported as a smoothed rate per calendar month, not a running total. See
        /// UpdateArrivalRate for why a calendar bucket was the wrong shape here.
        /// </summary>
        public int4 MonthlyArrivalsByMode => (int4)math.round(m_ArrivalRate);

        // Smoothed arrivals per month, and the accumulator feeding it.
        private float4 m_ArrivalRate;
        private int4 m_ArrivalsSinceSample;
        private float m_LastNormalizedDate;
        private bool m_HasLastDate;

        /// <summary>Arrivals so far in the current month, for diagnostics that need a live figure.</summary>
        public int4 ThisMonthArrivals => m_ThisMonthArrivals;

        /// <summary>Calendar month index, 0-11. Lets other systems reset on the same boundary.</summary>
        public int MonthIndex => m_CurrentMonth;

        private int4 m_ThisMonthArrivals;
        private int m_CurrentMonth = -1;

        /// <summary>Households actually created — attempts minus failed connection lookups.</summary>
        public int SpawnSuccesses { get; private set; }

        // 262144 frames per day; 256 gives 1024 updates/day, matching the cadence at which the
        // native spawner would realistically deliver arrivals without bursting.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 256;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<TimeSystem>();

            // Households dispatched but not yet counted. They lose ArrivalMode once counted, so
            // this query drains itself.
            m_ArrivedQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.ReadOnly<Components.ArrivalMode>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_NativeSpawnSystem = World.GetOrCreateSystemManaged<TouristSpawnSystem>();
            m_WelcomeSystem = World.GetOrCreateSystemManaged<HotelWelcomeSystem>();

            // Household prefabs to spawn from.
            //
            // DynamicHousehold is excluded deliberately, and this is NOT what the native tourist
            // spawner does — TouristSpawnSystem.cs:177 queries (ArchetypeData, HouseholdData) with
            // no exclusion, while the resident spawner at HouseholdSpawnSystem.cs:218 queries the
            // same two components plus Exclude<DynamicHousehold>().
            //
            // That difference matters, because HouseholdInitializeSystem skips dynamic prefabs
            // outright:
            //     if (m_DynamicHouseholds.HasComponent(prefab)) { continue; }        (:156)
            // and the `continue` bypasses the RemoveComponent<CurrentBuilding> at :250. So a
            // tourist household rolled onto a dynamic prefab is never given citizens, never stops
            // matching the initialise query, and is eventually culled as TouristNoTarget. It never
            // becomes a tourist.
            //
            // Roughly two thirds of household prefabs carry DynamicHousehold, so the native
            // tourist spawner loses about that share of everything it creates — on top of the
            // outside-connection losses in Finding 3.
            m_HouseholdPrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<ArchetypeData>(),
                ComponentType.ReadOnly<HouseholdData>(),
                ComponentType.Exclude<DynamicHousehold>());

            m_OutsideConnectionQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Game.Objects.ElectricityOutsideConnection>(),
                ComponentType.Exclude<Game.Objects.WaterPipeOutsideConnection>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());

            m_DemandParameterQuery = GetEntityQuery(ComponentType.ReadOnly<DemandParameterData>());

            // Counting query: only households that actually hold citizens count as tourists.
            m_TouristHouseholdQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.ReadOnly<HouseholdCitizen>(),
                ComponentType.Exclude<MovingAway>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            // Households the native spawner rolled onto a DynamicHousehold prefab. These never get
            // citizens (HouseholdInitializeSystem.cs:156), and because they are never initialised
            // they never receive UpdateFrame either — so TouristHouseholdBehaviorSystem never adds
            // LodgingSeeker, TouristFindTargetSystem never sees them, and nothing ever deletes
            // them. They accumulate for the lifetime of the save.
            //
            // Identified precisely by still holding CurrentBuilding (the initialise pass removes
            // it at :250) while having no citizens.
            m_LeakedHouseholdQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.ReadOnly<CurrentBuilding>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<MovingAway>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            // Hotels, for sizing demand against the rooms the city has actually built.
            m_HotelQuery = GetEntityQuery(
                ComponentType.ReadOnly<LodgingProvider>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.ReadOnly<Renter>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            RequireForUpdate(m_HouseholdPrefabQuery);
            RequireForUpdate(m_DemandParameterQuery);
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;
            if (settings == null)
            {
                return;
            }

            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<Tourism>(city))
            {
                return;
            }

            int attractiveness = EntityManager.GetComponentData<Tourism>(city).m_Attractiveness;

            int population = 0;
            if (EntityManager.HasComponent<Population>(city))
            {
                population = EntityManager.GetComponentData<Population>(city).m_Population;
            }

            CountConvertedArrivals();
            RollOverMonthIfNeeded();
            UpdateArrivalRate();

            CurrentTourists = CountTouristCitizens();

            // Extra allowance from hotels inside their opening period. Added on top of the target
            // rather than folded into it, so these are genuinely additional visitors rather than
            // the same tourists moving to the new hotel.
            int welcomeBonus = m_WelcomeSystem != null ? m_WelcomeSystem.WelcomeBonus : 0;
            bool welcoming = welcomeBonus > 0;

            IntrinsicTarget = ComputeIntrinsicTarget(settings, attractiveness, population);

            TargetTourists = ComputeTarget(settings, attractiveness, population, CountHotelRooms())
                             + welcomeBonus;

            if (!settings.FixTouristDemand)
            {
                RestoreNativeSpawner();
                return;
            }

            // Our spawner covers demand, and the native one leaks a dead household for roughly
            // every two it creates, so let it stand down rather than compete.
            if (settings.ReplaceNativeSpawner)
            {
                DisableNativeSpawner();
                CleanUpLeakedHouseholds();
            }
            else
            {
                RestoreNativeSpawner();
            }

            int deficit = TargetTourists - CurrentTourists;
            if (deficit <= 0)
            {
                return;
            }

            // Fill gradually: a large shortfall should not arrive in one tick. While a hotel is
            // opening, lift the ceiling so the boost reads as a surge of visitors rather than a
            // slow trickle that arrives after the opening period has already lapsed.
            int cap = math.max(1, settings.MaxArrivalsPerUpdate);

            if (welcoming)
            {
                // Never below the player's own ceiling. This used to be min(64, cap * multiplier),
                // which quietly *cut* arrivals during an opening once the setting went above 64 —
                // the opposite of what a welcome boost is for.
                cap = math.min(1024, cap * math.max(1, settings.HotelWelcomeArrivalMultiplier));
            }

            // Convert the shortfall into a number of travel parties, then spread it over a fill
            // horizon so the city approaches its target instead of overshooting.
            //
            // The old form was deficit / 16, which had a unit error: deficit counts citizens while
            // arrivals counts households, and a household is a party of two or three. It also took
            // no account of how often this runs — 1,024 times per in-game month — so a shortfall of
            // 20,000 asked for 1,250 parties per update, over a thousand times a month. The spawner
            // simply pinned at its ceiling and stayed there, dispatching a quarter of a million
            // visitors a month to sustain eighteen thousand. Measured: cutting the rate sixfold left
            // the tourist population unchanged, so the surplus was pure waste — entities created,
            // initialised, emptied and cleaned up, costing simulation time for no visitors.
            //
            // Dividing by the party size and the horizon makes the rate proportional to the gap, so
            // it settles by itself: wide gap, brisk arrivals; nearly full, a trickle. Numbers still
            // climb whenever the target exceeds the population, and the target already rises with
            // attractiveness, population and hotel rooms — so building rooms while attractiveness is
            // high reopens the gap and arrivals pick up again on their own.
            //
            // MaxArrivalsPerUpdate is now a ceiling for pacing rather than the thing driving the
            // rate, which is what it was always meant to be.
            int parties = (int)math.ceil(deficit / (kAveragePartySize * kFillUpdates));
            int arrivals = math.clamp(parties, 1, cap);

            SpawnTouristHouseholds(arrivals, settings);
        }

        private void DisableNativeSpawner()
        {
            if (m_NativeSpawnerDisabled || m_NativeSpawnSystem == null)
            {
                return;
            }

            m_NativeSpawnSystem.Enabled = false;
            m_NativeSpawnerDisabled = true;
            Mod.Log.Info("Native TouristSpawnSystem disabled; arrivals handled by TourismOverhaul.");
        }

        private void RestoreNativeSpawner()
        {
            if (!m_NativeSpawnerDisabled || m_NativeSpawnSystem == null)
            {
                return;
            }

            m_NativeSpawnSystem.Enabled = true;
            m_NativeSpawnerDisabled = false;
            Mod.Log.Info("Native TouristSpawnSystem restored.");
        }

        /// <summary>
        /// Deletes tourist households that can never be initialised, in small batches so a save
        /// with a large accumulated backlog is cleared gradually rather than in one spike.
        ///
        /// Only households whose prefab carries DynamicHousehold are touched. Ours never do, and a
        /// normal household loses CurrentBuilding as soon as it is initialised, so a freshly
        /// spawned household waiting its turn is never mistaken for a leaked one — it would have to
        /// be both uninitialised and on a dynamic prefab, which is exactly the leak.
        /// </summary>
        private void CleanUpLeakedHouseholds()
        {
            const int kMaxRemovalsPerUpdate = 64;

            if (m_LeakedHouseholdQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();

            EntityTypeHandle entityHandle = GetEntityTypeHandle();
            ComponentTypeHandle<PrefabRef> prefabHandle = GetComponentTypeHandle<PrefabRef>(isReadOnly: true);
            BufferTypeHandle<HouseholdCitizen> citizenHandle =
                GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);

            int removed = 0;

            NativeArray<ArchetypeChunk> chunks =
                m_LeakedHouseholdQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length && removed < kMaxRemovalsPerUpdate; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityHandle);
                    NativeArray<PrefabRef> prefabs = chunk.GetNativeArray(ref prefabHandle);

                    bool hasCitizens = chunk.Has(ref citizenHandle);
                    BufferAccessor<HouseholdCitizen> citizens =
                        hasCitizens ? chunk.GetBufferAccessor(ref citizenHandle) : default;

                    for (int i = 0; i < chunk.Count && removed < kMaxRemovalsPerUpdate; i++)
                    {
                        if (hasCitizens && citizens[i].Length > 0)
                        {
                            continue;
                        }

                        if (!EntityManager.HasComponent<DynamicHousehold>(prefabs[i].m_Prefab))
                        {
                            continue;
                        }

                        commandBuffer.AddComponent<Deleted>(entities[i]);
                        removed++;
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            if (removed > 0)
            {
                LeakedHouseholdsRemoved += removed;
            }
        }

        /// <summary>
        /// Target never drops below what the base game would have produced, so this can only add
        /// tourists, never remove them.
        /// </summary>
        /// <summary>
        /// Demand from the city alone: population and attractiveness, no rooms, no boosts.
        /// </summary>
        private static int ComputeIntrinsicTarget(
            TourismOverhaulSetting settings, int attractiveness, int population)
        {
            int vanillaTarget = TourismSystem.GetTargetTourists(math.max(0, attractiveness));

            if (!settings.FixTouristDemand)
            {
                return vanillaTarget;
            }

            float attractivenessFactor = math.clamp(attractiveness / 100f, 0f, 4f);

            float scaled = population / 1000f
                           * settings.TouristsPerThousandCitizens
                           * attractivenessFactor;

            return math.clamp(
                (int)math.round(math.max(vanillaTarget, scaled)),
                0,
                math.max(vanillaTarget, settings.MaximumTourists));
        }

        private static int ComputeTarget(
            TourismOverhaulSetting settings, int attractiveness, int population, int hotelRooms)
        {
            int vanillaTarget = TourismSystem.GetTargetTourists(math.max(0, attractiveness));

            if (!settings.FixTouristDemand)
            {
                return vanillaTarget;
            }

            // Attractiveness is capped near 100 by TourismSystem's logistic curve, but city
            // modifiers can push it higher; allow that to matter without letting it run away.
            float attractivenessFactor = math.clamp(attractiveness / 100f, 0f, 4f);

            float scaled = population / 1000f
                           * settings.TouristsPerThousandCitizens
                           * attractivenessFactor;

            // Rooms built are themselves a draw. A hotel earns per guest but pays a full payroll
            // regardless, so a new hotel opening into a city with no spare demand is insolvent from
            // day one. Letting the target rise with capacity means opening a hotel brings visitors
            // rather than merely dividing the existing ones — which is the difference between a
            // welcome boost and taking guests from the hotel down the road.
            //
            // Still gated by attractiveness, so rooms alone cannot conjure tourists into an
            // unattractive city.
            float roomDriven = hotelRooms
                               * (math.clamp(settings.HotelRoomDemandOccupancy, 0, 100) / 100f)
                               * attractivenessFactor;

            int target = (int)math.round(math.max(vanillaTarget, math.max(scaled, roomDriven)));

            target = math.clamp(target, 0, math.max(vanillaTarget, settings.MaximumTourists));

            // Never ask for more tourists than there are beds.
            //
            // A tourist who cannot book a room is evicted the same evening as TouristNoHotel, so
            // targeting beyond hotel capacity does not raise the tourist count — it just runs the
            // spawner flat out, burning pathfinding on arrivals that leave immediately. Capacity is
            // the real ceiling, so aim at it rather than past it.
            if (hotelRooms > 0)
            {
                target = math.min(target, hotelRooms);
            }
            else if (target == 0)
            {
                // With no hotel rooms yet, the first zoned motel or hotel can never become
                // viable if demand remains at zero. Force one visitor so the lodging demand
                // machinery can kick in and the first_zone_build_exception can work.
                target = 1;
            }

            return target;
        }

        /// <summary>
        /// Chooses which household template an arriving group uses.
        ///
        /// Each template has fixed occupant counts, and both the native tourist spawner and this
        /// one would otherwise pick uniformly at random — the game does not even use the m_Weight
        /// field the templates carry. Most templates are small, so arrivals average well under two
        /// people and tourist numbers grow slowly for the entity cost.
        ///
        /// Biasing the choice toward larger groups raises tourist head count without creating more
        /// households, which is cheaper: one four-person family costs a single household entity,
        /// one pathfinding request and one hotel room, where four solo travellers cost four of
        /// each.
        ///
        /// Uses tournament selection: sample a few templates and keep the largest. At a preference
        /// of 0 that samples one, which is the vanilla uniform pick.
        /// </summary>
        private static int SelectHouseholdPrefab(
            NativeArray<HouseholdData> householdDatas, ref Random random, TourismOverhaulSetting settings)
        {
            int count = householdDatas.Length;

            if (count == 0)
            {
                return 0;
            }

            int samples = 1 + (int)math.round(math.clamp(settings.PreferLargerTouristGroups, 0, 100) / 25f);

            int best = random.NextInt(count);
            int bestSize = CountOccupants(householdDatas[best]);

            for (int i = 1; i < samples; i++)
            {
                int candidate = random.NextInt(count);
                int size = CountOccupants(householdDatas[candidate]);

                if (size > bestSize)
                {
                    best = candidate;
                    bestSize = size;
                }
            }

            return best;
        }

        /// <summary>
        /// Banks the month's arrival counts when the in-game calendar turns over.
        ///
        /// Derived from normalizedDate, the position through the year, rather than from
        /// GetCurrentDateTime().Month. That property cannot be used: the date is built as
        ///
        ///     day = 1 + floor(daysPerYear * normalizedDate) % daysPerYear     (TimeSystem:183)
        ///     CreateDateTime(year, day, ...) => DateTime(0).AddDays(day - 1)  (TimeSystem:165)
        ///
        /// and TimeSettingsPrefab ships m_DaysPerYear = 12, so day never exceeds 12 and the
        /// resulting DateTime never leaves January. Month is permanently 1, so a rollover keyed on
        /// it never fires and the counters accumulate for the whole session.
        ///
        /// Twelve months to a year is a calendar fact rather than a game setting, so scaling
        /// normalizedDate by 12 gives the displayed month whatever daysPerYear is set to. With the
        /// shipped value of 12 that also means one in-game day is one displayed month.
        /// </summary>
        private void RollOverMonthIfNeeded()
        {
            if (m_TimeSystem == null)
            {
                return;
            }

            int month = (int)math.floor(math.saturate(m_TimeSystem.normalizedDate) * 12f) % 12;

            if (month == m_CurrentMonth)
            {
                return;
            }

            m_ThisMonthArrivals = default;
            m_CurrentMonth = month;
        }

        /// <summary>
        /// Maintains arrivals as a smoothed per-month rate rather than a running total.
        ///
        /// A calendar bucket was the wrong shape for this row. With m_DaysPerYear = 12 a displayed
        /// month is one in-game day, which is over an hour of real play at normal speed, so the
        /// figure climbed for that whole hour before it meant anything — and reset to zero on
        /// reload, because counters are not saved. A visibly climbing number invites the reading
        /// that arrivals are running away, when it is simply a total that has not finished.
        ///
        /// Measuring elapsed months from normalizedDate rather than from a frame count keeps this
        /// correct at any speed and for any m_DaysPerYear, since twelve months to a year is a
        /// calendar fact rather than a game setting.
        /// </summary>
        private void UpdateArrivalRate()
        {
            if (m_TimeSystem == null)
            {
                return;
            }

            float date = math.saturate(m_TimeSystem.normalizedDate);

            if (!m_HasLastDate)
            {
                m_LastNormalizedDate = date;
                m_HasLastDate = true;
                return;
            }

            float elapsed = date - m_LastNormalizedDate;

            // Year wrap, or the clock moved backwards after a load.
            if (elapsed < 0f)
            {
                elapsed += 1f;
            }

            float months = elapsed * 12f;

            // Too small an interval makes the division explode; wait for the next sample.
            if (months < 0.0001f)
            {
                return;
            }

            m_LastNormalizedDate = date;

            float4 sample = new float4(
                m_ArrivalsSinceSample.x, m_ArrivalsSinceSample.y,
                m_ArrivalsSinceSample.z, m_ArrivalsSinceSample.w) / months;

            m_ArrivalsSinceSample = default;

            // Seed on the first real sample so the row is meaningful immediately rather than
            // easing up from zero.
            m_ArrivalRate = math.all(m_ArrivalRate == 0f)
                ? sample
                : math.lerp(m_ArrivalRate, sample, 0.05f);
        }

        /// <summary>
        /// Connection type to panel row index. The most specific mode wins, so a combined road and
        /// rail terminal counts as rail.
        /// </summary>
        private static byte ModeIndex(OutsideConnectionTransferType type)
        {
            if ((type & OutsideConnectionTransferType.Air) != 0) return 2;
            if ((type & OutsideConnectionTransferType.Ship) != 0) return 3;
            if ((type & OutsideConnectionTransferType.Train) != 0) return 1;
            return 0;
        }

        /// <summary>
        /// Counts households that have now been given citizens, then forgets their arrival mode.
        ///
        /// This is what makes the arrivals figure mean "visitors who arrived" rather than "attempts
        /// that were made". A household that is dispatched and then discarded before initialisation
        /// never reaches this, so it is never counted — which is the whole point, since that was the
        /// bulk of the old figure.
        ///
        /// Counted in citizens actually present, not the template's expected head count, so the
        /// number matches the people standing in the city.
        /// </summary>
        private void CountConvertedArrivals()
        {
            if (m_ArrivedQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();

            EntityTypeHandle entityHandle = GetEntityTypeHandle();
            ComponentTypeHandle<Components.ArrivalMode> modeHandle =
                GetComponentTypeHandle<Components.ArrivalMode>(isReadOnly: true);
            BufferTypeHandle<HouseholdCitizen> citizenHandle =
                GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks = m_ArrivedQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    if (!chunk.Has(ref citizenHandle))
                    {
                        continue;
                    }

                    NativeArray<Entity> entities = chunk.GetNativeArray(entityHandle);
                    NativeArray<Components.ArrivalMode> modes = chunk.GetNativeArray(ref modeHandle);
                    BufferAccessor<HouseholdCitizen> citizens = chunk.GetBufferAccessor(ref citizenHandle);

                    for (int i = 0; i < entities.Length; i++)
                    {
                        int occupants = citizens[i].Length;

                        // Not initialised yet; leave the mode on it and try again next update.
                        if (occupants == 0)
                        {
                            continue;
                        }

                        RecordArrival(modes[i].m_Mode, occupants);
                        commandBuffer.RemoveComponent<Components.ArrivalMode>(entities[i]);
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        /// <summary>
        /// Attributes an arrival to the connection type it came through.
        ///
        /// OutsideConnectionTransferType is a flags enum and a connection may carry more than one,
        /// so the most specific mode wins: a combined road and rail terminal counts as rail.
        /// </summary>
        private void RecordArrival(byte mode, int citizens)
        {
            switch (mode)
            {
                case 1:
                    m_ThisMonthArrivals.y += citizens;
                    m_ArrivalsSinceSample.y += citizens;
                    break;
                case 2:
                    m_ThisMonthArrivals.z += citizens;
                    m_ArrivalsSinceSample.z += citizens;
                    break;
                case 3:
                    m_ThisMonthArrivals.w += citizens;
                    m_ArrivalsSinceSample.w += citizens;
                    break;
                default:
                    m_ThisMonthArrivals.x += citizens;
                    m_ArrivalsSinceSample.x += citizens;
                    break;
            }
        }

        /// <summary>
        /// How many people a household template will actually produce, on average.
        ///
        /// Not the same as CountOccupants, which sums the template's fields and is fine for
        /// comparing one template against another. HouseholdInitializeSystem does not spawn the
        /// child count — it spawns a random number below it (:173):
        ///
        ///     int num2 = random.NextInt(householdData.m_ChildCount);
        ///     for (int l = 0; l &lt; num2; l++) SpawnCitizen(...);
        ///
        /// while students, adults and elders are spawned exactly. Counting arrivals with the full
        /// child figure therefore over-reports every group, which is a large part of why arrivals
        /// looked so far out of step with the tourist population.
        /// </summary>
        private static int ExpectedOccupants(HouseholdData data)
        {
            int expectedChildren = math.max(0, data.m_ChildCount - 1) / 2;

            return data.m_StudentCount + data.m_AdultCount + data.m_ElderCount + expectedChildren;
        }

        private static int CountOccupants(HouseholdData data)
        {
            return data.m_ChildCount + data.m_AdultCount + data.m_ElderCount + data.m_StudentCount;
        }

        /// <summary>Total hotel rooms citywide, occupied plus free.</summary>
        private int CountHotelRooms()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null || settings.HotelRoomDemandOccupancy <= 0)
            {
                return 0;
            }

            int rooms = 0;

            ComponentTypeHandle<LodgingProvider> providerHandle =
                GetComponentTypeHandle<LodgingProvider>(isReadOnly: true);
            BufferTypeHandle<Renter> renterHandle = GetBufferTypeHandle<Renter>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks = m_HotelQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    NativeArray<LodgingProvider> providers = chunk.GetNativeArray(ref providerHandle);
                    BufferAccessor<Renter> renters = chunk.GetBufferAccessor(ref renterHandle);

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        rooms += math.max(0, providers[i].m_FreeRooms) + renters[i].Length;
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            return rooms;
        }

        /// <summary>
        /// Sum of citizens across live tourist households. Unlike
        /// CountHouseholdDataSystem (Finding 5) this does not require a Target component, so
        /// tourists in transit between destinations are counted.
        /// </summary>
        private int CountTouristCitizens()
        {
            int count = 0;

            BufferTypeHandle<HouseholdCitizen> citizenHandle =
                GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks =
                m_TouristHouseholdQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < chunks.Length; i++)
                {
                    ArchetypeChunk chunk = chunks[i];
                    BufferAccessor<HouseholdCitizen> citizens = chunk.GetBufferAccessor(ref citizenHandle);

                    for (int j = 0; j < chunk.Count; j++)
                    {
                        count += citizens[j].Length;
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            return count;
        }

        /// <summary>
        /// Mirrors TouristSpawnSystem's creation sequence, with the outside connection resolved
        /// before the entity is created so a failed lookup costs nothing.
        /// </summary>
        private void SpawnTouristHouseholds(int count, TourismOverhaulSetting settings)
        {
            NativeArray<Entity> prefabEntities = m_HouseholdPrefabQuery.ToEntityArray(Allocator.Temp);
            NativeArray<ArchetypeData> archetypes =
                m_HouseholdPrefabQuery.ToComponentDataArray<ArchetypeData>(Allocator.Temp);
            NativeArray<HouseholdData> householdDatas =
                m_HouseholdPrefabQuery.ToComponentDataArray<HouseholdData>(Allocator.Temp);
            NativeArray<Entity> connectionArray = m_OutsideConnectionQuery.ToEntityArray(Allocator.Temp);
            NativeList<Entity> connections = new NativeList<Entity>(connectionArray.Length, Allocator.Temp);

            try
            {
                if (prefabEntities.Length == 0 || connectionArray.Length == 0)
                {
                    return;
                }

                for (int i = 0; i < connectionArray.Length; i++)
                {
                    connections.Add(connectionArray[i]);
                }

                DemandParameterData demandParameters = m_DemandParameterQuery.GetSingleton<DemandParameterData>();

                ComponentLookup<OutsideConnectionData> outsideConnectionDatas =
                    GetComponentLookup<OutsideConnectionData>(isReadOnly: true);
                ComponentLookup<PrefabRef> prefabRefs = GetComponentLookup<PrefabRef>(isReadOnly: true);

                Random random = new Random(math.max(1u, m_SimulationSystem.frameIndex * 747796405u + 2891336453u));

                EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();

                for (int n = 0; n < count; n++)
                {
                    SpawnAttempts++;

                    // Resolve the arrival point first. If the roll lands on a transfer type the
                    // city does not have, skip rather than creating a household that can never
                    // be initialised.
                    if (!BuildingUtils.GetRandomOutsideConnectionByParameters(
                            ref connections,
                            ref outsideConnectionDatas,
                            ref prefabRefs,
                            random,
                            demandParameters.m_TouristOCSpawnParameters,
                            out Entity connection))
                    {
                        continue;
                    }

                    SpawnSuccesses++;

                    int index = SelectHouseholdPrefab(householdDatas, ref random, settings);

                    // Attribute the arrival here: the connection is already resolved and the
                    // household template fixes the head count, so nothing later has to be traced
                    // back to the door it came through.
                    OutsideConnectionTransferType arrivalType = OutsideConnectionTransferType.None;

                    if (prefabRefs.HasComponent(connection))
                    {
                        Entity connectionPrefab = prefabRefs[connection].m_Prefab;

                        if (outsideConnectionDatas.HasComponent(connectionPrefab))
                        {
                            arrivalType = outsideConnectionDatas[connectionPrefab].m_Type;
                        }
                    }

                    // Arrivals stand at the outside connection, as the base game intends. Placing
                    // them at a terminal instead was tried and reverted: it did not reduce
                    // TouristNoTarget evictions, because the origin search uses a zero radius and
                    // so fails wherever the household is unless it lands exactly on a lane. It also
                    // meant no inbound plane or ship was ever generated — departures used the
                    // airport while arrivals appeared out of thin air — and the churn it left
                    // behind cost simulation performance.

                    Entity household = commandBuffer.CreateEntity(archetypes[index].m_Archetype);

                    commandBuffer.SetComponent(household, new PrefabRef { m_Prefab = prefabEntities[index] });
                    commandBuffer.SetComponent(household, new Household { m_Flags = HouseholdFlags.Tourist });
                    commandBuffer.AddComponent(household, new TouristHousehold
                    {
                        m_Hotel = Entity.Null,
                        m_LeavingTime = 0u
                    });
                    commandBuffer.AddComponent(household, new CurrentBuilding
                    {
                        m_CurrentBuilding = connection
                    });

                    // Counted later, when the household is actually given citizens. See ArrivalMode.
                    commandBuffer.AddComponent(household, new Components.ArrivalMode
                    {
                        m_Mode = ModeIndex(arrivalType)
                    });
                }
            }
            finally
            {
                connections.Dispose();
                connectionArray.Dispose();
                householdDatas.Dispose();
                archetypes.Dispose();
                prefabEntities.Dispose();
            }
        }
    }
}
