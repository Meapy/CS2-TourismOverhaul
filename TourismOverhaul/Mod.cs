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

            // Fix C: give hotel guests a real, finite length of stay.
            updateSystem.UpdateAt<TouristStaySystem>(SystemUpdatePhase.GameSimulation);

            // Fix D: report the numbers the simulation actually produces.
            updateSystem.UpdateAt<TourismReportingSystem>(SystemUpdatePhase.GameSimulation);

            // Hotel room capacity multiplier. Replaces LodgingProviderSystem when enabled.
            updateSystem.UpdateAt<HotelCapacitySystem>(SystemUpdatePhase.GameSimulation);

            // Sends resident households out of the city on holiday, and counts who is away.
            updateSystem.UpdateAt<ResidentTravelSystem>(SystemUpdatePhase.GameSimulation);

            // Publishes our tourism figures to the UI layer for the frontend module.
            updateSystem.UpdateAt<TourismPanelUISystem>(SystemUpdatePhase.UIUpdate);

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
