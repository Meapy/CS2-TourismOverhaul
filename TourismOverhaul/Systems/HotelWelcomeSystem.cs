using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using TourismOverhaul.Components;
using Unity.Collections;
using Unity.Entities;
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
            // boost, gets stocked with lodging it cannot sell, and then throws in UpdateBonus
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

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // Hotels that already exist in a loaded city are not new arrivals.
            m_NeedsBaseline = true;
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

            UpdateBonus(settings, frame);
            StockOpeningHotels(settings, frame);
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
        /// </summary>
        private void StockOpeningHotels(TourismOverhaulSetting settings, uint frame)
        {
            int stock = math.max(0, settings.HotelWelcomeStock);

            if (stock == 0 || m_HotelQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> hotels = m_HotelQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < hotels.Length; i++)
                {
                    Entity hotel = hotels[i];

                    if (EntityManager.GetComponentData<HotelWelcome>(hotel).m_EndFrame <= frame)
                    {
                        continue;
                    }

                    if (!EntityManager.HasComponent<PrefabRef>(hotel)
                        || !EntityManager.HasBuffer<Game.Economy.Resources>(hotel))
                    {
                        continue;
                    }

                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(hotel).m_Prefab;

                    if (!EntityManager.HasComponent<IndustrialProcessData>(prefab))
                    {
                        continue;
                    }

                    // Whatever this hotel actually consumes, rather than assuming Food.
                    Resource input = EntityManager.GetComponentData<IndustrialProcessData>(prefab)
                        .m_Input1.m_Resource;

                    if (input == Resource.NoResource)
                    {
                        continue;
                    }

                    DynamicBuffer<Game.Economy.Resources> resources =
                        EntityManager.GetBuffer<Game.Economy.Resources>(hotel);

                    // Scale the opening stock to the hotel's own larder. A flat 200 is a sensible
                    // start for a zoned hotel and a rounding error for a signature building with
                    // thousands of rooms, which burns through it long before its first delivery.
                    // Half a tank is enough to trade on without simply gifting a full one.
                    int limit = GetStorageLimit(hotel, prefab);
                    int target = math.max(stock, limit / 2);

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
            finally
            {
                hotels.Dispose();
            }
        }

        /// <summary>
        /// The hotel's storage capacity, or 0 when it cannot be determined.
        ///
        /// StorageLimitData lives on the company, but is authored on the company prefab, so it may
        /// sit on either entity depending on how the company was created. Checking both is cheaper
        /// than assuming, and returning 0 simply falls back to the flat opening stock.
        /// </summary>
        private int GetStorageLimit(Entity company, Entity prefab)
        {
            if (EntityManager.HasComponent<StorageLimitData>(company))
            {
                return EntityManager.GetComponentData<StorageLimitData>(company).m_Limit;
            }

            if (EntityManager.HasComponent<StorageLimitData>(prefab))
            {
                return EntityManager.GetComponentData<StorageLimitData>(prefab).m_Limit;
            }

            return 0;
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
        /// Sums the rooms of hotels still inside their opening period and converts them into an
        /// extra tourist allowance.
        /// </summary>
        private void UpdateBonus(TourismOverhaulSetting settings, uint frame)
        {
            int bonusRooms = 0;
            int opening = 0;

            ComponentTypeHandle<HotelWelcome> welcomeHandle =
                GetComponentTypeHandle<HotelWelcome>(isReadOnly: true);
            ComponentTypeHandle<LodgingProvider> providerHandle =
                GetComponentTypeHandle<LodgingProvider>(isReadOnly: true);
            BufferTypeHandle<Renter> renterHandle = GetBufferTypeHandle<Renter>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks = m_HotelQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    NativeArray<HotelWelcome> welcomes = chunk.GetNativeArray(ref welcomeHandle);
                    NativeArray<LodgingProvider> providers = chunk.GetNativeArray(ref providerHandle);

                    // Renter is not part of the query, so a chunk need not have the buffer and
                    // GetBufferAccessor returns a default accessor that throws on indexing. This is
                    // the same guard CleanUpLeakedHouseholds uses, and it matters here for the very
                    // reason the query was widened above: a signature hotel is placed rather than
                    // zoned, so it can carry LodgingProvider without the renting components.
                    bool hasRenters = chunk.Has(ref renterHandle);
                    BufferAccessor<Renter> renters =
                        hasRenters ? chunk.GetBufferAccessor(ref renterHandle) : default;

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (welcomes[i].m_EndFrame <= frame)
                        {
                            continue;
                        }

                        bonusRooms += math.max(0, providers[i].m_FreeRooms)
                                      + (hasRenters ? renters[i].Length : 0);
                        opening++;
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            OpeningHotels = opening;
            WelcomeBonus = (int)math.round(
                bonusRooms * (math.clamp(settings.HotelWelcomeBoost, 0, 200) / 100f));
        }
    }
}
