using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Hotel room capacity multiplier.
    ///
    /// Vanilla room count is computed inside a Burst job as
    ///     LodgingProviderSystem.GetRoomCount(lotSize, level, propertyData)
    ///         => (int)(lotSize.x * lotSize.y * level * m_SpaceMultiplier)   (:264-267)
    ///
    /// None of those three inputs can be scaled in isolation:
    ///   - m_SpaceMultiplier also drives rent asked (PropertyUtils.cs:399) and workplace
    ///     capacity (BuildingUtils.cs:834), so raising it inflates rent and jobs everywhere;
    ///   - lotSize is building geometry;
    ///   - level is the visual upgrade level.
    ///
    /// Overwriting LodgingProvider.m_FreeRooms after the fact does not work either, because the
    /// native job evicts guests back down to the vanilla count on its next pass:
    ///     if (roomCount &lt; renters.Length) { ...evict the overflow... }        (:124-137)
    ///
    /// So this system disables LodgingProviderSystem and mirrors it, applying the multiplier to
    /// the room count. Everything else — eviction of non-tourists, lodging consumption, market
    /// price, guest charges, company income, m_Price, m_FreeRooms, customer statistics and the
    /// city resource usage accumulator — is reproduced exactly, at the same update cadence and
    /// with the same UpdateFrame sharding.
    ///
    /// MAINTENANCE RISK: this is a copy of a native system. If Colossal changes
    /// LodgingProviderSystem, this will silently diverge. Re-check it against the decompiled
    /// source after a game update. Turning the setting off restores the native system at runtime.
    /// </summary>
    public partial class HotelCapacitySystem : GameSystemBase
    {
        /// <summary>Mirrors LodgingProviderSystem.kUpdatesPerDay (:241).</summary>
        private const int kUpdatesPerDay = 32;

        private EntityQuery m_ProviderQuery;
        private EntityQuery m_LeisureParameterQuery;
        private EntityQuery m_LodgingPrefabQuery;

        private int m_LastWrittenServiceMultiplier = -1;

        /// <summary>Service capacity per lodging prefab after scaling. For diagnostics.</summary>
        public int ScaledMaxService { get; private set; }

        private SimulationSystem m_SimulationSystem;
        private ResourceSystem m_ResourceSystem;
        private CityProductionStatisticSystem m_CityProductionStatisticSystem;
        private LodgingProviderSystem m_NativeLodgingProvider;

        private bool m_NativeDisabled;

        private HotelLookups m_Lookups;
        private BufferLookup<Renter> m_Renters;
        private ComponentLookup<TouristHousehold> m_TouristHouseholds;
        private ComponentLookup<TouristHousehold> m_TouristWrite;
        private BufferLookup<Game.Economy.Resources> m_Resources;
        private ComponentLookup<CompanyStatisticData> m_Statistics;

        /// <summary>Written by the last update's job: [0] charged to guests, [1] total rooms.</summary>
        private NativeArray<long> m_Results;
        private JobHandle m_LastJob;
        private bool m_ResultsPending;
        private bool m_RoomsPending;

        /// <summary>Total rooms across all managed hotels at the last update. For diagnostics.</summary>
        public int TotalRooms { get; private set; }

        // Identical to LodgingProviderSystem.GetUpdateInterval (:259-262): 262144 / (32 * 16) = 512.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 262144 / (kUpdatesPerDay * 16);

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_ResourceSystem = World.GetOrCreateSystemManaged<ResourceSystem>();
            m_CityProductionStatisticSystem = World.GetOrCreateSystemManaged<CityProductionStatisticSystem>();
            m_NativeLodgingProvider = World.GetOrCreateSystemManaged<LodgingProviderSystem>();

            m_Lookups = new HotelLookups
            {
                m_PropertyRenters = GetComponentLookup<PropertyRenter>(isReadOnly: true),
                m_PrefabRefs = GetComponentLookup<PrefabRef>(isReadOnly: true),
                m_BuildingData = GetComponentLookup<BuildingData>(isReadOnly: true),
                m_PropertyData = GetComponentLookup<BuildingPropertyData>(isReadOnly: true),
                m_SpawnableData = GetComponentLookup<SpawnableBuildingData>(isReadOnly: true),
                m_SignatureData = GetComponentLookup<SignatureBuildingData>(isReadOnly: true),
            };
            m_Renters = GetBufferLookup<Renter>(isReadOnly: true);
            m_TouristHouseholds = GetComponentLookup<TouristHousehold>(isReadOnly: true);
            m_TouristWrite = GetComponentLookup<TouristHousehold>(isReadOnly: false);
            m_Resources = GetBufferLookup<Game.Economy.Resources>(isReadOnly: false);
            m_Statistics = GetComponentLookup<CompanyStatisticData>(isReadOnly: false);
            m_Results = new NativeArray<long>(2, Allocator.Persistent);

            // Mirrors LodgingProviderSystem.m_ProviderQuery (:278).
            m_ProviderQuery = GetEntityQuery(
                ComponentType.ReadWrite<LodgingProvider>(),
                ComponentType.ReadWrite<PropertyRenter>(),
                ComponentType.ReadWrite<ServiceAvailable>(),
                ComponentType.ReadOnly<UpdateFrame>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<Game.Companies.ProcessingCompany>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_LeisureParameterQuery = GetEntityQuery(ComponentType.ReadOnly<LeisureParametersData>());

            // Lodging company templates, for scaling service capacity alongside room count.
            m_LodgingPrefabQuery = GetEntityQuery(
                ComponentType.ReadWrite<ServiceCompanyData>(),
                ComponentType.ReadOnly<IndustrialProcessData>());

            RequireForUpdate(m_ProviderQuery);
            RequireForUpdate(m_LeisureParameterQuery);
        }

        protected override void OnDestroy()
        {
            m_LastJob.Complete();
            m_Results.Dispose();
            RestoreNativeSystem();
            base.OnDestroy();
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // Prefab data is rebuilt from source on load, so forget the scaling we applied — the
            // value on disk is the authored one again.
            m_LastWrittenServiceMultiplier = -1;

            // And scale again now, before the first tick, rather than on the first update up to
            // 512 frames later: in between, a hotel's saved service stock sits over the authored
            // maximum, and ServiceCompanySystem flags "not enough customers" until the update.
            TourismOverhaulSetting settings = Mod.Settings;
            int multiplier = settings != null ? math.clamp(settings.HotelRoomMultiplier, 1, 10) : 1;
            if (mode == GameMode.Game && multiplier > 1)
            {
                ScaleServiceCapacity(multiplier);
            }
        }

        /// <summary>
        /// Both the mirrored update and the observation run as Burst jobs. What they measure is
        /// collected here on the next update, 512 frames later.
        /// </summary>
        protected override void OnUpdate()
        {
            m_LastJob.Complete();

            if (m_ResultsPending)
            {
                LodgingChargedSinceReset += m_Results[0];
                m_ResultsPending = false;
            }

            if (m_RoomsPending)
            {
                TotalRooms = (int)m_Results[1];
                m_RoomsPending = false;
            }

            m_Results[0] = 0;
            m_Results[1] = 0;

            TourismOverhaulSetting settings = Mod.Settings;

            int multiplier = settings != null ? math.clamp(settings.HotelRoomMultiplier, 1, 10) : 1;
            // The multiplier being above 1 is the enable condition; a separate toggle would only be
            // another way to say the same thing.
            bool active = settings != null && multiplier > 1;

            if (!active)
            {
                RestoreNativeSystem();

                // The native system is doing the billing. Watch it rather than replacing it, so
                // the finance view still has a lodging figure. See ScheduleObservation.
                ScheduleObservation();
                return;
            }

            DisableNativeSystem();
            ScaleServiceCapacity(multiplier);
            ScheduleLodgingUpdate(multiplier);
        }

        /// <summary>
        /// Money charged to guests for rooms since the last reset. Read and cleared by
        /// TouristSpendingLedgerSystem, which owns the reporting period.
        ///
        /// Filled by whichever path is live: the mirrored update when the room multiplier is
        /// raised, and the observation walk below when it is not.
        /// </summary>
        public long LodgingChargedSinceReset { get; set; }

        /// <summary>
        /// Set when the native system is handed back, so the tick that re-enables it is not
        /// observed.
        ///
        /// Whether LodgingProviderSystem runs before or after this system within a frame is fixed
        /// by system ordering, but on the tick we re-enable it there is no charge to attribute
        /// either way: if it sorts before us it has already been skipped this frame, and if it
        /// sorts after us we have just billed the same guests ourselves. Counting that tick would
        /// report money nobody paid.
        /// </summary>
        private bool m_SkipNextObservation;

        private void DisableNativeSystem()
        {
            if (m_NativeDisabled || m_NativeLodgingProvider == null)
            {
                return;
            }

            m_NativeLodgingProvider.Enabled = false;
            m_NativeDisabled = true;
            Mod.Log.Info("Native LodgingProviderSystem disabled; hotel capacity handled by TourismOverhaul.");
        }

        private void RestoreNativeSystem()
        {
            if (!m_NativeDisabled || m_NativeLodgingProvider == null)
            {
                return;
            }

            m_NativeLodgingProvider.Enabled = true;
            m_NativeDisabled = false;
            m_SkipNextObservation = true;
            Mod.Log.Info("Native LodgingProviderSystem restored.");
        }

        /// <summary>
        /// Scales lodging companies' service capacity with the room multiplier.
        ///
        /// Multiplying rooms without this leaves a hotel's ServiceCompanyData.m_MaxService at its
        /// authored value, so a much larger hotel fills its service stock and stops:
        ///
        ///     // EconomyUtils.GetCompanyProductionPerDay (:1483-1491)
        ///     float num5 = serviceAvailable.m_ServiceAvailable / serviceCompanyData.m_MaxService;
        ///     if (num5 &gt;= 0.8f)
        ///         num4 = (int)math.ceil(math.lerp(num4, 0f, math.saturate((num5 - 0.8f) / 0.2f)));
        ///
        /// At full stock that returns zero production, which both halts the hotel and makes
        /// CompanyEconomyStatisticSystem report an income of zero (:188-189) — a hotel with
        /// hundreds of paying guests showing no income at all.
        ///
        /// Capacity has to scale with the rooms it serves, so this multiplies m_MaxService to
        /// match. Written on the prefab, so it applies to every hotel of that type, and restored
        /// when the multiplier returns to 1.
        /// </summary>
        private void ScaleServiceCapacity(int multiplier)
        {
            if (multiplier == m_LastWrittenServiceMultiplier)
            {
                return;
            }

            NativeArray<Entity> prefabs = m_LodgingPrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    if (!EntityManager.HasComponent<IndustrialProcessData>(prefabs[i]))
                    {
                        continue;
                    }

                    if ((EntityManager.GetComponentData<IndustrialProcessData>(prefabs[i]).m_Output.m_Resource
                         & Resource.Lodging) == Resource.NoResource)
                    {
                        continue;
                    }

                    ServiceCompanyData service = EntityManager.GetComponentData<ServiceCompanyData>(prefabs[i]);

                    // Recover the authored value from whatever we last wrote, so repeated changes
                    // scale from the original rather than compounding.
                    int baseMaxService = m_LastWrittenServiceMultiplier > 0
                        ? service.m_MaxService / m_LastWrittenServiceMultiplier
                        : service.m_MaxService;

                    service.m_MaxService = math.max(1, baseMaxService * multiplier);
                    EntityManager.SetComponentData(prefabs[i], service);

                    ScaledMaxService = service.m_MaxService;
                }
            }
            finally
            {
                prefabs.Dispose();
            }

            m_LastWrittenServiceMultiplier = multiplier;

            Mod.Log.Info($"Hotel service capacity scaled x{multiplier} (now {ScaledMaxService} per hotel).");
        }

        /// <summary>
        /// Counts what the native system is charging guests, without charging them anything.
        ///
        /// The room multiplier defaults to 1, so on a default setup this system stands down and
        /// LodgingProviderSystem does the billing — and LodgingChargedSinceReset was never
        /// incremented, leaving hotel spending at 0$ in the finance view for every player who had
        /// not raised the multiplier, and overstating every other category's share by the same
        /// amount. This walk closes that gap.
        ///
        /// It is an observation, not an estimate. Traced against LodgingProviderJob.Execute
        /// (:138-158), the native charge per guest is
        ///
        ///     float num4 = m_LeisureParameters.m_TouristLodgingConsumePerDay / kUpdatesPerDay;
        ///     float num5 = num4 * marketPrice;
        ///     EconomyUtils.AddResources(Resource.Money, -(int)num5, m_Resources[renter]);   (:145)
        ///
        /// so a guest's wallet loses exactly (int)num5, truncated, and the total is that times the
        /// number of guests. Note it is *not* the `amount` the company is credited at :150, which
        /// is RoundToInt(num5 * guests) — the untruncated price rounded once over the whole hotel.
        /// The ledger reports money leaving tourists, so the truncated per-guest figure is the
        /// right one.
        ///
        /// The guest count is reproduced the same way the native job arrives at it (:117-137):
        /// non-tourist renters are not guests, and a hotel whose renters exceed its rooms evicts
        /// the overflow before billing. Both are deterministic from state that can be read, so the
        /// count is the same whether the native job has already run this frame or is about to —
        /// which matters, because system ordering decides that and this must not depend on it.
        ///
        /// Nothing here writes. No AddResources, no ServiceAvailable, no LodgingProvider, no
        /// consumption into the city accumulator, no ServiceCompanyData scaling: every one of
        /// those is serialized company state that the native system owns while it is enabled, and
        /// writing any of it would either double-bill guests or fight the system that is running.
        /// The job is scheduled after the native job's writes, so it reads them rather than racing
        /// them.
        /// </summary>
        private void ScheduleObservation()
        {
            if (m_NativeLodgingProvider == null || !m_NativeLodgingProvider.Enabled)
            {
                return;
            }

            if (m_SkipNextObservation)
            {
                m_SkipNextObservation = false;
                return;
            }

            if (m_ProviderQuery.IsEmptyIgnoreFilter || m_LeisureParameterQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            LeisureParametersData leisure = m_LeisureParameterQuery.GetSingleton<LeisureParametersData>();
            ResourcePrefabs resourcePrefabs = m_ResourceSystem.GetPrefabs();

            float marketPrice = EconomyUtils.GetMarketPrice(Resource.Lodging, resourcePrefabs, EntityManager);
            float consumePerUpdate = (float)leisure.m_TouristLodgingConsumePerDay / kUpdatesPerDay;
            float pricePerUpdate = consumePerUpdate * marketPrice;

            // The native cast, reproduced. Below one unit of currency per update the truncation
            // takes the whole charge and guests genuinely pay nothing.
            int chargePerGuest = (int)pricePerUpdate;

            m_ResourceSystem.AddPrefabsReader(default(JobHandle));

            if (chargePerGuest <= 0)
            {
                return;
            }

            UpdateLookups();
            m_Renters.Update(this);
            m_TouristHouseholds.Update(this);

            JobHandle job = new ObserveJob
            {
                m_Lookups = m_Lookups,
                m_Renters = m_Renters,
                m_TouristHouseholds = m_TouristHouseholds,
                m_UpdateFrameType = GetSharedComponentTypeHandle<UpdateFrame>(),
                m_EntityType = GetEntityTypeHandle(),
                m_UpdateFrame = SimulationUtils.GetUpdateFrame(m_SimulationSystem.frameIndex, kUpdatesPerDay, 16),
                m_ChargePerGuest = chargePerGuest,
                m_Results = m_Results,
            }.Schedule(m_ProviderQuery, Dependency);

            m_ResultsPending = true;
            m_LastJob = job;
            Dependency = job;
        }

        /// <summary>
        /// How many guests the native job charges on this update, times the charge, summed into
        /// m_Results[0]. Read-only throughout; a Burst job because reading every hotel's renters on
        /// the main thread waited for every job writing Renter (48 ms per update, up to 161 ms).
        /// </summary>
        [BurstCompile]
        private struct ObserveJob : IJobChunk
        {
            public HotelLookups m_Lookups;
            [ReadOnly] public BufferLookup<Renter> m_Renters;
            [ReadOnly] public ComponentLookup<TouristHousehold> m_TouristHouseholds;
            [ReadOnly] public SharedComponentTypeHandle<UpdateFrame> m_UpdateFrameType;
            [ReadOnly] public EntityTypeHandle m_EntityType;
            public uint m_UpdateFrame;
            public int m_ChargePerGuest;
            public NativeArray<long> m_Results;

            public void Execute(
                in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                // UpdateFrame sharding, native :97. A hotel outside this frame's shard is untouched.
                if (chunk.GetSharedComponent(m_UpdateFrameType).m_Index != m_UpdateFrame)
                {
                    return;
                }

                NativeArray<Entity> companies = chunk.GetNativeArray(m_EntityType);

                for (int i = 0; i < chunk.Count; i++)
                {
                    Entity company = companies[i];

                    // Native :166-173 — a lodging company with no property has its renters cleared
                    // and bills nobody.
                    if (!m_Renters.TryGetBuffer(company, out DynamicBuffer<Renter> renters)
                        || !m_Lookups.TryGetRoomCount(company, out int roomCount, out _))
                    {
                        continue;
                    }

                    // Native :117-123 — only tourist households are guests.
                    int guests = 0;

                    for (int n = 0; n < renters.Length; n++)
                    {
                        if (m_TouristHouseholds.HasComponent(renters[n].m_Renter))
                        {
                            guests++;
                        }
                    }

                    // Native :124-137 — the overflow above capacity is evicted before anyone is
                    // billed. The vanilla room count, with no multiplier: the native system is the
                    // one running.
                    m_Results[0] += (long)m_ChargePerGuest * math.min(guests, roomCount);
                }
            }
        }

        /// <summary>
        /// Mirror of LodgingProviderJob.Execute (:95-175), as a single-threaded Burst job over the
        /// same chunks: it writes the guests' and the company's resources and the city's usage
        /// accumulator, so it must not run in parallel with itself.
        /// </summary>
        private void ScheduleLodgingUpdate(int multiplier)
        {
            LeisureParametersData leisure = m_LeisureParameterQuery.GetSingleton<LeisureParametersData>();
            ResourcePrefabs resourcePrefabs = m_ResourceSystem.GetPrefabs();

            float marketPrice = EconomyUtils.GetMarketPrice(Resource.Lodging, resourcePrefabs, EntityManager);
            float consumePerUpdate = (float)leisure.m_TouristLodgingConsumePerDay / kUpdatesPerDay;

            m_ResourceSystem.AddPrefabsReader(default(JobHandle));

            // The native system feeds citizen lodging consumption into the city statistics; so does
            // the job, through the same accumulator.
            NativeArray<int> usageAccumulator = m_CityProductionStatisticSystem
                .GetCityResourceUsageAccumulator(CityProductionStatisticSystem.CityResourceUsage.Consumer.Citizens,
                    out JobHandle accumulatorDeps);

            UpdateLookups();
            m_Resources.Update(this);
            m_Statistics.Update(this);
            m_TouristWrite.Update(this);

            JobHandle job = new LodgingJob
            {
                m_Lookups = m_Lookups,
                m_Resources = m_Resources,
                m_Statistics = m_Statistics,
                m_TouristHouseholds = m_TouristWrite,
                m_UpdateFrameType = GetSharedComponentTypeHandle<UpdateFrame>(),
                m_EntityType = GetEntityTypeHandle(),
                m_ProviderType = GetComponentTypeHandle<LodgingProvider>(isReadOnly: false),
                m_ServiceType = GetComponentTypeHandle<ServiceAvailable>(isReadOnly: false),
                m_RenterType = GetBufferTypeHandle<Renter>(isReadOnly: false),
                m_UpdateFrame = SimulationUtils.GetUpdateFrame(m_SimulationSystem.frameIndex, kUpdatesPerDay, 16),
                m_Multiplier = multiplier,
                m_ConsumePerUpdate = consumePerUpdate,
                m_PricePerUpdate = consumePerUpdate * marketPrice,
                m_UsageAccumulator = usageAccumulator,
                m_LodgingIndex = EconomyUtils.GetResourceIndex(Resource.Lodging),
                m_Results = m_Results,
            }.Schedule(m_ProviderQuery, JobHandle.CombineDependencies(Dependency, accumulatorDeps));

            m_CityProductionStatisticSystem.AddCityUsageAccumulatorWriter(
                CityProductionStatisticSystem.CityResourceUsage.Consumer.Citizens, job);

            m_ResultsPending = true;
            m_RoomsPending = true;
            m_LastJob = job;
            Dependency = job;
        }

        [BurstCompile]
        private struct LodgingJob : IJobChunk
        {
            public HotelLookups m_Lookups;
            public BufferLookup<Game.Economy.Resources> m_Resources;
            public ComponentLookup<CompanyStatisticData> m_Statistics;

            // Written: evicting a guest clears the household's hotel.
            public ComponentLookup<TouristHousehold> m_TouristHouseholds;

            [ReadOnly] public SharedComponentTypeHandle<UpdateFrame> m_UpdateFrameType;
            [ReadOnly] public EntityTypeHandle m_EntityType;
            public ComponentTypeHandle<LodgingProvider> m_ProviderType;
            public ComponentTypeHandle<ServiceAvailable> m_ServiceType;
            public BufferTypeHandle<Renter> m_RenterType;
            public uint m_UpdateFrame;
            public int m_Multiplier;
            public float m_ConsumePerUpdate;
            public float m_PricePerUpdate;
            public NativeArray<int> m_UsageAccumulator;
            public int m_LodgingIndex;

            /// <summary>[0] charged to guests, [1] total rooms.</summary>
            public NativeArray<long> m_Results;

            public void Execute(
                in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                if (chunk.GetSharedComponent(m_UpdateFrameType).m_Index != m_UpdateFrame)
                {
                    return;
                }

                NativeArray<Entity> companies = chunk.GetNativeArray(m_EntityType);
                NativeArray<LodgingProvider> providers = chunk.GetNativeArray(ref m_ProviderType);
                NativeArray<ServiceAvailable> services = chunk.GetNativeArray(ref m_ServiceType);
                BufferAccessor<Renter> renterAccessor = chunk.GetBufferAccessor(ref m_RenterType);

                for (int i = 0; i < chunk.Count; i++)
                {
                    m_Results[1] += UpdateHotel(companies[i], i, providers, services, renterAccessor);
                }
            }

            /// <summary>Returns the room count for this hotel, or 0 if it was not processed.</summary>
            private int UpdateHotel(
                Entity company,
                int index,
                NativeArray<LodgingProvider> providers,
                NativeArray<ServiceAvailable> services,
                BufferAccessor<Renter> renterAccessor)
            {
                DynamicBuffer<Renter> renters = renterAccessor[index];

                // Native :166-173 — a lodging company with no property has no guests.
                if (!m_Lookups.m_PropertyRenters.HasComponent(company))
                {
                    renters.Clear();
                    return 0;
                }

                if (!m_Lookups.TryGetRoomCount(company, out int roomCount, out bool signature))
                {
                    return 0;
                }

                // Signature buildings are hand-authored and excluded by request.
                if (!signature)
                {
                    roomCount *= m_Multiplier;
                }

                // Native :117-123 — anything that is not a tourist household is not a guest.
                for (int n = renters.Length - 1; n >= 0; n--)
                {
                    if (!m_TouristHouseholds.HasComponent(renters[n].m_Renter))
                    {
                        renters.RemoveAt(n);
                    }
                }

                // Native :124-137 — evict the overflow when capacity shrank (e.g. multiplier lowered).
                if (roomCount < renters.Length)
                {
                    int toEvict = renters.Length - roomCount;
                    int cursor = renters.Length - 1;

                    while (cursor >= 0 && toEvict > 0)
                    {
                        Entity guest = renters[cursor].m_Renter;

                        TouristHousehold tourist = m_TouristHouseholds[guest];
                        tourist.m_Hotel = Entity.Null;
                        m_TouristHouseholds[guest] = tourist;

                        renters.RemoveAt(cursor);
                        toEvict--;
                        cursor--;
                    }
                }

                // Native :138-164 — charge guests, pay the company, consume lodging.
                int guests = 0;
                for (int j = 0; j < renters.Length; j++)
                {
                    if (!m_Resources.TryGetBuffer(renters[j].m_Renter, out DynamicBuffer<Game.Economy.Resources> wallet))
                    {
                        continue;
                    }

                    EconomyUtils.AddResources(Resource.Money, -(int)m_PricePerUpdate, wallet);
                    guests++;
                }

                // Mathf.RoundToInt and Mathf.CeilToInt as the native job uses them, in Burst's math.
                int income = (int)math.round(m_PricePerUpdate * guests);

                // Exact lodging spend, counted where the guests are actually billed. Not `income`,
                // which is what the company receives: each guest is charged the truncated price
                // above, while the company is credited the rounded total of the untruncated price.
                // The ledger measures money leaving tourists' wallets, so it takes what they lost.
                m_Results[0] += (long)(int)m_PricePerUpdate * guests;
                int lodgingConsumed = (int)math.ceil(m_ConsumePerUpdate * guests);

                if (m_Resources.TryGetBuffer(company, out DynamicBuffer<Game.Economy.Resources> companyResources))
                {
                    EconomyUtils.AddResources(Resource.Money, income, companyResources);
                    EconomyUtils.AddResources(Resource.Lodging, -lodgingConsumed, companyResources);
                }

                m_UsageAccumulator[m_LodgingIndex] += lodgingConsumed;

                LodgingProvider provider = providers[index];
                provider.m_Price = (int)(m_PricePerUpdate * kUpdatesPerDay);
                provider.m_FreeRooms = roomCount - renters.Length;
                providers[index] = provider;

                ServiceAvailable service = services[index];
                service.m_ServiceAvailable = math.max(0, service.m_ServiceAvailable - lodgingConsumed);

                // A full hotel has sold everything it has. ServiceCompanySystem flags "not enough
                // customers" when unsold service is above 90% of m_MaxService (:169), and replaces
                // that with a free-rooms test for hotels only while m_FreeRooms > 0 (:170-173). A
                // hotel with every room taken therefore skips the rooms test and is judged on
                // service stock alone, which its own production keeps near the (doubled) maximum,
                // since guests draw little of it: full hotels wore the no-customers warning.
                // Emptying the stock when no room is free makes the game's own test pass, and it
                // removes the icon itself on its next update (:190-195).
                if (provider.m_FreeRooms <= 0 && renters.Length > 0)
                {
                    service.m_ServiceAvailable = 0;
                }

                services[index] = service;

                if (m_Statistics.TryGetComponent(company, out CompanyStatisticData statistics))
                {
                    statistics.m_CurrentNumberOfCustomers += guests;
                    m_Statistics[company] = statistics;
                }

                return roomCount;
            }
        }

        /// <summary>The read-only lookups both jobs use to size a hotel.</summary>
        private struct HotelLookups
        {
            [ReadOnly] public ComponentLookup<PropertyRenter> m_PropertyRenters;
            [ReadOnly] public ComponentLookup<PrefabRef> m_PrefabRefs;
            [ReadOnly] public ComponentLookup<BuildingData> m_BuildingData;
            [ReadOnly] public ComponentLookup<BuildingPropertyData> m_PropertyData;
            [ReadOnly] public ComponentLookup<SpawnableBuildingData> m_SpawnableData;
            [ReadOnly] public ComponentLookup<SignatureBuildingData> m_SignatureData;

            /// <summary>
            /// The vanilla room count of the building a lodging company rents, and whether it is a
            /// signature building. False when the company has no property or the building is not a
            /// sized spawnable one.
            /// </summary>
            public bool TryGetRoomCount(Entity company, out int roomCount, out bool signature)
            {
                roomCount = 0;
                signature = false;

                if (!m_PropertyRenters.TryGetComponent(company, out PropertyRenter renter)
                    || !m_PrefabRefs.TryGetComponent(renter.m_Property, out PrefabRef prefabRef))
                {
                    return false;
                }

                Entity prefab = prefabRef.m_Prefab;

                if (!m_BuildingData.TryGetComponent(prefab, out BuildingData building)
                    || !m_PropertyData.TryGetComponent(prefab, out BuildingPropertyData property)
                    || !m_SpawnableData.TryGetComponent(prefab, out SpawnableBuildingData spawnable))
                {
                    return false;
                }

                roomCount = LodgingProviderSystem.GetRoomCount(building.m_LotSize, spawnable.m_Level, property);
                signature = m_SignatureData.HasComponent(prefab);
                return true;
            }
        }

        private void UpdateLookups()
        {
            m_Lookups.m_PropertyRenters.Update(this);
            m_Lookups.m_PrefabRefs.Update(this);
            m_Lookups.m_BuildingData.Update(this);
            m_Lookups.m_PropertyData.Update(this);
            m_Lookups.m_SpawnableData.Update(this);
            m_Lookups.m_SignatureData.Update(this);
        }
    }
}
