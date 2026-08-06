using Game;
using Game.Citizens;
using Game.City;
using Game.Common;
using Game.Companies;
using Game.Creatures;
using Game.Economy;
using Game.Prefabs;
using Game.Routes;
using Game.Simulation;
using Game.Tools;
using Game.Vehicles;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Puts a cruise ship's passengers ashore, keeps them there, and sends them back aboard.
    ///
    /// Steps three and four of docs/CRUISE-LINE-PLAN.md. The ship, the route and the docking are
    /// all the game's own; what this adds is the cohort, which the game has no concept of.
    ///
    /// HOW THE SHIP IS HELD IN PORT
    ///
    /// The plan flagged m_StopDuration as the likely blocker, because a cruise needs a dwell of one
    /// to two in-game days and the field ships at 1f. It turns out not to be needed:
    /// Game.Vehicles.PublicTransport carries m_DepartureFrame, the frame the vessel is scheduled to
    /// leave, and pushing that forward holds the ship at the dock through the game's own scheduling
    /// rather than against it. The prefab field is left alone.
    ///
    /// HOW THE PASSENGERS SURVIVE THE EVENING
    ///
    /// TouristLeaveSystem:68 is the whole of tourist eviction:
    ///
    ///     bool num = m_LodgingProviders.HasComponent(touristHousehold.m_Hotel);
    ///     reason = (!num &amp;&amp; m_Time &gt; 0.8f) ? TouristNoHotel
    ///            : ((num3 &lt; num2 &amp;&amp; m_Time &gt; 0.7f) ? TouristNoMoney : None);
    ///
    /// Both branches key off m_Hotel naming an entity with a LodgingProvider, and the money test
    /// compares the wallet against that provider's m_Price. So a cruise passenger whose m_Hotel is
    /// the terminal, and whose terminal carries a LodgingProvider with m_Price = 0, is immune to
    /// both — through the game's own condition rather than a special case bolted next to it. No
    /// native system is disabled or mirrored.
    ///
    /// The terminal is the right anchor rather than the ship. It is a real building on the road
    /// network, so it is pathable, and it is where the passengers must return to anyway. A vehicle
    /// is neither.
    ///
    /// WHY THE STAND-IN PROVIDER DOES NOT MAKE THE TERMINAL A HOTEL
    ///
    /// Every query that treats a LodgingProvider as a hotel asks for more than that component.
    /// LodgingProviderSystem and HotelCapacitySystem both require PropertyRenter, ServiceAvailable
    /// and ProcessingCompany; the room counts in TourismPanelUISystem and TouristDemandSystem
    /// require PropertyRenter and Renter. A harbour has none of those, so it is never billed, never
    /// counted as capacity, and never reported as a hotel. The marker component
    /// CruiseTerminalLodging records which buildings we equipped so the provider can be taken off
    /// again without touching a building that legitimately has one.
    /// </summary>
    public partial class CruiseVoyageSystem : GameSystemBase
    {
        /// <summary>
        /// Shore leave, in frames. 262,144 frames is one in-game day, which is one displayed month
        /// and over an hour of real play.
        /// </summary>
        private const uint kFramesPerDay = 262144u;

        /// <summary>
        /// Fraction of shore leave reserved for getting back to the ship.
        ///
        /// Once inside this window a party is sent to the quay and stops being given new reasons to
        /// wander off. It has to be generous — walking across a city takes time, and a passenger
        /// who misses the ship is a passenger the player watches stand on the dock.
        /// </summary>
        private const float kLastCallFraction = 0.33f;

        /// <summary>
        /// Most of a stay a party may cut short, as a fraction. Some passengers have seen enough.
        /// </summary>
        private const float kEarlyReturnFraction = 0.33f;

        /// <summary>
        /// Share of a complement that stays aboard rather than going ashore at a call.
        ///
        /// Not everyone gets off at every port. These parties are simply not adopted: they keep no
        /// component of this mod's, remain in the vessel's own passenger buffer, and sail on. The
        /// ashore count and the recall therefore never see them, which is correct — they are the
        /// ship's business and not the city's.
        /// </summary>
        private const float kStayAboardFraction = 0.1f;

        /// <summary>Ship arrivals are mode 3 in ArrivalMode's road/train/air/ship ordering.</summary>
        private const byte kArrivalModeShip = 3;

        /// <summary>
        /// Most people one top-up may order, and the smallest shortfall worth ordering for.
        ///
        /// Doubles as a deadband. A gap smaller than this is left alone, because a party is the
        /// smallest thing that can be created and chasing the last few people creates a batch every
        /// update for the rest of the load — the thrash the earlier quayside top-up ran into.
        /// </summary>
        private const int kTopUpBatch = 1000;

        /// <summary>
        /// Smallest shortfall worth ordering for, in people.
        ///
        /// Small, and kept separate from the batch size on purpose: the batch is how much may be
        /// ordered at once, this is when ordering stops. Conflating them starves the end of a load,
        /// because the shortfall on a nearly full ship is by definition smaller than a batch.
        /// A party is a handful of people, so anything under about that is not worth a batch.
        /// </summary>
        private const int kQueueDeadband = 10;

        /// <summary>
        /// Advertised wait held on the city pier, to keep the line off the city's own commuters.
        ///
        /// Comfortably above the 500 that m_VehicleInterval contributes at PathUtils:1562, so this
        /// is the term that decides the cost and the stop is priced out for anyone who has another
        /// way to travel. A cruise passenger never pays it: they arrive aboard, and they leave from
        /// the quay they are already standing on.
        /// </summary>
        private const ushort kPierWaitingTime = 60000;

        /// <summary>
        /// Comfort factor held on the city pier, to add cost rather than remove it.
        ///
        /// PathUtils:1565 scales the comfort axis by <c>1 - m_ComfortFactor</c>, so the field is
        /// normally a fraction that discounts. A large negative value inverts it into a multiplier —
        /// at -100 the comfort term is charged a hundred and one times over. That is a second axis
        /// working against the stop, independent of the time axis the waiting figure drives, so a
        /// traveller who happens to weight comfort lightly is still penalised.
        /// </summary>
        private const float kPierComfortFactor = -100f;

        /// <summary>
        /// Frames between queue batches. 2048 is a hundred and twenty-eight per in-game day.
        ///
        /// Fixed rather than derived from the shortfall, and that is the whole safety property: the
        /// figures a batch is judged against take many updates to move, so ordering the shortfall
        /// every update ordered it repeatedly and ran away. Whatever the queue reads, the ceiling is
        /// one batch per interval.
        ///
        /// The queue this produces is an equilibrium, not a total. It settles where people join as
        /// fast as they give up waiting, so the figure to tune against is the plateau rather than
        /// the head count. Measured: 250 per 2048 frames plateaued at about 150; 500 per 1024, four
        /// times the rate, plateaued at about 304. It responds, but sub-linearly, because a longer
        /// queue sheds more people per unit time.
        ///
        /// Which means there is a practical ceiling here that ordering cannot pass. If the plateau
        /// stops moving as this rises, the limit is how long a citizen will wait for a vessel that
        /// visits rarely, and the lever is the line — more sailings, or a shorter dwell — not more
        /// people.
        /// </summary>
        private const uint kBatchIntervalFrames = 1024u;

        /// <summary>
        /// Longest a ship waits at the map edge for its complement to board.
        ///
        /// Loading cannot be instant — a household created this frame has no citizens for several
        /// updates — but the map edge is explicitly not a port of call, so the wait has to be short
        /// and bounded. Roughly two in-game hours: long enough for initialisation to catch up,
        /// short enough that the ship is not visibly parked offshore.
        /// </summary>
        private const uint kLoadTimeoutFrames = 21845u;

        private CruiseLineSystem m_CruiseLineSystem;
        private TouristDemandSystem m_DemandSystem;
        private SimulationSystem m_SimulationSystem;
        private EndFrameBarrier m_EndFrameBarrier;
        private PrefabSystem m_PrefabSystem;

        /// <summary>
        /// Vessel already reported as boarding somewhere this system cannot place, so the warning
        /// is written once rather than every sixteen frames for a whole call.
        /// </summary>
        private Entity m_UnresolvedShip;

        /// <summary>Earliest frame the next queue batch may be ordered. Not saved; a reload simply
        /// allows one batch immediately, which is harmless.</summary>
        private uint m_NextBatchFrame;

        /// <summary>Vessel already reported as having arrived empty, so the warning is written once.</summary>
        private Entity m_ReportedEmptyShip;

        private EntityQuery m_CruiseVehicleQuery;
        private EntityQuery m_AshoreQuery;
        private EntityQuery m_EquippedTerminalQuery;
        private EntityQuery m_ActiveCallQuery;

        /// <summary>How many people one ship has ashore, and how many of its parties are empty.</summary>
        private struct AshoreCount
        {
            public int m_People;
            public int m_EmptyParties;
        }

        /// <summary>
        /// Last update's shore-party head count, per ship. Published, not queried on demand.
        ///
        /// This used to be a chunk walk that any caller could run, and CruiseDepartureUISystem ran
        /// it from the UIUpdate phase to fill the panel's ashore row. That crashed the game the
        /// moment a docked cruise ship was clicked, with a NullReferenceException inside
        /// LookupCache.Update: the ComponentTypeHandle and BufferTypeHandle it walked with belong to
        /// *this* system's SystemState, and a type handle is only valid during the update of the
        /// system that owns it. Borrowed by another system in another phase it resolves against no
        /// archetype at all, and ArchetypeChunk.GetNativeArray dereferences the null.
        ///
        /// The fix is the one the skill states for the frontend and which applies just as well
        /// between two managed systems: publish the answer from the system that already knows it,
        /// rather than letting the consumer reach in. The count is taken once per update here, where
        /// the handles are legal, and every reader gets a dictionary lookup. That is also cheaper
        /// than it was — the walk happened once per docked ship per update *and* once per interface
        /// frame, and now happens once per update, full stop.
        /// </summary>
        private readonly Dictionary<Entity, AshoreCount> m_AshoreByShip =
            new Dictionary<Entity, AshoreCount>();

        /// <summary>Calls served since load, and passengers put ashore. For diagnostics.</summary>
        public int CallsServed { get; private set; }

        public int PassengersAshore { get; private set; }

        // Short, because holding the ship is a race and the window can be tiny.
        //
        // TransportBoardingHelpers:368 gives a vessel that was not already EnRoute a departure just
        // sixty frames out. StopBoarding:807 will honour a later one, but only if we have written
        // it by then — so the scan has to run inside that window or the ship is gone. The query is
        // a handful of ships, so this is cheap even at sixteen.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 16;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_CruiseLineSystem = World.GetOrCreateSystemManaged<CruiseLineSystem>();
            m_DemandSystem = World.GetOrCreateSystemManaged<TouristDemandSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            m_CruiseVehicleQuery = GetEntityQuery(
                ComponentType.ReadWrite<Game.Vehicles.PublicTransport>(),
                ComponentType.ReadOnly<CurrentRoute>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_AshoreQuery = GetEntityQuery(
                ComponentType.ReadOnly<Components.CruisePassenger>(),
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_EquippedTerminalQuery = GetEntityQuery(
                ComponentType.ReadOnly<Components.CruiseTerminalLodging>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_ActiveCallQuery = GetEntityQuery(
                ComponentType.ReadOnly<Components.CruiseCall>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnUpdate()
        {
            // Before the early returns, so a panel reading the published count never sees figures
            // left over from a line that has since been deleted.
            SnapshotPassengersAshore();

            if (m_CruiseLineSystem == null || !m_CruiseLineSystem.LineCreated)
            {
                return;
            }

            Entity cruiseLinePrefab = m_CruiseLineSystem.LinePrefabEntity;

            if (cruiseLinePrefab == Entity.Null)
            {
                return;
            }

            ServeDockedShips(cruiseLinePrefab);
            ReturnFinishedParties();
            SweepOrphanedTerminals();
        }

        /// <summary>
        /// Strips the stand-in lodging from terminals no live call is using.
        ///
        /// ReleaseTerminalLodging covers the ordinary path, where the call ends and the ship sails.
        /// It does not cover the ship disappearing while alongside — the player deletes the line,
        /// or removes the vessel — because the CruiseCall that names the terminal is on the ship
        /// and goes with it. The terminal would then keep a LodgingProvider for the rest of the
        /// save, and that persists: it is a native component on a player-owned building, written
        /// into the save file.
        ///
        /// This is the same discipline the notes record for AttractivenessProvider — anything that
        /// writes serialized state has to be able to take it back, on every path including the ones
        /// that are not clean shutdowns.
        /// </summary>
        private void SweepOrphanedTerminals()
        {
            if (m_EquippedTerminalQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            // Terminals still in use — by a live call, or by a party that has not sailed yet.
            NativeParallelHashSet<Entity> live = new NativeParallelHashSet<Entity>(16, Allocator.Temp);
            NativeArray<Entity> terminals = m_EquippedTerminalQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Components.CruiseCall> calls =
                m_ActiveCallQuery.ToComponentDataArray<Components.CruiseCall>(Allocator.Temp);
            NativeArray<Components.CruisePassenger> ashore =
                m_AshoreQuery.ToComponentDataArray<Components.CruisePassenger>(Allocator.Temp);

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();

            try
            {
                for (int i = 0; i < calls.Length; i++)
                {
                    live.Add(calls[i].m_Terminal);
                }

                // Passengers ashore pin their terminal too, and this is not a nicety.
                // HouseholdBehaviorSystem:247-251 nulls a tourist's hotel and marks it a
                // LodgingSeeker the moment that hotel stops having a LodgingProvider — so sweeping
                // a terminal out from under a party that is still ashore sends every one of them
                // hunting for a hotel room, which is precisely what a cruise passenger must never
                // do. The ship sailing early used to trigger exactly that chain.
                for (int i = 0; i < ashore.Length; i++)
                {
                    live.Add(ashore[i].m_Terminal);
                }

                for (int i = 0; i < terminals.Length; i++)
                {
                    if (live.Contains(terminals[i]))
                    {
                        continue;
                    }

                    commandBuffer.RemoveComponent<LodgingProvider>(terminals[i]);
                    commandBuffer.RemoveComponent<Components.CruiseTerminalLodging>(terminals[i]);

                    Mod.Log.Info(
                        $"Cruise terminal {terminals[i].Index} released; no call is using it.");
                }
            }
            finally
            {
                ashore.Dispose();
                calls.Dispose();
                terminals.Dispose();
                live.Dispose();
            }
        }

        /// <summary>
        /// Finds cruise ships boarding at a stop and either starts their call or holds them.
        /// </summary>
        private void ServeDockedShips(Entity cruiseLinePrefab)
        {
            uint frame = m_SimulationSystem.frameIndex;

            NativeArray<Entity> vehicles = m_CruiseVehicleQuery.ToEntityArray(Allocator.Temp);
            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
            NativeList<Entity> created = new NativeList<Entity>(64, Allocator.Temp);

            try
            {
                for (int i = 0; i < vehicles.Length; i++)
                {
                    Entity vehicle = vehicles[i];

                    if (!IsOnCruiseLine(vehicle, cruiseLinePrefab))
                    {
                        continue;
                    }

                    Game.Vehicles.PublicTransport transport =
                        EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);

                    // Both run wherever the vessel is, because both are about the stop rather than
                    // the ship: the queue has to be built while it is away, and the stop has to
                    // stay worth pathing to for that to happen.
                    MaintainOutsideConnectionAppeal(vehicle);
                    MaintainCruiseQueue(vehicle, frame, commandBuffer, created);

                    // Where the ship is has to be established before deciding anything, including
                    // whether to hold it. Holding is only ever valid at the port the call belongs
                    // to: applying it wherever the vessel happened to be pinned it at the outside
                    // connection for the whole of a shore leave it was not taking part in.
                    Entity stop = Entity.Null;
                    bool isOutsideConnection = false;

                    bool alongside =
                        (transport.m_State & PublicTransportFlags.Boarding) != 0
                        && TryResolveCurrentStop(vehicle, out stop, out isOutsideConnection);

                    if (EntityManager.HasComponent<Components.CruiseCall>(vehicle))
                    {
                        // No top-up. A call now lands exactly the complement the ship carried, so
                        // there is no target to converge on and nothing to create at the quay.
                        Components.CruiseCall call =
                            EntityManager.GetComponentData<Components.CruiseCall>(vehicle);

                        bool atOwnTerminal =
                            alongside && !isOutsideConnection && stop == call.m_Terminal;

                        HoldShipUntilReboard(
                            vehicle, ref transport, frame, atOwnTerminal,
                            alongside && isOutsideConnection, alongside, stop,
                            commandBuffer);
                        continue;
                    }

                    // A load in progress is followed by its manifest, not by whether the vessel
                    // still looks alongside — the same discipline as a call above. Gating this on
                    // "alongside" is what let a ship sail from the map edge with nobody aboard and
                    // write nothing to the log: the branch simply stopped running.
                    if (EntityManager.HasComponent<Components.CruiseManifest>(vehicle))
                    {
                        // A spent manifest must not shadow the arrival at a city quay.
                        //
                        // The manifest deliberately outlives its load, so that a vessel which has
                        // already loaded cannot immediately start loading again at the same stop.
                        // But this branch ends in `continue`, so while the component was present
                        // StartCall was never reached — the ship docked, no call was created, the
                        // hold was never applied and nothing was written to the log. Exactly the
                        // shape of "it sailed the moment it finished boarding".
                        //
                        // So a spent manifest only keeps the vessel here while it is still at an
                        // outside connection. Anywhere else it falls through, and StartCall clears
                        // the component as part of landing the complement.
                        bool spent = EntityManager
                            .GetComponentData<Components.CruiseManifest>(vehicle).m_Loaded != 0;

                        if (!spent || (alongside && isOutsideConnection))
                        {
                            ContinueLoading(
                                vehicle, stop, ref transport, frame,
                                alongside && isOutsideConnection, commandBuffer, created);
                            continue;
                        }
                    }

                    if (!alongside)
                    {
                        // A vessel that is boarding but that this system cannot place is the one
                        // failure that produces no output at all: every branch below needs a
                        // resolved stop, so the loop simply falls through and the ship sails with
                        // nobody while the log stays silent. That happened, and silence is the
                        // worst possible diagnostic. Written once per vessel until it resolves.
                        if ((transport.m_State & PublicTransportFlags.Boarding) != 0
                            && m_UnresolvedShip != vehicle)
                        {
                            m_UnresolvedShip = vehicle;

                            Mod.Log.Warn(
                                $"Cruise ship {vehicle.Index} is boarding but no stop on its route "
                                + "claims it, so nothing can be loaded or landed. Route below.");

                            LogRouteTopology(vehicle);
                            LogBoardingHolders(vehicle);
                        }

                        continue;
                    }

                    if (m_UnresolvedShip == vehicle)
                    {
                        m_UnresolvedShip = Entity.Null;
                    }

                    if (isOutsideConnection)
                    {
                        BeginLoading(
                            vehicle, stop, ref transport, frame, commandBuffer, created);
                        continue;
                    }

                    // A call may only be started by a vessel that has actually loaded at the map
                    // edge, and the spent manifest is the proof of it.
                    //
                    // Without this the ship never leaves a city. The line is public transport with
                    // its doors open for the whole dwell, so ordinary tourists board at the quay
                    // during shore leave — and the moment the call closes, this branch finds them
                    // aboard, adopts them and opens a fresh eight-hour call. Observed as a vessel
                    // that held, released, and immediately held again.
                    //
                    // StartCall consumes the manifest, so one load buys exactly one call.
                    if (!EntityManager.HasComponent<Components.CruiseManifest>(vehicle))
                    {
                        continue;
                    }

                    StartCall(vehicle, stop, ref transport, frame, commandBuffer);
                }
            }
            finally
            {
                created.Dispose();
                vehicles.Dispose();
            }
        }

        private bool IsOnCruiseLine(Entity vehicle, Entity cruiseLinePrefab)
        {
            if (!EntityManager.HasComponent<CurrentRoute>(vehicle))
            {
                return false;
            }

            Entity route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;

            if (route == Entity.Null
                || !EntityManager.Exists(route)
                || !EntityManager.HasComponent<PrefabRef>(route))
            {
                return false;
            }

            return EntityManager.GetComponentData<PrefabRef>(route).m_Prefab == cruiseLinePrefab;
        }

        /// <summary>
        /// The building the ship is alongside right now, and whether it is the map-edge connection.
        ///
        /// Resolved through BoardingVehicle rather than by taking the route's first stop. A
        /// waypoint that is currently accepting boarders carries BoardingVehicle naming the vessel
        /// (Game.Routes/BoardingVehicle.cs), so matching that against our ship says exactly which
        /// stop it is at. Guessing the first connected waypoint was wrong in the way that matters
        /// most: on a two-stop line — map edge and city quay — it is a coin flip whether the guess
        /// names the port the ship is actually in, and getting it wrong means the ship stops dead
        /// for a day at the map edge and lands its passengers in the wrong place.
        ///
        /// The hop is waypoint to stop to owner, because the stop is usually a sub-object of the
        /// building and the anchor wants to be the thing with a road connection.
        /// </summary>
        private bool TryResolveCurrentStop(
            Entity vehicle, out Entity building, out bool isOutsideConnection)
        {
            building = Entity.Null;
            isOutsideConnection = false;

            Entity route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;

            if (!EntityManager.HasBuffer<RouteWaypoint>(route))
            {
                return false;
            }

            DynamicBuffer<RouteWaypoint> waypoints =
                EntityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;

                if (waypoint == Entity.Null
                    || !EntityManager.Exists(waypoint)
                    || !EntityManager.HasComponent<Connected>(waypoint))
                {
                    continue;
                }

                Entity stop = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;

                if (stop == Entity.Null || !EntityManager.Exists(stop))
                {
                    continue;
                }

                // BoardingVehicle is on the stop, not on the waypoint. Game.Prefabs/TransportStop
                // adds it to the stop archetype (:58-86), and TransportBoardingHelpers writes it
                // through data.m_Stop (:369); StopBoarding reads it the same way, by hopping
                // Connected first (:797). Testing the waypoint finds nothing on every route, which
                // is silent — the loop simply never matches and no call is ever started.
                if (!EntityManager.HasComponent<BoardingVehicle>(stop)
                    || EntityManager.GetComponentData<BoardingVehicle>(stop).m_Vehicle != vehicle)
                {
                    continue;
                }

                // Walk the whole owner chain, not one hop.
                //
                // One hop was enough while the line's access connection was None, because a
                // waypoint then connected straight to the stop. With access restored,
                // WaypointConnectionSystem:1193 connects a waypoint to a *spawn location* whose
                // type matches the line's access type — so the entity behind Connected is now a
                // sub-object one or more levels below the building, and a single Owner hop lands
                // short of the thing that carries OutsideConnection. The symptom was that nothing
                // at the map edge classified as an outside connection any more, so the load never
                // started and not one line was written.
                //
                // GetTransportStationFromStop (TransportWatercraftAISystem:865-888) walks the same
                // chain for the same reason. Bounded because an Owner cycle would hang the
                // simulation, and a malformed prefab is not worth trusting.
                building = stop;
                isOutsideConnection =
                    EntityManager.HasComponent<Game.Objects.OutsideConnection>(stop);

                Entity walk = stop;

                for (int hop = 0; hop < 8; hop++)
                {
                    if (!EntityManager.HasComponent<Owner>(walk))
                    {
                        break;
                    }

                    Entity owner = EntityManager.GetComponentData<Owner>(walk).m_Owner;

                    if (owner == Entity.Null || !EntityManager.Exists(owner))
                    {
                        break;
                    }

                    building = owner;
                    walk = owner;

                    if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(owner))
                    {
                        isOutsideConnection = true;
                        break;
                    }
                }

                return true;
            }

            return false;
        }

        private void StartCall(
            Entity vehicle,
            Entity terminal,
            ref Game.Vehicles.PublicTransport transport,
            uint frame,
            EntityCommandBuffer commandBuffer)
        {
            uint shoreLeave = ShoreLeaveFrames();
            uint reboard = frame + shoreLeave;

            // Whoever the ship carried in is this call's shore party. That is the only thing that
            // starts a call: a cruise call exists because passengers arrived on the vessel, not
            // because a vessel touched a quay.
            int placed = AdoptCarriedPassengers(vehicle, terminal, reboard, commandBuffer);

            if (placed == 0)
            {
                SailOnEmpty(vehicle, terminal, commandBuffer);
                return;
            }

            m_ReportedEmptyShip = Entity.Null;

            // The sailing has landed, so any manifest it was still carrying is spent. This is the
            // other way a load ends: the vessel reached a city rather than filling or timing out.
            if (EntityManager.HasComponent<Components.CruiseManifest>(vehicle))
            {
                commandBuffer.RemoveComponent<Components.CruiseManifest>(vehicle);
            }

            EquipTerminalWithLodging(terminal, commandBuffer);

            commandBuffer.AddComponent(vehicle, new Components.CruiseCall
            {
                m_Terminal = terminal,
                m_DisembarkedFrame = frame,
                m_ReboardFrame = reboard,
                m_PartyCount = placed,

                // The complement is the target. The call no longer guesses a figure and then tops
                // up towards it — who the ship brought is the only number that was ever true.
                m_TargetPassengers = placed
            });

            // Hold the ship. Pushing m_DepartureFrame is the game's own scheduling, so unbunching
            // and vehicle interval keep working around it.
            transport.m_DepartureFrame = reboard;
            EntityManager.SetComponentData(vehicle, transport);

            CallsServed++;
            PassengersAshore += placed;

            Mod.Log.Info(
                $"Cruise call at terminal {terminal.Index}: {placed} parties came off the ship, "
                + $"shore leave {shoreLeave} frames, "
                + $"due back at frame {reboard}.");
        }

        /// <summary>
        /// Fills the vessel to capacity at the map edge, and holds it while the complement boards.
        ///
        /// The game routes passengers onto the line by itself, but only as many as happen to be
        /// travelling — which is far short of a ship. So the mod creates the difference as tourist
        /// households at the connection and then gets out of the way: they are given bodies, a
        /// destination and a path by the game, and they walk aboard as any passenger does. Nothing
        /// is placed on the vessel by hand. That was tried and could not work, because a citizen
        /// has no body until the game gives it one for a trip (TripNeededSystem:1615).
        ///
        /// Which means the load takes time — household initialisation, then a target search, then
        /// a walk to the stop — and the vessel would otherwise leave sixty frames after it arrived
        /// (TransportBoardingHelpers:368). So it is held. The hold is bounded by
        /// <see cref="kLoadTimeoutFrames"/> and released the moment the ship is full, because a
        /// vessel parked at the map edge is a vessel doing nothing.
        /// </summary>
        private void BeginLoading(
            Entity vehicle,
            Entity connection,
            ref Game.Vehicles.PublicTransport transport,
            uint frame,
            EntityCommandBuffer commandBuffer,
            NativeList<Entity> created)
        {
            // Last voyage's passengers get off first. They have reached the edge of the map, which
            // is where they came from and where their trip ends.
            int released = LandHomewardPassengers(vehicle, commandBuffer);

            if (released > 0)
            {
                Mod.Log.Info(
                    $"Cruise landed {released} homeward parties at outside connection "
                    + $"{connection.Index}.");
            }

            int capacity = CruiseCapacity(vehicle);
            int aboard = CountOutboundAboard(vehicle);

            // Nobody is created here any more. The complement is built by MaintainCruiseQueue while
            // the vessel is away, so by the time it ties up the people are already standing on the
            // dock — which is the difference between a dwell spent boarding and a dwell spent
            // waiting for households to turn into people.
            commandBuffer.AddComponent(vehicle, new Components.CruiseManifest
            {
                m_TargetPassengers = capacity,
                m_LoadDeadline = frame + kLoadTimeoutFrames
            });

            Mod.Log.Info(
                $"Cruise loading at outside connection {connection.Index}: {aboard} aboard of "
                + $"{capacity} the vessel can hold.");

            LogBoardingHolders(vehicle);

            HoldShip(vehicle, ref transport, frame + kLoadTimeoutFrames);
        }

        /// <summary>
        /// Carries a load in progress forward, wherever the vessel is.
        ///
        /// Followed by the manifest on the vehicle rather than by whether the ship still looks
        /// alongside, and that is the fix rather than an implementation detail. The previous version
        /// only ran while <c>alongside</c> held, so the moment the game stopped recognising the
        /// vessel as boarding at the map edge this stopped running altogether: the hold was never
        /// re-asserted, the release never fired, and nothing was written to the log. The observed
        /// symptom was a ship that logged its loading line, sailed with nobody, and never logged
        /// again.
        ///
        /// Re-asserting matters because the hold is not a one-time write.
        /// TransportBoardingHelpers:388 sets m_DepartureFrame to frame + 60 every time boarding
        /// begins, so a single push can be overwritten; HoldShip only ever raises the value, so
        /// calling it each update restores the hold without fighting the scheduler.
        /// </summary>
        private void ContinueLoading(
            Entity vehicle,
            Entity connection,
            ref Game.Vehicles.PublicTransport transport,
            uint frame,
            bool atConnection,
            EntityCommandBuffer commandBuffer,
            NativeList<Entity> created)
        {
            Components.CruiseManifest manifest =
                EntityManager.GetComponentData<Components.CruiseManifest>(vehicle);

            // Already sailed on this manifest. Nothing more to do until it reaches a city and the
            // component is cleared there.
            if (manifest.m_Loaded != 0)
            {
                return;
            }

            int aboard = CountOutboundAboard(vehicle);

            // The dwell is the load. Nothing ends it early.
            //
            // Two other tests were tried and both cut it short. Counting everyone aboard cannot
            // tell a cruise complement from the city's commuters — "1332 aboard of 1000" tripped a
            // capacity test on the first check and sailed the ship a tenth of a second in. Then
            // waiting for the quay to empty looked right and was worse in a subtler way: the queue
            // drains between batches, because the next batch is still turning into people, so an
            // empty quay means "boarding has caught up", not "boarding is finished". The vessel
            // left eight seconds into a two-hour window with 266 aboard and hundreds more on the
            // way.
            //
            // A cruise ship is not a bus and does not leave when the queue clears; it leaves when
            // its dwell is over. kLoadTimeoutFrames is that dwell, the queue refills throughout it,
            // and the reported figures say how well the two were matched.
            TryGetOutsideConnection(vehicle, out Entity _, out int waiting);

            bool timedOut = frame >= manifest.m_LoadDeadline;

            if (timedOut)
            {
                // Marked spent, not removed, and that distinction is the whole of it.
                //
                // Removing the manifest here let the very next scan see a vessel alongside an
                // outside connection with no load in progress — so BeginLoading started another
                // one, held it for a fresh timeout, and the ship never left the map edge. Observed:
                // "sailing from outside connection with 1930 aboard" followed two tenths of a second
                // later by "loading at outside connection ... 1930 aboard", indefinitely.
                //
                // The manifest is what says "this vessel has already loaded", so it has to outlive
                // the load and be cleared only when the ship reaches a city — which StartCall and
                // SailOnEmpty both do.
                manifest.m_Loaded = 1;
                EntityManager.SetComponentData(vehicle, manifest);

                ReleaseShip(vehicle, ref transport, frame);

                Mod.Log.Info(
                    $"Cruise sailing from outside connection after its full dwell with {aboard} "
                    + $"aboard, {waiting} still queued.");

                // The other half of the pair started in BeginLoading.
                LogBoardingHolders(vehicle);

                return;
            }

            // A load survives the vessel dropping out of its boarding state, and that is the whole
            // point of tracking it on the manifest rather than on "is it alongside right now".
            //
            // The flag flickers. Measured: a load began, the ship was reported out of boarding 192
            // frames later, and a second afterwards it was boarding again. Treating each of those
            // gaps as the end of the load tore down the manifest and let the next update start a
            // fresh one — which created another seven hundred households. Two loads inside three
            // seconds, and nothing bounding it. A momentary reading must never be allowed to
            // cancel a booked complement; only the deadline or a full ship ends a load.
            if (!atConnection)
            {
                return;
            }

            // Nothing is created here. The complement was built while the ship was away, and a
            // dwell is for boarding it — see MaintainCruiseQueue for why ordering against a
            // shortfall at this point runs away.
            HoldShip(vehicle, ref transport, manifest.m_LoadDeadline);
        }

        /// <summary>
        /// Gives a freshly created party a reason to travel.
        ///
        /// TouristTargetSearchSystem's query is TouristHousehold + LodgingSeeker without a Target,
        /// so this marker is what hands the party to the routing that finds it a destination — and
        /// a destination is what makes the game give its citizens bodies, at the connection, via
        /// TripNeededSystem:1614. Without it the household stays a record and nobody appears on the
        /// dock.
        ///
        /// Measured both ways, because it was not obvious: with the marker the map-edge queue held
        /// 164-169; with it removed the same load produced 14. The concern that it fires before the
        /// household has citizens — leaving RequestPath with a null origin — is real but harmless,
        /// because a failed search drops PathInformation and tries again, and by then the citizens
        /// exist.
        /// </summary>
        private static void MarkAsTravelling(
            NativeList<Entity> parties, EntityCommandBuffer commandBuffer)
        {
            for (int i = 0; i < parties.Length; i++)
            {
                commandBuffer.AddComponent<LodgingSeeker>(parties[i]);
            }
        }

        /// <summary>
        /// Builds the queue at the map-edge stop while the ship is away, so it is there when it
        /// arrives.
        ///
        /// This is what makes a cruise ship leave full, and it is the one thing every earlier
        /// attempt got backwards. Creating a complement when the vessel docks gives those people
        /// the length of the dwell to be given citizens, find a destination, be given bodies and
        /// walk to the quay — two in-game hours for work that takes most of a sailing gap. The
        /// city's own arrivals prove the timescale: left alone, ordinary visitors accumulated 165
        /// at this stop between two sailings, with nothing helping them but the waiting-time fix.
        ///
        /// So the queue is built continuously instead. A batch is ordered whenever the stop holds
        /// fewer people than the vessel can carry, and by the time the ship returns they are
        /// standing there ready to board. Nothing is placed aboard by hand; the game boards them
        /// through ResidentAISystem.TryEnterVehicle as it does every other passenger.
        ///
        /// Two bounds keep this from becoming the runaway the last top-up was:
        ///
        ///   The rate is fixed, not derived. One batch per <see cref="kBatchIntervalFrames"/>,
        ///   whatever the shortfall reads. The failed version ordered the whole gap every update,
        ///   and because the gap only closes many updates later it ordered it again and again —
        ///   ninety parties every 150ms. A control loop whose feedback lags its action needs the
        ///   action rate fixed, not the error.
        ///
        ///   The target is the vessel's real capacity, so the queue stops growing at a number
        ///   boarding can actually consume. See <see cref="CruiseCapacity"/>.
        /// </summary>
        private void MaintainCruiseQueue(
            Entity vehicle,
            uint frame,
            EntityCommandBuffer commandBuffer,
            NativeList<Entity> created)
        {
            if (frame < m_NextBatchFrame)
            {
                return;
            }

            if (!TryGetOutsideConnection(vehicle, out Entity connection, out int waiting))
            {
                return;
            }

            // Everyone already queued, plus everyone already aboard, counts against the ship.
            int shortfall = CruiseCapacity(vehicle) - waiting - CountOutboundAboard(vehicle);

            // Deadband, deliberately independent of the batch size. Tying the two together meant a
            // larger batch stopped topping up earlier — at 500 the ordering stopped once the
            // shortfall fell below 500, which is exactly the last few hundred places on a nearly
            // full ship. Observed: boarding climbed to about 1800 and then starved.
            if (shortfall < kQueueDeadband)
            {
                return;
            }

            m_NextBatchFrame = frame + kBatchIntervalFrames;

            // Make the stop cheap before anyone is created, not after.
            //
            // A visitor picks a route the moment they exist and does not reconsider, so the price of
            // this stop at the instant of creation is the only one that matters to them. The
            // standing suppression is gated on the vessel being below capacity, and a ship that is
            // full — or over, as happens when two vessels serve one line — switches it off while
            // this method carries on creating. Observed: batches of a thousand ordered against a
            // stop still advertising an average wait of 2500, every one of them routed onto some
            // other line, and the cruise queue never leaving zero.
            //
            // Clearing it here removes that window entirely: whatever the standing rule is doing,
            // the people ordered on this pass see a stop that costs what the city quay costs.
            ClearOutsideConnectionWait(vehicle);

            created.Clear();

            int placed = m_DemandSystem.CreateTouristHouseholdsAt(
                connection, math.min(shortfall, kTopUpBatch), kArrivalModeShip, commandBuffer,
                created, out int expected) * 2;

            if (placed <= 0)
            {
                return;
            }

            MarkAsTravelling(created, commandBuffer);

            Mod.Log.Info(
                $"Cruise queue at outside connection {connection.Index}: {waiting} waiting, "
                + $"created {placed} parties expecting {expected} people.");
        }

        /// <summary>
        /// The line's map-edge connection and how many people are queued at it.
        ///
        /// Resolved from the route rather than from where the vessel happens to be, so the queue can
        /// be built while the ship is somewhere else entirely — which is the whole point.
        /// </summary>
        private bool TryGetOutsideConnection(Entity vehicle, out Entity connection, out int waiting)
        {
            connection = Entity.Null;
            waiting = 0;

            if (!EntityManager.HasComponent<CurrentRoute>(vehicle))
            {
                return false;
            }

            Entity route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;

            if (route == Entity.Null
                || !EntityManager.Exists(route)
                || !EntityManager.HasBuffer<RouteWaypoint>(route))
            {
                return false;
            }

            DynamicBuffer<RouteWaypoint> waypoints =
                EntityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;

                if (waypoint == Entity.Null
                    || !EntityManager.Exists(waypoint)
                    || !EntityManager.HasComponent<Connected>(waypoint))
                {
                    continue;
                }

                Entity stop = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;

                if (!StopIsOutsideConnection(stop))
                {
                    continue;
                }

                // Households are created at the connection object itself, as the ordinary spawner
                // does (TouristDemandSystem.SpawnTouristHouseholds:1283-1288 passes the entity from
                // the outside-connection query). The stop is a sub-object of it, so the owner chain
                // is walked to the thing that actually carries OutsideConnection.
                connection = OutsideConnectionOf(stop);

                if (EntityManager.HasComponent<WaitingPassengers>(waypoint))
                {
                    waiting = EntityManager.GetComponentData<WaitingPassengers>(waypoint).m_Count;
                }

                return connection != Entity.Null;
            }

            return false;
        }

        /// <summary>The entity in a stop's owner chain that carries OutsideConnection.</summary>
        private Entity OutsideConnectionOf(Entity stop)
        {
            Entity walk = stop;

            for (int hop = 0; hop < 8; hop++)
            {
                if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(walk))
                {
                    return walk;
                }

                if (!EntityManager.HasComponent<Owner>(walk))
                {
                    return Entity.Null;
                }

                Entity owner = EntityManager.GetComponentData<Owner>(walk).m_Owner;

                if (owner == Entity.Null || !EntityManager.Exists(owner))
                {
                    return Entity.Null;
                }

                walk = owner;
            }

            return Entity.Null;
        }

        /// <summary>
        /// Keeps the map-edge stop worth pathing to until the ship is full, and stops when it is.
        ///
        /// The suppression is standing rather than tied to a load, because the thing it is competing
        /// with is standing: a visitor decides where to go the moment they exist, and if the stop
        /// was expensive at that instant it is not reconsidered later. Clearing the figure only
        /// while a load happened to be open meant most arrivals never saw the cheap version.
        ///
        /// It used to stop once the vessel reached capacity, on the theory that a full ship should
        /// price its stop back up and send later arrivals elsewhere. That gate was wrong twice over
        /// and is gone.
        ///
        /// It could not switch off cleanly, because the figure it tested — the vessel's Passenger
        /// buffer — counts every rider including locals and ordinary transit passengers, so it can
        /// sit above the vessel's own capacity and never come back down. Observed: "1574 aboard of
        /// 1500", suppression off permanently, the stop back at an average wait of 2545, and every
        /// batch created against that price routed onto the city's other passenger ship lines.
        ///
        /// And it was solving a problem that does not exist. The queue is already bounded by
        /// MaintainCruiseQueue, which orders nothing when the stop holds enough people. Pricing the
        /// stop back up as a second limit only starves the line of the arrivals it exists to carry.
        /// </summary>
        private void MaintainOutsideConnectionAppeal(Entity vehicle)
        {
            ClearOutsideConnectionWait(vehicle);
        }

        /// <summary>
        /// Holds the map-edge stop's advertised wait at zero.
        ///
        /// PathUtils:1562 prices boarding as
        /// <c>max(m_VehicleInterval * 0.5, m_AverageWaitingTime) - stopDuration</c> on the time axis,
        /// so the queue's own history is part of what a citizen pays to choose this stop. A cruise
        /// line sails rarely, which drives that average up, which makes the stop more expensive,
        /// which stops anyone pathing to it — and because nobody boards, the average never comes
        /// down. Measured at 2655 against 185 at the city quay on the same line: five times the
        /// cost, and no one waiting where 126 were waiting at the other end.
        ///
        /// Zeroing it leaves the vehicle-interval term at 500, which is exactly what the city quay
        /// costs, so this does not privilege the map edge — it stops a feedback loop from pricing it
        /// out of reach.
        ///
        /// Bounded twice over, which is what keeps it honest. It only runs while a load is in
        /// progress, so outside a call the figure is the game's own; and the game recomputes it
        /// continuously, so nothing has to be restored on disable — unlike a serialized value, this
        /// heals itself the moment the mod stops writing.
        /// </summary>
        private void ClearOutsideConnectionWait(Entity vehicle)
        {
            if (!EntityManager.HasComponent<CurrentRoute>(vehicle))
            {
                return;
            }

            Entity route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;

            if (route == Entity.Null
                || !EntityManager.Exists(route)
                || !EntityManager.HasBuffer<RouteWaypoint>(route))
            {
                return;
            }

            DynamicBuffer<RouteWaypoint> waypoints =
                EntityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;

                if (waypoint == Entity.Null
                    || !EntityManager.Exists(waypoint)
                    || !EntityManager.HasComponent<WaitingPassengers>(waypoint)
                    || !EntityManager.HasComponent<Connected>(waypoint))
                {
                    continue;
                }

                Entity stop = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;

                // Zero at the map edge, deliberately expensive at the city pier.
                //
                // The two stops on a cruise line want opposite things. The map edge has to be the
                // cheapest thing in sight, because the whole complement is created there and picks
                // its route the instant it exists. The pier has to be the most expensive, because
                // the vessel sits there for hours with its doors open and the city's own commuters
                // will otherwise fill it — measured as a passenger buffer of 1643 on a ship that
                // holds 1000, which then read as full and sailed before the people waiting at the
                // map edge could board.
                //
                // Both are the same lever, PathUtils:1562, which prices boarding as
                // max(m_VehicleInterval * 0.5, m_AverageWaitingTime). Raising the pier's figure
                // above the interval's 500 makes it the term that counts and puts the stop out of
                // reach of anyone with an alternative — which every local has and no cruise
                // passenger does, since they are already aboard when they arrive.
                bool atMapEdge = StopIsOutsideConnection(stop);

                // The pier is expensive while the shore party is out, and cheap once they are
                // coming back.
                //
                // Cost belongs to the stop and cannot tell one traveller from another, so a penalty
                // heavy enough to keep the city's commuters off also refuses the cruise passengers
                // their way home — they walked to the quay and then stood there, because boarding
                // was priced out of reach. What the stop can distinguish is *when*.
                //
                // For most of a call the vessel is idle at the quay with its doors open and needs
                // protecting from commuters, so the penalty stands. Inside last call the complement
                // is walking back and needs to board, so it lifts. Locals get a window in which the
                // line is attractive, but it is the window in which the ship is about to leave, so
                // the ride they get is the one out of the city — which is the ship's own direction
                // of travel and costs the player nothing.
                bool boarding = !atMapEdge && ReturningToQuay(vehicle);

                ushort wanted = (atMapEdge || boarding) ? (ushort)0 : kPierWaitingTime;

                WaitingPassengers queue = EntityManager.GetComponentData<WaitingPassengers>(waypoint);

                bool fresh = wanted == 0;

                // Reset the history, not just the figure it produces.
                //
                // m_AverageWaitingTime is derived, not stored in isolation: ResidentAISystem
                // accumulates into m_ConcludedAccumulation and m_SuccessAccumulation as passengers
                // wait and board (:3795-3797, :3980-3993), and the average is recomputed from them.
                // Writing the average alone is overwritten within seconds by a history that still
                // remembers a stop nobody could use — which is why a cleared figure kept climbing
                // back to 2500 and beyond.
                //
                // Clearing the accumulators as well makes the stop genuinely new: no remembered
                // waits, no remembered failures, an average of zero that stays zero until real
                // passengers give it a real one.
                bool stale = queue.m_OngoingAccumulation != 0
                             || queue.m_ConcludedAccumulation != 0
                             || queue.m_SuccessAccumulation != 0;

                if (queue.m_AverageWaitingTime != wanted || (fresh && stale))
                {
                    queue.m_AverageWaitingTime = wanted;

                    if (fresh)
                    {
                        // All three, because any one of them rebuilds the average on its own.
                        queue.m_OngoingAccumulation = 0;
                        queue.m_ConcludedAccumulation = 0;
                        queue.m_SuccessAccumulation = 0;
                    }

                    EntityManager.SetComponentData(waypoint, queue);
                }

                SetBoardable(stop, atMapEdge || boarding);
            }
        }

        /// <summary>
        /// Opens or closes a stop to boarding. Getting off is unaffected either way.
        ///
        /// Cost is not the only thing pathfinding consults, and this is the part that is not a
        /// price. PathUtils.GetTransportStopSpecification:1537 grants EdgeFlags.Forward — the
        /// direction that means "board here" — only when the stop carries StopFlags.Active. The
        /// specification is built with EdgeFlags.FreeBackward unconditionally at :1531, so alighting
        /// is always permitted and costs nothing whatever the flags say.
        ///
        /// That asymmetry is exactly what a cruise line wants at the city pier. Pricing the stop up
        /// only discourages: it was set to an advertised wait of 1000, well past the 500 the vehicle
        /// interval contributes, and 242 commuters still queued there and filled a vessel that holds
        /// 1000. Clearing Active does not discourage, it removes the edge — nobody can board, while
        /// the shore party still walks off exactly as before.
        ///
        /// Only ever applied to stops on this mod's own cruise route. Worth knowing that a stop can
        /// serve more than one line, so a harbour shared between a cruise line and an ordinary
        /// passenger ship line would have boarding closed for both. A dedicated terminal avoids it;
        /// a shared one is a limitation to state rather than to discover.
        /// </summary>
        private void SetBoardable(Entity stop, bool boardable)
        {
            if (!EntityManager.HasComponent<Game.Routes.TransportStop>(stop))
            {
                return;
            }

            Game.Routes.TransportStop data =
                EntityManager.GetComponentData<Game.Routes.TransportStop>(stop);

            // Active is never cleared, only ever set.
            //
            // Clearing it on the pier did stop commuters boarding — it removes EdgeFlags.Forward at
            // :1537 outright — but it also stopped the cruise passengers getting off. The
            // specification's FreeBackward at :1531 is not the whole of alighting; an inactive stop
            // is not served properly, and a shore party rode straight past the city it had come to
            // visit. That is a far worse fault than a few commuters on the ship, so the pier is
            // discouraged by price instead, which cannot break the thing the feature exists for.
            StopFlags flags = boardable
                ? data.m_Flags | StopFlags.Active | StopFlags.AllowEnter
                : data.m_Flags;

            // Comfort is the last term in the specification and the only other one that can be
            // moved from here: :1565 scales the comfort cost by (1 - m_ComfortFactor), so a factor
            // of 1 removes that axis from the sum entirely. Applied at the map edge only, where the
            // stop should be as attractive as the game can express — Active and AllowEnter so the
            // edge exists in both directions, an advertised wait of zero so the time term falls to
            // the vehicle interval alone, and no comfort penalty at all. There is nothing further
            // to give it short of the ticket price, which is charged to the passenger.
            // Comfort is never used as a penalty, and any penalty already written is repaired.
            //
            // It looked like a second axis to push against, and it is — but unlike
            // m_AverageWaitingTime, which the game recomputes from live queue behaviour, this is
            // authored prefab data that nothing recalculates. A value written here stays written:
            // in the save, on that stop, for every line that uses it, after this mod is gone. The
            // notes already record the rule it breaks — anything that scales serialized state must
            // hold the authored figure and be able to put it back — and a stop left at -100 is a
            // stop no citizen will ever board again, which is what "even the locals stopped
            // boarding" is.
            //
            // So the map edge is given a genuine 1 (no comfort cost, a real and sane value), and
            // anything negative found on a pier is a scar from the earlier version and is cleared
            // back to neutral. Discouragement is left entirely to the waiting figure, which heals
            // itself the moment this stops writing it.
            float comfort = boardable
                ? 1f
                : math.max(0f, data.m_ComfortFactor);

            if (data.m_Flags == flags && data.m_ComfortFactor == comfort)
            {
                return;
            }

            data.m_Flags = flags;
            data.m_ComfortFactor = comfort;

            EntityManager.SetComponentData(stop, data);
        }

        /// <summary>
        /// Whether this vessel's shore party is on its way back, so the pier should accept boarders.
        ///
        /// True inside the last-call window of an open call — the same window
        /// <see cref="ReturnFinishedParties"/> uses to send parties to the quay — so the stop opens
        /// exactly when there is somebody walking towards it and closes again as soon as the call
        /// ends.
        /// </summary>
        private bool ReturningToQuay(Entity vehicle)
        {
            if (!EntityManager.HasComponent<Components.CruiseCall>(vehicle))
            {
                return false;
            }

            Components.CruiseCall call =
                EntityManager.GetComponentData<Components.CruiseCall>(vehicle);

            uint lastCall = (uint)math.max(1f, ShoreLeaveFrames() * kLastCallFraction);

            return m_SimulationSystem.frameIndex + lastCall >= call.m_ReboardFrame;
        }

        /// <summary>Whether a stop, or anything that owns it, is an outside connection.</summary>
        private bool StopIsOutsideConnection(Entity stop)
        {
            if (stop == Entity.Null || !EntityManager.Exists(stop))
            {
                return false;
            }

            Entity walk = stop;

            for (int hop = 0; hop < 8; hop++)
            {
                if (EntityManager.HasComponent<Game.Objects.OutsideConnection>(walk))
                {
                    return true;
                }

                if (!EntityManager.HasComponent<Owner>(walk))
                {
                    return false;
                }

                Entity owner = EntityManager.GetComponentData<Owner>(walk).m_Owner;

                if (owner == Entity.Null || !EntityManager.Exists(owner))
                {
                    return false;
                }

                walk = owner;
            }

            return false;
        }

        /// <summary>
        /// People aboard who are not already going home.
        ///
        /// A homeward party is released at this very stop and is on its way off, so counting it
        /// would report the ship as fuller than it is about to be and cut the load short.
        /// </summary>
        private int CountOutboundAboard(Entity vehicle)
        {
            if (!EntityManager.HasBuffer<Passenger>(vehicle))
            {
                return 0;
            }

            DynamicBuffer<Passenger> manifest =
                EntityManager.GetBuffer<Passenger>(vehicle, isReadOnly: true);

            int aboard = 0;

            for (int i = 0; i < manifest.Length; i++)
            {
                Entity household = HouseholdOf(manifest[i].m_Passenger);

                if (household == Entity.Null)
                {
                    continue;
                }

                if (EntityManager.HasComponent<Components.CruisePassenger>(household)
                    && EntityManager.GetComponentData<Components.CruisePassenger>(household)
                           .m_Homeward != 0)
                {
                    continue;
                }

                aboard++;
            }

            return aboard;
        }

        /// <summary>Lets a held vessel go now, by bringing its departure back to this frame.</summary>
        private void ReleaseShip(
            Entity vehicle, ref Game.Vehicles.PublicTransport transport, uint frame)
        {
            if (transport.m_DepartureFrame > frame)
            {
                transport.m_DepartureFrame = frame;
                EntityManager.SetComponentData(vehicle, transport);
            }
        }

        /// <summary>
        /// How many people this vessel can actually carry.
        ///
        /// The vessel's own figure, not the mod's. Boarding is hard-capped by it: a citizen enters
        /// through ResidentAISystem.TryEnterVehicle:3751, which calls TryFindVehicle against a free
        /// space map, and that space comes from PublicTransportVehicleData.m_PassengerCapacity —
        /// authored on the vehicle prefab (Game.Prefabs/PublicTransport.cs:18, default 30). When
        /// there is no room the function returns without boarding anyone, whatever the mod wants.
        ///
        /// So a load targeted at the CruiseShipCapacity setting could never complete on a vessel
        /// smaller than that: the queue would be worked down to the ship's real limit, the target
        /// would stay unmet, and the hold would run to its timeout every single voyage. That is
        /// consistent with every load observed so far.
        ///
        /// The setting is kept as a ceiling rather than a target, so a player can ask for smaller
        /// calls than the ship allows but never for more people than it can hold. Raising the real
        /// capacity is not an option here: m_PassengerCapacity lives on the vehicle prefab, which is
        /// a stock passenger ship shared with every other line in the city, and editing it would
        /// change vessels this mod has no business touching.
        /// </summary>
        private int CruiseCapacity(Entity vehicle)
        {
            int wanted = Mod.Settings != null
                ? math.clamp(Mod.Settings.CruiseShipCapacity, 100, 5000)
                : 2000;

            if (!EntityManager.HasComponent<PrefabRef>(vehicle))
            {
                return wanted;
            }

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;

            if (prefab == Entity.Null
                || !EntityManager.Exists(prefab)
                || !EntityManager.HasComponent<PublicTransportVehicleData>(prefab))
            {
                return wanted;
            }

            int authored = EntityManager.GetComponentData<PublicTransportVehicleData>(prefab)
                .m_PassengerCapacity;

            return authored > 0 ? math.min(wanted, authored) : wanted;
        }

        /// <summary>
        /// Makes the shore party out of whoever the ship actually carried in.
        ///
        /// This is the whole cohort mechanism now, and it replaces two systems' worth of machinery:
        /// a complement created at the map edge, hand-boarded citizen by citizen, hand-landed at the
        /// quay, and topped up against a predicted head count. None of it was needed and most of it
        /// could not work — a citizen has no body until the game gives it one for a trip
        /// (TripNeededSystem:1615), so nothing at an outside connection can be put aboard by hand.
        ///
        /// With the line's access connection restored the game does all of it: passengers path to
        /// the city, board at the connection, ride, and walk off at the terminal under their own
        /// power. So the mod stops moving anyone. It reads the vessel's Passenger buffer at the
        /// moment it docks, and the tourists aboard become this call's party — they are already
        /// going ashore, and all that is added is the anchor that stops them looking for a hotel
        /// and the deadline that brings them back.
        ///
        /// Deliberately not disembarking anyone here. Stripping CurrentVehicle by hand would take
        /// them off mid-trip, and the game lands them at this stop anyway.
        ///
        /// Households are deduplicated because a party of four is four entries in the buffer and
        /// one household ashore, and the notes already record this class of error twice.
        /// </summary>
        private int AdoptCarriedPassengers(
            Entity vehicle, Entity terminal, uint reboard, EntityCommandBuffer commandBuffer)
        {
            if (!EntityManager.HasBuffer<Passenger>(vehicle))
            {
                return 0;
            }

            DynamicBuffer<Passenger> manifest =
                EntityManager.GetBuffer<Passenger>(vehicle, isReadOnly: true);

            NativeParallelHashSet<Entity> seen =
                new NativeParallelHashSet<Entity>(64, Allocator.Temp);

            // How much earlier than the ship a party may decide it has seen enough. A third of the
            // stay, so the quayside fills across the whole of last call rather than in one wave.
            uint earlyReturnSpread = (uint)math.max(
                1f, (reboard - m_SimulationSystem.frameIndex) * kEarlyReturnFraction);

            Random random = new Random(
                math.max(1u, m_SimulationSystem.frameIndex * 2654435761u + 1013904223u));

            int adopted = 0;

            try
            {
                for (int i = 0; i < manifest.Length; i++)
                {
                    Entity household = HouseholdOf(manifest[i].m_Passenger);

                    if (household == Entity.Null || !seen.Add(household))
                    {
                        continue;
                    }

                    // Only visitors. A local riding the line as public transport is the game's
                    // business and must not be held ashore or recalled — they are going to work.
                    if (!EntityManager.HasComponent<TouristHousehold>(household)
                        || EntityManager.HasComponent<Components.CruisePassenger>(household))
                    {
                        continue;
                    }

                    // Some of them never get off, which is what a cruise looks like. Left untagged
                    // and unlanded, so they stay in the vessel's passenger buffer, sail with it, and
                    // are simply aboard for the next call — no state of ours describes them and none
                    // has to.
                    if (random.NextFloat() < kStayAboardFraction)
                    {
                        continue;
                    }

                    // Each party gets its own deadline, a little short of the ship's.
                    //
                    // A single shared frame means the entire complement turns for the quay on the
                    // same update and arrives as one wave, which looks nothing like a cruise call
                    // emptying out. Spreading them over the last part of the stay means some are
                    // back early and some leave it late, and the quayside fills gradually.
                    //
                    // Only ever earlier than the ship's own reboard frame, never later, so no party
                    // is given a deadline the vessel will not wait for.
                    uint ownDeadline = reboard - (uint)random.NextInt(0, (int)earlyReturnSpread);

                    commandBuffer.AddComponent(household, new Components.CruisePassenger
                    {
                        m_Ship = vehicle,
                        m_Terminal = terminal,
                        m_ReboardFrame = ownDeadline
                    });

                    // The terminal is their lodging for the stay, which is what keeps them out of
                    // TouristLeaveSystem:68. Guarded by the TouristHousehold test above — a
                    // SetComponent through a command buffer needs the component to be there at
                    // playback, and getting that wrong took down three systems this session.
                    commandBuffer.SetComponent(household, new TouristHousehold
                    {
                        m_Hotel = terminal,
                        m_LeavingTime = reboard
                    });

                    // Take away the errand they arrived with.
                    //
                    // These are ordinary sea arrivals up to this moment, and an arriving visitor's
                    // first business is a bed: they are marked LodgingSeeker, given a hotel, and
                    // walk straight to it. Anchoring m_Hotel to the terminal stops them being
                    // marked again, but it does nothing about the walk already in progress — which
                    // is why a shore party visibly heads for the hotels instead of the sights.
                    //
                    // Dropping the marker and the target leaves them with no errand, and
                    // TouristTargetSearchSystem gives them one on its next pass: an attraction,
                    // a shop, a leisure venue. That is what they are here for.
                    commandBuffer.RemoveComponent<LodgingSeeker>(household);
                    commandBuffer.RemoveComponent<Target>(household);

                    CancelHotelTrip(household, commandBuffer);

                    adopted++;
                }
            }
            finally
            {
                seen.Dispose();
            }

            return adopted;
        }

        /// <summary>
        /// Cancels the errand a cruise passenger arrived carrying.
        ///
        /// These parties reach the ship because they were looking for a hotel — LodgingSeeker is
        /// what earns them a destination, and a destination is what earns them a body. So every one
        /// of them steps off the vessel with a room booked and a trip already under way towards it,
        /// which is why a shore party visibly walks inland to the hotels instead of the sights.
        ///
        /// Clearing the household's Target is not enough. A trip in progress lives on the citizens
        /// as TravelPurpose, and CitizenBehaviorSystem will keep serving it until it is gone;
        /// likewise an outstanding HouseholdNeed sends the party shopping for a specific resource
        /// before anything else is considered. Both are dropped here so the party arrives with no
        /// errand at all, and is given a fresh one — an attraction, a shop, a leisure venue — by the
        /// ordinary tourist behaviour on its next pass.
        ///
        /// The lodging anchor set alongside this call is what stops them being handed another hotel:
        /// TouristHousehold.m_Hotel names the terminal, the terminal carries a zero-price
        /// LodgingProvider, and HouseholdBehaviorSystem:243-251 only re-marks a household
        /// LodgingSeeker when that stops being true.
        /// </summary>
        private void CancelHotelTrip(Entity household, EntityCommandBuffer commandBuffer)
        {
            if (EntityManager.HasComponent<HouseholdNeed>(household))
            {
                commandBuffer.SetComponent(household, new HouseholdNeed
                {
                    m_Resource = Resource.NoResource,
                    m_Amount = 0
                });
            }

            // The shopping mark goes too, so the spending ledger stops attributing this party's
            // wallet drops to goods it is no longer out buying.
            if (EntityManager.HasComponent<Components.ExpectsPurchase>(household))
            {
                commandBuffer.RemoveComponent<Components.ExpectsPurchase>(household);
            }

            // A finished or in-flight lodging search would otherwise be read back and acted on.
            // TouristTargetSearchSystem drops this component to restart a search, so its absence is
            // the neutral state rather than a missing value.
            if (EntityManager.HasComponent<Game.Pathfind.PathInformation>(household))
            {
                commandBuffer.RemoveComponent<Game.Pathfind.PathInformation>(household);
            }

            if (!EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                return;
            }

            DynamicBuffer<HouseholdCitizen> citizens =
                EntityManager.GetBuffer<HouseholdCitizen>(household, isReadOnly: true);

            for (int i = 0; i < citizens.Length; i++)
            {
                Entity citizen = citizens[i].m_Citizen;

                if (citizen == Entity.Null || !EntityManager.Exists(citizen))
                {
                    continue;
                }

                // Removing rather than rewriting: TravelPurpose is added when a trip is issued
                // (TripNeededSystem:1621) and its absence is the state a citizen with nothing to do
                // is in, so taking it away returns them to that rather than inventing a purpose.
                if (EntityManager.HasComponent<TravelPurpose>(citizen))
                {
                    commandBuffer.RemoveComponent<TravelPurpose>(citizen);
                }

                // And the trips queued behind it. TripNeeded is a buffer, not a single value, and
                // CitizenBehaviorSystem serves the next entry as soon as the current purpose is
                // gone — so cancelling only the trip in progress lets a party pick its lodging trip
                // straight back up, which is why some of them still walked to a hotel. Emptying the
                // buffer leaves nothing queued, and the ordinary tourist behaviour fills it again
                // with whatever a visitor with a bed already booked would do: shopping, leisure,
                // attractions.
                if (EntityManager.HasBuffer<TripNeeded>(citizen))
                {
                    commandBuffer.SetBuffer<TripNeeded>(citizen);
                }
            }
        }

        /// <summary>The household behind a creature, or Entity.Null if the hops do not resolve.</summary>
        private Entity HouseholdOf(Entity creature)
        {
            if (creature == Entity.Null
                || !EntityManager.Exists(creature)
                || !EntityManager.HasComponent<Game.Creatures.Resident>(creature))
            {
                return Entity.Null;
            }

            Entity citizen = EntityManager.GetComponentData<Game.Creatures.Resident>(creature).m_Citizen;

            if (citizen == Entity.Null
                || !EntityManager.Exists(citizen)
                || !EntityManager.HasComponent<HouseholdMember>(citizen))
            {
                return Entity.Null;
            }

            Entity household = EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household;

            return household != Entity.Null && EntityManager.Exists(household)
                ? household
                : Entity.Null;
        }

        /// <summary>
        /// Lets a vessel that landed nobody carry straight on to its next stop.
        ///
        /// A cruise call is a thing that happens to passengers, not to a ship. If none came ashore
        /// there is nothing to wait for: no call is created, no lodging is put on the terminal, and
        /// crucially the departure frame is never pushed — so the vessel keeps the sixty-frame
        /// departure the game gave it at TransportBoardingHelpers:368 and leaves on its own
        /// schedule, back towards the outside connection to load.
        ///
        /// This replaces a fallback that created a complement at the quay when the ship arrived
        /// empty. That kept the terminal busy, but it meant an empty ship was indistinguishable
        /// from a full one and the vessel was held for a shore leave it had brought nobody for —
        /// which is the state the player sees as a cruise ship stuck at the pier doing nothing.
        ///
        /// Logged once per arrival rather than once per call, because an empty arrival is a fault
        /// worth seeing every time it happens: with a working line it should never occur.
        /// </summary>
        private void SailOnEmpty(
            Entity vehicle, Entity terminal, EntityCommandBuffer commandBuffer)
        {
            // The manifest, if any, described a sailing that carried nobody. Clearing it lets the
            // vessel load afresh when it next reaches the map edge.
            if (EntityManager.HasComponent<Components.CruiseManifest>(vehicle))
            {
                commandBuffer.RemoveComponent<Components.CruiseManifest>(vehicle);
            }

            // Once per arrival, not once per update.
            //
            // StartCall runs on every scan while the vessel is alongside, so an empty arrival wrote
            // this block every sixteen frames for as long as the ship sat there — twenty times in
            // three seconds in the observed log, each with a full route dump. A diagnostic that
            // repeats faster than the thing it describes changes is noise that hides the line you
            // need.
            if (m_ReportedEmptyShip == vehicle)
            {
                return;
            }

            m_ReportedEmptyShip = vehicle;

            Mod.Log.Warn(
                $"Cruise ship {vehicle.Index} reached terminal {terminal.Index} carrying nobody, "
                + "so no call was started and the vessel sails on to load.");

            ReportEmptyDisembark(vehicle);
            LogRouteTopology(vehicle);
        }

        /// <summary>
        /// Says what was in the ship's passenger buffer when it arrived carrying nobody.
        ///
        /// Two states look identical from the quayside and need different fixes:
        ///
        ///   buffer empty — nobody ever boarded. Note the buffer itself is not saved:
        ///     Game.Vehicles.Passenger is IEmptySerializable and
        ///     Game.Serialization.PassengerSystem:36-53 rebuilds it after load by walking every
        ///     entity with CurrentVehicle or CurrentTransport. Game.Creatures.CurrentVehicle *is*
        ///     serialized, so a properly boarded creature comes back — an empty buffer after a
        ///     reload therefore says the creature never had CurrentVehicle, not that the buffer
        ///     was lost.
        ///
        ///   buffer populated — people are aboard but none of them are ours, so the vessel is
        ///     carrying ordinary transit passengers and no cruise party came in on it.
        /// </summary>
        private void ReportEmptyDisembark(Entity vehicle)
        {
            int inBuffer = 0;
            int ours = 0;
            int missing = 0;

            if (EntityManager.HasBuffer<Passenger>(vehicle))
            {
                DynamicBuffer<Passenger> manifest =
                    EntityManager.GetBuffer<Passenger>(vehicle, isReadOnly: true);

                inBuffer = manifest.Length;

                for (int i = 0; i < manifest.Length; i++)
                {
                    Entity creature = manifest[i].m_Passenger;

                    if (creature == Entity.Null || !EntityManager.Exists(creature))
                    {
                        missing++;
                        continue;
                    }

                    if (IsCruiseCreature(creature))
                    {
                        ours++;
                    }
                }
            }

            Mod.Log.Warn(
                $"  passenger buffer holds {inBuffer} entries, {ours} of them this mod's, "
                + $"{missing} pointing at entities that no longer exist.");
        }

        /// <summary>Whether a creature belongs to one of this mod's cruise parties.</summary>
        private bool IsCruiseCreature(Entity creature)
        {
            if (!EntityManager.HasComponent<Game.Creatures.Resident>(creature))
            {
                return false;
            }

            Entity citizen = EntityManager.GetComponentData<Game.Creatures.Resident>(creature).m_Citizen;

            if (citizen == Entity.Null
                || !EntityManager.Exists(citizen)
                || !EntityManager.HasComponent<HouseholdMember>(citizen))
            {
                return false;
            }

            Entity household = EntityManager.GetComponentData<HouseholdMember>(citizen).m_Household;

            return household != Entity.Null
                   && EntityManager.Exists(household)
                   && EntityManager.HasComponent<Components.CruisePassenger>(household);
        }

        private void HoldShip(
            Entity vehicle, ref Game.Vehicles.PublicTransport transport, uint until)
        {
            if (transport.m_DepartureFrame < until)
            {
                transport.m_DepartureFrame = until;
                EntityManager.SetComponentData(vehicle, transport);
            }
        }

        /// <summary>
        /// Writes out every stop on a cruise route and what the mod makes of it.
        ///
        /// Logged only when a ship arrives carrying nobody, because that has two very different
        /// causes and they are indistinguishable from the quayside: either the line never reaches a
        /// map edge, so there was nowhere to load — the game shows its own "Not connected to the
        /// Outside Connections" warning in that case — or it does reach one and this system failed
        /// to recognise it. The first is the player's to fix by redrawing the line; the second is
        /// mine. One line per stop settles which.
        /// </summary>
        private void LogRouteTopology(Entity vehicle)
        {
            if (!EntityManager.HasComponent<CurrentRoute>(vehicle))
            {
                return;
            }

            Entity route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;

            if (route == Entity.Null
                || !EntityManager.Exists(route)
                || !EntityManager.HasBuffer<RouteWaypoint>(route))
            {
                Mod.Log.Warn("  cruise route has no waypoints at all.");
                return;
            }

            DynamicBuffer<RouteWaypoint> waypoints =
                EntityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);

            int outsideConnections = 0;

            Mod.Log.Info($"  cruise route stops ({waypoints.Length} waypoints):");

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;

                if (waypoint == Entity.Null || !EntityManager.Exists(waypoint))
                {
                    Mod.Log.Info($"    [{i}] waypoint missing");
                    continue;
                }

                if (!EntityManager.HasComponent<Connected>(waypoint))
                {
                    Mod.Log.Info($"    [{i}] waypoint {waypoint.Index}: not connected to a stop");
                    continue;
                }

                Entity stop = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;

                if (stop == Entity.Null || !EntityManager.Exists(stop))
                {
                    Mod.Log.Info($"    [{i}] waypoint {waypoint.Index}: stop missing");
                    continue;
                }

                Entity owner = EntityManager.HasComponent<Owner>(stop)
                    ? EntityManager.GetComponentData<Owner>(stop).m_Owner
                    : Entity.Null;

                bool isOutside =
                    EntityManager.HasComponent<Game.Objects.OutsideConnection>(stop)
                    || (owner != Entity.Null
                        && EntityManager.Exists(owner)
                        && EntityManager.HasComponent<Game.Objects.OutsideConnection>(owner));

                if (isOutside)
                {
                    outsideConnections++;
                }

                Mod.Log.Info(
                    $"    [{i}] stop {stop.Index}, owner {owner.Index}, "
                    + $"outside connection: {isOutside}, "
                    + $"boarding component: {EntityManager.HasComponent<BoardingVehicle>(stop)}");
            }

            if (outsideConnections == 0)
            {
                Mod.Log.Warn(
                    "  no stop on this cruise line is an outside connection, so there is nowhere "
                    + "to load passengers. Draw the line out to a sea connection at the map edge — "
                    + "the game shows the same thing as \"Not connected to the Outside "
                    + "Connections\" on the line panel.");
            }
        }

        /// <summary>
        /// Citizens ashore from a given ship, for the panel.
        ///
        /// A dictionary lookup against the snapshot taken in this system's own update. It touches
        /// no ECS state at all, which is the point: it is called from CruiseDepartureUISystem in the
        /// UIUpdate phase, and anything that resolved a chunk or a type handle from there would be
        /// reaching into another system's state. See <see cref="m_AshoreByShip"/>.
        /// </summary>
        public int CountAshoreFor(Entity vehicle)
        {
            return m_AshoreByShip.TryGetValue(vehicle, out AshoreCount count) ? count.m_People : 0;
        }

        /// <summary>
        /// Counts every shore party once, grouped by the ship it belongs to.
        ///
        /// One pass over the cruise households per update. The chunk walk is legal here and only
        /// here, because these type handles belong to this system and this is its update.
        /// </summary>
        private void SnapshotPassengersAshore()
        {
            m_AshoreByShip.Clear();

            if (m_AshoreQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            ComponentTypeHandle<Components.CruisePassenger> passengerHandle =
                GetComponentTypeHandle<Components.CruisePassenger>(isReadOnly: true);
            BufferTypeHandle<HouseholdCitizen> citizenHandle =
                GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks = m_AshoreQuery.ToArchetypeChunkArray(Allocator.Temp);

            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    NativeArray<Components.CruisePassenger> passengers =
                        chunk.GetNativeArray(ref passengerHandle);

                    // Guarded, not assumed. chunk.GetBufferAccessor on a chunk without the buffer
                    // returns a default accessor rather than failing, and the default throws on
                    // indexing — the latent crash the notes record for CleanUpLeakedHouseholds.
                    bool hasCitizens = chunk.Has(ref citizenHandle);
                    BufferAccessor<HouseholdCitizen> citizens =
                        hasCitizens ? chunk.GetBufferAccessor(ref citizenHandle) : default;

                    for (int i = 0; i < passengers.Length; i++)
                    {
                        Entity ship = passengers[i].m_Ship;

                        if (ship == Entity.Null)
                        {
                            continue;
                        }

                        m_AshoreByShip.TryGetValue(ship, out AshoreCount count);

                        int occupants = hasCitizens ? citizens[i].Length : 0;

                        if (occupants == 0)
                        {
                            count.m_EmptyParties++;
                        }
                        else
                        {
                            count.m_People += occupants;
                        }

                        m_AshoreByShip[ship] = count;
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        /// <summary>
        /// Keeps the ship alongside for the length of its call, and reports it when that fails.
        ///
        /// The lever is Game.Vehicles.PublicTransport.m_DepartureFrame, and it is the only one.
        /// TransportBoardingHelpers.BeginBoarding:378/:388 is the sole writer of that field in the
        /// whole game, so once it is pushed forward nothing moves it again until the vessel begins
        /// boarding somewhere else. Not m_StopDuration: that is the line's planning figure, it is
        /// summed into the fleet size at TransportLineSystem:470/:189 and applies at every stop
        /// including the map edge, and it has been tried and reverted twice. See SESSION-NOTES.
        ///
        /// Deliberately not re-asserting PublicTransportFlags.Boarding. An earlier version did, and
        /// it was worse than useless: StopBoarding is only consulted at PathEndReached
        /// (TransportWatercraftAISystem:268), so forcing the flag back on a vessel already under way
        /// tells the AI it is loading passengers in open water rather than bringing it home.
        /// </summary>
        private void HoldShipUntilReboard(
            Entity vehicle,
            ref Game.Vehicles.PublicTransport transport,
            uint frame,
            bool atOwnTerminal,
            bool atOutsideConnection,
            bool alongside,
            Entity stop,
            EntityCommandBuffer commandBuffer)
        {
            Components.CruiseCall call =
                EntityManager.GetComponentData<Components.CruiseCall>(vehicle);

            if (frame >= call.m_ReboardFrame)
            {
                // Shore leave is over. Anyone still ashore is collected by ReturnFinishedParties on
                // this same update, so the call can be closed here.
                ReleaseTerminalLodging(call.m_Terminal, commandBuffer);
                commandBuffer.RemoveComponent<Components.CruiseCall>(vehicle);

                Mod.Log.Info(
                    $"Cruise call closed at terminal {call.m_Terminal.Index}; "
                    + $"{call.m_PartyCount} parties reboarded.");

                return;
            }

            // Re-asserted every update, and no longer only when the vessel is recognisably alongside
            // its own terminal.
            //
            // TransportBoardingHelpers:388 rewrites m_DepartureFrame to frame + 60 whenever boarding
            // begins, so the hold is not a single write — it has to be restored. The old condition
            // required the stop to resolve and to match the call's terminal on that exact update,
            // and the boarding flag flickers: measured at the map edge, a vessel dropped out of
            // boarding 192 frames into a load and was back a second later. One flicker landing on a
            // BeginBoarding leaves frame + 60 standing and the ship sails.
            //
            // The only place holding would be wrong is the map edge, where a shore leave must never
            // pin the vessel. Everything else on a cruise line's route is a city quay, and a live
            // CruiseCall means this vessel owes that quay a dwell — so the test is simply "not at an
            // outside connection", which cannot flicker with the boarding state.
            if (!atOutsideConnection && transport.m_DepartureFrame < call.m_ReboardFrame)
            {
                transport.m_DepartureFrame = call.m_ReboardFrame;
                EntityManager.SetComponentData(vehicle, transport);
            }

            if (atOwnTerminal)
            {
                return;
            }

            // Shore leave is running and the ship is not alongside the quay it belongs to. Either it
            // has sailed, or it is still there and the game has stopped recognising it as boarding —
            // and those need different fixes, so they are told apart rather than guessed at.
            ReportEscape(vehicle, ref call, frame, alongside, stop, transport);
        }

        /// <summary>
        /// Records, once per call, why the hold stopped applying.
        ///
        /// The previous version of this check could not fire at all. It was reached only after
        /// atOwnTerminal had been confirmed, and atOwnTerminal implies the Boarding flag is set,
        /// while the check itself required that flag to be clear — so a ship leaving early was
        /// silent, and the handover's account of this fault was written from an earlier build.
        ///
        /// There are four ways a vessel can leave a stop the mod has pushed m_DepartureFrame on, and
        /// each leaves a different fingerprint. Guessing between them costs a build-and-look round
        /// each; the readings below separate them in one:
        ///
        ///   BoardingVehicle no longer names the ship — StopBoarding:797-800 sets its flag from the
        ///     stop, and when that flag is false the departure-frame test at :807 is skipped
        ///     entirely and :850 clears Boarding regardless of the hold. The hold never applied.
        ///     BoardingVehicleSystem nulls that field on every stop in the city whenever any
        ///     waypoint is Updated or Deleted, which would explain why this is intermittent.
        ///
        ///   PathOwner failed, or the target no longer exists — the forced stop at :255-259, which
        ///     passes forcedStop: true and skips the whole departure-frame block.
        ///
        ///   Lane flags without EndOfPath|EndReached — the forced stop at :316-318, same bypass,
        ///     reached because the vessel is no longer at the end of its path.
        ///
        ///   m_DepartureFrame below the reboard frame — BeginBoarding:388 overwrote the hold and
        ///     this system's next scan had not yet put it back.
        /// </summary>
        private void ReportEscape(
            Entity vehicle,
            ref Components.CruiseCall call,
            uint frame,
            bool alongside,
            Entity stop,
            Game.Vehicles.PublicTransport transport)
        {
            if (call.m_Escaped != 0)
            {
                return;
            }

            call.m_Escaped = 1;
            EntityManager.SetComponentData(vehicle, call);

            Mod.Log.Warn(
                $"Cruise ship {vehicle.Index} left terminal {call.m_Terminal.Index} before shore "
                + $"leave ended (frame {frame} of {call.m_ReboardFrame}). Passengers ashore will "
                + "still depart on schedule.");

            Mod.Log.Warn(
                $"  state {transport.m_State}, departure frame {transport.m_DepartureFrame} "
                + $"(hold wanted {call.m_ReboardFrame}), "
                + $"alongside: {alongside}"
                + (alongside ? $" at stop {stop.Index}, not terminal {call.m_Terminal.Index}" : ""));

            // Fully qualified. Game.Pathfind carries a good deal that shares names with
            // Game.Vehicles and Game.Net, and the notes already record what importing a namespace
            // wholesale for one type costs to untangle.
            if (EntityManager.HasComponent<Game.Pathfind.PathOwner>(vehicle))
            {
                Mod.Log.Warn(
                    "  path owner "
                    + $"{EntityManager.GetComponentData<Game.Pathfind.PathOwner>(vehicle).m_State}");
            }

            if (EntityManager.HasComponent<Game.Vehicles.WatercraftCurrentLane>(vehicle))
            {
                Mod.Log.Warn(
                    "  lane flags "
                    + $"{EntityManager.GetComponentData<Game.Vehicles.WatercraftCurrentLane>(vehicle).m_LaneFlags}");
            }

            Entity target = EntityManager.HasComponent<Target>(vehicle)
                ? EntityManager.GetComponentData<Target>(vehicle).m_Target
                : Entity.Null;

            Mod.Log.Warn(
                $"  target {target.Index}, exists: "
                + $"{target != Entity.Null && EntityManager.Exists(target)}");

            LogBoardingHolders(vehicle);
        }

        /// <summary>
        /// Writes out which vessel each stop on this ship's route currently admits.
        ///
        /// BoardingVehicle is the single condition StopBoarding's hold is gated on, and it lives on
        /// the stop rather than the waypoint — the trap the notes already record. One line per stop
        /// says whether the ship lost its claim on the quay, and if so who holds it now.
        /// </summary>
        private void LogBoardingHolders(Entity vehicle)
        {
            if (!EntityManager.HasComponent<CurrentRoute>(vehicle))
            {
                return;
            }

            Entity route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;

            if (route == Entity.Null
                || !EntityManager.Exists(route)
                || !EntityManager.HasBuffer<RouteWaypoint>(route))
            {
                return;
            }

            DynamicBuffer<RouteWaypoint> waypoints =
                EntityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;

                if (waypoint == Entity.Null
                    || !EntityManager.Exists(waypoint)
                    || !EntityManager.HasComponent<Connected>(waypoint))
                {
                    continue;
                }

                Entity connected = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;

                if (connected == Entity.Null || !EntityManager.Exists(connected))
                {
                    continue;
                }

                Entity holder = EntityManager.HasComponent<BoardingVehicle>(connected)
                    ? EntityManager.GetComponentData<BoardingVehicle>(connected).m_Vehicle
                    : Entity.Null;

                // WaitingPassengers is the queue the game keeps at a stop, and it is the number
                // that settles whether anyone can board at the map edge at all. It lives on the
                // *waypoint*, and TransportLinePrefab:87-90 only adds it when the line is a
                // passenger line — so its absence and its being zero mean different things, and
                // both are reported.
                string waiting = "no WaitingPassengers component";

                if (EntityManager.HasComponent<WaitingPassengers>(waypoint))
                {
                    WaitingPassengers queue =
                        EntityManager.GetComponentData<WaitingPassengers>(waypoint);

                    waiting = $"{queue.m_Count} waiting, avg wait {queue.m_AverageWaitingTime}";
                }

                Mod.Log.Warn(
                    $"  waypoint [{i}] stop {connected.Index}: boarding vehicle {holder.Index}"
                    + (holder == vehicle ? " (ours)" : holder == Entity.Null ? " (none)" : "")
                    + $", {waiting}");
            }
        }

        /// <summary>
        /// Walks parties back to the quay as their shore leave runs down, then puts them aboard.
        ///
        /// Two things happen here, in the order a passenger experiences them.
        ///
        /// LAST CALL. Inside the final <see cref="kLastCallFraction"/> of shore leave the party is
        /// pointed at the terminal with a Target — the same component TouristTargetSearchSystem
        /// uses to send a visitor anywhere else, so the walk back is ordinary native pathfinding.
        /// Its pending shopping need is cleared at the same moment, because CitizenBehaviorSystem
        /// checks the household's need before leisure and before anything else, so a party that
        /// still has one would turn round and go to a shop instead of the ship. Recalling once
        /// matters: a second Target replaces the walk already in progress, which is what
        /// m_Recalled prevents.
        ///
        /// SAILING. At the reboard frame the household leaves the city as any visitor does. Whether
        /// it physically reached the quay is not enforced — a party still walking is counted aboard
        /// anyway, because the alternative is a household stranded in a city with no hotel and no
        /// ship, which is worse in every way than a slightly generous departure.
        /// </summary>
        private void ReturnFinishedParties()
        {
            if (m_AshoreQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            uint frame = m_SimulationSystem.frameIndex;
            uint lastCall = (uint)math.max(1f, ShoreLeaveFrames() * kLastCallFraction);

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();

            EntityTypeHandle entityHandle = GetEntityTypeHandle();
            ComponentTypeHandle<Components.CruisePassenger> passengerHandle =
                GetComponentTypeHandle<Components.CruisePassenger>(isReadOnly: false);

            NativeArray<ArchetypeChunk> chunks = m_AshoreQuery.ToArchetypeChunkArray(Allocator.Temp);

            int recalled = 0;
            int sailed = 0;

            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    NativeArray<Entity> entities = chunk.GetNativeArray(entityHandle);
                    NativeArray<Components.CruisePassenger> passengers =
                        chunk.GetNativeArray(ref passengerHandle);

                    for (int i = 0; i < entities.Length; i++)
                    {
                        Components.CruisePassenger passenger = passengers[i];

                        // Still at sea, inbound: no port has been chosen yet, so there is no
                        // deadline to be past.
                        //
                        // The terminal is the sentinel, not the frame. m_ReboardFrame is zero until
                        // the ship docks and the shore leave is worked out, and zero is a perfectly
                        // real frame that every later frame is past — so testing the deadline alone
                        // marked a whole complement overdue the instant it was created and moved
                        // all 888 parties away a tenth of a second after loading, before any of
                        // them had citizens to put aboard.
                        if (passenger.m_Terminal == Entity.Null)
                        {
                            continue;
                        }

                        KeepOffTheHotels(entities[i], passenger.m_Terminal, commandBuffer);

                        // Sailing time. They are at or near the quay by now, having been walking
                        // back since last call, so ending the visit here is not something the
                        // player watches happen in the middle of the city.
                        //
                        // The departure is a MovingAway naming this ship's own connection. It may
                        // resolve by placement rather than travel — TripNeededSystem:1583-1599 puts
                        // a citizen straight at the target and marks them Arrived when no path is
                        // needed — and that is acceptable precisely because it happens at the
                        // harbour they already walked to.
                        // Away as soon as they are actually at the quay, rather than at the
                        // deadline.
                        //
                        // A party that has walked back and is standing at the terminal should leave
                        // on the ship that is sitting there — the departure trip's first leg is that
                        // vessel, because it is the only route from this stop to the map edge. Made
                        // to wait for the reboard frame instead, they stand about until the call
                        // closes and are then removed wherever they happen to be, which is the
                        // teleport the player notices.
                        //
                        // The deadline below is the backstop for anyone who never made it.
                        bool atQuay = frame < passenger.m_ReboardFrame
                                      && passenger.m_Recalled != 0
                                      && PartyHasReached(entities[i], passenger.m_Terminal);

                        // At the quay, send them on as travellers rather than as leavers.
                        //
                        // MovingAway is what a departing visitor gets, and it is the reason nobody
                        // boards: TripNeededSystem:1583-1599 may satisfy that purpose by *placing*
                        // the citizen at the target and marking them Arrived, with no journey and
                        // therefore no vessel. Purpose.Leisure always travels, so the trip is real,
                        // its destination is the map edge, and the only route there from this pier
                        // is the ship — so they walk aboard and the counter moves.
                        //
                        // They stay tagged and marked homeward, which is what LandHomewardPassengers
                        // uses to release them once the vessel reaches the connection. The tag is
                        // also what keeps them out of the hotels for the crossing.
                        if (atQuay)
                        {
                            TryGetOutsideConnection(passenger.m_Ship, out Entity port, out int _);

                            if (port != Entity.Null)
                            {
                                SendOnTrip(entities[i], port, commandBuffer);

                                passenger.m_Homeward = 1;
                                passenger.m_Terminal = Entity.Null;
                                passengers[i] = passenger;

                                sailed++;
                                continue;
                            }
                        }

                        if (frame >= passenger.m_ReboardFrame)
                        {
                            TryGetOutsideConnection(passenger.m_Ship, out Entity homePort, out int _);

                            commandBuffer.RemoveComponent<Components.CruisePassenger>(entities[i]);

                            commandBuffer.AddComponent(entities[i], new Game.Agents.MovingAway
                            {
                                m_Target = homePort,
                                m_Reason = Game.Agents.MoveAwayReason.None
                            });

                            sailed++;
                            continue;
                        }

                        // Not yet last call: they are still out seeing the city.
                        if (frame + lastCall < passenger.m_ReboardFrame)
                        {
                            continue;
                        }

                        // Last call. Send them home the way the game sends every visitor home.
                        //
                        // This used to put them back aboard by hand — add the creature to the
                        // vessel's Passenger buffer, give it CurrentVehicle, set InVehicle. That
                        // crashed the game with "Item already added (NativeQuadTree.Add)", and the
                        // reason is worth keeping: a party ashore has real bodies standing on the
                        // pier, inside the spatial index. Native boarding unspawns the creature as
                        // it steps aboard (VehicleUtils.CheckUnspawned); doing it by hand leaves
                        // the body both in the world and on the ship, and the next spatial insert
                        // throws. Bodies are the game's to move — the same lesson that killed
                        // hand-boarding at the map edge, arriving louder.
                        //
                        // MovingAway is a real trip with the outside connection as its
                        // destination, so the citizens walk to the quay and board the ship that
                        // serves that route, under their own power. It is also what lets the count
                        // on the vessel climb as the shore party returns, which is the visible half
                        // of the round trip.
                        //
                        // MoveAwayReason.None marks an ordinary departure, as TouristStaySystem
                        // does. The default is NoSuitableProperty, which would report every
                        // returning passenger as a housing failure in the diagnostics.
                        // Last call: walk back to the harbour, on an ordinary trip.
                        //
                        // A Target on the household is what TouristTargetSearchSystem uses to send a
                        // visitor anywhere else, so this is the same native journey as going to a
                        // museum — the party is visibly on foot across the city and arrives at the
                        // quay under its own power. That is the half the player watches, and it is
                        // why the departure itself is deferred to the reboard frame rather than
                        // issued here: MovingAway can resolve by placement instead of travel, and
                        // doing that mid-city is exactly the teleport that made the recall invisible.
                        //
                        // The party keeps its CruisePassenger tag, so it stays out of the hotels and
                        // stays counted against this ship, right up until it leaves.
                        // Re-issued every update until they sail, not once.
                        //
                        // A citizen is only re-evaluated on its own UpdateFrame, so a single write
                        // can land at a moment the party never looks at — observed as one of three
                        // walking back while the other two idled indoors. The first call clears
                        // whatever they were doing; every pass after that only touches citizens who
                        // are idle, so a walk already under way is never cancelled.
                        RecallToHarbour(
                            entities[i], passenger.m_Terminal, commandBuffer,
                            passenger.m_Recalled == 0);

                        if (passenger.m_Recalled == 0)
                        {
                            passenger.m_Recalled = 1;
                            passengers[i] = passenger;

                            recalled++;
                        }
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            if (recalled > 0 || sailed > 0)
            {
                Mod.Log.Info(
                    $"Cruise shore leave: {recalled} parties recalled to the quay, "
                    + $"{sailed} reboarded.");
            }
        }

        /// <summary>
        /// Points one party at the harbour and takes away its reasons to stop on the way.
        ///
        /// An ordinary trip, deliberately. The walk back is the visible half of a cruise call, so it
        /// has to be a real journey rather than a departure — a departure can be satisfied by
        /// placing the citizen at the destination (TripNeededSystem:1583-1599), which is what made
        /// an earlier version of this look like the whole shore party vanishing at once.
        ///
        /// Everything that could divert them on the way is cleared at the same moment.
        /// CitizenBehaviorSystem checks the household's need before leisure and before anything
        /// else, so a party still carrying a shopping need turns round and goes to a shop; a queued
        /// TripNeeded is served as soon as the current purpose ends; and a stale PathInformation is
        /// read back as a finished search. Recalling once matters too, which is what m_Recalled
        /// guards: a second Target would replace the walk already in progress.
        /// </summary>
        private void RecallToHarbour(
            Entity household, Entity terminal, EntityCommandBuffer commandBuffer, bool firstCall)
        {
            if (terminal == Entity.Null || !EntityManager.Exists(terminal))
            {
                return;
            }

            if (!firstCall)
            {
                NudgeIdleCitizens(household, terminal, commandBuffer);
                return;
            }

            if (EntityManager.HasComponent<HouseholdNeed>(household))
            {
                commandBuffer.SetComponent(household, new HouseholdNeed
                {
                    m_Resource = Resource.NoResource,
                    m_Amount = 0
                });
            }

            if (EntityManager.HasComponent<Components.ExpectsPurchase>(household))
            {
                commandBuffer.RemoveComponent<Components.ExpectsPurchase>(household);
            }

            if (EntityManager.HasComponent<Game.Pathfind.PathInformation>(household))
            {
                commandBuffer.RemoveComponent<Game.Pathfind.PathInformation>(household);
            }

            if (!EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                return;
            }

            DynamicBuffer<HouseholdCitizen> citizens =
                EntityManager.GetBuffer<HouseholdCitizen>(household, isReadOnly: true);

            for (int i = 0; i < citizens.Length; i++)
            {
                Entity citizen = citizens[i].m_Citizen;

                if (citizen == Entity.Null
                    || !EntityManager.Exists(citizen)
                    || !EntityManager.HasBuffer<TripNeeded>(citizen))
                {
                    continue;
                }

                // Whatever they were doing is over.
                if (EntityManager.HasComponent<TravelPurpose>(citizen))
                {
                    commandBuffer.RemoveComponent<TravelPurpose>(citizen);
                }

                // The trip is issued to the citizen, not to the household, and that is the fix.
                //
                // A Target on the household does not make anybody walk. TouristHouseholdBehaviorSystem
                // reads it at :59-66 and, when it names a valid building, simply continues — it
                // treats the household as already sorted and issues nothing. So the destination was
                // recorded and no journey ever started, which is exactly "recalled, but nobody
                // moves".
                //
                // TripNeeded is what the game actually acts on. TripNeededSystem walks this buffer,
                // spawns a body at the citizen's current location and hands it the target
                // (:1614-1615), so writing one entry naming the terminal produces an ordinary walk
                // across the city to the quay — the same machinery that takes a visitor to a museum.
                //
                // Purpose.Leisure rather than MovingAway on purpose: MovingAway can be satisfied by
                // placing the citizen at the destination instead of travelling to it
                // (:1583-1599), which is the teleport that made the whole shore party vanish at
                // once. Leisure always travels.
                DynamicBuffer<TripNeeded> trips = commandBuffer.SetBuffer<TripNeeded>(citizen);

                trips.Add(new TripNeeded
                {
                    m_TargetAgent = terminal,
                    m_Purpose = Purpose.Leisure,
                    m_Resource = Resource.NoResource,
                    m_Priority = byte.MaxValue
                });
            }
        }

        /// <summary>
        /// Issues one party a real journey to somewhere, replacing whatever it was doing.
        ///
        /// The trip goes on the citizens, because that is the only thing the game acts on — a
        /// Target on the household is read by TouristHouseholdBehaviorSystem:59-66 and skipped.
        /// Purpose.Leisure is used for every destination this mod chooses, including the map edge,
        /// because it is the one purpose that always travels: MovingAway can be satisfied by
        /// placing the citizen at the target instead (TripNeededSystem:1583-1599), which produces no
        /// journey and no boarding.
        /// </summary>
        private void SendOnTrip(
            Entity household, Entity destination, EntityCommandBuffer commandBuffer)
        {
            if (!EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                return;
            }

            DynamicBuffer<HouseholdCitizen> citizens =
                EntityManager.GetBuffer<HouseholdCitizen>(household, isReadOnly: true);

            for (int i = 0; i < citizens.Length; i++)
            {
                Entity citizen = citizens[i].m_Citizen;

                if (citizen == Entity.Null
                    || !EntityManager.Exists(citizen)
                    || !EntityManager.HasBuffer<TripNeeded>(citizen))
                {
                    continue;
                }

                if (EntityManager.HasComponent<TravelPurpose>(citizen))
                {
                    commandBuffer.RemoveComponent<TravelPurpose>(citizen);
                }

                DynamicBuffer<TripNeeded> trips = commandBuffer.SetBuffer<TripNeeded>(citizen);

                trips.Add(new TripNeeded
                {
                    m_TargetAgent = destination,
                    m_Purpose = Purpose.Leisure,
                    m_Resource = Resource.NoResource,
                    m_Priority = byte.MaxValue
                });
            }
        }

        /// <summary>
        /// Whether a recalled party has actually got to the quay.
        ///
        /// Any citizen standing in the terminal is enough. A party is not a unit once it is walking
        /// — its members path separately and arrive apart — and holding the whole household until
        /// the last straggler is inside means the early arrivals loiter at the quayside for no
        /// reason the player can see.
        ///
        /// CurrentBuilding is the test because it is what "inside this building" means for a
        /// citizen: it and CurrentTransport are alternatives (CitizenTravelPurposeSystem:631), so
        /// holding the first is precisely not being out in the world walking.
        /// </summary>
        private bool PartyHasReached(Entity household, Entity terminal)
        {
            if (terminal == Entity.Null || !EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                return false;
            }

            DynamicBuffer<HouseholdCitizen> citizens =
                EntityManager.GetBuffer<HouseholdCitizen>(household, isReadOnly: true);

            for (int i = 0; i < citizens.Length; i++)
            {
                Entity citizen = citizens[i].m_Citizen;

                if (citizen == Entity.Null
                    || !EntityManager.Exists(citizen)
                    || !EntityManager.HasComponent<CurrentBuilding>(citizen))
                {
                    continue;
                }

                if (EntityManager.GetComponentData<CurrentBuilding>(citizen).m_CurrentBuilding
                    == terminal)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Gives the harbour trip back to any citizen of a recalled party that has gone idle.
        ///
        /// The recall cannot be a single write. A citizen is re-evaluated only on its own
        /// UpdateFrame, and one that is indoors with nothing to do will sit there indefinitely if
        /// the trip it was handed was consumed or discarded on a pass it did not take part in —
        /// observed as one party of three walking back while the other two idled in a building.
        ///
        /// Idle is defined narrowly, and that is what makes re-issuing safe: no TravelPurpose, so
        /// nothing is in progress, and an empty TripNeeded buffer, so nothing is queued. A citizen
        /// already walking to the quay matches neither and is left alone, which is the failure the
        /// old once-only m_Recalled guard was protecting against.
        /// </summary>
        private void NudgeIdleCitizens(
            Entity household, Entity terminal, EntityCommandBuffer commandBuffer)
        {
            if (!EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                return;
            }

            DynamicBuffer<HouseholdCitizen> citizens =
                EntityManager.GetBuffer<HouseholdCitizen>(household, isReadOnly: true);

            for (int i = 0; i < citizens.Length; i++)
            {
                Entity citizen = citizens[i].m_Citizen;

                if (citizen == Entity.Null
                    || !EntityManager.Exists(citizen)
                    || !EntityManager.HasBuffer<TripNeeded>(citizen))
                {
                    continue;
                }

                // "Not already travelling" is the test, and nothing narrower.
                //
                // Two narrower ones were tried and both left people behind. Requiring an absent
                // TravelPurpose missed anyone who had arrived somewhere, because the purpose *is*
                // the thing they are busy doing. Requiring CurrentBuilding then missed a party
                // inside a tourist attraction, which holds its visitors differently.
                //
                // The only thing this must not do is interrupt someone already walking back, and
                // that state has one unambiguous marker: CurrentTransport, the citizen's link to a
                // body out in the world (CitizenTravelPurposeSystem:631 treats it and
                // CurrentBuilding as alternatives). Skipping on that and acting on everything else
                // interrupts every kind of indoors, named or not — which is what a last call is.
                if (EntityManager.HasComponent<CurrentTransport>(citizen))
                {
                    continue;
                }

                if (EntityManager.HasComponent<TravelPurpose>(citizen))
                {
                    commandBuffer.RemoveComponent<TravelPurpose>(citizen);
                }

                DynamicBuffer<TripNeeded> trips = commandBuffer.SetBuffer<TripNeeded>(citizen);

                trips.Add(new TripNeeded
                {
                    m_TargetAgent = terminal,
                    m_Purpose = Purpose.Leisure,
                    m_Resource = Resource.NoResource,
                    m_Priority = byte.MaxValue
                });
            }
        }

        /// <summary>
        /// Keeps a cruise passenger out of the hotel system, whatever else has gone wrong.
        ///
        /// A day tripper sleeps aboard and must never take a room — that is not a preference, it is
        /// the whole point of the feature, and a cruise party competing for hotel rooms would
        /// displace the visitors those rooms were built for.
        ///
        /// The anchor is TouristHousehold.m_Hotel naming something with a LodgingProvider, and
        /// HouseholdBehaviorSystem:243-251 marks the household a LodgingSeeker the moment that stops
        /// being true — either because the hotel is null, or because it no longer provides lodging.
        /// Both were reachable: a swept terminal, a deleted building, a save from a build before the
        /// terminal was equipped. Rather than enumerate the ways the anchor can break, this repairs
        /// it every update and strips the marker if one was already handed out.
        /// </summary>
        private void KeepOffTheHotels(
            Entity household, Entity terminal, EntityCommandBuffer commandBuffer)
        {
            if (EntityManager.HasComponent<LodgingSeeker>(household))
            {
                commandBuffer.RemoveComponent<LodgingSeeker>(household);
            }

            // Cancel a journey to a hotel, not just the marker that asked for one.
            //
            // The marker keeps coming back and there is nothing here that can stop it:
            // TouristHouseholdBehaviorSystem:74 nulls m_Hotel whenever the anchor building has no
            // Renter buffer, and a harbour has not got one, so every pass of that system decides
            // this household is unhoused and re-marks it. Stripping LodgingSeeker on our next
            // update is too late — TouristTargetSearchSystem has already run in between, found a
            // hotel and sent them walking to it, which is what "they all go to a hotel first" is.
            //
            // So the target is cancelled as well. A cruise passenger has no business travelling to
            // a building that provides lodging, and the terminal is excluded because that is their
            // own anchor. Everything else they might be heading for — a shop, a venue, an
            // attraction — is left strictly alone, so this cannot interrupt sightseeing.
            if (EntityManager.HasComponent<Target>(household))
            {
                Entity going = EntityManager.GetComponentData<Target>(household).m_Target;

                if (going != terminal
                    && going != Entity.Null
                    && EntityManager.HasComponent<LodgingProvider>(going))
                {
                    commandBuffer.RemoveComponent<Target>(household);
                    CancelHotelTrip(household, commandBuffer);
                }
            }

            if (terminal == Entity.Null || !EntityManager.Exists(terminal))
            {
                return;
            }

            // Re-equip the terminal if its provider has gone. Cheap: the component test fails
            // immediately in the ordinary case.
            if (!EntityManager.HasComponent<LodgingProvider>(terminal))
            {
                EquipTerminalWithLodging(terminal, commandBuffer);
            }

            if (!EntityManager.HasComponent<TouristHousehold>(household))
            {
                return;
            }

            TouristHousehold tourist = EntityManager.GetComponentData<TouristHousehold>(household);

            if (tourist.m_Hotel != terminal)
            {
                tourist.m_Hotel = terminal;
                commandBuffer.SetComponent(household, tourist);
            }
        }

        /// <summary>
        /// Lets a homeward complement off at the map edge — they have left the city.
        ///
        /// Called before loading the next one, so a ship never carries two complements at once.
        /// </summary>
        private int LandHomewardPassengers(Entity vehicle, EntityCommandBuffer commandBuffer)
        {
            int released = 0;

            EntityTypeHandle entityHandle = GetEntityTypeHandle();
            ComponentTypeHandle<Components.CruisePassenger> passengerHandle =
                GetComponentTypeHandle<Components.CruisePassenger>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks = m_AshoreQuery.ToArchetypeChunkArray(Allocator.Temp);

            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    NativeArray<Entity> entities = chunk.GetNativeArray(entityHandle);
                    NativeArray<Components.CruisePassenger> passengers =
                        chunk.GetNativeArray(ref passengerHandle);

                    for (int i = 0; i < entities.Length; i++)
                    {
                        if (passengers[i].m_Ship != vehicle || passengers[i].m_Homeward == 0)
                        {
                            continue;
                        }

                        // Only the tag comes off. They were already sent on their way at last call,
                        // and moving away a household that is already moving away would give it a
                        // second departure trip.
                        commandBuffer.RemoveComponent<Components.CruisePassenger>(entities[i]);

                        released++;
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            return released;
        }

        private uint ShoreLeaveFrames()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            int hours = settings != null ? math.clamp(settings.CruiseShoreLeaveHours, 2, 48) : 8;

            return (uint)((ulong)kFramesPerDay * (ulong)hours / 24UL);
        }

        /// <summary>
        /// Gives the terminal a zero-price stand-in LodgingProvider, if it has none of its own.
        ///
        /// Zero price is load-bearing, not cosmetic: TouristLeaveSystem's money check evicts when
        /// the wallet is below the provider's m_Price, so a price of zero can never trigger it. A
        /// cruise passenger is therefore immune to both the no-hotel and the no-money eviction for
        /// as long as they are ashore, which is exactly right — they have a bed on the ship and
        /// their passage is already paid.
        /// </summary>
        private void EquipTerminalWithLodging(Entity terminal, EntityCommandBuffer commandBuffer)
        {
            if (EntityManager.HasComponent<LodgingProvider>(terminal))
            {
                // Something already provides lodging here. Leave it alone — overwriting a real
                // provider would misprice a genuine hotel, and the passengers are covered either
                // way because all TouristLeaveSystem asks is that the component exists.
                return;
            }

            // m_FreeRooms is never read for this provider — nothing books a room at a harbour, and
            // every query that counts capacity also requires PropertyRenter or Renter, which a
            // harbour has not got. It is set to the ship's capacity anyway so the value is at least
            // truthful if something ever does look at it.
            commandBuffer.AddComponent(terminal, new LodgingProvider
            {
                m_Price = 0,
                m_FreeRooms = Mod.Settings != null
                    ? math.clamp(Mod.Settings.CruiseShipCapacity, 100, 5000)
                    : 2000
            });

            commandBuffer.AddComponent<Components.CruiseTerminalLodging>(terminal);
        }

        private void ReleaseTerminalLodging(Entity terminal, EntityCommandBuffer commandBuffer)
        {
            if (terminal == Entity.Null
                || !EntityManager.Exists(terminal)
                || !EntityManager.HasComponent<Components.CruiseTerminalLodging>(terminal))
            {
                return;
            }

            commandBuffer.RemoveComponent<LodgingProvider>(terminal);
            commandBuffer.RemoveComponent<Components.CruiseTerminalLodging>(terminal);
        }
    }
}
