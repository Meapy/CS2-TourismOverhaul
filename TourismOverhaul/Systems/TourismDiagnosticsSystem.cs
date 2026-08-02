using Game;
using Game.Agents;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Companies;
using Game.Prefabs;
using Game.Tools;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Entities;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Diagnostic snapshot, off by default.
    ///
    /// Tourist population is the product of arrival rate and how long visitors survive, and there
    /// are five independent ways a tourist can be removed. Reasoning about which one is binding
    /// from the outside is guesswork, so this reads it directly: every tourist household that is
    /// on its way out carries MovingAway.m_Reason, which names the cause.
    ///
    /// It also counts households with no citizens. That is the signature of the vanilla arrival
    /// bug — a household created without CurrentBuilding never matches
    /// HouseholdInitializeSystem's query and so never receives anyone. If that number is large,
    /// arrivals are being destroyed before they ever become tourists.
    /// </summary>
    public partial class TourismDiagnosticsSystem : GameSystemBase
    {
        private EntityQuery m_TouristHouseholdQuery;
        private EntityQuery m_LeavingQuery;
        private EntityQuery m_WaitingQuery;
        private EntityQuery m_HotelQuery;
        private EntityQuery m_CityQuery;
        private EntityQuery m_DemandParameterQuery;

        private TouristDemandSystem m_DemandSystem;
        private TouristEconomySystem m_EconomySystem;
        private TouristRoutingSystem m_RoutingSystem;
        private HotelZoneSystem m_HotelZoneSystem;

        private int m_LastSpawnAttempts;
        private int m_LastSpawnSuccesses;
        private bool m_Announced;

        /// <summary>
        /// Households already counted as leaving, so each departure is counted once.
        ///
        /// The old instantaneous count of MovingAway households was misleading: it showed roughly
        /// 30 at any moment, which reads as a trickle, when in fact thousands pass through that
        /// state between snapshots. Counting the transition instead of the population is the only
        /// way to compare departures against arrivals honestly.
        /// </summary>
        private NativeHashSet<Entity> m_CountedMovers;

        // Departures this month, by reason: households, and the citizens they carried.
        private int4 m_LeftHouseholds;
        private int4 m_LeftCitizens;

        /// <summary>
        /// Households that began leaving while still holding no citizens.
        ///
        /// These never became tourists at all. A large number here means arrivals are failing
        /// before HouseholdInitializeSystem ever reaches them, which is a different fault entirely
        /// from visitors who arrive and then leave too soon.
        /// </summary>
        private int m_LeftNeverInitialised;

        // Tourist money, sampled between snapshots so spending can be inferred from the balance.
        private long m_LastWalletTotal;
        private bool m_HasWalletSample;

        private int m_TrackedMonth = -1;
        private int m_UpdatesSinceSnapshot;

        // Runs often, logs rarely. Departure tracking has to sample faster than a household passes
        // through MovingAway or transitions are missed; the expensive census still runs every 32nd
        // call, preserving the original one-snapshot-per-8192-frames cadence.
        private const int kUpdatesPerSnapshot = 32;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 256;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_DemandSystem = World.GetOrCreateSystemManaged<TouristDemandSystem>();
            m_EconomySystem = World.GetOrCreateSystemManaged<TouristEconomySystem>();
            m_RoutingSystem = World.GetOrCreateSystemManaged<TouristRoutingSystem>();
            m_HotelZoneSystem = World.GetOrCreateSystemManaged<HotelZoneSystem>();

            m_TouristHouseholdQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_LeavingQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.ReadOnly<MovingAway>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            // Arrived somewhere but not yet given a destination.
            m_WaitingQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.ReadOnly<CurrentBuilding>(),
                ComponentType.Exclude<Target>(),
                ComponentType.Exclude<MovingAway>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_HotelQuery = GetEntityQuery(
                ComponentType.ReadOnly<LodgingProvider>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_CountedMovers = new NativeHashSet<Entity>(4096, Allocator.Persistent);

            m_CityQuery = GetEntityQuery(ComponentType.ReadOnly<Tourism>());
            m_DemandParameterQuery = GetEntityQuery(ComponentType.ReadOnly<DemandParameterData>());
        }

        /// <summary>
        /// Reports the inequality CommercialDemandSystem uses to decide whether the city wants more
        /// hotels: currentTourists * m_HotelRoomPercentRequirement > total rooms (:187).
        ///
        /// Worth logging explicitly because when it fails there is no other symptom — hotels simply
        /// never spawn, and a hotel zone looks broken when the zone is fine and the city just does
        /// not want any more rooms.
        /// </summary>
        /// <summary>
        /// Arrivals against departures for the current month, both counted in citizens.
        ///
        /// These are the two halves of the same balance and should be read together. If departures
        /// roughly match arrivals, visitors are arriving and leaving too quickly and the length of
        /// stay is the fault. If departures fall far short, arrivals are being lost some other way
        /// and the stay timer is irrelevant.
        ///
        /// "never held anyone" is the tiebreaker: those households reached the leaving state
        /// without ever being given citizens, so they were never visitors in the first place.
        /// </summary>
        private string DescribeCohort()
        {
            if (m_DemandSystem == null)
            {
                return "  cohort: demand system unavailable";
            }

            int4 arrivals = m_DemandSystem.ThisMonthArrivals;
            int arrived = arrivals.x + arrivals.y + arrivals.z + arrivals.w;
            int left = m_LeftCitizens.x + m_LeftCitizens.y + m_LeftCitizens.z + m_LeftCitizens.w;
            int leftHouseholds =
                m_LeftHouseholds.x + m_LeftHouseholds.y + m_LeftHouseholds.z + m_LeftHouseholds.w;

            return
                $"  this month: {arrived} cims arrived, {left} left " +
                $"({leftHouseholds} households, {m_LeftNeverInitialised} of them never held anyone)\n" +
                $"  departures by reason (cims): NoTarget {m_LeftCitizens.x}, " +
                $"NoHotel {m_LeftCitizens.y}, NoMoney {m_LeftCitizens.z}, other {m_LeftCitizens.w}\n" +
                $"  unaccounted: {arrived - left} cims arrived but never recorded as leaving";
        }

        /// <summary>
        /// Follows the money tourists are carrying, which is the only way to see what they spend
        /// without instrumenting every till in the city.
        ///
        /// Every visitor is created holding a wallet, so money enters the economy on arrival and
        /// leaves with them if unspent. Between two snapshots:
        ///
        ///     spent = brought in by arrivals - change in the balance - taken away by departures
        ///
        /// Departures are the term this cannot see directly, so the figure is an upper bound on
        /// spending rather than an exact total. It is still the number that matters: if arrivals
        /// bring in far more than the city ever absorbs, the wallet is too large, and if the
        /// balance climbs steadily then visitors are hoarding rather than spending.
        ///
        /// Lodging is charged nightly by the hotel system and shopping is settled by the native
        /// buyer path, so the split between them has to come from watching hotel and shop income
        /// rather than from here.
        /// </summary>
        private string DescribeSpending()
        {
            long held = 0;

            BufferTypeHandle<Game.Economy.Resources> resourceHandle =
                GetBufferTypeHandle<Game.Economy.Resources>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks =
                m_TouristHouseholdQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    if (!chunk.Has(ref resourceHandle))
                    {
                        continue;
                    }

                    BufferAccessor<Game.Economy.Resources> resources =
                        chunk.GetBufferAccessor(ref resourceHandle);

                    for (int i = 0; i < resources.Length; i++)
                    {
                        held += Game.Economy.EconomyUtils.GetResources(
                            Game.Economy.Resource.Money, resources[i]);
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            long change = m_HasWalletSample ? held - m_LastWalletTotal : 0;

            m_LastWalletTotal = held;
            m_HasWalletSample = true;

            int budget = m_EconomySystem != null ? m_EconomySystem.TouristBudget : 0;
            int tourists = m_DemandSystem != null ? m_DemandSystem.CurrentTourists : 0;
            long perHead = tourists > 0 ? held / tourists : 0;

            return
                $"  tourist money: {held} held citywide, {change:+#;-#;0} since last snapshot, " +
                $"{perHead} per visitor against a {budget} starting wallet";
        }

        /// <summary>
        /// The spending split, with the raw signal counts behind it.
        ///
        /// The counts matter more than the money when a category reads zero: a category with no
        /// signals is not being detected, while one with signals but no money is being detected and
        /// then losing the spend to something else.
        /// </summary>
        private string DescribeLedger()
        {
            TouristSpendingLedgerSystem ledger =
                World.GetOrCreateSystemManaged<TouristSpendingLedgerSystem>();

            if (ledger == null)
            {
                return "  spending: ledger unavailable";
            }

            return
                $"  spending: hotels {ledger.Lodging}, shops {ledger.Goods}, fares {ledger.Fares}, " +
                $"leisure {ledger.Leisure}, unattributed {ledger.Other}\n" +
                $"  signals: {ledger.GoodsSignalsSeen} purchase(s) seen, " +
                $"{ledger.RidesCharged} ride(s) charged";
        }

        private string DescribeLodgingDemand()
        {
            if (m_CityQuery.IsEmptyIgnoreFilter || m_DemandParameterQuery.IsEmptyIgnoreFilter)
            {
                return "  lodging demand: city not loaded yet";
            }

            int2 lodging = m_CityQuery.GetSingleton<Tourism>().m_Lodging;
            float requirement = m_DemandParameterQuery.GetSingleton<DemandParameterData>()
                .m_HotelRoomPercentRequirement;

            int wanted = (int)(m_DemandSystem.CurrentTourists * requirement);
            bool wantsMore = wanted - lodging.y > 0;

            return
                $"  lodging: {lodging.x} rooms occupied of {lodging.y} total; city wants {wanted} " +
                $"({requirement:0.00}/tourist) -> hotels will spawn: {(wantsMore ? "YES" : "NO")}";
        }

        protected override void OnDestroy()
        {
            if (m_CountedMovers.IsCreated)
            {
                m_CountedMovers.Dispose();
            }

            base.OnDestroy();
        }

        /// <summary>
        /// Counts each household once, on the update it first appears as leaving.
        ///
        /// Also records whether it ever held anyone. A household that starts moving away with an
        /// empty citizen buffer never became a tourist, which separates "arrivals are failing" from
        /// "visitors are leaving too soon" — the two produce identical symptoms in every other
        /// figure this system reports.
        /// </summary>
        private void TrackDepartures()
        {
            EntityTypeHandle entityHandle = GetEntityTypeHandle();
            ComponentTypeHandle<MovingAway> movingAwayHandle =
                GetComponentTypeHandle<MovingAway>(isReadOnly: true);
            BufferTypeHandle<HouseholdCitizen> citizenHandle =
                GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks = m_LeavingQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityHandle);
                    NativeArray<MovingAway> leaving = chunk.GetNativeArray(ref movingAwayHandle);

                    bool hasCitizens = chunk.Has(ref citizenHandle);
                    BufferAccessor<HouseholdCitizen> citizens =
                        hasCitizens ? chunk.GetBufferAccessor(ref citizenHandle) : default;

                    for (int i = 0; i < entities.Length; i++)
                    {
                        if (!m_CountedMovers.Add(entities[i]))
                        {
                            continue;
                        }

                        int occupants = hasCitizens ? citizens[i].Length : 0;

                        if (occupants == 0)
                        {
                            m_LeftNeverInitialised++;
                        }

                        switch (leaving[i].m_Reason)
                        {
                            case MoveAwayReason.TouristNoTarget:
                                m_LeftHouseholds.x++;
                                m_LeftCitizens.x += occupants;
                                break;
                            case MoveAwayReason.TouristNoHotel:
                                m_LeftHouseholds.y++;
                                m_LeftCitizens.y += occupants;
                                break;
                            case MoveAwayReason.TouristNoMoney:
                                m_LeftHouseholds.z++;
                                m_LeftCitizens.z += occupants;
                                break;
                            default:
                                m_LeftHouseholds.w++;
                                m_LeftCitizens.w += occupants;
                                break;
                        }
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;
            if (settings == null || !settings.DiagnosticLogging)
            {
                m_Announced = false;
                return;
            }

            TrackDepartures();

            // Reset on the same month boundary the arrival counters use, so the two are directly
            // comparable rather than covering different windows.
            int month = m_DemandSystem != null ? m_DemandSystem.MonthIndex : -1;
            if (month != m_TrackedMonth)
            {
                m_TrackedMonth = month;
                m_LeftHouseholds = default;
                m_LeftCitizens = default;
                m_LeftNeverInitialised = 0;
                m_CountedMovers.Clear();
            }

            if (++m_UpdatesSinceSnapshot < kUpdatesPerSnapshot)
            {
                return;
            }

            m_UpdatesSinceSnapshot = 0;

            if (!m_Announced)
            {
                m_Announced = true;
                Mod.Log.Info("Diagnostic logging enabled — snapshot every 8192 frames (32 per in-game day).");
            }

            CountHouseholds(out int households, out int withCitizens, out int citizens);
            CountLeaveReasons(out int noTarget, out int noHotel, out int noMoney, out int other);
            CountEmptyByCause(out int emptyDynamic, out int emptyOther, out int emptyNoBuilding);

            int attempts = m_DemandSystem.SpawnAttempts - m_LastSpawnAttempts;
            int successes = m_DemandSystem.SpawnSuccesses - m_LastSpawnSuccesses;
            m_LastSpawnAttempts = m_DemandSystem.SpawnAttempts;
            m_LastSpawnSuccesses = m_DemandSystem.SpawnSuccesses;

            int waiting = m_WaitingQuery.CalculateEntityCount();
            int freeRooms = CountFreeRooms();

            Mod.Log.Info(
                "--- tourism diagnostics ---\n" +
                $"  tourists {m_DemandSystem.CurrentTourists} / target {m_DemandSystem.TargetTourists}\n" +
                $"  households {households}, of which {withCitizens} have citizens " +
                $"({households - withCitizens} EMPTY), {citizens} citizens total\n" +
                $"  spawned by mod since last snapshot: {successes}/{attempts} " +
                $"({attempts - successes} lost to no matching outside connection)\n" +
                $"  leaving now: TouristNoTarget {noTarget}, TouristNoHotel {noHotel}, " +
                $"TouristNoMoney {noMoney}, other {other}\n" +
                $"  waiting at a connection with no destination: {waiting}\n" +
                $"  free hotel rooms: {freeRooms}\n" +
                DescribeCohort() + "\n" +
                DescribeSpending() + "\n" +
                DescribeLedger() + "\n" +
                DescribeLodgingDemand() + "\n" +
                $"  hotel zones: {m_HotelZoneSystem.HotelBuildingsMoved} hotel and " +
                $"{m_HotelZoneSystem.MotelBuildingsMoved} motel building prefabs assigned\n" +
                $"  EMPTY breakdown: {emptyDynamic} dynamic-prefab, {emptyOther} ordinary-prefab, " +
                $"{emptyNoBuilding} with NO CurrentBuilding\n" +
                $"  native spawner disabled: {m_DemandSystem.NativeSpawnerDisabled}, " +
                $"leaked cleaned up so far: {m_DemandSystem.LeakedHouseholdsRemoved}\n" +
                $"  nightly hotel price {m_EconomySystem.NightlyHotelPrice}, " +
                $"tourist budget {m_EconomySystem.TouristBudget}\n" +
                $"  arrival backlog per connection (road/train/air/ship): " +
                $"{m_RoutingSystem.BacklogPerConnection.x:0.0}/" +
                $"{m_RoutingSystem.BacklogPerConnection.y:0.0}/" +
                $"{m_RoutingSystem.BacklogPerConnection.z:0.0}/" +
                $"{m_RoutingSystem.BacklogPerConnection.w:0.0}");
        }

        /// <summary>
        /// Splits empty households by whether their prefab carries DynamicHousehold.
        ///
        /// Households on dynamic prefabs are the known leak (Finding 7). Empty households on
        /// ordinary prefabs are something else entirely — they should have been initialised, so if
        /// this number is large the fault is in how they are created, not in prefab selection.
        /// </summary>
        private void CountEmptyByCause(out int dynamicPrefab, out int otherPrefab, out int noCurrentBuilding)
        {
            dynamicPrefab = 0;
            otherPrefab = 0;
            noCurrentBuilding = 0;

            EntityTypeHandle entityHandle = GetEntityTypeHandle();
            ComponentTypeHandle<PrefabRef> prefabHandle = GetComponentTypeHandle<PrefabRef>(isReadOnly: true);
            BufferTypeHandle<HouseholdCitizen> citizenHandle =
                GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);
            ComponentTypeHandle<CurrentBuilding> buildingHandle =
                GetComponentTypeHandle<CurrentBuilding>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks =
                m_TouristHouseholdQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    bool hasCitizens = chunk.Has(ref citizenHandle);
                    bool hasBuilding = chunk.Has(ref buildingHandle);

                    if (!chunk.Has(ref prefabHandle))
                    {
                        continue;
                    }

                    NativeArray<PrefabRef> prefabs = chunk.GetNativeArray(ref prefabHandle);
                    BufferAccessor<HouseholdCitizen> citizens =
                        hasCitizens ? chunk.GetBufferAccessor(ref citizenHandle) : default;

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (hasCitizens && citizens[i].Length > 0)
                        {
                            continue;
                        }

                        if (!hasBuilding)
                        {
                            // Cannot ever be initialised — the initialise query requires it.
                            noCurrentBuilding++;
                        }
                        else if (EntityManager.HasComponent<DynamicHousehold>(prefabs[i].m_Prefab))
                        {
                            dynamicPrefab++;
                        }
                        else
                        {
                            otherPrefab++;
                        }
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        private void CountHouseholds(out int households, out int withCitizens, out int citizens)
        {
            households = 0;
            withCitizens = 0;
            citizens = 0;

            BufferTypeHandle<HouseholdCitizen> citizenHandle =
                GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks =
                m_TouristHouseholdQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    households += chunk.Count;

                    if (!chunk.Has(ref citizenHandle))
                    {
                        continue;
                    }

                    BufferAccessor<HouseholdCitizen> buffers = chunk.GetBufferAccessor(ref citizenHandle);

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (buffers[i].Length > 0)
                        {
                            withCitizens++;
                            citizens += buffers[i].Length;
                        }
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        private void CountLeaveReasons(out int noTarget, out int noHotel, out int noMoney, out int other)
        {
            noTarget = 0;
            noHotel = 0;
            noMoney = 0;
            other = 0;

            ComponentTypeHandle<MovingAway> movingAwayHandle =
                GetComponentTypeHandle<MovingAway>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks = m_LeavingQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    NativeArray<MovingAway> leaving = chunks[c].GetNativeArray(ref movingAwayHandle);

                    for (int i = 0; i < leaving.Length; i++)
                    {
                        switch (leaving[i].m_Reason)
                        {
                            case MoveAwayReason.TouristNoTarget: noTarget++; break;
                            case MoveAwayReason.TouristNoHotel: noHotel++; break;
                            case MoveAwayReason.TouristNoMoney: noMoney++; break;
                            default: other++; break;
                        }
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        private int CountFreeRooms()
        {
            int free = 0;

            ComponentTypeHandle<LodgingProvider> providerHandle =
                GetComponentTypeHandle<LodgingProvider>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks = m_HotelQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    NativeArray<LodgingProvider> providers = chunks[c].GetNativeArray(ref providerHandle);

                    for (int i = 0; i < providers.Length; i++)
                    {
                        if (providers[i].m_FreeRooms > 0)
                        {
                            free += providers[i].m_FreeRooms;
                        }
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            return free;
        }
    }
}
