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
