using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Puts a floor under the "lack of resources" efficiency penalty for hotels.
    ///
    /// ProcessingCompanySystem treats an empty larder as all or nothing (:198):
    ///
    ///     BuildingUtils.SetEfficiencyFactor(bufferData, EfficiencyFactor.LackResources,
    ///                                       (num != 0) ? 1 : 0);
    ///
    /// A zero multiplier drops the building to 0% efficiency, so a hotel that runs out of food
    /// stops serving guests entirely, earns nothing, and folds — while still paying its staff. It
    /// cannot trade its way back out, because it needs income to buy the supplies that would
    /// restore it. A hotel with no food in the kitchen ought to be a hotel with unhappy guests, not
    /// a hotel that ceases to exist.
    ///
    /// This raises that one factor to a configurable floor for lodging companies only, leaving
    /// every other efficiency factor and every other kind of business untouched. The value is
    /// rewritten each pass because ProcessingCompanySystem resets it whenever it runs.
    /// </summary>
    public partial class HotelEfficiencyFloorSystem : GameSystemBase
    {
        private EntityQuery m_HotelQuery;

        /// <summary>Hotels currently held above the floor. For diagnostics.</summary>
        public int HotelsSupported { get; private set; }

        /// <summary>
        /// Matches ProcessingCompanySystem exactly: 262144 / (kCompanyUpdatesPerDay * 16), which is
        /// 262144 / (256 * 16) = 64 frames.
        ///
        /// This has to be the same interval, not merely a frequent one. Running every 128 frames
        /// corrected only every second write, so the factor sat at 0 half the time and the panel
        /// flickered between -100% and -50%. Registered with UpdateAfter so the correction lands in
        /// the same frame the zero is written, rather than a frame later.
        /// </summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_HotelQuery = GetEntityQuery(
                ComponentType.ReadOnly<LodgingProvider>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null || !settings.EnableHotelEfficiencyFloor)
            {
                HotelsSupported = 0;
                return;
            }

            if (m_HotelQuery.IsEmptyIgnoreFilter)
            {
                HotelsSupported = 0;
                return;
            }

            // A setting of 50 means "worst case 50% efficiency", i.e. a -50% penalty rather than
            // the -100% the base game applies.
            float floor = math.clamp(settings.HotelEfficiencyFloor, 0, 100) / 100f;
            int supported = 0;

            NativeArray<Entity> hotels = m_HotelQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < hotels.Length; i++)
                {
                    Entity property = EntityManager.GetComponentData<PropertyRenter>(hotels[i]).m_Property;

                    if (property == Entity.Null
                        || !EntityManager.Exists(property)
                        || !EntityManager.HasBuffer<Efficiency>(property))
                    {
                        continue;
                    }

                    DynamicBuffer<Efficiency> efficiencies = EntityManager.GetBuffer<Efficiency>(property);

                    for (int e = 0; e < efficiencies.Length; e++)
                    {
                        if (efficiencies[e].m_Factor != EfficiencyFactor.LackResources)
                        {
                            continue;
                        }

                        if (efficiencies[e].m_Efficiency < floor)
                        {
                            BuildingUtils.SetEfficiencyFactor(
                                efficiencies, EfficiencyFactor.LackResources, floor);
                            supported++;
                        }

                        break;
                    }
                }
            }
            finally
            {
                hotels.Dispose();
            }

            HotelsSupported = supported;
        }
    }
}
