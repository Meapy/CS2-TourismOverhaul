using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace TourismOverhaul
{
    /// <summary>
    /// Mod settings.
    ///
    /// Only choices a player would reasonably want to make are shown. Everything else — the bug
    /// fixes, and the pacing and throttling values that were tuned during development — is marked
    /// [SettingsUIHidden] and left at a fixed value. Hidden entries still live in the settings file,
    /// so they can be edited by hand if someone really needs to, but they do not clutter the panel.
    ///
    /// The bug fixes in particular are hidden deliberately: turning off "discard 80% of arrivals"
    /// is not a preference, it is just a broken game.
    /// </summary>
    [FileLocation("ModsSettings/TourismOverhaul/TourismOverhaul")]
    [SettingsUIGroupOrder(GroupDemand, GroupRouting, GroupHotels, GroupStay, GroupOutbound, GroupDisplay, GroupReporting)]
    [SettingsUIShowGroupName(GroupDemand, GroupRouting, GroupHotels, GroupStay, GroupOutbound, GroupDisplay, GroupReporting)]
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

        // ================================================================ shown

        // ---- How much tourism ----

        /// <summary>Tourists supported per 1000 citizens at full attractiveness.</summary>
        [SettingsUISlider(min = 0f, max = 200f, step = 5f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupDemand)]
        public int TouristsPerThousandCitizens { get; set; } = 60;

        /// <summary>Upper bound on tourist citizens.</summary>
        [SettingsUISlider(min = 1500f, max = 100000f, step = 500f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupDemand)]
        public int MaximumTourists { get; set; } = 60000;

        /// <summary>Bias toward families and groups rather than lone travellers.</summary>
        [SettingsUISlider(min = 0f, max = 100f, step = 25f, unit = "percentage")]
        [SettingsUISection(SectionMain, GroupDemand)]
        public int PreferLargerTouristGroups { get; set; } = 50;

        /// <summary>
        /// How strongly visitors are pushed toward shops rather than parks.
        ///
        /// Tourists never shop in the base game: the behaviour system picks shopping over leisure,
        /// but only for a household that has a need, and needs are generated from household stock
        /// running down — which a tourist household never has. Zero restores that behaviour.
        /// </summary>
        [SettingsUISlider(min = 0f, max = 100f, step = 5f, unit = "percentage")]
        [SettingsUISection(SectionMain, GroupDemand)]
        public int TouristShoppingChance { get; set; } = 25;

        /// <summary>Money per day each visitor spends on shopping, leisure and fares.</summary>
        [SettingsUISlider(min = 0f, max = 20000f, step = 250f, unit = "money")]
        [SettingsUISection(SectionMain, GroupDemand)]
        public int TouristDailySpending { get; set; } = 2000;

        // ---- How they get here ----

        [SettingsUISlider(min = 0f, max = 100f, step = 5f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupRouting)]
        public int ArrivalWeightRoad { get; set; } = 5;

        [SettingsUISlider(min = 0f, max = 100f, step = 5f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupRouting)]
        public int ArrivalWeightTrain { get; set; } = 20;

        [SettingsUISlider(min = 0f, max = 100f, step = 5f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupRouting)]
        public int ArrivalWeightAir { get; set; } = 35;

        [SettingsUISlider(min = 0f, max = 100f, step = 5f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupRouting)]
        public int ArrivalWeightShip { get; set; } = 35;

        /// <summary>Share of tourist groups that bring a car.</summary>
        [SettingsUISlider(min = 0f, max = 100f, step = 5f, unit = "percentage")]
        [SettingsUISection(SectionMain, GroupRouting)]
        public int TouristCarChance { get; set; } = 50;

        // ---- Hotels ----

        /// <summary>Multiplies rooms per hotel. 1 leaves hotels at their normal size.</summary>
        [SettingsUISlider(min = 1f, max = 10f, step = 1f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupHotels)]
        public int HotelRoomMultiplier { get; set; } = 1;

        /// <summary>
        /// Adds dedicated hotel and motel zones and moves the game's lodging buildings into them.
        /// </summary>
        [SettingsUISection(SectionMain, GroupHotels)]
        public bool EnableHotelZones { get; set; } = true;

        /// <summary>Surge of extra visitors when a hotel opens, so it can fill before folding.</summary>
        [SettingsUISection(SectionMain, GroupHotels)]
        public bool EnableHotelWelcome { get; set; } = true;

        // ---- Length of stay ----

        /// <summary>Average nights a tourist with a room stays.</summary>
        [SettingsUISlider(min = 1f, max = 14f, step = 1f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupStay)]
        public int AverageStayDays { get; set; } = 5;

        // ---- Residents travelling ----

        [SettingsUISection(SectionMain, GroupOutbound)]
        public bool EnableResidentHolidays { get; set; } = true;

        /// <summary>Extra holiday departures per 1,000 households per day.</summary>
        [SettingsUISlider(min = 0f, max = 250f, step = 5f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupOutbound)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HolidaysDisabled))]
        public int HolidayTripsPerThousandHouseholds { get; set; } = 100;

        // ---- Display ----

        [SettingsUISection(SectionMain, GroupDisplay)]
        public bool HighlightTourists { get; set; } = false;

        [SettingsUISection(SectionMain, GroupDisplay)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HighlightDisabled))]
        public bool HighlightTouristVehicles { get; set; } = true;

        [SettingsUISlider(min = 0f, max = 255f, step = 5f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupDisplay)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HighlightDisabled))]
        public int MarkerRed { get; set; } = 0;

        [SettingsUISlider(min = 0f, max = 255f, step = 5f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupDisplay)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HighlightDisabled))]
        public int MarkerGreen { get; set; } = 220;

        [SettingsUISlider(min = 0f, max = 255f, step = 5f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupDisplay)]
        [SettingsUIDisableByCondition(typeof(TourismOverhaulSetting), nameof(HighlightDisabled))]
        public int MarkerBlue { get; set; } = 255;

        // ---- Troubleshooting ----

        /// <summary>Writes a periodic tourism breakdown to the mod log.</summary>
        [SettingsUISection(SectionMain, GroupReporting)]
        public bool DiagnosticLogging { get; set; } = false;

        // ================================================================ hidden
        //
        // Fixed values. These were sliders during development; none of them is a choice a player
        // benefits from making, and several are simply "should the mod work".

        /// <summary>Core arrival fix. Off means 80% of arrivals are discarded, as in the base game.</summary>
        [SettingsUIHidden]
        public bool FixArrivalRouting { get; set; } = true;

        /// <summary>
        /// Gives the tourist target search a usable origin radius.
        ///
        /// The base game searches with a radius of zero, so any arrival not standing directly on a
        /// pedestrian lane finds nothing and is evicted on its first attempt. That is why air and
        /// sea arrivals fail at 94-98% while road arrivals fail at 34%. Off restores the base game
        /// behaviour, including the single-shot eviction.
        /// </summary>
        [SettingsUIHidden]
        public bool FixTouristTargetSearch { get; set; } = true;

        /// <summary>
        /// Frees hotel rooms still held by households that have no one left in them.
        ///
        /// Availability is computed as rooms minus the length of the hotel's renter list, so a
        /// household that empties without leaving that list holds its room forever. Off leaves the
        /// rooms permanently occupied.
        /// </summary>
        [SettingsUIHidden]
        public bool ReclaimAbandonedHotelRooms { get; set; } = true;

        /// <summary>Use the arrival mix weights rather than spreading evenly.</summary>
        [SettingsUIHidden]
        public bool PreferAirAndSea { get; set; } = true;

        /// <summary>Shift arrivals away from congested connections.</summary>
        [SettingsUIHidden]
        public bool LoadAwareArrivals { get; set; } = true;

        /// <summary>Waiting tourists per connection at which a route's share halves.</summary>
        [SettingsUIHidden]
        public int ArrivalBacklogSensitivity { get; set; } = 25;

        /// <summary>Core demand fix.</summary>
        [SettingsUIHidden]
        public bool FixTouristDemand { get; set; } = true;

        /// <summary>
        /// How many tourist households may arrive per update. Caps how quickly a shortfall against
        /// the target is filled.
        /// </summary>
        [SettingsUISlider(min = 1f, max = 256f, step = 1f, unit = "integer")]
        [SettingsUISection(SectionMain, GroupDemand)]
        public int MaxArrivalsPerUpdate { get; set; } = 24;

        /// <summary>Occupancy the city sizes demand against.</summary>
        [SettingsUIHidden]
        public int HotelRoomDemandOccupancy { get; set; } = 80;

        /// <summary>Stands down the native spawner, which leaks dead households.</summary>
        [SettingsUIHidden]
        public bool ReplaceNativeSpawner { get; set; } = true;

        [SettingsUIHidden]
        public int HotelWelcomeDays { get; set; } = 7;

        [SettingsUIHidden]
        public int HotelWelcomeBoost { get; set; } = 100;

        [SettingsUIHidden]
        public int HotelWelcomeArrivalMultiplier { get; set; } = 3;

        [SettingsUIHidden]
        public int HotelWelcomeStock { get; set; } = 200;

        /// <summary>Stops an out-of-stock hotel dropping to 0% efficiency, which is unrecoverable.</summary>
        [SettingsUIHidden]
        public bool EnableHotelEfficiencyFloor { get; set; } = true;

        [SettingsUIHidden]
        public int HotelEfficiencyFloor { get; set; } = 50;

        /// <summary>Rehouses guests of a closed hotel rather than evicting them from the city.</summary>
        [SettingsUIHidden]
        public bool RebookDisplacedTourists { get; set; } = true;

        [SettingsUIHidden]
        public int MaxRebookingsPerUpdate { get; set; } = 128;


        /// <summary>Sizes arrival wallets against the live hotel rate.</summary>
        [SettingsUIHidden]
        public bool FixTouristBudget { get; set; } = true;

        /// <summary>Nights of lodging a tourist arrives able to pay for.</summary>
        [SettingsUIHidden]
        public int TouristBudgetDays { get; set; } = 5;

        /// <summary>Raises lodging building demand so hotels get zoned sooner.</summary>
        [SettingsUIHidden]
        public bool FixHotelDemand { get; set; } = true;

        [SettingsUIHidden]
        public float HotelRoomsPerTourist { get; set; } = 1.2f;

        /// <summary>Implements the game's unused length-of-stay timer.</summary>
        [SettingsUIHidden]
        public bool FixLengthOfStay { get; set; } = true;

        /// <summary>Reports real tourist figures rather than the base game's inflated ones.</summary>
        [SettingsUIHidden]
        public bool FixReporting { get; set; } = true;

        [SettingsUIHidden]
        public int HolidayMinimumSavings { get; set; } = 2000;

        [SettingsUIHidden]
        public float MarkerSize { get; set; } = 3f;

        [SettingsUIHidden]
        public int MarkerOpacity { get; set; } = 85;

        // ================================================================ misc

        /// <summary>Marker colour assembled from the channel sliders.</summary>
        public UnityEngine.Color GetMarkerColor()
        {
            return new UnityEngine.Color(
                UnityEngine.Mathf.Clamp01(MarkerRed / 255f),
                UnityEngine.Mathf.Clamp01(MarkerGreen / 255f),
                UnityEngine.Mathf.Clamp01(MarkerBlue / 255f),
                UnityEngine.Mathf.Clamp01(MarkerOpacity / 100f));
        }

        public bool RoutingDisabled() => !FixArrivalRouting;

        public bool LoadAwarenessDisabled() => !FixArrivalRouting || !LoadAwareArrivals;

        public bool ArrivalMixDisabled() => !FixArrivalRouting || !PreferAirAndSea;

        public bool DemandDisabled() => !FixTouristDemand;

        public bool StayDisabled() => !FixLengthOfStay;

        public bool BudgetDisabled() => !FixTouristBudget;

        public bool HotelDemandDisabled() => !FixHotelDemand;

        public bool HighlightDisabled() => !HighlightTourists;


        public bool WelcomeDisabled() => !FixTouristDemand || !EnableHotelWelcome;

        public bool RebookingDisabled() => !RebookDisplacedTourists;

        public bool EfficiencyFloorDisabled() => !EnableHotelEfficiencyFloor;

        public bool HolidaysDisabled() => !EnableResidentHolidays;

        public override void SetDefaults()
        {
            // Shown
            TouristsPerThousandCitizens = 60;
            MaximumTourists = 60000;
            PreferLargerTouristGroups = 50;
            TouristDailySpending = 2000;

            ArrivalWeightRoad = 5;
            ArrivalWeightTrain = 20;
            ArrivalWeightAir = 35;
            ArrivalWeightShip = 35;
            TouristCarChance = 50;

            HotelRoomMultiplier = 1;
            EnableHotelZones = true;
            EnableHotelWelcome = true;

            AverageStayDays = 5;

            EnableResidentHolidays = true;
            HolidayTripsPerThousandHouseholds = 100;

            HighlightTourists = false;
            HighlightTouristVehicles = true;
            MarkerRed = 0;
            MarkerGreen = 220;
            MarkerBlue = 255;

            DiagnosticLogging = false;

            // Hidden
            FixArrivalRouting = true;
            FixTouristTargetSearch = true;
            ReclaimAbandonedHotelRooms = true;
            PreferAirAndSea = true;
            LoadAwareArrivals = true;
            ArrivalBacklogSensitivity = 15;

            FixTouristDemand = true;
            MaxArrivalsPerUpdate = 128;
            TouristShoppingChance = 25;
            HotelRoomDemandOccupancy = 80;
            ReplaceNativeSpawner = true;

            HotelWelcomeDays = 7;
            HotelWelcomeBoost = 100;
            HotelWelcomeArrivalMultiplier = 3;
            HotelWelcomeStock = 200;

            EnableHotelEfficiencyFloor = true;
            HotelEfficiencyFloor = 50;

            RebookDisplacedTourists = true;
            MaxRebookingsPerUpdate = 128;


            FixTouristBudget = true;
            TouristBudgetDays = 5;
            FixHotelDemand = true;
            HotelRoomsPerTourist = 1.2f;

            FixLengthOfStay = true;
            FixReporting = true;

            HolidayMinimumSavings = 2000;

            MarkerSize = 3f;
            MarkerOpacity = 85;
        }
    }
}
