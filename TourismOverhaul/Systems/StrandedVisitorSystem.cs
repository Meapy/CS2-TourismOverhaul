using Game;
using Game.Agents;
using Game.Citizens;
using Game.Common;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Sends home tourist parties left sitting inside an outside connection with nothing to do.
    ///
    /// Before LandHomewardPassengers gave them MovingAway, a cruise party that sailed home was set
    /// down in the ship's outside connection as an ordinary tourist with a hotel and no trip, and
    /// stayed there: every few seconds it tried to reach an attraction or a leisure spot from the
    /// map edge and failed at the cost limit. In a 665k save that was about a hundred citizens,
    /// each failing 55 times in three minutes, using 8.7% of all route search work. Those parties
    /// lost their CruisePassenger tag before the fix, so the fix cannot reach them; this does.
    ///
    /// The test has to leave alone the visitors who are meant to be there. The cruise queue creates
    /// arrivals at the same connection, and any visitor arriving by sea starts in one. Those are
    /// LodgingSeekers until they are given a hotel, and once they have one they leave for it. So a
    /// party counts as stranded only when all of these hold, on two passes at least
    /// <see cref="kStrandedFrames"/> apart:
    ///
    ///   - a tourist household with a hotel (not a LodgingSeeker), not already moving away, and not
    ///     a cruise party the voyage system is still managing;
    ///   - every one of its citizens inside a building that is an outside connection.
    ///
    /// Stranded parties get MovingAway to the connection they are in, which completes on the spot,
    /// the same departure the voyage system gives a party written off at the deadline.
    /// </summary>
    public partial class StrandedVisitorSystem : GameSystemBase
    {
        /// <summary>How long a party must stay stranded before it is sent home: 1.5 in-game hours.</summary>
        private const uint kStrandedFrames = 16384;

        private EntityQuery m_TouristQuery;
        private EndFrameBarrier m_EndFrameBarrier;

        /// <summary>Household to the frame it was first seen stranded.</summary>
        private NativeParallelHashMap<Entity, uint> m_FirstSeen;
        private NativeParallelHashMap<Entity, uint> m_StillStranded;

        private Game.Simulation.SimulationSystem m_SimulationSystem;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => 4096;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<Game.Simulation.SimulationSystem>();
            m_FirstSeen = new NativeParallelHashMap<Entity, uint>(64, Allocator.Persistent);
            m_StillStranded = new NativeParallelHashMap<Entity, uint>(64, Allocator.Persistent);

            m_TouristQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.ReadOnly<HouseholdCitizen>(),
                ComponentType.Exclude<LodgingSeeker>(),
                ComponentType.Exclude<MovingAway>(),
                ComponentType.Exclude<Components.CruisePassenger>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnDestroy()
        {
            m_FirstSeen.Dispose();
            m_StillStranded.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            if (m_TouristQuery.IsEmptyIgnoreFilter)
            {
                m_FirstSeen.Clear();
                return;
            }

            // Read on the main thread, so wait for the jobs writing what is read, and only those.
            EntityManager.CompleteDependencyBeforeRO<HouseholdCitizen>();
            EntityManager.CompleteDependencyBeforeRO<CurrentBuilding>();

            ComponentLookup<CurrentBuilding> buildings = GetComponentLookup<CurrentBuilding>(isReadOnly: true);
            ComponentLookup<Game.Objects.OutsideConnection> connections =
                GetComponentLookup<Game.Objects.OutsideConnection>(isReadOnly: true);
            BufferTypeHandle<HouseholdCitizen> citizenHandle = GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);
            EntityTypeHandle entityHandle = GetEntityTypeHandle();

            uint frame = m_SimulationSystem.frameIndex;
            EntityCommandBuffer commandBuffer = default;
            int sent = 0;
            m_StillStranded.Clear();

            NativeArray<ArchetypeChunk> chunks = m_TouristQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    NativeArray<Entity> households = chunks[c].GetNativeArray(entityHandle);
                    BufferAccessor<HouseholdCitizen> citizens = chunks[c].GetBufferAccessor(ref citizenHandle);

                    for (int i = 0; i < households.Length; i++)
                    {
                        if (!IsStranded(citizens[i], buildings, connections, out Entity connection))
                        {
                            continue;
                        }

                        Entity household = households[i];
                        if (!m_FirstSeen.TryGetValue(household, out uint firstSeen))
                        {
                            m_StillStranded.TryAdd(household, frame);
                            continue;
                        }

                        if (frame - firstSeen < kStrandedFrames)
                        {
                            m_StillStranded.TryAdd(household, firstSeen);
                            continue;
                        }

                        if (sent == 0)
                        {
                            commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
                        }

                        commandBuffer.AddComponent(household, new MovingAway
                        {
                            m_Target = connection,
                            m_Reason = MoveAwayReason.None
                        });
                        sent++;
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            // Only parties seen stranded on this pass keep their clock; anyone who left starts over.
            (m_FirstSeen, m_StillStranded) = (m_StillStranded, m_FirstSeen);

            if (sent > 0)
            {
                Mod.Log.Info(
                    $"Sent home {sent} tourist parties that had been sitting in an outside connection "
                    + "with a hotel and nothing to do for over 1.5 in-game hours.");
            }
        }

        /// <summary>Every citizen of the party inside the same outside connection building.</summary>
        private static bool IsStranded(
            DynamicBuffer<HouseholdCitizen> citizens,
            ComponentLookup<CurrentBuilding> buildings,
            ComponentLookup<Game.Objects.OutsideConnection> connections,
            out Entity connection)
        {
            connection = Entity.Null;

            if (citizens.Length == 0)
            {
                return false;
            }

            for (int i = 0; i < citizens.Length; i++)
            {
                if (!buildings.TryGetComponent(citizens[i].m_Citizen, out CurrentBuilding building)
                    || !connections.HasComponent(building.m_CurrentBuilding)
                    || (connection != Entity.Null && building.m_CurrentBuilding != connection))
                {
                    return false;
                }

                connection = building.m_CurrentBuilding;
            }

            return true;
        }
    }
}
