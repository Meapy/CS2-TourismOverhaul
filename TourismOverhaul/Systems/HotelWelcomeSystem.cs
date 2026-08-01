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

            m_HotelQuery = GetEntityQuery(
                ComponentType.ReadOnly<LodgingProvider>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.ReadOnly<Renter>(),
                ComponentType.ReadOnly<HotelWelcome>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_UnseenHotelQuery = GetEntityQuery(
                ComponentType.ReadOnly<LodgingProvider>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.Exclude<HotelWelcome>(),
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

                    int held = EconomyUtils.GetResources(input, resources);

                    if (held < stock)
                    {
                        EconomyUtils.AddResources(input, stock - held, resources);
                    }
                }
            }
            finally
            {
                hotels.Dispose();
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
                    BufferAccessor<Renter> renters = chunk.GetBufferAccessor(ref renterHandle);

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        if (welcomes[i].m_EndFrame <= frame)
                        {
                            continue;
                        }

                        bonusRooms += math.max(0, providers[i].m_FreeRooms) + renters[i].Length;
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
