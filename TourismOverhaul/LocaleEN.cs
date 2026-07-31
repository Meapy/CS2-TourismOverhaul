using System.Collections.Generic;
using Colossal;

namespace TourismOverhaul
{
    public sealed class LocaleEN : IDictionarySource
    {
        private readonly TourismOverhaulSetting m_Setting;

        public LocaleEN(TourismOverhaulSetting setting)
        {
            m_Setting = setting;
        }

        public IEnumerable<KeyValuePair<string, string>> ReadEntries(
            IList<IDictionaryEntryError> errors,
            Dictionary<string, int> indexCounts)
        {
            return new Dictionary<string, string>
            {
                { m_Setting.GetSettingsLocaleID(), "Tourism Overhaul" },
                { m_Setting.GetOptionTabLocaleID(TourismOverhaulSetting.SectionMain), "Main" },

                {
                    m_Setting.GetOptionGroupLocaleID(TourismOverhaulSetting.GroupRouting),
                    "Arrivals"
                },
                {
                    m_Setting.GetOptionGroupLocaleID(TourismOverhaulSetting.GroupDemand),
                    "Tourist demand"
                },
                {
                    m_Setting.GetOptionGroupLocaleID(TourismOverhaulSetting.GroupStay),
                    "Length of stay"
                },
                {
                    m_Setting.GetOptionGroupLocaleID(TourismOverhaulSetting.GroupReporting),
                    "Reporting"
                },
                {
                    m_Setting.GetOptionGroupLocaleID(TourismOverhaulSetting.GroupDisplay),
                    "Display"
                },

                // ---- A
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.FixArrivalRouting)),
                    "Fix arrival routing"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.FixArrivalRouting)),
                    "The base game sends 50% of tourists to air connections and 30% to sea connections. " +
                    "If your city has no airport or harbour those arrivals are discarded instead of " +
                    "being rerouted, so most tourists never appear. This redistributes the share across " +
                    "the connection types your city actually has."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.PreferAirAndSea)),
                    "Prefer air and sea"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.PreferAirAndSea)),
                    "Keep the base game's preference for planes and ferries when those connections exist. " +
                    "Turn off to spread arrivals evenly across every available connection type."
                },

                // ---- B
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.FixTouristDemand)),
                    "Scale tourism with city size"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.FixTouristDemand)),
                    "The base game targets attractiveness x 15 tourists, which caps at 1500 because " +
                    "attractiveness itself caps at 100. That is under 1% of a large city. This tops " +
                    "arrivals up toward a target that scales with population."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.TouristsPerThousandCitizens)),
                    "Tourists per 1000 citizens"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.TouristsPerThousandCitizens)),
                    "Tourist citizens supported per 1000 residents at full attractiveness. " +
                    "Scales down proportionally as attractiveness drops."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.MaximumTourists)),
                    "Maximum tourists"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.MaximumTourists)),
                    "Hard ceiling on tourist citizens, regardless of population and attractiveness."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.MaxArrivalsPerUpdate)),
                    "Maximum arrivals per update"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.MaxArrivalsPerUpdate)),
                    "Caps how fast a shortfall is filled. Lower values give smoother arrivals and less " +
                    "pathfinding work per tick."
                },

                // ---- C
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.FixLengthOfStay)),
                    "Enable length of stay"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.FixLengthOfStay)),
                    "The base game never reads its own tourist departure timer, so hotel guests stay " +
                    "until they run out of money. This gives them a real, varied stay length."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.AverageStayDays)),
                    "Average stay (days)"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.AverageStayDays)),
                    "Average nights a tourist with a hotel room stays. Actual stays vary around this."
                },

                // ---- D
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.FixReporting)),
                    "Fix reported numbers"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.FixReporting)),
                    "The base game's tourism panel shows a figure around eight times larger than the " +
                    "number of tourists it will ever produce, and undercounts tourists who are between " +
                    "destinations. This reports the real values."
                },

                // ---- display
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HighlightTourists)),
                    "Outline tourists"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HighlightTourists)),
                    "Draws the game's standard selection outline around tourists in the city so they " +
                    "can be told apart from residents at a glance."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HighlightTouristVehicles)),
                    "Outline tourist vehicles"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HighlightTouristVehicles)),
                    "Also outline the vehicle a tourist is riding. A bus or train carrying a single " +
                    "tourist is outlined as a whole, since the outline applies to the vehicle."
                }
            };
        }

        public void Unload()
        {
        }
    }
}
