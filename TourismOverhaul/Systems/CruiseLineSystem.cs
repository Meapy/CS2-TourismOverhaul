using Game;
using Game.City;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using SizeClass = Game.Vehicles.SizeClass;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Adds a Passenger Cruise Line tool beside the game's own Passenger Ship Line.
    ///
    /// STEP ONE ONLY. This creates the line prefab and gets it into the toolbar. It does not yet
    /// carry cruise behaviour — no cohort, no shore leave, no return boarding. Drawing a line with
    /// it today produces an ordinary passenger ship line under a different name and colour. See
    /// docs/CRUISE-LINE-PLAN.md for the design this is the first step of, and for the reason the
    /// steps are ordered this way: whether a runtime-created route prefab reaches the toolbar at
    /// all is the largest unverified risk in the feature, and it is cheap to answer first.
    ///
    /// HOW A LINE IS DEFINED
    ///
    /// TransportLinePrefab (:13-129) is a RoutePrefab subclass. Everything about a line — which
    /// vehicle family serves it, how passengers reach it, how the vessel travels, dwell time,
    /// vehicle spacing — is authored on the prefab, and RoutePrefab.LateInitialize (:54-89) derives
    /// the four runtime archetypes from the prefab's own components with CreateArchetype. That last
    /// part is what makes a runtime-created line possible at all: nothing is hard-coded against the
    /// stock prefabs.
    ///
    /// So this clones the stock passenger ship line rather than authoring a line from nothing.
    /// Copying m_TransportType (Ship) is also what satisfies "reuse the default ship" — vessels are
    /// not named on the line prefab, they are selected by transport type, so inheriting the type
    /// inherits the game's own ship selection with no vehicle asset of our own.
    ///
    /// WHY OnGamePreload
    ///
    /// Prefabs must exist before they are baked into entities. HotelZoneSystem creates its zones at
    /// the same point and for the same reason; a prefab added after baking is not picked up.
    ///
    /// WHAT IS DELIBERATELY NOT CHANGED YET
    ///
    /// m_DefaultVehicleInterval and m_StopDuration are copied from the template untouched. A cruise
    /// wants a long interval and a dwell of one to two in-game days, but m_StopDuration ships at 1f
    /// and its units are not yet known — a day is 262,144 frames, which is almost certainly outside
    /// what the field was authored to express. Changing it blind would confuse "the tool does not
    /// appear" with "the tool appears and the ship behaves oddly", and those need different fixes.
    /// Measure first.
    /// </summary>
    public partial class CruiseLineSystem : GameSystemBase
    {
        private const string kLineName = "TourismOverhaul PassengerCruiseLine";

        /// <summary>The mod's own cruise ship, a copy of the stock passenger ship. See <see cref="CreateCruiseShip"/>.</summary>
        private const string kShipName = "TourismOverhaul CruiseShip";

        // Served from the mod's own UI folder, same route as the zone icons: the file lives in
        // ui/src/images and reaches this path because webpack's asset/resource rule emits it to
        // images/ with publicPath coui://ui-mods/. Prefixed because ui-mods is shared across every
        // installed UI mod.
        private const string kIcon = "coui://ui-mods/images/tourism-overhaul-cruise-line.svg";

        private PrefabSystem m_PrefabSystem;
        private EntityQuery m_TransportLinePrefabQuery;

        /// <summary>
        /// Vehicle interval for the cruise line, chosen to pin the fleet at a single ship.
        ///
        /// TransportLineSystem:189 sizes the fleet with
        /// <c>CalculateVehicleCount(interval, stableDuration)</c>, which is
        /// <c>max(1, round(lineDuration / max(1, vehicleInterval)))</c> (:906-909), and
        /// stableDuration is the line's stop durations summed over its waypoints (:470). Any
        /// interval comfortably larger than that sum rounds the division to zero, and the max()
        /// floor makes the answer one.
        ///
        /// A thousand is far above what any plausible itinerary accumulates, so this holds at one
        /// ship however many ports the player adds — which is what a cruise line should be. The
        /// interval the line then reports (:190) becomes the full round trip, so the single vessel
        /// sails continuously rather than idling at a depot.
        /// </summary>
        private const float kCruiseVehicleInterval = 1000f;

        private TransportLinePrefab m_CruiseLine;
        private bool m_Attempted;

        private WatercraftPrefab m_CruiseShip;
        private bool m_ShipAttempted;
        private EntityQuery m_PassengerShipQuery;
        private EntityQuery m_ThemeQuery;
        private CityConfigurationSystem m_CityConfiguration;

        /// <summary>The theme the ship's requirement currently names, so it is only rewritten when that changes.</summary>
        private Entity m_RequirementTheme;
        private bool m_WarnedNoOtherTheme;

        /// <summary>
        /// The prefab entity for the cruise ship, or Entity.Null before it is baked.
        /// CruiseVoyageSystem names it as the cruise route's vehicle model.
        /// </summary>
        public Entity ShipPrefabEntity
        {
            get
            {
                if (m_CruiseShip == null || m_PrefabSystem == null)
                {
                    return Entity.Null;
                }

                try
                {
                    return m_PrefabSystem.GetEntity(m_CruiseShip);
                }
                catch (System.Exception)
                {
                    return Entity.Null;
                }
            }
        }

        /// <summary>Whether the cruise line prefab exists. For diagnostics.</summary>
        public bool LineCreated => m_CruiseLine != null;

        /// <summary>
        /// The prefab entity for the cruise line, or Entity.Null before it is baked.
        ///
        /// This is how CruiseVoyageSystem tells a cruise ship from an ordinary passenger ship: a
        /// vehicle's CurrentRoute names a route, and the route's PrefabRef names this. There is no
        /// flag on the vehicle itself that distinguishes the two.
        /// </summary>
        public Entity LinePrefabEntity
        {
            get
            {
                if (m_CruiseLine == null || m_PrefabSystem == null)
                {
                    return Entity.Null;
                }

                try
                {
                    return m_PrefabSystem.GetEntity(m_CruiseLine);
                }
                catch (System.Exception)
                {
                    // Not yet baked into an entity. Callers treat null as "not ready".
                    return Entity.Null;
                }
            }
        }

        // The prefabs are a one-off at preload. What runs on an interval is keeping the ship's capacity
        // on the setting and its theme requirement on the city's other theme, both cheap.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 512;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            m_TransportLinePrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<TransportLineData>(),
                ComponentType.ReadOnly<RouteData>());

            m_PassengerShipQuery = GetEntityQuery(
                ComponentType.ReadOnly<WatercraftData>(),
                ComponentType.ReadOnly<PublicTransportVehicleData>(),
                ComponentType.ReadOnly<PrefabData>());

            m_ThemeQuery = GetEntityQuery(ComponentType.ReadOnly<ThemeData>());
            m_CityConfiguration = World.GetOrCreateSystemManaged<CityConfigurationSystem>();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);

            CreateCruiseLine();
            CreateCruiseShip();
        }

        protected override void OnGameLoadingComplete(
            Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // Free second attempt, for the case where the transport line prefabs were not yet baked
            // at preload. Costs nothing when the first attempt worked.
            CreateCruiseLine();
            CreateCruiseShip();
            MaintainCruiseShip();
        }

        protected override void OnUpdate()
        {
            MaintainCruiseShip();
        }

        /// <summary>The Cruise ship passengers setting, which is this ship's capacity.</summary>
        private static int SettingCapacity() =>
            Mod.Settings != null ? math.clamp(Mod.Settings.CruiseShipCapacity, 100, 5000) : 2000;

        /// <summary>
        /// Creates the cruise ship: the stock passenger ship, copied, with the setting as its capacity.
        ///
        /// Only its capacity differs, and that is the point. The stock ship's capacity is authored on its
        /// prefab (PublicTransport.m_PassengerCapacity), which every ordinary passenger ship line in the
        /// city shares, so it cannot be changed for the cruise line alone; a prefab of our own can. The
        /// look is the stock ship's — PrefabBase.Clone copies every component by value, so the meshes
        /// and materials are referenced, not duplicated — because a mod cannot author new 3D art.
        ///
        /// Two things keep it where it belongs, both through the game's own vehicle selection
        /// (TransportVehicleSelectData.GetRandomVehicle):
        ///
        ///   - The cruise route names it as its vehicle model (CruiseVoyageSystem). A model the route
        ///     names gets priority 2 and always wins, and TransportLineSystem.CheckVehicles abandons any
        ///     vehicle on the line that is not of it, so the line swaps its ship for this one.
        ///   - It requires the theme the city is not using (<see cref="MaintainCruiseShip"/>).
        ///     VehicleSelectRequirementData.CheckRequirements passes a theme requirement only when it is
        ///     the city's default theme, or when the line has explicitly chosen this model — so no
        ///     ordinary ferry line ever picks it, and the cruise line always can. Any theme the stock
        ///     ship itself carried is dropped for that reason.
        ///
        /// Created at preload, with the line, for the same reason: a prefab has to exist before the save
        /// that references it is loaded.
        /// </summary>
        private void CreateCruiseShip()
        {
            if (m_CruiseShip != null)
            {
                return;
            }

            WatercraftPrefab template = FindPassengerShipTemplate();

            if (template == null)
            {
                if (!m_ShipAttempted)
                {
                    m_ShipAttempted = true;
                    return;
                }

                Mod.Log.Warn("No passenger ship prefab found to base the cruise ship on; the cruise line uses the stock ship.");
                return;
            }

            m_ShipAttempted = true;

            try
            {
                WatercraftPrefab ship = (WatercraftPrefab)template.Clone(kShipName);
                PublicTransport transport = ship.GetComponent<PublicTransport>();
                int authored = transport.m_PassengerCapacity;
                transport.m_PassengerCapacity = SettingCapacity();

                if (ship.GetComponent<ThemeObject>() != null)
                {
                    ship.Remove<ThemeObject>();
                }

                // And the stock ship's old names. ObsoleteIdentifiers lists the IDs a prefab used to
                // have ("Passenger Ship", "ShipPassenger01_Propping") so older saves still find it;
                // copied, the cruise ship claimed them too — the game logged "Duplicate prefab ID" for
                // both — and a saved ordinary passenger ship could have resolved to the cruise ship.
                if (ship.GetComponent<ObsoleteIdentifiers>() != null)
                {
                    ship.Remove<ObsoleteIdentifiers>();
                }

                if (!m_PrefabSystem.AddPrefab(ship))
                {
                    Mod.Log.Warn($"PrefabSystem rejected the cruise ship prefab \"{kShipName}\".");
                    return;
                }

                m_CruiseShip = ship;

                var parts = new System.Collections.Generic.List<string>();

                foreach (ComponentBase component in ship.components)
                {
                    parts.Add(component.GetType().Name);
                }

                Mod.Log.Info(
                    $"Created vehicle prefab \"{kShipName}\" from \"{template.name}\" "
                    + $"(capacity {authored} -> {transport.m_PassengerCapacity}, size class {ship.m_SizeClass}; "
                    + $"components: {string.Join(", ", parts)}).");
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not create the cruise ship prefab: {e.Message}");
            }
        }

        /// <summary>
        /// The stock passenger ship to copy: a transport-line watercraft of the cruise line's size class,
        /// the largest if there are several, never our own copy.
        /// </summary>
        private WatercraftPrefab FindPassengerShipTemplate()
        {
            SizeClass sizeClass = m_CruiseLine != null ? m_CruiseLine.m_SizeClass : SizeClass.Large;
            NativeArray<Entity> prefabs = m_PassengerShipQuery.ToEntityArray(Allocator.Temp);
            WatercraftPrefab best = null;
            int bestCapacity = -1;

            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    PublicTransportVehicleData data = EntityManager.GetComponentData<PublicTransportVehicleData>(prefabs[i]);
                    WatercraftData watercraft = EntityManager.GetComponentData<WatercraftData>(prefabs[i]);

                    if ((data.m_PurposeMask & PublicTransportPurpose.TransportLine) == 0
                        || watercraft.m_SizeClass != sizeClass
                        || data.m_PassengerCapacity <= bestCapacity
                        || !m_PrefabSystem.TryGetPrefab(prefabs[i], out WatercraftPrefab prefab)
                        || prefab == null
                        || prefab.name == kShipName)
                    {
                        continue;
                    }

                    best = prefab;
                    bestCapacity = data.m_PassengerCapacity;
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not search for a passenger ship template: {e.Message}");
            }
            finally
            {
                prefabs.Dispose();
            }

            return best;
        }

        /// <summary>
        /// Keeps the ship's capacity on the setting, and its theme requirement on a theme the city is not
        /// using. Both are written to the baked prefab entity, which is what vehicle selection and
        /// boarding read: PublicTransportVehicleData.m_PassengerCapacity is the space a passenger checks
        /// in ResidentAISystem.TryEnterVehicle, for ships already sailing as much as for new ones.
        /// </summary>
        private void MaintainCruiseShip()
        {
            Entity ship = ShipPrefabEntity;

            if (ship == Entity.Null || !EntityManager.Exists(ship))
            {
                return;
            }

            int capacity = SettingCapacity();

            if (EntityManager.HasComponent<PublicTransportVehicleData>(ship))
            {
                PublicTransportVehicleData data = EntityManager.GetComponentData<PublicTransportVehicleData>(ship);

                if (data.m_PassengerCapacity != capacity)
                {
                    data.m_PassengerCapacity = capacity;
                    EntityManager.SetComponentData(ship, data);
                    m_CruiseShip.GetComponent<PublicTransport>().m_PassengerCapacity = capacity;
                    Mod.Log.Info($"Cruise ship capacity set to {capacity}.");
                }
            }

            Entity defaultTheme = m_CityConfiguration != null ? m_CityConfiguration.defaultTheme : Entity.Null;
            Entity other = Entity.Null;
            NativeArray<Entity> themes = m_ThemeQuery.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < themes.Length && other == Entity.Null; i++)
            {
                if (themes[i] != defaultTheme)
                {
                    other = themes[i];
                }
            }

            themes.Dispose();

            if (other == Entity.Null)
            {
                if (!m_WarnedNoOtherTheme)
                {
                    m_WarnedNoOtherTheme = true;
                    Mod.Log.Warn("Only one theme is loaded, so ordinary passenger ship lines may also pick the cruise ship.");
                }

                return;
            }

            if (other == m_RequirementTheme)
            {
                return;
            }

            DynamicBuffer<ObjectRequirementElement> requirements = EntityManager.HasBuffer<ObjectRequirementElement>(ship)
                ? EntityManager.GetBuffer<ObjectRequirementElement>(ship)
                : EntityManager.AddBuffer<ObjectRequirementElement>(ship);

            requirements.Clear();
            requirements.Add(new ObjectRequirementElement(other, 0, ObjectRequirementType.IgnoreExplicit));
            m_RequirementTheme = other;
        }

        private void CreateCruiseLine()
        {
            if (m_CruiseLine != null)
            {
                return;
            }

            TransportLinePrefab template = FindPassengerShipLineTemplate();

            if (template == null)
            {
                // Only worth saying once. At preload the prefab set may legitimately be empty, and
                // a warning on every load would be noise rather than information.
                if (!m_Attempted)
                {
                    m_Attempted = true;
                    return;
                }

                Mod.Log.Warn(
                    "No passenger ship line prefab found; the Passenger Cruise Line tool is unavailable.");
                return;
            }

            m_Attempted = true;
            m_CruiseLine = ClonePassengerShipLine(template);
        }

        /// <summary>
        /// The stock passenger ship line, identified by what it does rather than by its name.
        ///
        /// Matching on TransportLineData rather than a prefab name keeps this working if Colossal
        /// renames the asset, and it is the same set of fields the boarding code itself reads.
        /// Cargo lines share the ship transport type, hence the explicit exclusion.
        /// </summary>
        private TransportLinePrefab FindPassengerShipLineTemplate()
        {
            NativeArray<Entity> prefabs = m_TransportLinePrefabQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    TransportLineData data =
                        EntityManager.GetComponentData<TransportLineData>(prefabs[i]);

                    if (data.m_TransportType != TransportType.Ship
                        || !data.m_PassengerTransport
                        || data.m_CargoTransport)
                    {
                        continue;
                    }

                    if (m_PrefabSystem.TryGetPrefab(prefabs[i], out TransportLinePrefab prefab)
                        && prefab != null)
                    {
                        return prefab;
                    }
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not search for a passenger ship line template: {e.Message}");
            }
            finally
            {
                prefabs.Dispose();
            }

            return null;
        }

        private TransportLinePrefab ClonePassengerShipLine(TransportLinePrefab template)
        {
            try
            {
                TransportLinePrefab line = ScriptableObject.CreateInstance<TransportLinePrefab>();
                line.name = kLineName;

                // RoutePrefab: how the drawn line looks on the map.
                line.m_Material = template.m_Material;
                line.m_Width = template.m_Width;
                line.m_SegmentLength = template.m_SegmentLength;
                line.m_LocaleID = template.m_LocaleID;

                // The one visual departure from the template. A cruise line drawn in the passenger
                // ship line's colour is indistinguishable from it on the map, which matters more
                // here than usual because both run over the same water. This is the funnel orange
                // from the tool icon, so the tile and the line agree.
                line.m_Color = new Color(0.886f, 0.408f, 0.235f);

                // Pedestrian access, as the template has it. This field is not optional and it took
                // a while to find that out.
                //
                // It was previously forced to None to stop the city's own citizens boarding, since
                // a cruise ship holds at the quay in the boarding state for the whole shore leave
                // and commuters would fill it and be carried off the map when it sailed. That does
                // work — but it works by removing the line's passenger machinery altogether, not by
                // excluding locals from it. TransportLinePrefab:75 gates WaitingPassengers on the
                // waypoint archetype on this field, TransportStop:71 does the same for the stop,
                // and WaypointConnectionSystem:1203 uses it to find the access lane. Set to None
                // there is no lane, no waiting queue, and nobody can board anywhere — which is why
                // the line panel read 0 / 2,800 and why no cohort could ever be carried in.
                //
                // Since the whole feature is passengers riding the ship, the access has to be open.
                //
                // Locals are kept off by cost rather than by a closed door, and the cost is already
                // there. PathUtils:1562-1563 adds max(m_VehicleInterval * 0.5, averageWaitingTime)
                // to the time axis of every boarding decision, and m_DefaultVehicleInterval is 1000
                // on this line to pin the fleet at one vessel — so boarding a cruise ship is priced
                // at around five hundred against a bus that costs a few. A commuter with any other
                // way to travel will always take it.
                //
                // A cruise passenger at a map-edge connection has no other way, and pathfinding
                // charges cost only to choose between alternatives. With one option the penalty is
                // free. That asymmetry is the whole reason this works without a second mechanism:
                // the same number that keeps the fleet at one ship keeps the locals off it.
                //
                // If they turn out to board anyway, m_TicketPrice is the next lever
                // (PathUtils:1564) — but note it is charged to the passenger at
                // ResidentAISystem:3922-3928, so it would empty the tourists' wallets too, and the
                // wallet is what the rest of the mod exists to spend. Measure before reaching for
                // it.
                line.m_AccessConnectionType = template.m_AccessConnectionType;

                line.m_RouteConnectionType = template.m_RouteConnectionType;
                line.m_AccessTrackType = template.m_AccessTrackType;
                line.m_RouteTrackType = template.m_RouteTrackType;
                line.m_AccessRoadType = template.m_AccessRoadType;
                line.m_RouteRoadType = template.m_RouteRoadType;

                // Ship. Vessels are selected by transport type rather than named on the line, so
                // this is what makes the cruise line use the game's own ship with no asset of ours.
                line.m_TransportType = template.m_TransportType;

                line.m_SizeClass = template.m_SizeClass;
                line.m_PathfindPrefab = template.m_PathfindPrefab;
                line.m_VehicleNotification = template.m_VehicleNotification;

                // One ship, template dwell.
                //
                // m_StopDuration is deliberately left at the template's value. It reads like a
                // dwell instruction and is not one — it is the line's *planning* figure, applied at
                // every stop and summed into the fleet calculation at TransportLineSystem:470. Two
                // things went wrong when it was set to a twelve-hour shore leave: the game decided
                // the line needed thirty vessels, and the ship waited at the outside connection as
                // well, because the figure belongs to the line rather than to any one stop.
                //
                // Holding a ship in port is CruiseVoyageSystem's job instead, through the vehicle's
                // own departure frame, which it applies only at a city terminal.
                line.m_DefaultVehicleInterval = kCruiseVehicleInterval;
                line.m_DefaultUnbunchingFactor = template.m_DefaultUnbunchingFactor;
                line.m_StopDuration = template.m_StopDuration;

                // Passengers, not freight. TransportLinePrefab:87-90 adds WaitingPassengers to
                // waypoints only when this is set, and a cruise line without it would have nowhere
                // for anyone to wait.
                line.m_PassengerTransport = true;
                line.m_CargoTransport = false;

                // Toolbar placement, borrowed from the line we copied so it lands in the ship group
                // beside it. Priority is nudged past the template's so it sorts immediately after
                // the passenger ship line rather than ahead of it.
                UIObject templateUI = template.GetComponent<UIObject>();

                if (templateUI != null)
                {
                    UIObject ui = line.AddComponent<UIObject>();
                    ui.m_Group = templateUI.m_Group;
                    ui.m_Priority = templateUI.m_Priority + 10;
                    // Falls back to the stock icon if ours is missing, so a failed UI build leaves
                    // a usable tile rather than a blank one. The zone icons' absence was silent in
                    // exactly this way.
                    ui.m_Icon = string.IsNullOrEmpty(kIcon) ? templateUI.m_Icon : kIcon;
                    ui.m_IsDebugObject = false;
                }
                else
                {
                    Mod.Log.Warn(
                        $"Passenger ship line template \"{template.name}\" has no UIObject; the "
                        + "cruise line tool will have no toolbar tile.");
                }

                if (!m_PrefabSystem.AddPrefab(line))
                {
                    Mod.Log.Warn($"PrefabSystem rejected the cruise line prefab \"{kLineName}\".");
                    return null;
                }

                // Logged in full because the frontend cannot be read, only measured — the UI ships
                // packed inside .cok archives. If the tile does not appear, these are the values
                // the next attempt has to work from, and gathering them now saves a build round.
                Mod.Log.Info(
                    $"Cruise line access connection: {line.m_AccessConnectionType}.");

                Mod.Log.Info(
                    $"Created route prefab \"{kLineName}\" from \"{template.name}\" "
                    + $"(transport type {template.m_TransportType}, size class {template.m_SizeClass}, "
                    + $"locale id \"{template.m_LocaleID}\", "
                    + $"UI group \"{(templateUI?.m_Group != null ? templateUI.m_Group.name : "none")}\", "
                    + $"priority {templateUI?.m_Priority.ToString() ?? "none"} -> "
                    + $"{(templateUI != null ? (templateUI.m_Priority + 10).ToString() : "none")}, "
                    + $"icon {kIcon}).");

                return line;
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not create the cruise line prefab: {e.Message}");
                return null;
            }
        }
    }
}
