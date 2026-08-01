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

        // 262144 frames per in-game day; 8192 gives 32 snapshots per day, which is a few minutes
        // of real time at normal speed. A full day between snapshots is far too coarse to debug
        // with — you would play for over an hour before seeing a single line.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 8192;

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

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;
            if (settings == null || !settings.DiagnosticLogging)
            {
                m_Announced = false;
                return;
            }

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
