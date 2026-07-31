using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.City;
using Game.Common;
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

        private CitySystem m_CitySystem;
        private SimulationSystem m_SimulationSystem;
        private EndFrameBarrier m_EndFrameBarrier;

        /// <summary>Accurate tourist citizen count from the most recent update.</summary>
        public int CurrentTourists { get; private set; }

        /// <summary>Target tourist citizen count from the most recent update.</summary>
        public int TargetTourists { get; private set; }

        // 262144 frames per day; 256 gives 1024 updates/day, matching the cadence at which the
        // native spawner would realistically deliver arrivals without bursting.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 256;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            // Same prefab set the native spawner picks from.
            m_HouseholdPrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<ArchetypeData>(),
                ComponentType.ReadOnly<HouseholdData>());

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

            CurrentTourists = CountTouristCitizens();
            TargetTourists = ComputeTarget(settings, attractiveness, population);

            if (!settings.FixTouristDemand)
            {
                return;
            }

            int deficit = TargetTourists - CurrentTourists;
            if (deficit <= 0)
            {
                return;
            }

            // Fill gradually: a large shortfall should not arrive in one tick.
            int arrivals = math.clamp(deficit / 64, 1, math.max(1, settings.MaxArrivalsPerUpdate));

            SpawnTouristHouseholds(arrivals);
        }

        /// <summary>
        /// Target never drops below what the base game would have produced, so this can only add
        /// tourists, never remove them.
        /// </summary>
        private static int ComputeTarget(TourismOverhaulSetting settings, int attractiveness, int population)
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

            int target = (int)math.round(math.max(vanillaTarget, scaled));

            return math.clamp(target, 0, math.max(vanillaTarget, settings.MaximumTourists));
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
        private void SpawnTouristHouseholds(int count)
        {
            NativeArray<Entity> prefabEntities = m_HouseholdPrefabQuery.ToEntityArray(Allocator.Temp);
            NativeArray<ArchetypeData> archetypes =
                m_HouseholdPrefabQuery.ToComponentDataArray<ArchetypeData>(Allocator.Temp);
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

                    int index = random.NextInt(prefabEntities.Length);

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
                }
            }
            finally
            {
                connections.Dispose();
                connectionArray.Dispose();
                archetypes.Dispose();
                prefabEntities.Dispose();
            }
        }
    }
}
