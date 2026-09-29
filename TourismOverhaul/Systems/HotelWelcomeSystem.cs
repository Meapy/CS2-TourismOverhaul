using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using TourismOverhaul.Components;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Gives newly opened hotels an arrival surge.
    ///
    /// A hotel earns per guest but pays a full payroll from the day it opens, so one that opens
    /// into a city with no spare demand is insolvent before it ever fills. Simply making tourists
    /// prefer the new hotel does not help: the pathfinder already favours it heavily via the
    /// -10 per free room term (CitizenPathfindSetup.cs:72), and redirecting guests only moves the
    /// losses to whichever hotel they left.
    ///
    /// So this adds tourists rather than moving them. While a hotel is inside its opening period,
    /// its rooms contribute an extra allowance on top of the normal target, and
    /// <see cref="TouristDemandSystem"/> raises its arrival rate to deliver them promptly instead
    /// of trickling them in over days.
    ///
    /// The boost is temporary. Once it lapses, demand falls back to the ordinary population- and
    /// room-driven target, so a city cannot hold tourists it has no lasting draw for.
    /// </summary>
    public partial class HotelWelcomeSystem : GameSystemBase
    {
        /// <summary>Simulation frames in one in-game day.</summary>
        private const uint kFramesPerDay = 262144u;

        private EntityQuery m_HotelQuery;
        private EntityQuery m_UnseenHotelQuery;

        private SimulationSystem m_SimulationSystem;
        private EndFrameBarrier m_EndFrameBarrier;

        private bool m_NeedsBaseline = true;

        private ComponentLookup<PrefabRef> m_PrefabRefs;
        private ComponentLookup<IndustrialProcessData> m_ProcessData;
        private ComponentLookup<StorageLimitData> m_StorageLimits;
        private BufferLookup<Game.Economy.Resources> m_Resources;

        /// <summary>Written by the last update's BonusJob: rooms in opening hotels, and how many.</summary>
        private NativeArray<int> m_BonusCounts;
        private JobHandle m_LastJob;
        private bool m_BonusPending;

        /// <summary>Extra tourists currently allowed by hotels inside their opening period.</summary>
        public int WelcomeBonus { get; private set; }

        /// <summary>Hotels currently inside their opening period.</summary>
        public int OpeningHotels { get; private set; }

        // 262144 frames per in-game day; 512 gives 512 checks/day, so a new hotel is picked up
        // within seconds of opening.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 512;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            m_PrefabRefs = GetComponentLookup<PrefabRef>(isReadOnly: true);
            m_ProcessData = GetComponentLookup<IndustrialProcessData>(isReadOnly: true);
            m_StorageLimits = GetComponentLookup<StorageLimitData>(isReadOnly: true);
            m_Resources = GetBufferLookup<Game.Economy.Resources>(isReadOnly: false);
            m_BonusCounts = new NativeArray<int>(2, Allocator.Persistent);

            // LodgingProvider alone identifies a hotel company. PropertyRenter and Renter used to
            // be required as well, which quietly excluded hotels in signature buildings: those are
            // placed by the player rather than zoned, so the company does not occupy the building
            // on the same terms and need not carry PropertyRenter. The effect was that a signature
            // hotel opened with an empty larder while every zoned hotel opened stocked.
            //
            // Nothing downstream needs either component — stocking reads PrefabRef and the
            // Resources buffer, both of which any company has — so the narrower query bought
            // nothing and cost the assets most likely to be a city's flagship hotel.
            // CruiseTerminalLodging is excluded from both queries. CruiseVoyageSystem gives a
            // cruise terminal a stand-in LodgingProvider so its passengers count as lodged for
            // TouristLeaveSystem:68, and because bare LodgingProvider is what identifies a hotel
            // here, that harbour otherwise reads as a hotel that has just opened — gets a welcome
            // boost, gets stocked with lodging it cannot sell, and then throws in the bonus count
            // because a harbour has no Renter buffer.
            m_HotelQuery = GetEntityQuery(
                ComponentType.ReadOnly<LodgingProvider>(),
                ComponentType.ReadOnly<HotelWelcome>(),
                ComponentType.Exclude<Components.CruiseTerminalLodging>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_UnseenHotelQuery = GetEntityQuery(
                ComponentType.ReadOnly<LodgingProvider>(),
                ComponentType.Exclude<HotelWelcome>(),
                ComponentType.Exclude<Components.CruiseTerminalLodging>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnDestroy()
        {
            m_LastJob.Complete();
            m_BonusCounts.Dispose();
            base.OnDestroy();
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // Hotels that already exist in a loaded city are not new arrivals.
            m_NeedsBaseline = true;
            m_BonusPending = false;
            WelcomeBonus = 0;
            OpeningHotels = 0;
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null || !settings.EnableHotelWelcome)
            {
                WelcomeBonus = 0;
                OpeningHotels = 0;
                return;
            }

            // Scheduled 512 frames ago; this only collects its counts.
            m_LastJob.Complete();

            if (m_BonusPending)
            {
                m_BonusPending = false;
                OpeningHotels = m_BonusCounts[1];
                WelcomeBonus = (int)math.round(
                    m_BonusCounts[0] * (math.clamp(settings.HotelWelcomeBoost, 0, 200) / 100f));
            }

            uint frame = m_SimulationSystem.frameIndex;

            if (m_NeedsBaseline)
            {
                // Record every existing hotel as already seen, with no boost. Without this, the
                // first update after loading a city would treat its entire hotel stock as newly
                // opened and flood the city with tourists.
                MarkUnseenHotels(0u);
                m_NeedsBaseline = false;
                return;
            }

            uint duration = (uint)math.max(1, settings.HotelWelcomeDays) * kFramesPerDay;
            MarkUnseenHotels(frame + duration);

            if (m_HotelQuery.IsEmptyIgnoreFilter)
            {
                WelcomeBonus = 0;
                OpeningHotels = 0;
                return;
            }

            m_BonusCounts[0] = 0;
            m_BonusCounts[1] = 0;

            JobHandle job = ScheduleBonusCount(frame, Dependency);
            job = ScheduleStocking(settings, frame, job);

            m_BonusPending = true;
            m_LastJob = job;
            Dependency = job;
        }

        /// <summary>
        /// Keeps hotels stocked with their input resource while they are opening.
        ///
        /// A hotel converts an input — Food, for the ones in the base game — into Lodging. A new
        /// hotel starts with an empty larder and has to buy stock in through the ordinary supply
        /// chain, which takes deliveries and time. Until then it cannot serve guests, its
        /// efficiency collapses, and it can fold before its first delivery arrives. That failure
        /// looks like "not enough customers" but is really an empty pantry.
        ///
        /// This tops the larder up during the opening period only. It buys nothing and pays
        /// nothing — a deliberate grace, the trading equivalent of opening stock — and once the
        /// period lapses the hotel supplies itself through the normal buyer path like any other
        /// company.
        ///
        /// A Burst job, like the room count: writing a Resources buffer from the main thread first
        /// waits for every job that reads or writes any company's resources.
        /// </summary>
        private JobHandle ScheduleStocking(TourismOverhaulSetting settings, uint frame, JobHandle dependency)
        {
            int stock = math.max(0, settings.HotelWelcomeStock);

            if (stock == 0 || m_HotelQuery.IsEmptyIgnoreFilter)
            {
                return dependency;
            }

            m_PrefabRefs.Update(this);
            m_ProcessData.Update(this);
            m_StorageLimits.Update(this);
            m_Resources.Update(this);

            return new StockJob
            {
                m_EntityType = GetEntityTypeHandle(),
                m_WelcomeType = GetComponentTypeHandle<HotelWelcome>(isReadOnly: true),
                m_PrefabRefs = m_PrefabRefs,
                m_ProcessData = m_ProcessData,
                m_StorageLimits = m_StorageLimits,
                m_Resources = m_Resources,
                m_Frame = frame,
                m_Stock = stock,
            }.Schedule(m_HotelQuery, dependency);
        }

        [BurstCompile]
        private struct StockJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            [ReadOnly] public ComponentTypeHandle<HotelWelcome> m_WelcomeType;
            [ReadOnly] public ComponentLookup<PrefabRef> m_PrefabRefs;
            [ReadOnly] public ComponentLookup<IndustrialProcessData> m_ProcessData;
            [ReadOnly] public ComponentLookup<StorageLimitData> m_StorageLimits;
            public BufferLookup<Game.Economy.Resources> m_Resources;
            public uint m_Frame;
            public int m_Stock;

            public void Execute(
                in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<Entity> hotels = chunk.GetNativeArray(m_EntityType);
                NativeArray<HotelWelcome> welcomes = chunk.GetNativeArray(ref m_WelcomeType);

                for (int i = 0; i < chunk.Count; i++)
                {
                    Entity hotel = hotels[i];

                    if (welcomes[i].m_EndFrame <= m_Frame
                        || !m_PrefabRefs.TryGetComponent(hotel, out PrefabRef prefabRef)
                        || !m_Resources.HasBuffer(hotel)
                        || !m_ProcessData.TryGetComponent(prefabRef.m_Prefab, out IndustrialProcessData process))
                    {
                        continue;
                    }

                    // Whatever this hotel actually consumes, rather than assuming Food.
                    Resource input = process.m_Input1.m_Resource;

                    if (input == Resource.NoResource)
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Economy.Resources> resources = m_Resources[hotel];

                    // Scale the opening stock to the hotel's own larder. A flat 200 is a sensible
                    // start for a zoned hotel and a rounding error for a signature building with
                    // thousands of rooms, which burns through it long before its first delivery.
                    // Half a tank is enough to trade on without simply gifting a full one.
                    int limit = GetStorageLimit(hotel, prefabRef.m_Prefab);
                    int target = math.max(m_Stock, limit / 2);

                    if (limit > 0)
                    {
                        target = math.min(target, limit);
                    }

                    int held = EconomyUtils.GetResources(input, resources);

                    if (held < target)
                    {
                        EconomyUtils.AddResources(input, target - held, resources);
                    }
                }
            }

            /// <summary>
            /// The hotel's storage capacity, or 0 when it cannot be determined.
            ///
            /// StorageLimitData lives on the company, but is authored on the company prefab, so it
            /// may sit on either entity depending on how the company was created. Checking both is
            /// cheaper than assuming, and returning 0 simply falls back to the flat opening stock.
            /// </summary>
            private int GetStorageLimit(Entity company, Entity prefab)
            {
                if (m_StorageLimits.TryGetComponent(company, out StorageLimitData own))
                {
                    return own.m_Limit;
                }

                return m_StorageLimits.TryGetComponent(prefab, out StorageLimitData authored)
                    ? authored.m_Limit
                    : 0;
            }
        }

        /// <summary>
        /// Stamps hotels that have not been seen before. An end frame of 0 records the hotel
        /// without granting a boost.
        /// </summary>
        private void MarkUnseenHotels(uint endFrame)
        {
            if (m_UnseenHotelQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();

            NativeArray<Entity> hotels = m_UnseenHotelQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < hotels.Length; i++)
                {
                    commandBuffer.AddComponent(hotels[i], new HotelWelcome { m_EndFrame = endFrame });
                }

                if (endFrame > 0u && hotels.Length > 0)
                {
                    Mod.Log.Info($"{hotels.Length} hotel(s) opened; welcome boost running.");
                }
            }
            finally
            {
                hotels.Dispose();
            }
        }

        /// <summary>
        /// Sums the rooms of hotels still inside their opening period, into m_BonusCounts, for
        /// the next update to turn into an extra tourist allowance.
        ///
        /// Counted by a Burst job rather than on the main thread, where reading LodgingProvider and
        /// Renter waited for every job writing either (43 ms per update, up to 72 ms). The figure is
        /// therefore one update (512 frames) old when it is used, well inside an opening period of
        /// days.
        /// </summary>
        private JobHandle ScheduleBonusCount(uint frame, JobHandle dependency)
        {
            return new BonusJob
            {
                m_WelcomeType = GetComponentTypeHandle<HotelWelcome>(isReadOnly: true),
                m_ProviderType = GetComponentTypeHandle<LodgingProvider>(isReadOnly: true),
                m_RenterType = GetBufferTypeHandle<Renter>(isReadOnly: true),
                m_Frame = frame,
                m_Counts = m_BonusCounts,
            }.Schedule(m_HotelQuery, dependency);
        }

        [BurstCompile]
        private struct BonusJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<HotelWelcome> m_WelcomeType;
            [ReadOnly] public ComponentTypeHandle<LodgingProvider> m_ProviderType;
            [ReadOnly] public BufferTypeHandle<Renter> m_RenterType;
            public uint m_Frame;

            /// <summary>[0] rooms in opening hotels, [1] opening hotels.</summary>
            public NativeArray<int> m_Counts;

            public void Execute(
                in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<HotelWelcome> welcomes = chunk.GetNativeArray(ref m_WelcomeType);
                NativeArray<LodgingProvider> providers = chunk.GetNativeArray(ref m_ProviderType);

                // Renter is not part of the query, so a chunk need not have the buffer: a signature
                // hotel is placed rather than zoned, so it can carry LodgingProvider without the
                // renting components.
                bool hasRenters = chunk.Has(ref m_RenterType);
                BufferAccessor<Renter> renters = hasRenters ? chunk.GetBufferAccessor(ref m_RenterType) : default;

                for (int i = 0; i < chunk.Count; i++)
                {
                    if (welcomes[i].m_EndFrame <= m_Frame)
                    {
                        continue;
                    }

                    m_Counts[0] += math.max(0, providers[i].m_FreeRooms) + (hasRenters ? renters[i].Length : 0);
                    m_Counts[1]++;
                }
            }
        }
    }
}
