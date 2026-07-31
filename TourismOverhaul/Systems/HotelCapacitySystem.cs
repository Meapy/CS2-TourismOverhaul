using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

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

        private SimulationSystem m_SimulationSystem;
        private ResourceSystem m_ResourceSystem;
        private CityProductionStatisticSystem m_CityProductionStatisticSystem;
        private LodgingProviderSystem m_NativeLodgingProvider;

        private bool m_NativeDisabled;

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

            RequireForUpdate(m_ProviderQuery);
            RequireForUpdate(m_LeisureParameterQuery);
        }

        protected override void OnDestroy()
        {
            RestoreNativeSystem();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            int multiplier = settings != null ? math.clamp(settings.HotelRoomMultiplier, 1, 10) : 1;
            bool active = settings != null && settings.EnableHotelCapacity && multiplier > 1;

            if (!active)
            {
                RestoreNativeSystem();
                return;
            }

            DisableNativeSystem();
            RunLodgingUpdate(multiplier);
        }

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
            Mod.Log.Info("Native LodgingProviderSystem restored.");
        }

        /// <summary>
        /// Main-thread mirror of LodgingProviderJob.Execute (:95-175). Hotel counts are in the
        /// hundreds, and this runs 32 times per in-game day, so a job is not warranted.
        /// </summary>
        private void RunLodgingUpdate(int multiplier)
        {
            uint updateFrame = SimulationUtils.GetUpdateFrame(m_SimulationSystem.frameIndex, kUpdatesPerDay, 16);

            LeisureParametersData leisure = m_LeisureParameterQuery.GetSingleton<LeisureParametersData>();
            ResourcePrefabs resourcePrefabs = m_ResourceSystem.GetPrefabs();

            float marketPrice = EconomyUtils.GetMarketPrice(Resource.Lodging, resourcePrefabs, EntityManager);
            float consumePerUpdate = (float)leisure.m_TouristLodgingConsumePerDay / kUpdatesPerDay;
            float pricePerUpdate = consumePerUpdate * marketPrice;

            // The native system feeds citizen lodging consumption into the city statistics. Take
            // the same accumulator and settle outstanding writers before touching it.
            NativeArray<int> usageAccumulator = m_CityProductionStatisticSystem
                .GetCityResourceUsageAccumulator(CityProductionStatisticSystem.CityResourceUsage.Consumer.Citizens,
                    out JobHandle accumulatorDeps);
            accumulatorDeps.Complete();

            int lodgingIndex = EconomyUtils.GetResourceIndex(Resource.Lodging);

            EntityTypeHandle entityHandle = GetEntityTypeHandle();
            ComponentTypeHandle<LodgingProvider> providerHandle = GetComponentTypeHandle<LodgingProvider>(false);
            ComponentTypeHandle<ServiceAvailable> serviceHandle = GetComponentTypeHandle<ServiceAvailable>(false);
            BufferTypeHandle<Renter> renterHandle = GetBufferTypeHandle<Renter>(false);
            SharedComponentTypeHandle<UpdateFrame> updateFrameHandle = GetSharedComponentTypeHandle<UpdateFrame>();

            int totalRooms = 0;

            NativeArray<ArchetypeChunk> chunks = m_ProviderQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    if (chunk.GetSharedComponent(updateFrameHandle).m_Index != updateFrame)
                    {
                        continue;
                    }

                    NativeArray<Entity> entities = chunk.GetNativeArray(entityHandle);
                    NativeArray<LodgingProvider> providers = chunk.GetNativeArray(ref providerHandle);
                    NativeArray<ServiceAvailable> services = chunk.GetNativeArray(ref serviceHandle);
                    BufferAccessor<Renter> renterAccessor = chunk.GetBufferAccessor(ref renterHandle);

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        totalRooms += UpdateHotel(
                            entities[i], i, providers, services, renterAccessor,
                            multiplier, consumePerUpdate, pricePerUpdate,
                            usageAccumulator, lodgingIndex);
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            TotalRooms = totalRooms;

            m_CityProductionStatisticSystem.AddCityUsageAccumulatorWriter(
                CityProductionStatisticSystem.CityResourceUsage.Consumer.Citizens, default(JobHandle));
            m_ResourceSystem.AddPrefabsReader(default(JobHandle));
        }

        /// <summary>Returns the room count for this hotel, or 0 if it was not processed.</summary>
        private int UpdateHotel(
            Entity company,
            int index,
            NativeArray<LodgingProvider> providers,
            NativeArray<ServiceAvailable> services,
            BufferAccessor<Renter> renterAccessor,
            int multiplier,
            float consumePerUpdate,
            float pricePerUpdate,
            NativeArray<int> usageAccumulator,
            int lodgingIndex)
        {
            DynamicBuffer<Renter> renters = renterAccessor[index];

            // Native :166-173 — a lodging company with no property has no guests.
            if (!EntityManager.HasComponent<PropertyRenter>(company))
            {
                renters.Clear();
                return 0;
            }

            Entity property = EntityManager.GetComponentData<PropertyRenter>(company).m_Property;

            if (!EntityManager.HasComponent<PrefabRef>(property))
            {
                return 0;
            }

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(property).m_Prefab;

            if (!EntityManager.HasComponent<BuildingData>(prefab)
                || !EntityManager.HasComponent<BuildingPropertyData>(prefab)
                || !EntityManager.HasComponent<SpawnableBuildingData>(prefab))
            {
                return 0;
            }

            BuildingData buildingData = EntityManager.GetComponentData<BuildingData>(prefab);
            BuildingPropertyData propertyData = EntityManager.GetComponentData<BuildingPropertyData>(prefab);
            SpawnableBuildingData spawnableData = EntityManager.GetComponentData<SpawnableBuildingData>(prefab);

            int roomCount = LodgingProviderSystem.GetRoomCount(
                buildingData.m_LotSize, spawnableData.m_Level, propertyData);

            // Signature buildings are hand-authored and excluded by request.
            if (!EntityManager.HasComponent<SignatureBuildingData>(prefab))
            {
                roomCount *= multiplier;
            }

            // Native :117-123 — anything that is not a tourist household is not a guest.
            for (int n = renters.Length - 1; n >= 0; n--)
            {
                if (!EntityManager.HasComponent<TouristHousehold>(renters[n].m_Renter))
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

                    TouristHousehold tourist = EntityManager.GetComponentData<TouristHousehold>(guest);
                    tourist.m_Hotel = Entity.Null;
                    EntityManager.SetComponentData(guest, tourist);

                    renters.RemoveAt(cursor);
                    toEvict--;
                    cursor--;
                }
            }

            // Native :138-164 — charge guests, pay the company, consume lodging.
            int guests = 0;
            for (int j = 0; j < renters.Length; j++)
            {
                Entity guest = renters[j].m_Renter;

                if (!EntityManager.HasBuffer<Game.Economy.Resources>(guest))
                {
                    continue;
                }

                EconomyUtils.AddResources(Resource.Money, -(int)pricePerUpdate,
                    EntityManager.GetBuffer<Game.Economy.Resources>(guest));
                guests++;
            }

            int income = Mathf.RoundToInt(pricePerUpdate * guests);
            int lodgingConsumed = Mathf.CeilToInt(consumePerUpdate * guests);

            if (EntityManager.HasBuffer<Game.Economy.Resources>(company))
            {
                DynamicBuffer<Game.Economy.Resources> companyResources =
                    EntityManager.GetBuffer<Game.Economy.Resources>(company);

                EconomyUtils.AddResources(Resource.Money, income, companyResources);
                EconomyUtils.AddResources(Resource.Lodging, -lodgingConsumed, companyResources);
            }

            usageAccumulator[lodgingIndex] += lodgingConsumed;

            ServiceAvailable service = services[index];
            service.m_ServiceAvailable = math.max(0, service.m_ServiceAvailable - lodgingConsumed);
            services[index] = service;

            LodgingProvider provider = providers[index];
            provider.m_Price = (int)(pricePerUpdate * kUpdatesPerDay);
            provider.m_FreeRooms = roomCount - renters.Length;
            providers[index] = provider;

            if (EntityManager.HasComponent<CompanyStatisticData>(company))
            {
                CompanyStatisticData statistics = EntityManager.GetComponentData<CompanyStatisticData>(company);
                statistics.m_CurrentNumberOfCustomers += guests;
                EntityManager.SetComponentData(company, statistics);
            }

            return roomCount;
        }
    }
}
