using Game;
using Game.Agents;
using Game.Citizens;
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
    /// Two corrections are applied to the singleton, both plain component writes:
    ///
    ///   1. Availability — probability mass only goes to transfer types the city actually has.
    ///
    ///   2. Load — vanilla never looks at how busy a connection is. OutsideConnectionData carries
    ///      only m_Type and m_Remoteness; there is no capacity concept anywhere in the arrival
    ///      path. A saturated airport therefore keeps receiving its full 50% share, and any
    ///      arrival that cannot path onward from it is deleted as TouristNoTarget rather than
    ///      rerouted. Measuring the backlog of tourists still waiting at each connection lets
    ///      share shift away from congested routes before those arrivals are lost.
    ///
    /// The native spawn path is otherwise untouched — no patching, no reflection.
    /// </summary>
    public partial class TouristRoutingSystem : GameSystemBase
    {
        /// <summary>DemandPrefab's shipped default, used if we never observe an untouched value.</summary>
        private static readonly float4 kVanillaSplit = new float4(0.1f, 0.1f, 0.5f, 0.3f);

        /// <summary>Only rewrite the singleton when a component moves by at least this much.</summary>
        private const float kWriteThreshold = 0.01f;

        /// <summary>Log at most when a component moves by at least this much, to avoid spam.</summary>
        private const float kLogThreshold = 0.05f;

        private EntityQuery m_OutsideConnectionQuery;
        private EntityQuery m_DemandParameterQuery;
        private EntityQuery m_ArrivingTouristQuery;

        private float4 m_BaseSplit = kVanillaSplit;
        private bool m_BaseSplitCaptured;

        private float4 m_LastWritten;
        private float4 m_LastLogged;
        private bool m_HasWritten;

        /// <summary>Available transfer types at the last update. Exposed for diagnostics.</summary>
        public OutsideConnectionTransferType AvailableTypes { get; private set; }

        /// <summary>Waiting tourists per connection, indexed road/train/air/ship. Diagnostics.</summary>
        public float4 BacklogPerConnection { get; private set; }

        // 262144 frames per in-game day; 1024 gives 256 updates/day — responsive enough to track
        // congestion building up, while the work is proportional to arriving tourists only.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1024;

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

            // Tourists that have arrived somewhere but have not yet been given a destination.
            // These are the ones at risk of being deleted as TouristNoTarget.
            m_ArrivingTouristQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.ReadOnly<CurrentBuilding>(),
                ComponentType.Exclude<Target>(),
                ComponentType.Exclude<MovingAway>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

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

            CollectConnections(out OutsideConnectionTransferType available, out int4 connectionCounts,
                out NativeParallelHashMap<Entity, OutsideConnectionTransferType> connectionTypes);

            try
            {
                AvailableTypes = available;

                if (available == OutsideConnectionTransferType.None)
                {
                    // No usable connections at all — nothing sensible to redistribute to.
                    return;
                }

                float4 backlog = float4.zero;

                if (settings.LoadAwareArrivals)
                {
                    backlog = MeasureBacklog(connectionTypes, connectionCounts);
                }

                BacklogPerConnection = backlog;

                float4 split = BuildSplit(
                    m_BaseSplit, available, settings.PreferAirAndSea,
                    backlog, math.max(1, settings.ArrivalBacklogSensitivity));

                if (m_HasWritten && math.all(math.abs(m_LastWritten - split) < kWriteThreshold))
                {
                    return;
                }

                parameters.m_TouristOCSpawnParameters = split;
                EntityManager.SetComponentData(parameterEntity, parameters);

                m_LastWritten = split;
                m_HasWritten = true;

                if (math.any(math.abs(m_LastLogged - split) >= kLogThreshold))
                {
                    m_LastLogged = split;
                    Mod.Log.Info(
                        $"Tourist arrival split: road {split.x:0.00} train {split.y:0.00} " +
                        $"air {split.z:0.00} ship {split.w:0.00} " +
                        $"(available: {available}, waiting/connection: {backlog.x:0.0}/{backlog.y:0.0}/" +
                        $"{backlog.z:0.0}/{backlog.w:0.0})");
                }
            }
            finally
            {
                connectionTypes.Dispose();
            }
        }

        /// <summary>
        /// Union of transfer types across every outside connection the tourist spawner can see,
        /// how many connections serve each type, and a lookup from connection to its types.
        /// </summary>
        private void CollectConnections(
            out OutsideConnectionTransferType available,
            out int4 connectionCounts,
            out NativeParallelHashMap<Entity, OutsideConnectionTransferType> connectionTypes)
        {
            available = OutsideConnectionTransferType.None;
            connectionCounts = int4.zero;

            NativeArray<Entity> connections = m_OutsideConnectionQuery.ToEntityArray(Allocator.Temp);
            connectionTypes = new NativeParallelHashMap<Entity, OutsideConnectionTransferType>(
                math.max(1, connections.Length), Allocator.Temp);

            try
            {
                for (int i = 0; i < connections.Length; i++)
                {
                    if (!EntityManager.HasComponent<PrefabRef>(connections[i]))
                    {
                        continue;
                    }

                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(connections[i]).m_Prefab;
                    if (!EntityManager.HasComponent<OutsideConnectionData>(prefab))
                    {
                        continue;
                    }

                    OutsideConnectionTransferType type =
                        EntityManager.GetComponentData<OutsideConnectionData>(prefab).m_Type
                        & OutsideConnectionTransferType.All;

                    if (type == OutsideConnectionTransferType.None)
                    {
                        continue;
                    }

                    available |= type;
                    connectionTypes.TryAdd(connections[i], type);
                    connectionCounts += ToMask(type);
                }
            }
            finally
            {
                connections.Dispose();
            }
        }

        /// <summary>
        /// Tourists still waiting at each connection, divided by the number of connections serving
        /// that type — so three airports absorb three times the traffic before air is throttled.
        ///
        /// A connection serving several types (road and rail, say) contributes its backlog to each
        /// of them, since there is no way to attribute a waiting tourist to one mode.
        /// </summary>
        private float4 MeasureBacklog(
            NativeParallelHashMap<Entity, OutsideConnectionTransferType> connectionTypes,
            int4 connectionCounts)
        {
            int4 waiting = int4.zero;

            ComponentTypeHandle<CurrentBuilding> currentBuildingHandle =
                GetComponentTypeHandle<CurrentBuilding>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks =
                m_ArrivingTouristQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    NativeArray<CurrentBuilding> buildings = chunk.GetNativeArray(ref currentBuildingHandle);

                    for (int i = 0; i < buildings.Length; i++)
                    {
                        if (connectionTypes.TryGetValue(buildings[i].m_CurrentBuilding, out var type))
                        {
                            waiting += ToMask(type);
                        }
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            return new float4(waiting) / math.max(new float4(1f), new float4(connectionCounts));
        }

        private static int4 ToMask(OutsideConnectionTransferType type)
        {
            return new int4(
                (type & OutsideConnectionTransferType.Road) != 0 ? 1 : 0,
                (type & OutsideConnectionTransferType.Train) != 0 ? 1 : 0,
                (type & OutsideConnectionTransferType.Air) != 0 ? 1 : 0,
                (type & OutsideConnectionTransferType.Ship) != 0 ? 1 : 0);
        }

        /// <summary>Availability-only split, kept for the standalone check.</summary>
        internal static float4 BuildSplit(OutsideConnectionTransferType available, bool preferAirAndSea)
        {
            return BuildSplit(kVanillaSplit, available, preferAirAndSea, float4.zero, 25);
        }

        /// <summary>
        /// Redistribute the base split over the available types, damped by congestion and
        /// renormalised to sum to 1. Component order matches BuildingUtils: x Road, y Train,
        /// z Air, w Ship.
        ///
        /// Pure helper, kept internal so it can be exercised by a standalone check.
        /// </summary>
        internal static float4 BuildSplit(
            float4 baseSplit,
            OutsideConnectionTransferType available,
            bool preferAirAndSea,
            float4 backlogPerConnection,
            int sensitivity)
        {
            float4 mask = new float4(ToMask(available));

            float availableCount = mask.x + mask.y + mask.z + mask.w;
            if (availableCount <= 0f)
            {
                return baseSplit;
            }

            float4 weights = preferAirAndSea ? baseSplit * mask : mask;

            // Congestion damping: a route carrying `sensitivity` waiting tourists per connection
            // gets half its share; twice that, a third; and so on. Never reaches zero, so a
            // temporarily busy route recovers rather than being abandoned.
            float4 damping = 1f / (1f + math.max(float4.zero, backlogPerConnection) / sensitivity);
            weights *= damping;

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
