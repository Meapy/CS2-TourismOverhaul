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
            m_DepartureTime.Update(FormatTimeOfDay(call.m_ReboardFrame));
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
                    return controller;
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
        private static string FormatTimeOfDay(uint frame)
        {
            float dayFraction = (float)(frame % kFramesPerDay) / kFramesPerDay;

            int totalMinutes = (int)math.round(dayFraction * 24f * 60f);

            // Rounding can land exactly on midnight of the next day.
            totalMinutes %= 24 * 60;

            int hours = totalMinutes / 60;
            int minutes = totalMinutes % 60;

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
