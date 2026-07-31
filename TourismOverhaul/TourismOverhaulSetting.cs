using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace TourismOverhaul
{
    [FileLocation("ModsSettings/TourismOverhaul/TourismOverhaul")]
    [SettingsUIGroupOrder(GroupRouting, GroupDemand, GroupBudget, GroupStay, GroupHotels, GroupOutbound, GroupReporting, GroupDisplay)]
    [SettingsUIShowGroupName(GroupRouting, GroupDemand, GroupBudget, GroupStay, GroupHotels, GroupOutbound, GroupReporting, GroupDisplay)]
    public sealed class TourismOverhaulSetting : ModSetting
    {
        public const string SectionMain = "Main";

        public const string GroupRouting = "Arrivals";
        public const string GroupDemand = "Demand";
        public const string GroupStay = "LengthOfStay";
        public const string GroupReporting = "Reporting";
        public const string GroupDisplay = "Display";
        public const string GroupHotels = "Hotels";
        public const string GroupOutbound = "Outbound";
        public const string GroupBudget = "Budget";

        public TourismOverhaulSetting(IMod mod) : base(mod)
        {
        }

        // ---------------------------------------------------------------- A

        [SettingsUISection(SectionMain, GroupRouting)]
        public bool FixArrivalRouting { get; set; } = true;

        /// <summary>
        /// When true the air/ship share is redistributed to whatever the city actually has.
        /// When false the vanilla 10/10/50/30 split is left alone (arrivals will still be lost).
        /// </summary>
        [SettingsUISection(SectionMain, GroupRouting)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(RoutingDisabled))]
        public bool PreferAirAndSea { get; set; } = true;

        /// <summary>
        /// Shifts arrival share away from connections with a backlog of tourists who have not yet
        /// found a destination. Vanilla has no capacity concept for outside connections at all.
        /// </summary>
        [SettingsUISection(SectionMain, GroupRouting)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(RoutingDisabled))]
        public bool LoadAwareArrivals { get; set; } = true;

        /// <summary>Waiting tourists per connection at which that route's share is halved.</summary>
        [SettingsUISlider(min = 5f, max = 200f, step = 5f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupRouting)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(LoadAwarenessDisabled))]
        public int ArrivalBacklogSensitivity { get; set; } = 25;

        // ---------------------------------------------------------------- B

        [SettingsUISection(SectionMain, GroupDemand)]
        public bool FixTouristDemand { get; set; } = true;

        /// <summary>
        /// Tourists supported per 1000 citizens at 100 attractiveness.
        /// Vanilla behaves like a flat 1500 regardless of city size.
        /// </summary>
        [SettingsUISlider(min = 0f, max = 200f, step = 5f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupDemand)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(DemandDisabled))]
        public int TouristsPerThousandCitizens { get; set; } = 60;

        /// <summary>
        /// Upper bound on tourist citizens, as a safety valve for very large cities.
        /// </summary>
        [SettingsUISlider(min = 1500f, max = 100000f, step = 500f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupDemand)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(DemandDisabled))]
        public int MaximumTourists { get; set; } = 25000;

        /// <summary>
        /// Maximum tourist households created per update. Higher fills a deficit faster
        /// but produces burstier arrivals and more pathfinding work per tick.
        /// </summary>
        [SettingsUISlider(min = 1f, max = 32f, step = 1f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupDemand)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(DemandDisabled))]
        public int MaxArrivalsPerUpdate { get; set; } = 8;

        // ---------------------------------------------------------------- E

        /// <summary>
        /// Sizes tourist arrival wallets against the live hotel rate. Without this most arrivals
        /// cannot afford a single night and are evicted as TouristNoMoney the same day.
        /// </summary>
        [SettingsUISection(SectionMain, GroupBudget)]
        public bool FixTouristBudget { get; set; } = true;

        /// <summary>Nights of hotel cost a tourist arrives able to pay for.</summary>
        [SettingsUISlider(min = 1f, max = 21f, step = 1f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupBudget)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(BudgetDisabled))]
        public int TouristBudgetDays { get; set; } = 5;

        /// <summary>
        /// Money per day, on top of lodging, for shopping, leisure and transit fares.
        /// </summary>
        [SettingsUISlider(min = 0f, max = 20000f, step = 250f, unit = "money")]
        [SettingsUISection(SectionMain, GroupBudget)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(BudgetDisabled))]
        public int TouristDailySpending { get; set; } = 2000;

        /// <summary>
        /// Raises Lodging building demand so hotels get zoned before rooms run out.
        /// </summary>
        [SettingsUISection(SectionMain, GroupBudget)]
        public bool FixHotelDemand { get; set; } = true;

        /// <summary>Rooms per tourist the city treats as necessary. Vanilla ships 0.5.</summary>
        [SettingsUISlider(min = 0.1f, max = 3f, step = 0.1f, unit = "floatSingleFraction")]
        [SettingsUISection(SectionMain, GroupBudget)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HotelDemandDisabled))]
        public float HotelRoomsPerTourist { get; set; } = 1.2f;

        // ---------------------------------------------------------------- C

        [SettingsUISection(SectionMain, GroupStay)]
        public bool FixLengthOfStay { get; set; } = true;

        /// <summary>
        /// Average stay for a tourist who has secured a hotel room, in in-game days.
        /// </summary>
        [SettingsUISlider(min = 1f, max = 14f, step = 1f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupStay)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(StayDisabled))]
        public int AverageStayDays { get; set; } = 3;

        // ---------------------------------------------------------------- D

        [SettingsUISection(SectionMain, GroupReporting)]
        public bool FixReporting { get; set; } = true;

        // ---------------------------------------------------------------- outbound

        /// <summary>
        /// Sends resident households out of the city on holiday more often than vanilla, which
        /// suppresses LeisureType.Travel to roughly 1-3% of leisure trips.
        /// </summary>
        [SettingsUISection(SectionMain, GroupOutbound)]
        public bool EnableResidentHolidays { get; set; } = false;

        /// <summary>Extra holiday departures per 1,000 eligible households per in-game day.</summary>
        [SettingsUISlider(min = 0f, max = 250f, step = 1f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupOutbound)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HolidaysDisabled))]
        public int HolidayTripsPerThousandHouseholds { get; set; } = 20;

        /// <summary>Households below this much money never get sent away.</summary>
        [SettingsUISlider(min = 0f, max = 20000f, step = 500f, unit = "money")]
        [SettingsUISection(SectionMain, GroupOutbound)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HolidaysDisabled))]
        public int HolidayMinimumSavings { get; set; } = 2000;

        // ---------------------------------------------------------------- display

        /// <summary>
        /// Draws the game's standard selection outline around tourists walking in the city.
        /// </summary>
        [SettingsUISection(SectionMain, GroupDisplay)]
        public bool HighlightTourists { get; set; } = false;

        /// <summary>
        /// Also outline the vehicle a tourist is riding. Note that a bus or train carrying one
        /// tourist is outlined as a whole, since the outline applies to the vehicle entity.
        /// </summary>
        [SettingsUISection(SectionMain, GroupDisplay)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HighlightDisabled))]
        public bool HighlightTouristVehicles { get; set; } = true;

        /// <summary>Diameter of the ring drawn on the ground, in metres.</summary>
        [SettingsUISlider(min = 1f, max = 12f, step = 0.5f, unit = "floatSingleFraction")]
        [SettingsUISection(SectionMain, GroupDisplay)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HighlightDisabled))]
        public float MarkerSize { get; set; } = 3f;

        [SettingsUISlider(min = 0f, max = 255f, step = 1f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupDisplay)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HighlightDisabled))]
        public int MarkerRed { get; set; } = 0;

        [SettingsUISlider(min = 0f, max = 255f, step = 1f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupDisplay)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HighlightDisabled))]
        public int MarkerGreen { get; set; } = 220;

        [SettingsUISlider(min = 0f, max = 255f, step = 1f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupDisplay)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HighlightDisabled))]
        public int MarkerBlue { get; set; } = 255;

        [SettingsUISlider(min = 5f, max = 100f, step = 5f, unit = "percentage")]
        [SettingsUISection(SectionMain, GroupDisplay)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HighlightDisabled))]
        public int MarkerOpacity { get; set; } = 85;

        /// <summary>Marker colour assembled from the sliders.</summary>
        public UnityEngine.Color GetMarkerColor()
        {
            return new UnityEngine.Color(
                UnityEngine.Mathf.Clamp01(MarkerRed / 255f),
                UnityEngine.Mathf.Clamp01(MarkerGreen / 255f),
                UnityEngine.Mathf.Clamp01(MarkerBlue / 255f),
                UnityEngine.Mathf.Clamp01(MarkerOpacity / 100f));
        }

        // ---------------------------------------------------------------- hotels

        /// <summary>
        /// Replaces the native LodgingProviderSystem with a copy that multiplies room counts.
        /// See HotelCapacitySystem for why no lighter lever exists.
        /// </summary>
        [SettingsUISection(SectionMain, GroupHotels)]
        public bool EnableHotelCapacity { get; set; } = false;

        [SettingsUISlider(min = 1f, max = 10f, step = 1f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupHotels)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HotelCapacityDisabled))]
        public int HotelRoomMultiplier { get; set; } = 1;

        // ---------------------------------------------------------------- misc

        public bool RoutingDisabled() => !FixArrivalRouting;

        public bool LoadAwarenessDisabled() => !FixArrivalRouting || !LoadAwareArrivals;

        public bool DemandDisabled() => !FixTouristDemand;

        public bool StayDisabled() => !FixLengthOfStay;

        public bool BudgetDisabled() => !FixTouristBudget;

        public bool HotelDemandDisabled() => !FixHotelDemand;

        public bool HighlightDisabled() => !HighlightTourists;

        public bool HotelCapacityDisabled() => !EnableHotelCapacity;

        public bool HolidaysDisabled() => !EnableResidentHolidays;

        public override void SetDefaults()
        {
            FixArrivalRouting = true;
            PreferAirAndSea = true;
            LoadAwareArrivals = true;
            ArrivalBacklogSensitivity = 25;

            FixTouristDemand = true;
            TouristsPerThousandCitizens = 60;
            MaximumTourists = 60000;
            MaxArrivalsPerUpdate = 12;

            FixTouristBudget = true;
            TouristBudgetDays = 5;
            TouristDailySpending = 2000;
            FixHotelDemand = true;
            HotelRoomsPerTourist = 1.2f;

            FixLengthOfStay = true;
            AverageStayDays = 5;

            FixReporting = true;

            HighlightTourists = false;
            HighlightTouristVehicles = true;
            MarkerSize = 3f;
            MarkerRed = 0;
            MarkerGreen = 220;
            MarkerBlue = 255;
            MarkerOpacity = 85;

            EnableHotelCapacity = true;
            HotelRoomMultiplier = 4;

            EnableResidentHolidays = true;
            HolidayTripsPerThousandHouseholds = 100;
            HolidayMinimumSavings = 2000;
        }
    }
}
