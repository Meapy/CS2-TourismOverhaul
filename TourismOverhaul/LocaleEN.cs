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
                // Zone prefabs are named through the asset keys, not the settings ones. Without
                // these the zones appear in the zoning menu as unlabelled tiles.
                { "Assets.NAME[TourismOverhaul Finance]", "Tourism Finance" },
                {
                    "Assets.DESCRIPTION[TourismOverhaul Finance]",
                    "Where visitors are spending their money: hotels, shops, transport fares and " +
                    "everything else."
                },
                { "Infoviews.INFOVIEW[TourismOverhaul Finance]", "Tourism Finance" },

                { "Assets.NAME[TourismOverhaul Hotels]", "Hotels" },
                {
                    "Assets.DESCRIPTION[TourismOverhaul Hotels]",
                    "A commercial zone for hotels. Only hotels are built here, and hotels are no " +
                    "longer built in ordinary commercial zones."
                },
                { "Assets.NAME[TourismOverhaul Motels]", "Motels" },
                {
                    "Assets.DESCRIPTION[TourismOverhaul Motels]",
                    "A commercial zone for motels. Smaller and cheaper than hotels, and well suited " +
                    "to roadside plots near your outside connections."
                },

                // Row labels for the Tourism and Tourism Finance info-view panels. The frontend
                // reads these through the game's own translate() with the English text as its
                // fallback, so a locale that omits one still renders rather than showing the key.
                { "TourismOverhaul.PANEL[TouristsInCity]", "Tourists in city" },
                { "TourismOverhaul.PANEL[HotelRoomsFree]", "Hotel rooms free" },
                { "TourismOverhaul.PANEL[Occupancy]", "Occupancy" },
                { "TourismOverhaul.PANEL[LocalCimsAway]", "Local cims away" },
                { "TourismOverhaul.PANEL[ArrivalsByMode]", "Arrivals by mode" },
                { "TourismOverhaul.PANEL[Road]", "Road" },
                { "TourismOverhaul.PANEL[Train]", "Train" },
                { "TourismOverhaul.PANEL[Plane]", "Plane" },
                { "TourismOverhaul.PANEL[Sea]", "Sea" },
                { "TourismOverhaul.PANEL[TouristSpending]", "Tourist spending" },
                { "TourismOverhaul.PANEL[Hotels]", "Hotels" },
                { "TourismOverhaul.PANEL[Shops]", "Shops" },
                { "TourismOverhaul.PANEL[Fares]", "Fares" },
                { "TourismOverhaul.PANEL[Leisure]", "Leisure" },
                { "TourismOverhaul.PANEL[Unattributed]", "Unattributed" },

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
                    "Use the arrival mix below to decide how tourists reach the city. Turn off to " +
                    "spread arrivals evenly across every connection type you have."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.ArrivalWeightRoad)),
                    "Arrivals by road"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.ArrivalWeightRoad)),
                    "Relative weight for road arrivals. The base game uses 10 against 10 rail, " +
                    "50 air and 30 sea.\n\n" +
                    "Note that road arrivals are the ones handed a car, so lowering this is the " +
                    "main way to reduce tourist traffic. It cannot remove it entirely: the game " +
                    "gives a car to anyone arriving at a connection carrying the road flag, and " +
                    "many connections serve road alongside rail or sea. Use \"Tourists arriving " +
                    "with a car\" for direct control."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.ArrivalWeightTrain)),
                    "Arrivals by rail"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.ArrivalWeightTrain)),
                    "Relative weight for rail arrivals."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.ArrivalWeightAir)),
                    "Arrivals by air"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.ArrivalWeightAir)),
                    "Relative weight for air arrivals."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.ArrivalWeightShip)),
                    "Arrivals by sea"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.ArrivalWeightShip)),
                    "Relative weight for sea arrivals.\n\n" +
                    "These four are relative to each other, not percentages — only the connection " +
                    "types your city actually has receive a share, and the rest is redistributed."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.TouristCarChance)),
                    "Tourists arriving with a car"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.TouristCarChance)),
                    "Share of tourist groups that bring a car.\n\n" +
                    "The base game hands one to every household arriving at a connection carrying " +
                    "the road flag — no dice roll, and including connections that also serve rail " +
                    "or sea, so a visitor who came by train still drives. 100% is that behaviour " +
                    "unchanged.\n\n" +
                    "Lower it and the rest travel by public transport, taxi and on foot. Each " +
                    "group's outcome is fixed, so a family keeps or loses its car for the whole " +
                    "visit rather than flickering."
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
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HotelRoomDemandOccupancy)),
                    "Hotel rooms attract tourists"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HotelRoomDemandOccupancy)),
                    "Occupancy the city aims to fill its hotel rooms to.\n\n" +
                    "Building rooms raises the tourist target, so opening a hotel draws new " +
                    "visitors instead of taking them from the hotel down the road — which is what " +
                    "stops new hotels folding before they fill. Still scaled by attractiveness, " +
                    "so rooms alone will not fill an unappealing city.\n\n" +
                    "Set to 0 to size tourist demand on population alone."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.EnableAttractionCrowding)),
                    "Crowded places lose their appeal"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.EnableAttractionCrowding)),
                    "Attractions become less appealing the busier they get, so visitors spread " +
                    "across your city instead of all piling into the same few places.\n\n" +
                    "The base game never notices how full somewhere already is, so the single most " +
                    "attractive park stays the first choice however packed it becomes.\n\n" +
                    "How quickly a place fills up depends on its size: a small square is crowded " +
                    "after a handful of visitors, while a large attraction absorbs many more before " +
                    "anyone looks elsewhere."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HistoricBuildingAttractiveness)),
                    "Historical building appeal"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HistoricBuildingAttractiveness)),
                    "How much each building you have marked as historical adds to city " +
                    "attractiveness.\n\n" +
                    "In the base game, ordinary buildings contribute nothing — attractiveness comes " +
                    "almost entirely from parks and signature landmarks. A beautifully preserved " +
                    "old quarter draws no visitors at all unless a landmark happens to sit inside " +
                    "it.\n\n" +
                    "Marking a building historical already says something about its character. Now " +
                    "it draws visitors too. Deliberately small per building: one old house should " +
                    "be worth very little, a whole preserved quarter worth travelling for. Set to " +
                    "0 to turn it off."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.TouristShoppingChance)),
                    "Shopping over sightseeing"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.TouristShoppingChance)),
                    "How often visitors head for the shops instead of a park or attraction.\n\n" +
                    "Tourists never shop in the base game. Citizens choose shopping over leisure " +
                    "whenever their household wants something, but that want comes from supplies " +
                    "running low at home — and a visitor has no home and no cupboard, so it never " +
                    "happens. They sightsee for the whole visit and their money never reaches your " +
                    "shops.\n\n" +
                    "Raise this and your commercial districts will start earning from tourism. " +
                    "Set to 0 for the base game's behaviour."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.MaxArrivalsPerUpdate)),
                    "Arrival speed"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.MaxArrivalsPerUpdate)),
                    "How many tourist groups can arrive at once. Higher fills a shortfall faster " +
                    "but means burstier arrivals and more routing work each tick.\n\n" +
                    "This only sets the pace, not the destination — the tourist target decides how " +
                    "many end up in your city. Raise it if numbers climb too slowly after building " +
                    "hotels or attractions."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.PreferLargerTouristGroups)),
                    "Prefer larger tourist groups"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.PreferLargerTouristGroups)),
                    "Tourists arrive as households, and the base game picks the household template " +
                    "at random — mostly small ones, averaging under two people.\n\n" +
                    "Raising this favours families and groups, so more tourists arrive for the " +
                    "same number of households. That is much cheaper for the simulation: a family " +
                    "of four needs one hotel room and one route, where four lone travellers need " +
                    "four of each. Set to 0 for the base game's random pick."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.EnableHotelWelcome)),
                    "New hotels get an opening boost"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.EnableHotelWelcome)),
                    "A hotel pays a full payroll from the day it opens but only earns per guest, " +
                    "so a new one often goes bankrupt before word gets around.\n\n" +
                    "This brings a surge of extra visitors to the city when a hotel opens — " +
                    "genuinely additional tourists, not the ones already staying elsewhere, so " +
                    "existing hotels are not emptied to fill the new one."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HotelWelcomeDays)),
                    "Opening boost length (days)"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HotelWelcomeDays)),
                    "How long the surge lasts. After it lapses the hotel has to hold its guests on " +
                    "the city's ordinary appeal."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HotelWelcomeBoost)),
                    "Opening boost size"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HotelWelcomeBoost)),
                    "Extra visitors during the boost, as a percentage of the new hotel's rooms. " +
                    "At 100% the city draws enough additional tourists to fill it."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HotelWelcomeArrivalMultiplier)),
                    "Opening arrival speed"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HotelWelcomeArrivalMultiplier)),
                    "How much faster tourists arrive while a hotel is opening. Without this the " +
                    "extra visitors would trickle in over days and the boost would lapse before " +
                    "they reached the city."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HotelWelcomeStock)),
                    "Opening stock"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HotelWelcomeStock)),
                    "Food kept in a new hotel's storage while it is opening.\n\n" +
                    "Hotels turn food into lodging, and a new one opens with an empty larder — it " +
                    "cannot serve guests until deliveries start arriving, and often folds first. " +
                    "The building complains about customers when the real problem is an empty " +
                    "pantry. Once the opening period ends it buys its own supplies like any other " +
                    "business."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.ReplaceNativeSpawner)),
                    "Replace the base game's tourist spawner"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.ReplaceNativeSpawner)),
                    "The base game's tourist spawner picks household templates without excluding " +
                    "the dynamic ones, which cannot be given citizens. Those households are never " +
                    "cleaned up either, so they pile up for the life of the save — roughly two " +
                    "out of every three tourists it creates.\n\n" +
                    "This stands it down, since this mod's own spawner already covers demand, and " +
                    "clears out the dead households it left behind."
                },

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.EnableHotelZones)),
                    "Hotel and motel zones"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.EnableHotelZones)),
                    "Adds two zones to the zoning menu — Hotels and Motels — and moves the game's " +
                    "lodging buildings into them.\n\n" +
                    "The game already has 80 hotel and motel buildings, but they sit among the " +
                    "ordinary shops in commercial zones, so zoning commercial gives you a hotel " +
                    "only now and then. With these zones, hotels appear where you zone for them, " +
                    "and stop turning up at random elsewhere.\n\n" +
                    "Note that zoning is stored per cell, so a save with hotel zoning painted will " +
                    "have unrecognised cells if this mod is later removed."
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
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.LodgingCostPercent)),
                    "Hotel room cost"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.LodgingCostPercent)),
                    "What a room costs per night, against the base game's own rate.\n\n" +
                    "The base game works out at around ¢1,500 a night, which is steep next to what " +
                    "the rest of the economy charges for anything. Lower this and rooms get " +
                    "cheaper — and because spending money is set against the room rate, visitors " +
                    "carry proportionally less too, so the whole tourist economy scales together " +
                    "instead of drifting apart.\n\n" +
                    "Hotels earn less per guest at lower settings. That is the honest cost of " +
                    "cheaper rooms."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.SpendingPerNightPercent)),
                    "Spending money per night"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.SpendingPerNightPercent)),
                    "How much a visitor carries for each night, beyond the room, as a share of the " +
                    "room rate. At 100% they budget as much for your city as for the bed.\n\n" +
                    "Covers shopping, leisure venues and transport fares — tourists pay real ticket " +
                    "prices from the same wallet. A reserve is always added on top, because a " +
                    "household that runs too low stops shopping for the rest of its visit while " +
                    "still occupying a room.\n\n" +
                    "Raise it if you want tourism to show up in your commercial income."
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

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.RebookDisplacedTourists)),
                    "Rehouse guests of closed hotels"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.RebookDisplacedTourists)),
                    "When a hotel goes out of business its guests lose their rooms, and anyone " +
                    "without a room by the evening is thrown out of the city — so a single " +
                    "closure can wipe out thousands of tourists at once.\n\n" +
                    "This moves them into other hotels that still have space first. Guests are " +
                    "only sent home if the city genuinely has no free rooms left."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.MaxRebookingsPerUpdate)),
                    "Rehousing speed"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.MaxRebookingsPerUpdate)),
                    "How many displaced households are rehoused per pass. Higher clears a large " +
                    "closure faster; lower spreads the work out."
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

                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.DiagnosticLogging)),
                    "Diagnostic logging"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.DiagnosticLogging)),
                    "Writes a daily tourism breakdown to TourismOverhaul.log — arrivals, empty " +
                    "households, free rooms, and why tourists are leaving. Useful when tourist " +
                    "numbers are not behaving as expected."
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
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.EnableHotelEfficiencyFloor)),
                    "Hotels keep running when out of stock"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.EnableHotelEfficiencyFloor)),
                    "The base game treats an empty larder as all or nothing: a hotel with no food " +
                    "drops to 0% efficiency, serves nobody and earns nothing, while still paying " +
                    "its staff. It cannot recover, because it needs income to buy the supplies " +
                    "that would restore it.\n\n" +
                    "This softens the penalty so a hotel runs short-staffed instead of shutting " +
                    "down, and can trade its way back. Only affects hotels, and only the lack of " +
                    "resources penalty."
                },
                {
                    m_Setting.GetOptionLabelLocaleID(nameof(TourismOverhaulSetting.HotelEfficiencyFloor)),
                    "Minimum efficiency when out of stock"
                },
                {
                    m_Setting.GetOptionDescLocaleID(nameof(TourismOverhaulSetting.HotelEfficiencyFloor)),
                    "The worst a hotel's efficiency falls to when it has no supplies. The base " +
                    "game uses 0%. At 50% it runs at half capacity until deliveries resume."
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
