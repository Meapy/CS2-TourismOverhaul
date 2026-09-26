using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Game.Routes;
using Game.Vehicles;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using PublicTransport = Game.Vehicles.PublicTransport;
using TransportStop = Game.Routes.TransportStop;

namespace TourismOverhaul.Systems
{
    public partial class CruiseVoyageSystem
    {
        /// <summary>
        /// What the vessel job saw of one cruise ship, for the main thread to act on next update.
        ///
        /// Everything here is written by the game's own jobs (the vessel's transport state and
        /// target, the stops' boarding claims and queues, the passengers aboard), which is exactly
        /// what the main thread cannot read without waiting for the frame's vehicle and creature
        /// jobs. The job reads it after them; the main thread reads this copy.
        /// </summary>
        private struct VesselObservation
        {
            public Entity m_Vehicle;
            public uint m_Frame;
            public PublicTransportFlags m_State;
            public uint m_DepartureFrame;

            /// <summary>Boarding at a stop on its route that names it in BoardingVehicle.</summary>
            public bool m_Alongside;
            public Entity m_Stop;
            public bool m_AtOutsideConnection;

            /// <summary>The line's map-edge connection, how many wait there, and who is aboard outbound.</summary>
            public Entity m_Connection;
            public int m_Waiting;
            public int m_OutboundAboard;

            /// <summary>Everyone in the vessel's passenger buffer, whoever they are: how full it physically is.</summary>
            public int m_PassengersAboard;

            /// <summary>Visitors aboard outbound: tourist households, not counting last voyage's homeward parties.</summary>
            public int m_TouristsAboard;

            /// <summary>On a call: alongside its own terminal, reclaimed it this update, or neither.</summary>
            public bool m_AtOwnTerminal;
            public bool m_Reclaimed;
            public bool m_ClaimRestored;
            public bool m_AwayFromTerminal;

            // Why it left, if it did: the readings ReportEscape logs.
            public bool m_HasPathOwner;
            public Game.Pathfind.PathFlags m_PathState;
            public bool m_HasLane;
            public WatercraftLaneFlags m_LaneFlags;
            public Entity m_Target;
            public bool m_TargetExists;
        }

        /// <summary>A change to a vessel's departure frame decided on the main thread, applied by the job.</summary>
        private struct HoldRequest
        {
            public Entity m_Vehicle;
            public uint m_Frame;

            /// <summary>False: hold until at least m_Frame. True: let it go at m_Frame if held longer.</summary>
            public bool m_Release;
        }

        /// <summary>
        /// The per-update half of serving the cruise ships, as a Burst job in the same frame.
        ///
        /// On the main thread this had to read Game.Vehicles.PublicTransport and the vessel's Target
        /// before it could decide anything, and the first such read waited for every vehicle and
        /// creature job in the frame: 4-6 ms per update, against a few hundredths of a millisecond of
        /// actual work. Here it runs after those jobs instead.
        ///
        /// It does the parts that must happen in the frame the vessel is seen, because a hold has to
        /// be written inside the sixty frames TransportBoardingHelpers:368 gives a boarding ship:
        ///
        ///   - applies the holds and releases the main thread decided (<see cref="HoldRequest"/>);
        ///   - re-asserts the holds that are re-asserted every update — a call's hold at any stop but
        ///     the map edge (BeginBoarding:388 rewrites it to frame + 60 whenever boarding begins),
        ///     and a load's hold at the connection until its deadline;
        ///   - keeps the stops priced (ClearOutsideConnectionWait) and restores a lost quay claim
        ///     (ReclaimQuay);
        ///   - and records a <see cref="VesselObservation"/> per ship.
        ///
        /// Everything that logs, creates households or starts or ends a call stays on the main thread
        /// and works from those observations on the next update. Starting a load or a call therefore
        /// happens sixteen frames after the ship is seen rather than on the same update; the ship
        /// still has at least 44 of its 60 frames, so the hold lands in time.
        /// </summary>
        [BurstCompile]
        private struct VesselJob : IJob
        {
            [ReadOnly] public NativeList<Entity> m_Vehicles;
            [ReadOnly] public NativeList<HoldRequest> m_Requests;
            public NativeList<VesselObservation> m_Observations;
            public EntityCommandBuffer m_CommandBuffer;

            public Entity m_CruiseLinePrefab;
            public uint m_Frame;
            public uint m_ShoreLeave;
            public uint m_LastCall;
            public uint m_Grace;
            public uint m_Spread;

            [ReadOnly] public EntityStorageInfoLookup m_Entities;
            [ReadOnly] public ComponentLookup<CurrentRoute> m_CurrentRoutes;
            [ReadOnly] public ComponentLookup<PrefabRef> m_PrefabRefs;
            [ReadOnly] public BufferLookup<RouteWaypoint> m_RouteWaypoints;
            [ReadOnly] public ComponentLookup<Connected> m_Connected;
            [ReadOnly] public ComponentLookup<Owner> m_Owners;
            [ReadOnly] public ComponentLookup<Game.Objects.OutsideConnection> m_OutsideConnections;
            [ReadOnly] public ComponentLookup<Target> m_Targets;
            [ReadOnly] public ComponentLookup<Components.CruiseCall> m_CruiseCalls;
            [ReadOnly] public ComponentLookup<Components.CruiseManifest> m_CruiseManifests;
            [ReadOnly] public BufferLookup<Passenger> m_Passengers;
            [ReadOnly] public ComponentLookup<Game.Creatures.Resident> m_Residents;
            [ReadOnly] public ComponentLookup<HouseholdMember> m_HouseholdMembers;
            [ReadOnly] public ComponentLookup<Components.CruisePassenger> m_CruisePassengers;
            [ReadOnly] public ComponentLookup<TouristHousehold> m_TouristHouseholds;
            [ReadOnly] public ComponentLookup<Game.Pathfind.PathOwner> m_PathOwners;
            [ReadOnly] public ComponentLookup<WatercraftCurrentLane> m_WatercraftLanes;

            public ComponentLookup<PublicTransport> m_PublicTransports;
            public ComponentLookup<WaitingPassengers> m_WaitingPassengers;
            public ComponentLookup<TransportStop> m_TransportStops;
            public ComponentLookup<BoardingVehicle> m_BoardingVehicles;

            public void Execute()
            {
                m_Observations.Clear();

                // The main thread's decisions first, as they were applied in its update before.
                for (int r = 0; r < m_Requests.Length; r++)
                {
                    HoldRequest request = m_Requests[r];

                    if (!m_PublicTransports.HasComponent(request.m_Vehicle))
                    {
                        continue;
                    }

                    PublicTransport held = m_PublicTransports[request.m_Vehicle];

                    if (request.m_Release ? held.m_DepartureFrame > request.m_Frame : held.m_DepartureFrame < request.m_Frame)
                    {
                        held.m_DepartureFrame = request.m_Frame;
                        m_PublicTransports[request.m_Vehicle] = held;
                    }
                }

                for (int i = 0; i < m_Vehicles.Length; i++)
                {
                    Entity vehicle = m_Vehicles[i];

                    if (!IsOnCruiseLine(vehicle))
                    {
                        continue;
                    }

                    PublicTransport transport = m_PublicTransports[vehicle];

                    // Both run wherever the vessel is, because both are about the stop rather than
                    // the ship: the queue has to be built while it is away, and the stop has to stay
                    // worth pathing to for that to happen.
                    ClearOutsideConnectionWait(vehicle);

                    var seen = new VesselObservation
                    {
                        m_Vehicle = vehicle,
                        m_Frame = m_Frame,
                        m_State = transport.m_State,
                    };

                    TryGetOutsideConnection(vehicle, out seen.m_Connection, out seen.m_Waiting);
                    seen.m_OutboundAboard = CountOutboundAboard(vehicle, out seen.m_TouristsAboard);
                    seen.m_PassengersAboard = m_Passengers.HasBuffer(vehicle) ? m_Passengers[vehicle].Length : 0;

                    // Where the ship is has to be established before deciding anything. Holding is
                    // only ever valid at the port the call belongs to.
                    seen.m_Alongside = (transport.m_State & PublicTransportFlags.Boarding) != 0
                                       && TryResolveCurrentStop(vehicle, out seen.m_Stop, out seen.m_AtOutsideConnection);

                    bool atConnection = seen.m_Alongside && seen.m_AtOutsideConnection;

                    if (m_CruiseCalls.HasComponent(vehicle))
                    {
                        Components.CruiseCall call = m_CruiseCalls[vehicle];

                        // A call the main thread has just closed is still here until playback; it
                        // holds nothing.
                        if (m_Frame < call.m_ReboardFrame)
                        {
                            // Re-asserted every update at every stop but the map edge. A live call
                            // means the vessel owes its quay a dwell, and the map edge is the only
                            // place a hold would be wrong — a test that cannot flicker with the
                            // boarding state, unlike "alongside its own terminal".
                            if (!atConnection && transport.m_DepartureFrame < call.m_ReboardFrame)
                            {
                                transport.m_DepartureFrame = call.m_ReboardFrame;
                            }

                            seen.m_AtOwnTerminal = seen.m_Alongside && !seen.m_AtOutsideConnection
                                                   && seen.m_Stop == call.m_Terminal;

                            if (!seen.m_AtOwnTerminal)
                            {
                                seen.m_Reclaimed = ReclaimQuay(vehicle, transport, call.m_Terminal, out seen.m_ClaimRestored);

                                if (!seen.m_Reclaimed && call.m_Escaped == 0)
                                {
                                    seen.m_AwayFromTerminal = true;
                                    RecordEscapeReadings(vehicle, ref seen);
                                }
                            }
                        }
                    }
                    else if (m_CruiseManifests.HasComponent(vehicle))
                    {
                        // A load in progress is followed by its manifest, not by whether the vessel
                        // looks alongside: the flag flickers mid-load. Held at the connection until
                        // the deadline; the main thread marks it spent and releases the ship when the
                        // deadline passes.
                        Components.CruiseManifest manifest = m_CruiseManifests[vehicle];

                        if (manifest.m_Loaded == 0 && m_Frame < manifest.m_LoadDeadline && atConnection
                            && transport.m_DepartureFrame < manifest.m_LoadDeadline)
                        {
                            transport.m_DepartureFrame = manifest.m_LoadDeadline;
                        }
                    }

                    seen.m_DepartureFrame = transport.m_DepartureFrame;
                    m_PublicTransports[vehicle] = transport;
                    m_Observations.Add(seen);
                }
            }

            private bool IsOnCruiseLine(Entity vehicle)
            {
                if (!m_CurrentRoutes.HasComponent(vehicle) || !m_PublicTransports.HasComponent(vehicle))
                {
                    return false;
                }

                Entity route = m_CurrentRoutes[vehicle].m_Route;

                return route != Entity.Null
                       && m_PrefabRefs.HasComponent(route)
                       && m_PrefabRefs[route].m_Prefab == m_CruiseLinePrefab;
            }

            /// <summary>The entity in a stop's owner chain that carries OutsideConnection.</summary>
            private Entity OutsideConnectionOf(Entity stop)
            {
                Entity walk = stop;

                for (int hop = 0; hop < 8; hop++)
                {
                    if (m_OutsideConnections.HasComponent(walk))
                    {
                        return walk;
                    }

                    if (!m_Owners.HasComponent(walk))
                    {
                        return Entity.Null;
                    }

                    Entity owner = m_Owners[walk].m_Owner;

                    if (owner == Entity.Null || !m_Entities.Exists(owner))
                    {
                        return Entity.Null;
                    }

                    walk = owner;
                }

                return Entity.Null;
            }

            private bool StopIsOutsideConnection(Entity stop)
            {
                return stop != Entity.Null && m_Entities.Exists(stop) && OutsideConnectionOf(stop) != Entity.Null;
            }

            /// <summary>
            /// The line's map-edge connection and how many people are queued at it, resolved from the
            /// route so the queue can be built while the ship is elsewhere. Households are created at
            /// the connection object itself (TouristDemandSystem.SpawnTouristHouseholds:1283-1288).
            /// </summary>
            private bool TryGetOutsideConnection(Entity vehicle, out Entity connection, out int waiting)
            {
                connection = Entity.Null;
                waiting = 0;

                if (!m_CurrentRoutes.HasComponent(vehicle))
                {
                    return false;
                }

                Entity route = m_CurrentRoutes[vehicle].m_Route;

                if (route == Entity.Null || !m_Entities.Exists(route) || !m_RouteWaypoints.HasBuffer(route))
                {
                    return false;
                }

                DynamicBuffer<RouteWaypoint> waypoints = m_RouteWaypoints[route];

                for (int i = 0; i < waypoints.Length; i++)
                {
                    Entity waypoint = waypoints[i].m_Waypoint;

                    if (waypoint == Entity.Null || !m_Entities.Exists(waypoint) || !m_Connected.HasComponent(waypoint))
                    {
                        continue;
                    }

                    Entity stop = m_Connected[waypoint].m_Connected;

                    if (!StopIsOutsideConnection(stop))
                    {
                        continue;
                    }

                    connection = OutsideConnectionOf(stop);

                    if (m_WaitingPassengers.HasComponent(waypoint))
                    {
                        waiting = m_WaitingPassengers[waypoint].m_Count;
                    }

                    return connection != Entity.Null;
                }

                return false;
            }

            /// <summary>
            /// People aboard who are not already going home. A homeward party is released at this very
            /// stop, so counting it would report the ship fuller than it is about to be.
            /// </summary>
            private int CountOutboundAboard(Entity vehicle, out int tourists)
            {
                tourists = 0;

                if (!m_Passengers.HasBuffer(vehicle))
                {
                    return 0;
                }

                DynamicBuffer<Passenger> manifest = m_Passengers[vehicle];
                int aboard = 0;

                for (int i = 0; i < manifest.Length; i++)
                {
                    Entity household = HouseholdOf(manifest[i].m_Passenger);

                    if (household == Entity.Null)
                    {
                        continue;
                    }

                    if (m_CruisePassengers.HasComponent(household) && m_CruisePassengers[household].m_Homeward != 0)
                    {
                        continue;
                    }

                    aboard++;

                    if (m_TouristHouseholds.HasComponent(household))
                    {
                        tourists++;
                    }
                }

                return aboard;
            }

            private Entity HouseholdOf(Entity creature)
            {
                if (creature == Entity.Null || !m_Residents.HasComponent(creature))
                {
                    return Entity.Null;
                }

                Entity citizen = m_Residents[creature].m_Citizen;

                if (citizen == Entity.Null || !m_HouseholdMembers.HasComponent(citizen))
                {
                    return Entity.Null;
                }

                Entity household = m_HouseholdMembers[citizen].m_Household;

                return household != Entity.Null && m_Entities.Exists(household) ? household : Entity.Null;
            }

            /// <summary>
            /// The building the ship is alongside, and whether it is the map-edge connection.
            ///
            /// Through BoardingVehicle, which is on the stop, not the waypoint (TransportStop:58-86,
            /// TransportBoardingHelpers:369, StopBoarding:797). The owner chain is walked in full,
            /// because with the line's access connection restored a waypoint connects to a spawn
            /// location one or more levels below the building (WaypointConnectionSystem:1193), the
            /// same walk GetTransportStationFromStop makes (TransportWatercraftAISystem:865-888).
            /// </summary>
            private bool TryResolveCurrentStop(Entity vehicle, out Entity building, out bool isOutsideConnection)
            {
                building = Entity.Null;
                isOutsideConnection = false;

                Entity route = m_CurrentRoutes[vehicle].m_Route;

                if (!m_RouteWaypoints.HasBuffer(route))
                {
                    return false;
                }

                DynamicBuffer<RouteWaypoint> waypoints = m_RouteWaypoints[route];

                for (int i = 0; i < waypoints.Length; i++)
                {
                    Entity waypoint = waypoints[i].m_Waypoint;

                    if (waypoint == Entity.Null || !m_Entities.Exists(waypoint) || !m_Connected.HasComponent(waypoint))
                    {
                        continue;
                    }

                    Entity stop = m_Connected[waypoint].m_Connected;

                    if (stop == Entity.Null || !m_Entities.Exists(stop))
                    {
                        continue;
                    }

                    if (!m_BoardingVehicles.HasComponent(stop) || m_BoardingVehicles[stop].m_Vehicle != vehicle)
                    {
                        continue;
                    }

                    building = stop;
                    isOutsideConnection = m_OutsideConnections.HasComponent(stop);

                    Entity walk = stop;

                    for (int hop = 0; hop < 8; hop++)
                    {
                        if (!m_Owners.HasComponent(walk))
                        {
                            break;
                        }

                        Entity owner = m_Owners[walk].m_Owner;

                        if (owner == Entity.Null || !m_Entities.Exists(owner))
                        {
                            break;
                        }

                        building = owner;
                        walk = owner;

                        if (m_OutsideConnections.HasComponent(owner))
                        {
                            isOutsideConnection = true;
                            break;
                        }
                    }

                    return true;
                }

                return false;
            }

            /// <summary>The building a stop belongs to — the owner walk TryResolveCurrentStop makes.</summary>
            private Entity BuildingOf(Entity stop)
            {
                Entity walk = stop;

                for (int hop = 0; hop < 8; hop++)
                {
                    if (m_OutsideConnections.HasComponent(walk) || !m_Owners.HasComponent(walk))
                    {
                        break;
                    }

                    Entity owner = m_Owners[walk].m_Owner;

                    if (owner == Entity.Null || !m_Entities.Exists(owner))
                    {
                        break;
                    }

                    walk = owner;
                }

                return walk;
            }

            /// <summary>
            /// Puts the ship's name back on its own quay when the game has dropped it mid-call.
            ///
            /// StopBoarding (TransportWatercraftAISystem:797-807) honours the held departure only while
            /// the stop's BoardingVehicle names the vessel, and BoardingVehicleSystem blanks it after a
            /// load and whenever any waypoint is Updated, if it cannot match the vessel's Target back to
            /// the stop. A vessel still boarding and still targeting its call's terminal is alongside
            /// whatever that field says. Never taken from another live vessel.
            /// </summary>
            private bool ReclaimQuay(Entity vehicle, PublicTransport transport, Entity terminal, out bool restored)
            {
                restored = false;

                if ((transport.m_State & PublicTransportFlags.Boarding) == 0 || !m_Targets.HasComponent(vehicle))
                {
                    return false;
                }

                Entity waypoint = m_Targets[vehicle].m_Target;

                if (waypoint == Entity.Null || !m_Entities.Exists(waypoint) || !m_Connected.HasComponent(waypoint))
                {
                    return false;
                }

                Entity stop = m_Connected[waypoint].m_Connected;

                if (stop == Entity.Null || !m_BoardingVehicles.HasComponent(stop) || BuildingOf(stop) != terminal)
                {
                    return false;
                }

                BoardingVehicle claim = m_BoardingVehicles[stop];

                if (claim.m_Vehicle == vehicle)
                {
                    return true;
                }

                if (claim.m_Vehicle != Entity.Null && m_Entities.Exists(claim.m_Vehicle))
                {
                    return false;
                }

                claim.m_Vehicle = vehicle;
                m_BoardingVehicles[stop] = claim;
                restored = true;
                return true;
            }

            private void RecordEscapeReadings(Entity vehicle, ref VesselObservation seen)
            {
                seen.m_HasPathOwner = m_PathOwners.HasComponent(vehicle);
                seen.m_PathState = seen.m_HasPathOwner ? m_PathOwners[vehicle].m_State : default;
                seen.m_HasLane = m_WatercraftLanes.HasComponent(vehicle);
                seen.m_LaneFlags = seen.m_HasLane ? m_WatercraftLanes[vehicle].m_LaneFlags : default;
                seen.m_Target = m_Targets.HasComponent(vehicle) ? m_Targets[vehicle].m_Target : Entity.Null;
                seen.m_TargetExists = seen.m_Target != Entity.Null && m_Entities.Exists(seen.m_Target);
            }

            /// <summary>
            /// Whether this vessel's shore party is on its way back, so the pier should accept boarders:
            /// from the moment the earliest party of the call can be recalled — the same arithmetic
            /// StartCall and AdoptCarriedPassengers use, measured from the call's start.
            /// </summary>
            private bool ReturningToQuay(Entity vehicle)
            {
                if (!m_CruiseCalls.HasComponent(vehicle))
                {
                    return false;
                }

                Components.CruiseCall call = m_CruiseCalls[vehicle];

                long lastCall = m_LastCall;
                long ashoreUntil = (long)call.m_ReboardFrame - m_Grace;
                long spread = m_Spread;

                return m_Frame + lastCall >= ashoreUntil - spread;
            }

            /// <summary>
            /// Holds the map-edge stop's advertised wait at zero and the city pier's high, except in
            /// last call, when the pier is free.
            ///
            /// PathUtils:1562 prices boarding as max(m_VehicleInterval * 0.5, m_AverageWaitingTime), so a
            /// rarely-sailing line's own queue history prices the map edge out of reach, while the pier,
            /// with its doors open for hours, fills with commuters unless it is priced up. Last call is
            /// free because pricing it sent returning parties onto an ordinary ferry instead (1 of 1,015
            /// reboarded). The history that produces the figure is written, not just the figure, because
            /// WaitingPassengersSystem:177-192 rebuilds the average every 256 frames and only tags the
            /// waypoint PathfindUpdated when that changes it — and the pathfinder reads the cost only
            /// after such a tag. Concluded = wanted with one success reproduces it; ongoing is cleared.
            /// </summary>
            private void ClearOutsideConnectionWait(Entity vehicle)
            {
                if (!m_CurrentRoutes.HasComponent(vehicle))
                {
                    return;
                }

                Entity route = m_CurrentRoutes[vehicle].m_Route;

                if (route == Entity.Null || !m_Entities.Exists(route) || !m_RouteWaypoints.HasBuffer(route))
                {
                    return;
                }

                DynamicBuffer<RouteWaypoint> waypoints = m_RouteWaypoints[route];

                for (int i = 0; i < waypoints.Length; i++)
                {
                    Entity waypoint = waypoints[i].m_Waypoint;

                    if (waypoint == Entity.Null
                        || !m_Entities.Exists(waypoint)
                        || !m_WaitingPassengers.HasComponent(waypoint)
                        || !m_Connected.HasComponent(waypoint))
                    {
                        continue;
                    }

                    Entity stop = m_Connected[waypoint].m_Connected;

                    bool atMapEdge = StopIsOutsideConnection(stop);
                    bool boarding = !atMapEdge && ReturningToQuay(vehicle);
                    ushort wanted = (atMapEdge || boarding) ? (ushort)0 : kPierWaitingTime;

                    WaitingPassengers queue = m_WaitingPassengers[waypoint];
                    int successes = wanted > 0 ? 1 : 0;

                    bool drifted = queue.m_AverageWaitingTime != wanted;
                    bool stale = queue.m_OngoingAccumulation != 0
                                 || queue.m_ConcludedAccumulation != wanted
                                 || queue.m_SuccessAccumulation != successes;

                    if (drifted || stale)
                    {
                        queue.m_AverageWaitingTime = wanted;
                        queue.m_OngoingAccumulation = 0;
                        queue.m_ConcludedAccumulation = wanted;
                        queue.m_SuccessAccumulation = (ushort)successes;
                        m_WaitingPassengers[waypoint] = queue;

                        if (drifted)
                        {
                            m_CommandBuffer.AddComponent<PathfindUpdated>(waypoint);
                        }
                    }

                    SetBoardable(stop, atMapEdge || boarding);
                }
            }

            /// <summary>
            /// Opens a stop to boarding at the map edge and in last call. Active is only ever set,
            /// never cleared — clearing it on the pier also stopped the shore party getting off — and
            /// the comfort factor is 1 when boardable and never below 0 otherwise, repairing any
            /// negative value an earlier build wrote into this authored, serialized field.
            /// </summary>
            private void SetBoardable(Entity stop, bool boardable)
            {
                if (!m_TransportStops.HasComponent(stop))
                {
                    return;
                }

                TransportStop data = m_TransportStops[stop];

                StopFlags flags = boardable ? data.m_Flags | StopFlags.Active | StopFlags.AllowEnter : data.m_Flags;
                float comfort = boardable ? 1f : math.max(0f, data.m_ComfortFactor);

                if (data.m_Flags == flags && data.m_ComfortFactor == comfort)
                {
                    return;
                }

                data.m_Flags = flags;
                data.m_ComfortFactor = comfort;
                m_TransportStops[stop] = data;
            }
        }
    }
}
