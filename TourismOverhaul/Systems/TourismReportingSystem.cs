using Game;
using Game.City;
using Game.Simulation;
using Unity.Entities;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Fix D — honest reporting.
    ///
    /// Vanilla defects (see docs/TOURISM-DIAGNOSIS.md, Findings 5 and 6):
    ///
    /// TourismSystem.cs:108 computes
    ///     m_AverageTourists = round(2 * probability * 100000 / 16)
    /// which is 12500 * probability. At probability 1.0 the panel advertises 12,500 tourists while
    /// GetTargetTourists will never allow more than 1,500 — roughly an eight-fold overstatement.
    ///
    /// CountHouseholdDataSystem.cs:481 only counts a tourist household when it holds a Target
    /// component, so every tourist currently between destinations is invisible to the reported
    /// count and to the spawn probability that reads it.
    ///
    /// This system overwrites those two fields on the city's Tourism component with values taken
    /// from TouristDemandSystem's direct census. It writes nothing else, so attractiveness and
    /// lodging remain exactly as the native TourismSystem computed them.
    /// </summary>
    public partial class TourismReportingSystem : GameSystemBase
    {
        private CitySystem m_CitySystem;
        private TouristDemandSystem m_DemandSystem;

        // Native TourismSystem only refreshes 8 times a day; 512 updates/day keeps the panel
        // responsive without meaningful cost, since the census is already computed elsewhere.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 512;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_CitySystem = World.GetOrCreateSystemManaged<CitySystem>();
            m_DemandSystem = World.GetOrCreateSystemManaged<TouristDemandSystem>();
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;
            if (settings == null || !settings.FixReporting)
            {
                return;
            }

            Entity city = m_CitySystem.City;
            if (city == Entity.Null || !EntityManager.HasComponent<Tourism>(city))
            {
                return;
            }

            int current = m_DemandSystem.CurrentTourists;
            int target = m_DemandSystem.TargetTourists;

            // TouristDemandSystem has not run yet this session.
            if (target <= 0)
            {
                return;
            }

            Tourism tourism = EntityManager.GetComponentData<Tourism>(city);

            if (tourism.m_CurrentTourists == current && tourism.m_AverageTourists == target)
            {
                return;
            }

            tourism.m_CurrentTourists = current;

            // "Average tourists" now means the population the city can actually sustain, which is
            // the number the player can meaningfully act on.
            tourism.m_AverageTourists = target;

            EntityManager.SetComponentData(city, tourism);
        }
    }
}
