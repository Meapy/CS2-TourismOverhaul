using Game;
using Game.Agents;
using Game.Citizens;
using Game.Common;
using Game.Simulation;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Fix C — real length of stay.
    ///
    /// Vanilla defect (see docs/TOURISM-DIAGNOSIS.md, Finding 4):
    /// TouristHousehold.m_LeavingTime is written as 0 by TouristSpawnSystem and
    /// HouseholdInitializeSystem, is serialized, and is never read by any system.
    /// TourismSystem.GetTouristRandomStay() returns 262144 (one in-game day) and is never called.
    /// Both are unfinished stubs. The only departure logic is TouristLeaveSystem, which evicts
    /// tourists without a hotel each evening and tourists who cannot pay. A tourist who has a room
    /// and money therefore stays indefinitely.
    ///
    /// This system implements the missing mechanic using the field the base game already declares
    /// and serializes, so no new saved state is introduced. It only manages tourists who hold a
    /// hotel room; day-trippers without lodging are left entirely to the native
    /// TouristLeaveSystem, so no native behaviour is suppressed.
    /// </summary>
    public partial class TouristStaySystem : GameSystemBase
    {
        /// <summary>Simulation frames in one in-game day, per TourismSystem.GetTouristRandomStay.</summary>
        private const uint kFramesPerDay = 262144u;

        private EntityQuery m_TouristHouseholdQuery;
        private SimulationSystem m_SimulationSystem;
        private EndFrameBarrier m_EndFrameBarrier;

        // 512 gives 512 updates/day, matching native TouristLeaveSystem's cadence.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 512;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            m_TouristHouseholdQuery = GetEntityQuery(
                ComponentType.ReadWrite<TouristHousehold>(),
                ComponentType.ReadOnly<HouseholdCitizen>(),
                ComponentType.Exclude<MovingAway>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            RequireForUpdate(m_TouristHouseholdQuery);
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;
            if (settings == null || !settings.FixLengthOfStay)
            {
                return;
            }

            uint frame = m_SimulationSystem.frameIndex;
            uint averageStayFrames = (uint)math.max(1, settings.AverageStayDays) * kFramesPerDay;

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
            Random random = new Random(math.max(1u, frame * 2654435761u + 1013904223u));

            EntityTypeHandle entityHandle = GetEntityTypeHandle();
            ComponentTypeHandle<TouristHousehold> touristHandle =
                GetComponentTypeHandle<TouristHousehold>(isReadOnly: false);

            NativeArray<ArchetypeChunk> chunks =
                m_TouristHouseholdQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < chunks.Length; i++)
                {
                    ArchetypeChunk chunk = chunks[i];
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityHandle);
                    NativeArray<TouristHousehold> tourists = chunk.GetNativeArray(ref touristHandle);

                    for (int j = 0; j < chunk.Count; j++)
                    {
                        TouristHousehold tourist = tourists[j];

                        // No room booked yet: native TouristLeaveSystem owns this case.
                        if (tourist.m_Hotel == Entity.Null)
                        {
                            // Clear any stale timer so a re-booking gets a fresh stay.
                            if (tourist.m_LeavingTime != 0u)
                            {
                                tourist.m_LeavingTime = 0u;
                                tourists[j] = tourist;
                            }
                            continue;
                        }

                        if (tourist.m_LeavingTime == 0u)
                        {
                            // Just checked in — decide how long they are here for.
                            tourist.m_LeavingTime = frame + GetStayLength(ref random, averageStayFrames);
                            tourists[j] = tourist;
                            continue;
                        }

                        if (frame >= tourist.m_LeavingTime)
                        {
                            // Stay is over. MoveAwayReason.None marks an ordinary departure rather
                            // than one of the native failure reasons.
                            CitizenUtils.HouseholdMoveAway(commandBuffer, entities[j], MoveAwayReason.None);
                        }
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        /// <summary>
        /// Stay length spread across roughly half to one and a half times the configured average,
        /// so departures do not synchronise into waves.
        /// </summary>
        internal static uint GetStayLength(ref Random random, uint averageStayFrames)
        {
            float multiplier = random.NextFloat(0.5f, 1.5f);
            float frames = averageStayFrames * multiplier;

            // Never shorter than a few hours, never longer than the guard below.
            float clamped = math.clamp(frames, kFramesPerDay / 4f, averageStayFrames * 2f);

            return (uint)math.max(1f, clamped);
        }
    }
}
