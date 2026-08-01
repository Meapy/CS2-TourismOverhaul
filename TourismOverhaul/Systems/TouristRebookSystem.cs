using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Rehouses tourists whose hotel closed under them, instead of letting them be thrown out of
    /// the city.
    ///
    /// When a hotel company folds, TouristHouseholdBehaviorSystem clears m_Hotel and adds
    /// LodgingSeeker, and TouristFindTargetSystem does try to find them somewhere else. But
    /// TouristLeaveSystem evicts anyone still without a room once the day passes 80%
    /// (TouristLeaveSystem.cs:68), and a thousand households all rebooking at once do not get
    /// through the pathfinder in time. One bankruptcy therefore empties the city — observed as
    /// TouristNoHotel jumping from 7 to 1488 in a single snapshot, with the tourist count falling
    /// from 4112 to 1065.
    ///
    /// Rather than suppress the eviction, this rehouses displaced guests before it runs. The
    /// booking itself is exactly what the game's own HotelReserveJob does
    /// (TouristFindTargetSystem.cs:170-199): take a free room, add the household to the hotel's
    /// renter list, point the household at the hotel and drop LodgingSeeker.
    ///
    /// Tourists with nowhere to go are left alone — if the city genuinely has no free rooms they
    /// should leave, and that is the native behaviour.
    /// </summary>
    public partial class TouristRebookSystem : GameSystemBase
    {
        private EntityQuery m_DisplacedQuery;
        private EntityQuery m_HotelQuery;

        private EndFrameBarrier m_EndFrameBarrier;

        /// <summary>Households rehoused since load. For diagnostics.</summary>
        public int Rebooked { get; private set; }

        // 262144 frames per in-game day; 128 gives 2048 passes/day, comfortably ahead of
        // TouristLeaveSystem at 512 and well before the evening eviction check.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 128;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            // Tourists with citizens but no room. Households already on their way out are left be.
            m_DisplacedQuery = GetEntityQuery(
                ComponentType.ReadWrite<TouristHousehold>(),
                ComponentType.ReadOnly<HouseholdCitizen>(),
                ComponentType.Exclude<MovingAway>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_HotelQuery = GetEntityQuery(
                ComponentType.ReadWrite<LodgingProvider>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.ReadWrite<Renter>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null || !settings.RebookDisplacedTourists)
            {
                return;
            }

            if (m_DisplacedQuery.IsEmptyIgnoreFilter || m_HotelQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeList<Entity> displaced = CollectDisplaced(settings.MaxRebookingsPerUpdate);
            try
            {
                if (displaced.Length > 0)
                {
                    Rehouse(displaced);
                }
            }
            finally
            {
                displaced.Dispose();
            }
        }

        /// <summary>Tourist households holding no room, capped so a mass closure is spread out.</summary>
        private NativeList<Entity> CollectDisplaced(int limit)
        {
            NativeList<Entity> displaced = new NativeList<Entity>(64, Allocator.Temp);

            EntityTypeHandle entityHandle = GetEntityTypeHandle();
            ComponentTypeHandle<TouristHousehold> touristHandle =
                GetComponentTypeHandle<TouristHousehold>(isReadOnly: true);
            BufferTypeHandle<HouseholdCitizen> citizenHandle =
                GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks = m_DisplacedQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length && displaced.Length < limit; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    NativeArray<Entity> entities = chunk.GetNativeArray(entityHandle);
                    NativeArray<TouristHousehold> tourists = chunk.GetNativeArray(ref touristHandle);
                    BufferAccessor<HouseholdCitizen> citizens = chunk.GetBufferAccessor(ref citizenHandle);

                    for (int i = 0; i < chunk.Count && displaced.Length < limit; i++)
                    {
                        // A household still holding a live hotel is fine.
                        if (tourists[i].m_Hotel != Entity.Null
                            && EntityManager.Exists(tourists[i].m_Hotel)
                            && EntityManager.HasComponent<LodgingProvider>(tourists[i].m_Hotel))
                        {
                            continue;
                        }

                        if (citizens[i].Length == 0)
                        {
                            continue;
                        }

                        displaced.Add(entities[i]);
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            return displaced;
        }

        /// <summary>
        /// Books displaced households into hotels that still have space, largest first so guests
        /// spread across the city rather than piling into the first hotel found.
        /// </summary>
        private void Rehouse(NativeList<Entity> displaced)
        {
            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();

            ComponentTypeHandle<LodgingProvider> providerHandle =
                GetComponentTypeHandle<LodgingProvider>(isReadOnly: false);
            BufferTypeHandle<Renter> renterHandle = GetBufferTypeHandle<Renter>(isReadOnly: false);
            EntityTypeHandle entityHandle = GetEntityTypeHandle();

            int next = 0;

            NativeArray<ArchetypeChunk> chunks = m_HotelQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length && next < displaced.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    NativeArray<Entity> hotels = chunk.GetNativeArray(entityHandle);
                    NativeArray<LodgingProvider> providers = chunk.GetNativeArray(ref providerHandle);
                    BufferAccessor<Renter> renters = chunk.GetBufferAccessor(ref renterHandle);

                    for (int i = 0; i < chunk.Count && next < displaced.Length; i++)
                    {
                        LodgingProvider provider = providers[i];

                        if (provider.m_FreeRooms <= 0)
                        {
                            continue;
                        }

                        Entity hotel = hotels[i];

                        if (!EntityManager.HasComponent<PropertyRenter>(hotel))
                        {
                            continue;
                        }

                        Entity property = EntityManager.GetComponentData<PropertyRenter>(hotel).m_Property;

                        DynamicBuffer<Renter> renterList = renters[i];

                        // Fill this hotel's spare rooms before moving to the next.
                        while (provider.m_FreeRooms > 0 && next < displaced.Length)
                        {
                            Entity household = displaced[next++];

                            if (!EntityManager.Exists(household)
                                || !EntityManager.HasComponent<TouristHousehold>(household))
                            {
                                continue;
                            }

                            // Mirrors HotelReserveJob (TouristFindTargetSystem.cs:182-192).
                            provider.m_FreeRooms--;
                            renterList.Add(new Renter { m_Renter = household });

                            TouristHousehold tourist =
                                EntityManager.GetComponentData<TouristHousehold>(household);
                            tourist.m_Hotel = hotel;
                            // Clear the stay timer so TouristStaySystem gives them a fresh visit
                            // rather than expiring them on the old hotel's schedule.
                            tourist.m_LeavingTime = 0u;
                            EntityManager.SetComponentData(household, tourist);

                            commandBuffer.RemoveComponent<LodgingSeeker>(household);

                            // Send them to the new hotel, as TouristFindTargetSystem would.
                            if (property != Entity.Null && EntityManager.Exists(property))
                            {
                                commandBuffer.AddComponent(household, new Target(property));
                            }

                            Rebooked++;
                        }

                        providers[i] = provider;
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }
    }
}
