using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Tools;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
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
    /// should leave, and that is the native behaviour. So are new arrivals still inside their
    /// outside connection: they have never had a room, and their own hotel search is what checks
    /// that one can be reached from where they stand.
    /// </summary>
    public partial class TouristRebookSystem : GameSystemBase
    {
        private EntityQuery m_DisplacedQuery;
        private EntityQuery m_HotelQuery;

        private EndFrameBarrier m_EndFrameBarrier;

        // Both passes walk every tourist household in the city (58k in a 665k save) to find the few
        // without a room: 6-10 ms per update on the main thread, spiking to 40 ms. They now run as one
        // Burst job, with the same walk in the same order.
        private EntityStorageInfoLookup m_Entities;
        private ComponentLookup<LodgingProvider> m_LodgingProviders;
        private ComponentLookup<PropertyRenter> m_PropertyRenters;
        private ComponentLookup<CurrentBuilding> m_CurrentBuildings;
        private ComponentLookup<Game.Objects.OutsideConnection> m_OutsideConnections;
        private ComponentLookup<TouristHousehold> m_TouristHouseholds;
        private BufferLookup<Renter> m_Renters;

        /// <summary>Households the last job rehoused; added to <see cref="Rebooked"/> on the next update.</summary>
        private NativeReference<int> m_LastRebooked;
        private JobHandle m_LastJob;

        /// <summary>Households rehoused since load. For diagnostics.</summary>
        public int Rebooked { get; private set; }

        // 262144 frames per in-game day; 128 gives 2048 passes/day, comfortably ahead of
        // TouristLeaveSystem at 512 and well before the evening eviction check.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 128;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_Entities = GetEntityStorageInfoLookup();
            m_LodgingProviders = GetComponentLookup<LodgingProvider>(isReadOnly: false);
            m_PropertyRenters = GetComponentLookup<PropertyRenter>(isReadOnly: true);
            m_CurrentBuildings = GetComponentLookup<CurrentBuilding>(isReadOnly: true);
            m_OutsideConnections = GetComponentLookup<Game.Objects.OutsideConnection>(isReadOnly: true);
            m_TouristHouseholds = GetComponentLookup<TouristHousehold>(isReadOnly: false);
            m_Renters = GetBufferLookup<Renter>(isReadOnly: false);
            m_LastRebooked = new NativeReference<int>(Allocator.Persistent);
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            // Tourists with citizens but no room. Households already on their way out are left be,
            // and so are cruise parties.
            //
            // A cruise party's anchor is the harbour, and the game takes it away from each party
            // every 1024 frames (TouristHouseholdBehaviorSystem:74-92, because they are deliberately
            // not in the terminal's Renter list) until the shore-party sweep restores it, up to 64
            // frames later. A pass landing in that window booked the party into a real hotel — a
            // room taken and a renter entry added — which the sweep then cancelled without giving
            // either back. A cruise passenger sleeps on the ship and must never take a room.
            m_DisplacedQuery = GetEntityQuery(
                ComponentType.ReadWrite<TouristHousehold>(),
                ComponentType.ReadOnly<HouseholdCitizen>(),
                ComponentType.Exclude<Components.CruisePassenger>(),
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

        protected override void OnDestroy()
        {
            m_LastJob.Complete();
            m_LastRebooked.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            // Scheduled 128 frames ago; this only collects its count.
            m_LastJob.Complete();
            Rebooked += m_LastRebooked.Value;
            m_LastRebooked.Value = 0;

            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null || !settings.RebookDisplacedTourists)
            {
                return;
            }

            if (m_DisplacedQuery.IsEmptyIgnoreFilter || m_HotelQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            m_Entities.Update(this);
            m_LodgingProviders.Update(this);
            m_PropertyRenters.Update(this);
            m_CurrentBuildings.Update(this);
            m_OutsideConnections.Update(this);
            m_TouristHouseholds.Update(this);
            m_Renters.Update(this);

            NativeList<ArchetypeChunk> touristChunks =
                m_DisplacedQuery.ToArchetypeChunkListAsync(Allocator.TempJob, out JobHandle touristsReady);
            NativeList<ArchetypeChunk> hotelChunks =
                m_HotelQuery.ToArchetypeChunkListAsync(Allocator.TempJob, out JobHandle hotelsReady);

            JobHandle job = new RebookJob
            {
                m_TouristChunks = touristChunks,
                m_HotelChunks = hotelChunks,
                m_EntityType = GetEntityTypeHandle(),
                m_CitizenType = GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true),
                m_Entities = m_Entities,
                m_LodgingProviders = m_LodgingProviders,
                m_PropertyRenters = m_PropertyRenters,
                m_CurrentBuildings = m_CurrentBuildings,
                m_OutsideConnections = m_OutsideConnections,
                m_TouristHouseholds = m_TouristHouseholds,
                m_Renters = m_Renters,
                m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer(),
                m_Limit = settings.MaxRebookingsPerUpdate,
                m_Rebooked = m_LastRebooked,
            }.Schedule(JobHandle.CombineDependencies(Dependency, touristsReady, hotelsReady));

            touristChunks.Dispose(job);
            hotelChunks.Dispose(job);
            m_EndFrameBarrier.AddJobHandleForProducer(job);
            m_LastJob = job;
            Dependency = job;
        }

        [BurstCompile]
        private struct RebookJob : IJob
        {
            [ReadOnly] public NativeList<ArchetypeChunk> m_TouristChunks;
            [ReadOnly] public NativeList<ArchetypeChunk> m_HotelChunks;
            [ReadOnly] public EntityTypeHandle m_EntityType;
            [ReadOnly] public BufferTypeHandle<HouseholdCitizen> m_CitizenType;
            [ReadOnly] public EntityStorageInfoLookup m_Entities;
            [ReadOnly] public ComponentLookup<PropertyRenter> m_PropertyRenters;
            [ReadOnly] public ComponentLookup<CurrentBuilding> m_CurrentBuildings;
            [ReadOnly] public ComponentLookup<Game.Objects.OutsideConnection> m_OutsideConnections;
            public ComponentLookup<LodgingProvider> m_LodgingProviders;
            public ComponentLookup<TouristHousehold> m_TouristHouseholds;
            public BufferLookup<Renter> m_Renters;
            public EntityCommandBuffer m_CommandBuffer;
            public int m_Limit;
            public NativeReference<int> m_Rebooked;

            public void Execute()
            {
                NativeList<Entity> displaced = CollectDisplaced();

                if (displaced.Length > 0)
                {
                    Rehouse(displaced);
                }

                displaced.Dispose();
            }

            /// <summary>Tourist households holding no room, capped so a mass closure is spread out.</summary>
            private NativeList<Entity> CollectDisplaced()
            {
                NativeList<Entity> displaced = new NativeList<Entity>(64, Allocator.Temp);

                for (int c = 0; c < m_TouristChunks.Length && displaced.Length < m_Limit; c++)
                {
                    ArchetypeChunk chunk = m_TouristChunks[c];
                    NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                    BufferAccessor<HouseholdCitizen> citizens = chunk.GetBufferAccessor(ref m_CitizenType);

                    for (int i = 0; i < chunk.Count && displaced.Length < m_Limit; i++)
                    {
                        Entity hotel = m_TouristHouseholds[entities[i]].m_Hotel;

                        // A household still holding a live hotel is fine.
                        if (hotel != Entity.Null && m_Entities.Exists(hotel) && m_LodgingProviders.HasComponent(hotel))
                        {
                            continue;
                        }

                        if (citizens[i].Length == 0)
                        {
                            continue;
                        }

                        // Not displaced: still arriving. A party whose hotel closed is somewhere in
                        // the city; a party still standing inside an outside connection has never
                        // had a room, and is waiting on its own hotel search. That search is the
                        // one that knows whether a room can be reached from where they are —
                        // booking them here skipped it, so a visitor set down at an air connection
                        // with no airline got a room it could never walk to and sat at the map edge
                        // with it until the stranded sweep sent it home hours later. It also put
                        // every road arrival in whichever hotel came first rather than one near
                        // where it came in.
                        if (StillArriving(citizens[i]))
                        {
                            continue;
                        }

                        displaced.Add(entities[i]);
                    }
                }

                return displaced;
            }

            /// <summary>Whether every citizen of the party is inside an outside connection.</summary>
            private bool StillArriving(DynamicBuffer<HouseholdCitizen> citizens)
            {
                for (int i = 0; i < citizens.Length; i++)
                {
                    if (!m_CurrentBuildings.TryGetComponent(citizens[i].m_Citizen, out CurrentBuilding building)
                        || !m_OutsideConnections.HasComponent(building.m_CurrentBuilding))
                    {
                        return false;
                    }
                }

                return true;
            }

            /// <summary>
            /// Books displaced households into hotels that still have space, filling each hotel's
            /// spare rooms before moving on. Mirrors HotelReserveJob (TouristFindTargetSystem.cs:182-192):
            /// take a free room, add the household to the renter list, point the household at the
            /// hotel, drop LodgingSeeker, and send them there.
            /// </summary>
            private void Rehouse(NativeList<Entity> displaced)
            {
                int next = 0;

                for (int c = 0; c < m_HotelChunks.Length && next < displaced.Length; c++)
                {
                    ArchetypeChunk chunk = m_HotelChunks[c];
                    NativeArray<Entity> hotels = chunk.GetNativeArray(m_EntityType);

                    for (int i = 0; i < chunk.Count && next < displaced.Length; i++)
                    {
                        Entity hotel = hotels[i];
                        LodgingProvider provider = m_LodgingProviders[hotel];

                        if (provider.m_FreeRooms <= 0 || !m_PropertyRenters.HasComponent(hotel))
                        {
                            continue;
                        }

                        Entity property = m_PropertyRenters[hotel].m_Property;
                        DynamicBuffer<Renter> renterList = m_Renters[hotel];

                        while (provider.m_FreeRooms > 0 && next < displaced.Length)
                        {
                            Entity household = displaced[next++];

                            if (!m_Entities.Exists(household) || !m_TouristHouseholds.HasComponent(household))
                            {
                                continue;
                            }

                            provider.m_FreeRooms--;
                            renterList.Add(new Renter { m_Renter = household });

                            TouristHousehold tourist = m_TouristHouseholds[household];
                            tourist.m_Hotel = hotel;

                            // Clear the stay timer so TouristStaySystem gives them a fresh visit rather
                            // than expiring them on the old hotel's schedule.
                            tourist.m_LeavingTime = 0u;
                            m_TouristHouseholds[household] = tourist;

                            m_CommandBuffer.RemoveComponent<LodgingSeeker>(household);

                            // Send them to the new hotel, as TouristFindTargetSystem would.
                            if (property != Entity.Null && m_Entities.Exists(property))
                            {
                                m_CommandBuffer.AddComponent(household, new Target(property));
                            }

                            m_Rebooked.Value++;
                        }

                        m_LodgingProviders[hotel] = provider;
                    }
                }
            }
        }
    }
}
