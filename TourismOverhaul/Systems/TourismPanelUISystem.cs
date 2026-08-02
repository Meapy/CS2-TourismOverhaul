using Colossal.UI.Binding;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Tools;
using Game.UI;
using Unity.Collections;
using Unity.Entities;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Publishes tourism figures this mod computes to the UI layer, so the frontend module can
    /// add them to the native Tourism info view panel.
    ///
    /// The native panel (Game.UI.InGame.TourismInfoviewUISystem) publishes its rows as
    /// ValueBindings in the "tourismInfo" group — attractiveness, tourismRate, averageHotelPrice,
    /// weatherEffect. Its React component only renders those four, so new rows need a frontend
    /// module regardless; this system makes sure the data is there for it to read.
    ///
    /// Bindings are published under a separate group so nothing collides with the native ones.
    /// </summary>
    public partial class TourismPanelUISystem : UISystemBase
    {
        private const string kGroup = "tourismOverhaul";

        private ValueBinding<int> m_CitizensAway;
        private ValueBinding<int> m_CurrentTourists;
        private ValueBinding<int> m_TargetTourists;
        private ValueBinding<int> m_HotelRoomsFree;
        private ValueBinding<int> m_HotelRoomsTotal;

        private ValueBinding<int> m_ArrivalsRoad;
        private ValueBinding<int> m_ArrivalsTrain;
        private ValueBinding<int> m_ArrivalsAir;
        private ValueBinding<int> m_ArrivalsShip;

        private ValueBinding<int> m_SpentLodging;
        private ValueBinding<int> m_SpentGoods;
        private ValueBinding<int> m_SpentFares;
        private ValueBinding<int> m_SpentOther;

        private TouristSpendingLedgerSystem m_LedgerSystem;

        /// <summary>
        /// Whether the Tourism Finance view is the one currently open.
        ///
        /// Published from C# because the frontend cannot reliably discover it. The panel component
        /// receives only [closeHint, focusKey, onClose] and reads the active view from a binding
        /// internally; reaching that binding through the module registry failed twice, first for
        /// the wrong call signature and then with "activeInfoview is not defined". ToolSystem knows
        /// the answer directly, so ask it rather than continuing to guess at the UI plumbing.
        /// </summary>
        private ValueBinding<bool> m_FinanceViewActive;

        private Game.Tools.ToolSystem m_ToolSystem;

        private EntityQuery m_HotelQuery;

        private ResidentTravelSystem m_ResidentTravelSystem;
        private TouristDemandSystem m_DemandSystem;

        // 262144 frames per in-game day; 512 keeps the panel responsive without cost, since most
        // values here are already computed by another system.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 512;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_ResidentTravelSystem = World.GetOrCreateSystemManaged<ResidentTravelSystem>();
            m_DemandSystem = World.GetOrCreateSystemManaged<TouristDemandSystem>();

            // Mirrors TourismInfoviewUISystem.m_HotelQuery (:121) — lodging companies renting a
            // property, which is what LodgingProviderSystem maintains m_FreeRooms for.
            m_HotelQuery = GetEntityQuery(
                ComponentType.ReadOnly<LodgingProvider>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.ReadOnly<Renter>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            AddBinding(m_CitizensAway = new ValueBinding<int>(kGroup, "citizensAway", 0));
            AddBinding(m_CurrentTourists = new ValueBinding<int>(kGroup, "currentTourists", 0));
            AddBinding(m_TargetTourists = new ValueBinding<int>(kGroup, "targetTourists", 0));
            AddBinding(m_HotelRoomsFree = new ValueBinding<int>(kGroup, "hotelRoomsFree", 0));
            AddBinding(m_HotelRoomsTotal = new ValueBinding<int>(kGroup, "hotelRoomsTotal", 0));

            AddBinding(m_ArrivalsRoad = new ValueBinding<int>(kGroup, "arrivalsRoad", 0));
            AddBinding(m_ArrivalsTrain = new ValueBinding<int>(kGroup, "arrivalsTrain", 0));
            AddBinding(m_ArrivalsAir = new ValueBinding<int>(kGroup, "arrivalsAir", 0));
            AddBinding(m_ArrivalsShip = new ValueBinding<int>(kGroup, "arrivalsShip", 0));

            m_LedgerSystem = World.GetOrCreateSystemManaged<TouristSpendingLedgerSystem>();

            AddBinding(m_SpentLodging = new ValueBinding<int>(kGroup, "spentLodging", 0));
            AddBinding(m_SpentGoods = new ValueBinding<int>(kGroup, "spentGoods", 0));
            AddBinding(m_SpentFares = new ValueBinding<int>(kGroup, "spentFares", 0));
            AddBinding(m_SpentOther = new ValueBinding<int>(kGroup, "spentOther", 0));

            m_ToolSystem = World.GetOrCreateSystemManaged<Game.Tools.ToolSystem>();
            AddBinding(m_FinanceViewActive = new ValueBinding<bool>(kGroup, "financeViewActive", false));
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            m_CitizensAway.Update(m_ResidentTravelSystem != null ? m_ResidentTravelSystem.CitizensAway : 0);
            m_CurrentTourists.Update(m_DemandSystem != null ? m_DemandSystem.CurrentTourists : 0);
            m_TargetTourists.Update(m_DemandSystem != null ? m_DemandSystem.TargetTourists : 0);

            CountRooms(out int free, out int total);
            m_HotelRoomsFree.Update(free);
            m_HotelRoomsTotal.Update(total);

            // (road, train, air, ship), in citizens, over the last full calendar month.
            Unity.Mathematics.int4 arrivals = m_DemandSystem != null
                ? m_DemandSystem.MonthlyArrivalsByMode
                : default;

            m_ArrivalsRoad.Update(arrivals.x);
            m_ArrivalsTrain.Update(arrivals.y);
            m_ArrivalsAir.Update(arrivals.z);
            m_ArrivalsShip.Update(arrivals.w);

            // Clamped into int for the binding: a month's spending in a large city fits easily,
            // and the panel formats it as money rather than showing raw precision.
            // Matched by name rather than entity, so it survives the prefab being recreated on load.
            m_FinanceViewActive.Update(
                m_ToolSystem != null
                && m_ToolSystem.activeInfoview != null
                && m_ToolSystem.activeInfoview.name == "TourismOverhaul Finance");

            if (m_LedgerSystem != null)
            {
                m_SpentLodging.Update(Clamp(m_LedgerSystem.Lodging));
                m_SpentGoods.Update(Clamp(m_LedgerSystem.Goods));
                m_SpentFares.Update(Clamp(m_LedgerSystem.Fares));
                m_SpentOther.Update(Clamp(m_LedgerSystem.Other));
            }
        }

        private static int Clamp(long value)
        {
            return value > int.MaxValue ? int.MaxValue : (int)value;
        }

        /// <summary>
        /// Free and total hotel rooms across the city.
        ///
        /// Occupancy is the Renter buffer length, and LodgingProvider.m_FreeRooms is what the
        /// lodging system publishes as remaining capacity — the same field the pathfinder scores
        /// hotels by. Total is therefore occupied + free, which also picks up any room multiplier
        /// HotelCapacitySystem has applied without having to recompute it here.
        ///
        /// Note m_FreeRooms is refreshed per UpdateFrame shard rather than every tick, so this
        /// figure lags the true value slightly.
        /// </summary>
        private void CountRooms(out int free, out int total)
        {
            free = 0;
            total = 0;

            ComponentTypeHandle<LodgingProvider> providerHandle =
                GetComponentTypeHandle<LodgingProvider>(isReadOnly: true);
            BufferTypeHandle<Renter> renterHandle = GetBufferTypeHandle<Renter>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks = m_HotelQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    NativeArray<LodgingProvider> providers = chunk.GetNativeArray(ref providerHandle);
                    BufferAccessor<Renter> renters = chunk.GetBufferAccessor(ref renterHandle);

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        int freeRooms = providers[i].m_FreeRooms;
                        if (freeRooms < 0)
                        {
                            freeRooms = 0;
                        }

                        free += freeRooms;
                        total += freeRooms + renters[i].Length;
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
