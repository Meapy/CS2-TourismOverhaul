using Colossal.IO.AssetDatabase;
using Game.Modding;
using Game.Settings;

namespace TourismOverhaul
{
    [FileLocation("ModsSettings/TourismOverhaul/TourismOverhaul")]
    [SettingsUIGroupOrder(GroupRouting, GroupDemand, GroupStay, GroupReporting, GroupDisplay)]
    [SettingsUIShowGroupName(GroupRouting, GroupDemand, GroupStay, GroupReporting, GroupDisplay)]
    public sealed class TourismOverhaulSetting : ModSetting
    {
        public const string SectionMain = "Main";

        public const string GroupRouting = "Arrivals";
        public const string GroupDemand = "Demand";
        public const string GroupStay = "LengthOfStay";
        public const string GroupReporting = "Reporting";
        public const string GroupDisplay = "Display";

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

        // ---------------------------------------------------------------- misc

        public bool RoutingDisabled() => !FixArrivalRouting;

        public bool DemandDisabled() => !FixTouristDemand;

        public bool StayDisabled() => !FixLengthOfStay;

        public bool HighlightDisabled() => !HighlightTourists;

        public override void SetDefaults()
        {
            FixArrivalRouting = true;
            PreferAirAndSea = true;

            FixTouristDemand = true;
            TouristsPerThousandCitizens = 60;
            MaximumTourists = 25000;
            MaxArrivalsPerUpdate = 8;

            FixLengthOfStay = true;
            AverageStayDays = 3;

            FixReporting = true;

            HighlightTourists = false;
            HighlightTouristVehicles = true;
        }
    }
}
