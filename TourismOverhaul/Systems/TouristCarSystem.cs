using Game;
using Game.Agents;
using Game.Citizens;
using Game.Common;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Optionally stops arriving tourists bringing a car.
    ///
    /// HouseholdInitializeSystem gives a personal car to any household whose arrival point carries
    /// the Road flag (:193-227), with no probability attached:
    ///
    ///     if ((GetOutsideConnectionType(building, ...) &amp; OutsideConnectionTransferType.Road) != None)
    ///         ... CreateVehicle(...); AddComponent(car, new Owner(household)); AddBuffer&lt;OwnedVehicle&gt;(household);
    ///
    /// TouristFindTargetSystem then calls PathUtils.UpdateOwnedVehicleMethods (:111), which adds
    /// car routing for households that own one, so those tourists drive.
    ///
    /// Note the test reads the connection's flags rather than the mode the tourist was routed by.
    /// Outside connections are often multi-modal, so a connection flagged Road | Train hands a car
    /// to a tourist who arrived by train — which is why far more tourists drive than the road share
    /// of the arrival split would suggest.
    ///
    /// This thins them out instead of removing them wholesale: a share of arriving households keep
    /// their car and the rest fall back on public transport, taxis and walking, which the
    /// pathfinder already offers them.
    ///
    /// The decision is derived from the household's own identity rather than a running random
    /// draw, so it is stable. Re-rolling each pass would mean every household eventually lost its
    /// car, since a car once taken is never given back — the chance would decay to zero instead of
    /// holding at the configured share.
    /// </summary>
    public partial class TouristCarSystem : GameSystemBase
    {
        private EntityQuery m_TouristCarQuery;
        private EndFrameBarrier m_EndFrameBarrier;

        /// <summary>Tourist cars removed since load. For diagnostics.</summary>
        public int CarsRemoved { get; private set; }

        // 262144 frames per in-game day; 128 gives 2048 passes/day, so a car is taken away before
        // its owners have travelled far in it.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 128;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            m_TouristCarQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.ReadOnly<OwnedVehicle>(),
                ComponentType.Exclude<MovingAway>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null)
            {
                return;
            }

            int chance = math.clamp(settings.TouristCarChance, 0, 100);

            if (chance >= 100 || m_TouristCarQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();

            EntityTypeHandle entityHandle = GetEntityTypeHandle();
            BufferTypeHandle<OwnedVehicle> vehicleHandle = GetBufferTypeHandle<OwnedVehicle>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks = m_TouristCarQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    NativeArray<Entity> households = chunk.GetNativeArray(entityHandle);
                    BufferAccessor<OwnedVehicle> vehicles = chunk.GetBufferAccessor(ref vehicleHandle);

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (KeepsCar(households[i], chance))
                        {
                            continue;
                        }

                        DynamicBuffer<OwnedVehicle> owned = vehicles[i];

                        for (int v = 0; v < owned.Length; v++)
                        {
                            Entity vehicle = owned[v].m_Vehicle;

                            // Only take a car that is sitting still. Deleting one mid-journey would
                            // strand its passengers.
                            if (vehicle == Entity.Null
                                || !EntityManager.Exists(vehicle)
                                || !EntityManager.HasComponent<ParkedCar>(vehicle))
                            {
                                continue;
                            }

                            commandBuffer.AddComponent<Deleted>(vehicle);
                            CarsRemoved++;
                        }

                        // Drop the buffer so the pathfinder stops offering car routes. The game
                        // re-adds it if a household ever acquires another vehicle.
                        commandBuffer.RemoveComponent<OwnedVehicle>(households[i]);
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        /// <summary>
        /// Whether this household is one of the ones that keeps its car.
        ///
        /// Derived from the entity's own index and version, so the same household always gets the
        /// same answer. A fresh random draw each pass would look right for a moment and then decay:
        /// every household would eventually roll badly once, and a car taken is never returned.
        /// </summary>
        private static bool KeepsCar(Entity household, int chancePercent)
        {
            if (chancePercent <= 0)
            {
                return false;
            }

            uint hash = math.hash(new int2(household.Index, household.Version));

            return (int)(hash % 100u) < chancePercent;
        }
    }
}
