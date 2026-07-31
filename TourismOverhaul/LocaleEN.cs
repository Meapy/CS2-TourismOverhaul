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
                    m_Setting.GetOptionGroupLocaleID(TourismOverhaulSetting.GroupBudget),
                    "Tourist spending"
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
                {
                    m_Setting.GetOptionGroupLocaleID(TourismOverhaulSetting.GroupHotels),
                    "Hotels"
                },
                {
                    m_Setting.GetOptionGroupLocaleID(TourismOverhaulSetting.GroupOutbound),
                    "Residents travelling"
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
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.LoadAwareArrivals)),
                    "Reroute around congestion"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.LoadAwareArrivals)),
                    "The base game has no capacity concept for outside connections — a saturated " +
                    "airport keeps receiving its full share, and arrivals that cannot travel " +
                    "onward from it are deleted rather than rerouted.\n\n" +
                    "This measures how many tourists are still waiting at each connection and " +
                    "shifts arrivals toward routes that are moving."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.ArrivalBacklogSensitivity)),
                    "Congestion sensitivity"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.ArrivalBacklogSensitivity)),
                    "Tourists waiting at a connection before that route's share is halved. Lower " +
                    "values reroute sooner; higher values tolerate busier entrances. Counted per " +
                    "connection, so three airports absorb three times as much before air is throttled."
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

                // ---- E
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.FixTouristBudget)),
                    "Give tourists a realistic budget"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.FixTouristBudget)),
                    "Hotels charge 100 x the market price of Lodging per night, often well over " +
                    "1000, while the base game gives each tourist household a one-off wallet of " +
                    "0-2000 that never grows. Most arrivals therefore cannot afford a single " +
                    "night and are thrown out the same day for having no money.\n\n" +
                    "This sizes arrival wallets against the actual nightly rate, so tourists can " +
                    "pay for the stay they came for. It is usually the single change that lets " +
                    "tourist numbers climb instead of churning."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.TouristBudgetDays)),
                    "Nights a tourist can afford"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.TouristBudgetDays)),
                    "How many nights of hotel cost each arriving household carries. Never drops " +
                    "below the average stay, since a tourist who runs out of money leaves early " +
                    "regardless of their planned stay."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.TouristDailySpending)),
                    "Daily spending money"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.TouristDailySpending)),
                    "Money per day on top of the hotel bill, for shopping, leisure venues and " +
                    "public transport fares — tourists pay real ticket prices from the same " +
                    "wallet.\n\n" +
                    "A reserve is always added on top, because a household that drops below " +
                    "1000 stops shopping for the rest of its visit while still occupying a room. " +
                    "Raise this if you want tourism to show up in your commercial income."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.FixHotelDemand)),
                    "Build hotels sooner"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.FixHotelDemand)),
                    "The base game only raises hotel demand once tourists outnumber rooms two to " +
                    "one, so lodging is permanently behind. This raises the threshold, letting " +
                    "commercial zoning produce hotels before rooms run out."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HotelRoomsPerTourist)),
                    "Rooms wanted per tourist"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HotelRoomsPerTourist)),
                    "How many hotel rooms the city considers necessary per tourist. The base game " +
                    "uses 0.5. Above 1.0 keeps spare capacity so arrivals always find a room."
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

                // ---- outbound
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.EnableResidentHolidays)),
                    "Residents take holidays"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.EnableResidentHolidays)),
                    "The base game suppresses out-of-town travel to roughly 1-3% of leisure trips, " +
                    "and blocks it entirely for households with no spare money. This sends more " +
                    "households away on holiday, using the game's own travel behaviour — they " +
                    "head to a real outside connection and come home afterwards."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HolidayTripsPerThousandHouseholds)),
                    "Holiday departures"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HolidayTripsPerThousandHouseholds)),
                    "Extra departures per 1,000 eligible households per day, on top of whatever the " +
                    "base game already sends. Set to 0 for vanilla behaviour.\n\n" +
                    "How many are away at once depends on this rate and on how long each trip " +
                    "lasts, which the base game controls — so watch \"Local cims away\" in the " +
                    "Tourism info view and adjust until it looks right for your city."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HolidayMinimumSavings)),
                    "Minimum savings to travel"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HolidayMinimumSavings)),
                    "Households with less money than this are never sent away, keeping the base " +
                    "game's principle that holidays are for those who can afford them."
                },

                // ---- display
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HighlightTourists)),
                    "Mark tourists"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HighlightTourists)),
                    "Draws a coloured ring on the ground under each tourist so they can be told " +
                    "apart from residents at a glance. The ring uses the game's own overlay " +
                    "renderer, so its colour is fully configurable, unlike the built-in hover " +
                    "outline whose colour is shared across the whole game."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HighlightTouristVehicles)),
                    "Mark tourist vehicles"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HighlightTouristVehicles)),
                    "Also mark the vehicle a tourist is riding. A bus or train carrying a single " +
                    "tourist is marked as a whole, since the ring applies to the vehicle."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.MarkerSize)),
                    "Marker size"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.MarkerSize)),
                    "Diameter of the ring, in metres. Larger rings are easier to spot when zoomed out."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.MarkerRed)),
                    "Marker colour: red"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.MarkerRed)),
                    "Red channel, 0 to 255."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.MarkerGreen)),
                    "Marker colour: green"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.MarkerGreen)),
                    "Green channel, 0 to 255."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.MarkerBlue)),
                    "Marker colour: blue"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.MarkerBlue)),
                    "Blue channel, 0 to 255."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.MarkerOpacity)),
                    "Marker opacity"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.MarkerOpacity)),
                    "How solid the ring is drawn. The fill inside the ring is always fainter than " +
                    "the outline."
                },

                // ---- hotels
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.EnableHotelCapacity)),
                    "Increase hotel rooms"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.EnableHotelCapacity)),
                    "Multiplies the number of rooms in zoned commercial hotels. Signature buildings " +
                    "are left at their authored capacity.\n\n" +
                    "This replaces the game's lodging system with a copy that applies the " +
                    "multiplier, because room count is computed inside a compiled job from values " +
                    "that also control rent and workplaces. Turning this off restores the original " +
                    "system immediately."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HotelRoomMultiplier)),
                    "Hotel room multiplier"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HotelRoomMultiplier)),
                    "Rooms per hotel, as a multiple of the normal count. Lowering this evicts guests " +
                    "who no longer fit, exactly as the game does when a hotel is downgraded."
                }
            };
        }

        public void Unload()
        {
        }
    }
}
