using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Releases hotel rooms still held by households that no longer have anyone in them.
    ///
    /// THE LEAK
    ///
    /// A room is booked by appending the household to the hotel's Renter buffer, and
    /// HotelCapacitySystem recomputes availability from that buffer on every pass:
    ///
    ///     provider.m_FreeRooms = roomCount - renters.Length;
    ///
    /// So a room frees itself the moment its renter leaves the buffer — and stays occupied forever
    /// if the renter never does. That is what has been happening. A tourist household loses its
    /// citizens when the visit ends but the household entity survives, keeping its place in the
    /// buffer and its room with it.
    ///
    /// Measured over a single snapshot: empty households rose by 3,253 while occupied rooms rose by
    /// 5,229, with the tourist population falling by 6,883 over the same period. Occupancy climbing
    /// while guests leave is the signature. Free rooms had collapsed to 475 of 35,163, which in turn
    /// drove a spike of TouristNoMoney departures as guests who could not move sat burning their
    /// budget.
    ///
    /// THE FIX
    ///
    /// Two passes, both bounded so a large backlog is cleared gradually rather than in one spike:
    ///
    ///   1. Drop Renter entries whose household is gone or empty, which frees the room by way of
    ///      the recomputation above. Nothing else is touched, so a hotel with real guests is
    ///      unaffected.
    ///   2. Delete the husk households themselves, reclaiming the entities.
    ///
    /// A household is only considered a husk when it holds no citizens AND has no CurrentBuilding.
    /// That second condition matters: a freshly spawned household is briefly empty while it waits
    /// for HouseholdInitializeSystem, and it carries CurrentBuilding until it is initialised. Using
    /// both conditions means an arrival in flight can never be mistaken for a corpse.
    /// </summary>
    public partial class HotelRoomReclaimSystem : GameSystemBase
    {
        // Sized to outpace husk creation rather than merely keep up with it. At 512 and 256 the
        // release pass sat permanently at its ceiling while the backlog barely moved, which means
        // the cap, not the work, was the limit. These still bound a frame's worth of work.

        /// <summary>Rooms released per update.</summary>
        private const int kMaxRoomsPerUpdate = 2048;

        /// <summary>Husk households deleted per update.</summary>
        private const int kMaxDeletionsPerUpdate = 1024;

        private EntityQuery m_HotelQuery;
        private EntityQuery m_HuskQuery;

        private EndFrameBarrier m_EndFrameBarrier;

        /// <summary>Rooms released since load. For diagnostics.</summary>
        public int RoomsReclaimed { get; private set; }

        /// <summary>Husk households deleted since load. For diagnostics.</summary>
        public int HusksRemoved { get; private set; }

        // 262144 frames per in-game day, so this runs 128 times a day.
        //
        // Every pass walks each hotel's full renter buffer with per-entity lookups, which is the
        // expensive part and scales with the number of guests rather than the number of departures.
        // Running it four times less often costs nothing that matters: a room sits idle for a few
        // seconds longer before it is offered again, and the caps below still clear far more per
        // day than any city generates. The initial backlog drains a little slower, once.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 2048;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            m_HotelQuery = GetEntityQuery(
                ComponentType.ReadOnly<LodgingProvider>(),
                ComponentType.ReadWrite<Renter>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            // Emptied tourist households. CurrentBuilding is excluded rather than tested, so an
            // arrival still waiting to be initialised is never in this query at all.
            m_HuskQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.Exclude<CurrentBuilding>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null || !settings.ReclaimAbandonedHotelRooms)
            {
                return;
            }

            ReleaseRooms();
            RemoveHusks();
        }

        /// <summary>
        /// Removes Renter entries pointing at households that are gone or empty.
        /// </summary>
        private void ReleaseRooms()
        {
            if (m_HotelQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            int released = 0;

            NativeArray<Entity> hotels = m_HotelQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < hotels.Length && released < kMaxRoomsPerUpdate; i++)
                {
                    DynamicBuffer<Renter> renters = EntityManager.GetBuffer<Renter>(hotels[i]);

                    // Backwards, so removing an entry cannot skip the one after it.
                    for (int r = renters.Length - 1; r >= 0 && released < kMaxRoomsPerUpdate; r--)
                    {
                        if (!IsAbandoned(renters[r].m_Renter))
                        {
                            continue;
                        }

                        renters.RemoveAt(r);
                        released++;
                    }
                }
            }
            finally
            {
                hotels.Dispose();
            }

            RoomsReclaimed += released;

            // Only worth reporting when it is doing unusual work. At steady state this fires every
            // pass and says nothing, which buries everything else in the log.
            if (released >= kMaxRoomsPerUpdate && Mod.Settings != null && Mod.Settings.DiagnosticLogging)
            {
                Mod.Log.Info(
                    $"Released {released} hotel room(s) held by empty households — at the per-update " +
                    $"ceiling, so a backlog is still draining.");
            }
        }

        /// <summary>
        /// Whether a renter has ceased to be a guest in any meaningful sense.
        ///
        /// Deliberately conservative: a household that still holds anyone keeps its room, whatever
        /// state it is otherwise in, including one that is packing to leave. Releasing a room out
        /// from under a guest who is still present would evict a real visitor.
        /// </summary>
        private bool IsAbandoned(Entity household)
        {
            if (household == Entity.Null || !EntityManager.Exists(household))
            {
                return true;
            }

            if (EntityManager.HasComponent<Deleted>(household))
            {
                return true;
            }

            if (!EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                return true;
            }

            return EntityManager.GetBuffer<HouseholdCitizen>(household, isReadOnly: true).Length == 0;
        }

        /// <summary>
        /// Deletes emptied tourist households, which are otherwise immortal.
        /// </summary>
        private void RemoveHusks()
        {
            if (m_HuskQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();

            int removed = 0;

            NativeArray<Entity> households = m_HuskQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < households.Length && removed < kMaxDeletionsPerUpdate; i++)
                {
                    Entity household = households[i];

                    if (!EntityManager.HasBuffer<HouseholdCitizen>(household))
                    {
                        continue;
                    }

                    if (EntityManager.GetBuffer<HouseholdCitizen>(household, isReadOnly: true).Length > 0)
                    {
                        continue;
                    }

                    commandBuffer.AddComponent(household, default(Deleted));
                    removed++;
                }
            }
            finally
            {
                households.Dispose();
            }

            HusksRemoved += removed;
        }
    }
}
