using Colossal.IO.AssetDatabase;
using Colossal.Logging;
using Game;
using Game.Modding;
using Game.SceneFlow;
using TourismOverhaul.Systems;

namespace TourismOverhaul
{
    /// <summary>
    /// Entry point. See docs/TOURISM-DIAGNOSIS.md for the traced defects each system addresses.
    /// </summary>
    public sealed class Mod : IMod
    {
        public const string ModName = "TourismOverhaul";

        public static readonly ILog Log = LogManager.GetLogger(ModName);

        public static TourismOverhaulSetting Settings { get; private set; }

        public void OnLoad(UpdateSystem updateSystem)
        {
            Log.Info($"{ModName}: OnLoad");

            Settings = new TourismOverhaulSetting(this);
            Settings.RegisterInOptionsUI();
            GameManager.instance.localizationManager.AddSource("en-US", new LocaleEN(Settings));
            AssetDatabase.global.LoadSettings(ModName, Settings, new TourismOverhaulSetting(this));

            // Fix A: rewrite the tourist outside-connection split to match what the city has.
            updateSystem.UpdateAt<TouristRoutingSystem>(SystemUpdatePhase.GameSimulation);

            // Fix B: top up arrivals toward a population-scaled target.
            updateSystem.UpdateAt<TouristDemandSystem>(SystemUpdatePhase.GameSimulation);

            // Fix E: size tourist wallets against hotel prices, and get hotels built sooner.
            updateSystem.UpdateAt<TouristEconomySystem>(SystemUpdatePhase.GameSimulation);

            // Keep out-of-stock hotels running instead of shutting down entirely.
            //
            // Ordered directly after ProcessingCompanySystem, which is what zeroes the lack of
            // resources factor. Correcting it in the same frame avoids the panel flickering between
            // -100% and the floor.
            updateSystem.UpdateAfter<HotelEfficiencyFloorSystem, Game.Simulation.ProcessingCompanySystem>(
                SystemUpdatePhase.GameSimulation);

            // Thin out cars brought by arriving tourists.
            updateSystem.UpdateAt<TouristCarSystem>(SystemUpdatePhase.GameSimulation);

            // Rehouse guests of closed hotels before the evening eviction sweep runs.
            updateSystem.UpdateAt<TouristRebookSystem>(SystemUpdatePhase.GameSimulation);

            // Fix C: give hotel guests a real, finite length of stay.
            updateSystem.UpdateAt<TouristStaySystem>(SystemUpdatePhase.GameSimulation);

            // Fix D: report the numbers the simulation actually produces.
            updateSystem.UpdateAt<TourismReportingSystem>(SystemUpdatePhase.GameSimulation);

            // Hotel room capacity multiplier. Replaces LodgingProviderSystem when enabled.
            updateSystem.UpdateAt<HotelCapacitySystem>(SystemUpdatePhase.GameSimulation);

            // Sends resident households out of the city on holiday, and counts who is away.
            updateSystem.UpdateAt<ResidentTravelSystem>(SystemUpdatePhase.GameSimulation);

            // Dedicated hotel and motel zones, built from the game's own lodging assets.
            updateSystem.UpdateAt<HotelZoneSystem>(SystemUpdatePhase.GameSimulation);

            // Opening surge for newly built hotels. Must run before TouristDemandSystem reads it.
            updateSystem.UpdateBefore<HotelWelcomeSystem, TouristDemandSystem>(SystemUpdatePhase.GameSimulation);

            // Publishes our tourism figures to the UI layer for the frontend module.
            updateSystem.UpdateAt<TourismPanelUISystem>(SystemUpdatePhase.UIUpdate);


            // Opt-in daily breakdown for when tourist numbers misbehave.
            updateSystem.UpdateAt<TourismDiagnosticsSystem>(SystemUpdatePhase.GameSimulation);

            // One-shot survey of which building assets can host a hotel.
            updateSystem.UpdateAt<HotelAssetSurveySystem>(SystemUpdatePhase.GameSimulation);

            // Optional view feature: mark tourists, then draw a coloured ring for each.
            updateSystem.UpdateAt<TouristHighlightSystem>(SystemUpdatePhase.GameSimulation);
            updateSystem.UpdateBefore<TouristMarkerRenderSystem, Game.Rendering.OverlayRenderSystem>(
                SystemUpdatePhase.Rendering);

            Log.Info($"{ModName}: systems registered");
        }

        public void OnDispose()
        {
            Log.Info($"{ModName}: OnDispose");

            if (Settings != null)
            {
                Settings.UnregisterInOptionsUI();
                Settings = null;
            }
        }
    }
}
