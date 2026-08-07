using Colossal.UI.Binding;
using Game.UI;
using Game.UI.InGame;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Publishes a docked cruise ship's sailing time for the selected-vehicle panel.
    ///
    /// The native panel shows a vehicle's state as "Boarding" and nothing more, which for a cruise
    /// ship is close to useless: it is boarding for twelve in-game hours and the player has no way
    /// to tell whether that is nearly over or barely begun.
    ///
    /// Only the managed half lives here, deliberately. The interface ships packed inside .cok
    /// archives, so the panel's module path can only be discovered at runtime — the frontend logs
    /// candidates and the row is added once the path is pinned. Publishing the value from C# first
    /// means the binding is already correct and testable when that happens, rather than two
    /// unknowns being debugged at once.
    ///
    /// The time is formatted here rather than on the frontend because the frame-to-clock conversion
    /// belongs with the rest of the time model: 262,144 frames is an in-game day, and everything
    /// else in this mod that reasons about time does so in frames.
    /// </summary>
    public partial class CruiseDepartureUISystem : UISystemBase
    {
        private const string kGroup = "tourismOverhaul";

        /// <summary>Simulation frames in one in-game day.</summary>
        private const uint kFramesPerDay = 262144u;

        private SelectedInfoUISystem m_SelectedInfoUISystem;
        private Game.Simulation.SimulationSystem m_SimulationSystem;

        /// <summary>The game's clock, for turning a departure frame into a time the player reads.</summary>
        private Game.Simulation.TimeSystem m_TimeSystem;

        private ValueBinding<bool> m_Docked;
        private ValueBinding<string> m_DepartureTime;
        private ValueBinding<int> m_MinutesRemaining;
        private ValueBinding<int> m_Ashore;

        private CruiseVoyageSystem m_VoyageSystem;

        /// <summary>Last selection explained in the log, so it says so once rather than per frame.</summary>
        private Entity m_LoggedSelection;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SelectedInfoUISystem = World.GetOrCreateSystemManaged<SelectedInfoUISystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<Game.Simulation.SimulationSystem>();
            m_TimeSystem = World.GetOrCreateSystemManaged<Game.Simulation.TimeSystem>();

            AddBinding(m_Docked = new ValueBinding<bool>(kGroup, "cruiseDocked", false));
            AddBinding(m_DepartureTime = new ValueBinding<string>(kGroup, "cruiseDeparture", string.Empty));
            AddBinding(m_MinutesRemaining = new ValueBinding<int>(kGroup, "cruiseMinutesLeft", 0));
            AddBinding(m_Ashore = new ValueBinding<int>(kGroup, "cruiseAshore", 0));

            m_VoyageSystem = World.GetOrCreateSystemManaged<CruiseVoyageSystem>();
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            if (m_SelectedInfoUISystem == null || m_SimulationSystem == null)
            {
                return;
            }

            Entity vehicle = ResolveVehicle(m_SelectedInfoUISystem.selectedEntity);

            if (vehicle == Entity.Null
                || !EntityManager.HasComponent<Components.CruiseCall>(vehicle))
            {
                // Not a docked cruise ship. Bindings only push when the value actually changes, so
                // clearing every frame costs nothing once it has settled.
                m_Docked.Update(false);
                m_DepartureTime.Update(string.Empty);
                m_MinutesRemaining.Update(0);
                m_Ashore.Update(0);

                LogSelectionOnce(vehicle);
                return;
            }

            m_LoggedSelection = Entity.Null;

            Components.CruiseCall call =
                EntityManager.GetComponentData<Components.CruiseCall>(vehicle);

            uint frame = m_SimulationSystem.frameIndex;

            m_Docked.Update(true);
            m_DepartureTime.Update(FormatTimeOfDay(call.m_ReboardFrame, frame));
            m_MinutesRemaining.Update(MinutesBetween(frame, call.m_ReboardFrame));

            // The native PASSENGERS row counts the ship's own buffer, which is correctly zero while
            // the complement is ashore — so on a docked cruise ship it says nothing useful. This is
            // the figure the player actually wants there: how many of its passengers are out in the
            // city, and therefore how many have to be back before it sails.
            m_Ashore.Update(
                m_VoyageSystem != null ? m_VoyageSystem.CountAshoreFor(vehicle) : 0);
        }

        /// <summary>
        /// The vehicle behind a selection, hopping a Controller if the click landed on a part.
        ///
        /// A vehicle in this game need not be one entity. SelectedInfoUISystem:363 makes the same
        /// hop, resolving Controller before reading the selection — so a ship clicked in the world
        /// can report a sub-entity, and testing for CruiseCall on that finds nothing however
        /// correctly the call was created.
        /// </summary>
        private Entity ResolveVehicle(Entity selected)
        {
            if (selected == Entity.Null || !EntityManager.Exists(selected))
            {
                return Entity.Null;
            }

            if (EntityManager.HasComponent<Components.CruiseCall>(selected))
            {
                return selected;
            }

            // Game.Vehicles.Controller, fully qualified. Importing the namespace wholesale would
            // pull in PublicTransport and friends alongside Game.UI.InGame, and the notes already
            // record what related-but-different types in two namespaces cost to untangle.
            if (EntityManager.HasComponent<Game.Vehicles.Controller>(selected))
            {
                Entity controller =
                    EntityManager.GetComponentData<Game.Vehicles.Controller>(selected).m_Controller;

                if (controller != Entity.Null && EntityManager.Exists(controller))
                {
                    if (EntityManager.HasComponent<Components.CruiseCall>(controller))
                    {
                        return controller;
                    }

                    selected = controller;
                }
            }

            // Last resort: match by route.
            //
            // The Controller hop only resolves upwards, so a click that lands on the controller
            // while the call sits on some other part of the same vessel finds nothing — measured as
            // "Selected transport vehicle 275382 has no cruise call" with a call open and its
            // passengers ashore. Every part of a vessel shares CurrentRoute and a cruise line runs
            // one vessel, so a call on the same route is this ship's call whichever entity the
            // click happened to resolve to.
            if (m_VoyageSystem != null)
            {
                Entity onRoute = m_VoyageSystem.FindCallOnSameRoute(selected);

                if (onRoute != Entity.Null)
                {
                    return onRoute;
                }
            }

            return selected;
        }

        /// <summary>
        /// Says once, per selection, why a selected public transport vehicle shows no sailing time.
        ///
        /// Two causes look identical from the panel — the vehicle is not a cruise ship at all, or it
        /// is one with no call open because it is between ports. Without this the only symptom is a
        /// missing row, which is also what a failed frontend registration looks like.
        /// </summary>
        private void LogSelectionOnce(Entity vehicle)
        {
            if (vehicle == Entity.Null
                || vehicle == m_LoggedSelection
                || !EntityManager.HasComponent<Game.Vehicles.PublicTransport>(vehicle))
            {
                return;
            }

            m_LoggedSelection = vehicle;

            Mod.Log.Info(
                $"Selected transport vehicle {vehicle.Index} has no cruise call, so no sailing "
                + $"time is shown (manifest aboard: "
                + $"{EntityManager.HasComponent<Components.CruiseManifest>(vehicle)}).");
        }

        /// <summary>
        /// The in-game clock time a frame falls at, as HH:mm.
        ///
        /// Position through the day is the frame modulo a day's worth of frames — the same model
        /// the arrival window and shore leave use. Deliberately not GetCurrentDateTime, which
        /// cannot express this: TimeSettingsPrefab ships twelve days to a year, so the date the
        /// game builds never leaves January and its Month is permanently 1. See the time section of
        /// docs/SESSION-NOTES.md.
        /// </summary>
        /// <summary>
        /// The clock time a future frame falls on, as the game itself would show it.
        ///
        /// Derived from the current time of day plus the gap, rather than from the frame index
        /// alone. <c>frame % 262144</c> looks like it should give the time of day and does not:
        /// TimeSystem.GetTicks:89-91 counts from <c>TimeData.m_FirstFrame</c> and adds the map's
        /// authored time and date offsets before taking the remainder, so a raw frame index is out
        /// by a constant that depends on when the city was founded. The displayed departure was
        /// wrong by that constant in every save.
        ///
        /// Working from the delta sidesteps all of it. Ticks advance one per frame, so the gap
        /// between now and the departure is the same in both, and TimeSystem.normalizedTime is
        /// already the game's own fraction of the current day — offsets applied.
        ///
        /// Hours and minutes are floored rather than rounded, matching GetCurrentDateTime:191-192,
        /// so this agrees with the clock in the corner rather than being a minute ahead of it.
        /// </summary>
        private string FormatTimeOfDay(uint targetFrame, uint currentFrame)
        {
            // Never returns empty. The row is hidden when this string is blank, so a missing clock
            // would read as "no cruise ship selected" rather than as the fault it is — and a wrong
            // hour is a far smaller problem than a row that silently does not exist.
            float dayFraction = m_TimeSystem != null
                ? m_TimeSystem.normalizedTime
                : (currentFrame % kFramesPerDay) / (float)kFramesPerDay;

            if (targetFrame > currentFrame)
            {
                dayFraction += (targetFrame - currentFrame) / (float)kFramesPerDay;
            }

            dayFraction = math.frac(dayFraction);

            float hourOfDay = 24f * dayFraction;

            int hours = (int)math.floor(hourOfDay);
            int minutes = (int)math.floor(60f * (hourOfDay - hours));

            return $"{hours:00}:{minutes:00}";
        }

        /// <summary>In-game minutes from one frame to another, floored at zero.</summary>
        private static int MinutesBetween(uint from, uint to)
        {
            if (to <= from)
            {
                return 0;
            }

            return (int)math.round((to - from) / (float)kFramesPerDay * 24f * 60f);
        }
    }
}
