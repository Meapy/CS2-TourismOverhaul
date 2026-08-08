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

            // Every other locale the game ships with. LocaleOverlay fills whatever a locale has not
            // translated from the English source, so a partial translation never leaves a blank
            // label — see Translations.cs for why the long descriptions stay in English.
            foreach (string locale in Translations.SupportedLocales)
            {
                GameManager.instance.localizationManager.AddSource(
                    locale, new LocaleOverlay(Settings, Translations.For(locale, Settings)));
            }

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

            // The Passenger Cruise Line tool. Creates its prefab at preload, like the zones above,
            // because prefabs must exist before they are baked into entities.
            updateSystem.UpdateAt<CruiseLineSystem>(SystemUpdatePhase.GameSimulation);

            // Puts a docked cruise ship's passengers ashore and sends them back aboard. Must run
            // before TouristStaySystem, which owns m_LeavingTime for ordinary visitors — a cruise
            // passenger's leaving time is the ship's departure, not a random stay length.
            updateSystem.UpdateBefore<CruiseVoyageSystem, TouristStaySystem>(
                SystemUpdatePhase.GameSimulation);

            // Opening surge for newly built hotels. Must run before TouristDemandSystem reads it.
            updateSystem.UpdateBefore<HotelWelcomeSystem, TouristDemandSystem>(SystemUpdatePhase.GameSimulation);

            // Publishes our tourism figures to the UI layer for the frontend module.
            updateSystem.UpdateAt<TourismPanelUISystem>(SystemUpdatePhase.UIUpdate);

            // Sailing time for a selected docked cruise ship. The managed half only — the panel row
            // waits on the module path being pinned from the frontend's discovery log.
            updateSystem.UpdateAt<CruiseDepartureUISystem>(SystemUpdatePhase.UIUpdate);

            // The tourist demand bar. Separate from the panel system because that one runs at
            // interval 512 — around eight seconds of real play — and a demand bar that steps every
            // eight seconds does not read as a demand bar. This one takes the default UI interval,
            // as CityInfoUISystem does for the native six.
            updateSystem.UpdateAt<TouristDemandUISystem>(SystemUpdatePhase.UIUpdate);


            // Frees rooms held by emptied households, and deletes the households. Without it,
            // availability decays permanently as guests come and go.
            updateSystem.UpdateAt<HotelRoomReclaimSystem>(SystemUpdatePhase.GameSimulation);

            // Replaces the native tourist target search, whose origin radius of zero strands every
            // arrival that is not standing on a lane. Must run in the simulation phase, in place of
            // TouristFindTargetSystem which it disables.
            updateSystem.UpdateAt<TouristTargetSearchSystem>(SystemUpdatePhase.GameSimulation);

            // Adds the Tourism Finance info view beside the stock Tourism one.
            updateSystem.UpdateAt<TourismFinanceViewSystem>(SystemUpdatePhase.GameSimulation);

            // Gives leisure venues more service capacity, which lowers their surge pricing.
            updateSystem.UpdateAt<LeisurePricingSystem>(SystemUpdatePhase.GameSimulation);

            // Watches tourist wallets and attributes what leaves them to a category.
            updateSystem.UpdateAt<TouristSpendingLedgerSystem>(SystemUpdatePhase.GameSimulation);

            // Damps a crowded attraction's appeal so visitors spread out. Must run after
            // HistoricAttractivenessSystem, which sets the values this one scales.
            updateSystem.UpdateAt<AttractionCrowdingSystem>(SystemUpdatePhase.GameSimulation);

            // Makes old town buildings attractive in their own right, so a historic district draws
            // visitors without needing a landmark dropped into it.
            updateSystem.UpdateAt<HistoricAttractivenessSystem>(SystemUpdatePhase.GameSimulation);

            // Gives visitors a shopping need, so they choose shops over parks. Must run in the
            // simulation phase, before CitizenBehaviorSystem reads the need on its next tick.
            updateSystem.UpdateAt<TouristShoppingSystem>(SystemUpdatePhase.GameSimulation);

            // Opt-in daily breakdown for when tourist numbers misbehave.
            updateSystem.UpdateAt<TourismDiagnosticsSystem>(SystemUpdatePhase.GameSimulation);

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
