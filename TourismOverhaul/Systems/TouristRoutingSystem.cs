using Game;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Fix A — adaptive outside-connection routing.
    ///
    /// Vanilla defect (see docs/TOURISM-DIAGNOSIS.md, Finding 3):
    /// DemandPrefab.m_TouristOCSpawnParameters defaults to (Road .1, Train .1, Air .5, Ship .3).
    /// BuildingUtils.GetRandomOutsideConnectionByParameters makes ONE roll for a transfer type and
    /// then filters outside connections to exactly that type. When no connection of that type
    /// exists it returns false with no fallback — and TouristSpawnSystem still creates the
    /// household, just without CurrentBuilding. HouseholdInitializeSystem's query requires
    /// CurrentBuilding, so that household never receives citizens and is later destroyed as
    /// TouristNoTarget. On a city with no airport and no harbour that discards 80% of arrivals.
    ///
    /// This system rewrites the singleton so probability mass is only assigned to transfer types
    /// the city actually has. It is a plain component write — no patching, no reflection, and the
    /// native spawn path is otherwise untouched.
    /// </summary>
    public partial class TouristRoutingSystem : GameSystemBase
    {
        /// <summary>DemandPrefab's shipped default, used if we never observe an untouched value.</summary>
        private static readonly float4 kVanillaSplit = new float4(0.1f, 0.1f, 0.5f, 0.3f);

        private EntityQuery m_OutsideConnectionQuery;
        private EntityQuery m_DemandParameterQuery;

        private float4 m_BaseSplit = kVanillaSplit;
        private bool m_BaseSplitCaptured;

        /// <summary>Last split we wrote, so we can skip redundant writes.</summary>
        private float4 m_LastWritten;
        private bool m_HasWritten;

        /// <summary>Available transfer types at the last update. Exposed for logging/diagnostics.</summary>
        public OutsideConnectionTransferType AvailableTypes { get; private set; }

        // 262144 frames per in-game day; 4096 gives 64 updates/day. Outside connections change
        // rarely, and the value is only read by TouristSpawnSystem when it spawns.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 4096;

        protected override void OnCreate()
        {
            base.OnCreate();

            // Mirrors TouristSpawnSystem.m_OutsideConnectionQuery so we see exactly the set of
            // connections the spawner will search.
            m_OutsideConnectionQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Game.Objects.ElectricityOutsideConnection>(),
                ComponentType.Exclude<Game.Objects.WaterPipeOutsideConnection>(),
                ComponentType.Exclude<Temp>(),
                ComponentType.Exclude<Deleted>());

            m_DemandParameterQuery = GetEntityQuery(ComponentType.ReadWrite<DemandParameterData>());

            RequireForUpdate(m_DemandParameterQuery);
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // DemandParameterData is rebuilt from the prefab on load, so re-capture the untouched
            // split rather than trusting a value we may have written in a previous session.
            m_BaseSplitCaptured = false;
            m_HasWritten = false;
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;
            if (settings == null || !settings.FixArrivalRouting)
            {
                return;
            }

            Entity parameterEntity = m_DemandParameterQuery.GetSingletonEntity();
            DemandParameterData parameters = EntityManager.GetComponentData<DemandParameterData>(parameterEntity);

            if (!m_BaseSplitCaptured)
            {
                // First look after a load: whatever is there is the unmodified value.
                m_BaseSplit = parameters.m_TouristOCSpawnParameters;
                m_BaseSplitCaptured = true;
            }

            OutsideConnectionTransferType available = GetAvailableTransferTypes();
            AvailableTypes = available;

            if (available == OutsideConnectionTransferType.None)
            {
                // No usable connections at all — nothing sensible to redistribute to.
                return;
            }

            float4 split = BuildSplit(available, settings.PreferAirAndSea);

            if (m_HasWritten && math.all(m_LastWritten == split)
                             && math.all(parameters.m_TouristOCSpawnParameters == split))
            {
                return;
            }

            parameters.m_TouristOCSpawnParameters = split;
            EntityManager.SetComponentData(parameterEntity, parameters);

            m_LastWritten = split;
            m_HasWritten = true;

            Mod.Log.Info(
                $"Tourist arrival split updated: road {split.x:0.00} train {split.y:0.00} " +
                $"air {split.z:0.00} ship {split.w:0.00} (available: {available})");
        }

        /// <summary>
        /// Union of the transfer types of every outside connection the tourist spawner can see.
        /// </summary>
        private OutsideConnectionTransferType GetAvailableTransferTypes()
        {
            OutsideConnectionTransferType available = OutsideConnectionTransferType.None;

            NativeArray<Entity> connections = m_OutsideConnectionQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < connections.Length; i++)
                {
                    if (!EntityManager.HasComponent<PrefabRef>(connections[i]))
                    {
                        continue;
                    }

                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(connections[i]).m_Prefab;
                    if (EntityManager.HasComponent<OutsideConnectionData>(prefab))
                    {
                        available |= EntityManager.GetComponentData<OutsideConnectionData>(prefab).m_Type;
                    }
                }
            }
            finally
            {
                connections.Dispose();
            }

            // Only the four types the spawn parameters address.
            return available & OutsideConnectionTransferType.All;
        }

        /// <summary>
        /// Redistribute the base split over the available types, renormalised to sum to 1.
        /// Component order matches BuildingUtils: x Road, y Train, z Air, w Ship.
        /// </summary>
        internal static float4 BuildSplit(OutsideConnectionTransferType available, bool preferAirAndSea)
        {
            return BuildSplit(kVanillaSplit, available, preferAirAndSea);
        }

        /// <summary>Pure helper, kept internal so it can be exercised by a standalone check.</summary>
        internal static float4 BuildSplit(float4 baseSplit, OutsideConnectionTransferType available, bool preferAirAndSea)
        {
            float4 mask = new float4(
                (available & OutsideConnectionTransferType.Road) != 0 ? 1f : 0f,
                (available & OutsideConnectionTransferType.Train) != 0 ? 1f : 0f,
                (available & OutsideConnectionTransferType.Air) != 0 ? 1f : 0f,
                (available & OutsideConnectionTransferType.Ship) != 0 ? 1f : 0f);

            float availableCount = mask.x + mask.y + mask.z + mask.w;
            if (availableCount <= 0f)
            {
                return baseSplit;
            }

            float4 weights = preferAirAndSea ? baseSplit * mask : mask;

            float total = weights.x + weights.y + weights.z + weights.w;
            if (total <= 0f)
            {
                // Base split assigned nothing to the available types; fall back to an even spread
                // so arrivals are never discarded.
                weights = mask;
                total = availableCount;
            }

            return weights / total;
        }
    }
}
