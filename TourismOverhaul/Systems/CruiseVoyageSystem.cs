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
using Unity.Jobs;
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
    public partial class CruiseVoyageSystem : GameSystemBase, Game.Serialization.IPreSerialize
    {
        /// <summary>
        /// Shore leave, in frames. 262,144 frames is one in-game day, which is one displayed month
        /// and over an hour of real play.
        /// </summary>
        private const uint kFramesPerDay = 262144u;

        /// <summary>
        /// How often a recalled party is told again, in frames. 2048 is a hundred and twenty-eight
        /// times per in-game day.
        ///
        /// Often enough that a tourist cannot get through more than a short errand before being
        /// turned round, rare enough that the clear is not run over the whole complement every
        /// update.
        /// </summary>
        private const uint kRecallRefreshFrames = 1024u;

        /// <summary>
        /// Longest a ship waits past its scheduled departure for passengers still ashore: one in-game
        /// hour, and only while they are still coming back (see <see cref="kOverstayStallFrames"/>).
        /// Beyond it the stragglers are written off.
        ///
        /// A fixed hour rather than a fraction of the shore leave, which let a long shore leave hold
        /// the ship for as long again. The recall is what brings people back in time; the overstay
        /// is only for the last few walking up the quay.
        /// </summary>
        private const uint kMaxOverstayFrames = kFramesPerDay / 24u;

        /// <summary>How far each wait for stragglers pushes the sailing, in frames.</summary>
        private const uint kOverstayStepFrames = 1024u;

        /// <summary>
        /// How long the ship keeps waiting with nobody else getting back, in frames (about 45 in-game
        /// minutes). A party with no way to the quay must not hold the ship for the whole overstay.
        /// </summary>
        private const uint kOverstayStallFrames = 8192u;

        /// <summary>
        /// Frames between shore-party sweeps. Four times the vessel's own cadence.
        ///
        /// The vessel needs sixteen because a hold has to be written inside the sixty-frame window
        /// TransportBoardingHelpers:368 gives it. A party ashore has no such deadline, so sweeping
        /// it at the same rate was walking two thousand households sixteen times a second to check
        /// state measured in in-game hours.
        /// </summary>
        private const uint kShorePartyInterval = 64u;

        /// <summary>
        /// Share of a complement that stays aboard rather than going ashore at a call.
        ///
        /// Not everyone gets off at every port. These parties are simply not adopted: they keep no
        /// component of this mod's, remain in the vessel's own passenger buffer, and sail on. The
        /// ashore count and the recall therefore never see them, which is correct — they are the
        /// ship's business and not the city's.
        /// </summary>
        private const float kStayAboardFraction = 0.1f;

        /// <summary>
        /// The share of shore leave reserved for actually getting aboard.
        ///
        /// A party's own deadline used to be the same frame the ship sailed, which left no time at
        /// all for the last leg — a party that reached the quay on its deadline had to path to the
        /// map-edge connection, walk to the stop and board, all within the frame the vessel was
        /// already leaving on. Holding their deadline this far short of the vessel's turns "be back
        /// by the time it goes" into "be back in time to board it", which is what a last call means.
        ///
        /// The vessel's own departure is unchanged, and so is the frame after which anyone still
        /// ashore is written off — this only moves when the shore party is expected back.
        /// </summary>
        private const float kBoardingGraceFraction = 0.15f;

        /// <summary>
        /// Scale of the recall lead, in in-game hours: the lead is kRecallLeadHours * ln(1 + stay / 4h).
        ///
        /// Log-based so the lead grows with the stay without getting out of hand. A fraction of the stay
        /// was always wrong at one end: the walk back takes about as long whatever the stay, so half of
        /// a ten-hour call recalled parties 40 minutes in and had most back with five hours to go, while
        /// a fixed 20% recalled too late and many cut it fine. The log keeps short stays near half and
        /// long ones well under it: 8 h -> 4.9 h, 12 h -> 6.2 h, 24 h -> 8.8 h, 48 h -> 11.5 h before
        /// sailing.
        /// </summary>
        private const float kRecallLeadHours = 4.5f;

        /// <summary>Parties are recalled over this share of the lead after the first, so the quay fills gradually.</summary>
        private const float kRecallSpreadOfLead = 0.2f;

        /// <summary>The boarding grace is kBoardingGraceFraction of the stay, but never more than this share of the lead.</summary>
        private const float kGraceCapOfLead = 0.4f;

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
        private const uint kBatchIntervalFrames = 512u;

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

        /// <summary>Set by a load; <see cref="ReportHoldsAfterLoad"/> clears it.</summary>
        private bool m_ReportHoldsAfterLoad;

        /// <summary>Earliest frame the next queue batch may be ordered. Not saved; a reload simply
        /// allows one batch immediately, which is harmless.</summary>
        private uint m_NextBatchFrame;

        /// <summary>Per ship on an overstay: fewest people seen ashore, and the frame that low was first seen.</summary>
        private readonly Dictionary<Entity, (int People, uint Since)> m_OverstayProgress =
            new Dictionary<Entity, (int People, uint Since)>();

        /// <summary>Queue batches in a row after which nobody was waiting or aboard; see MaintainCruiseQueue.</summary>
        private int m_FruitlessBatches;

        private bool m_WarnedStalledQueue;

        /// <summary>Batches with nobody turning up before the queue slows down: four complements.</summary>
        private const int kStalledBatches = 4;

        /// <summary>How often a stalled queue still tries, in frames: an eighth of the normal rate.</summary>
        private const uint kStalledIntervalFrames = kBatchIntervalFrames * 8;

        /// <summary>Vessel already reported as having arrived empty, so the warning is written once.</summary>
        private Entity m_ReportedEmptyShip;

        private EntityQuery m_CruiseVehicleQuery;
        private EntityQuery m_AshoreQuery;

        /// <summary>
        /// Every household carrying our tag, whatever else it has lost.
        ///
        /// Wider than m_AshoreQuery deliberately. That one also requires TouristHousehold, because
        /// everything it drives is about a visitor; this one exists for the save, and the save does
        /// not care — CruisePassenger is a serializable component, so a household that kept the tag
        /// and lost TouristHousehold is still written out, entity references and all. Scrubbing
        /// through the narrower query would step straight past it.
        /// </summary>
        private EntityQuery m_CruiseTaggedQuery;
        private EntityQuery m_EquippedTerminalQuery;
        private EntityQuery m_ActiveCallQuery;
        private EntityQuery m_CruiseRouteQuery;

        /// <summary>Surplus line already removed, and topology already complained about.</summary>
        private Entity m_RejectedLine;

        private Entity m_WarnedTopology;

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
            m_HouseholdNeeds = GetComponentLookup<HouseholdNeed>(isReadOnly: true);
            m_ExpectsPurchases = GetComponentLookup<Components.ExpectsPurchase>(isReadOnly: true);
            m_PathInformations = GetComponentLookup<Game.Pathfind.PathInformation>(isReadOnly: true);
            m_TravelPurposes = GetComponentLookup<TravelPurpose>(isReadOnly: true);
            m_LodgingSeekers = GetComponentLookup<LodgingSeeker>(isReadOnly: true);
            m_Targets = GetComponentLookup<Target>(isReadOnly: true);
            m_LodgingProviders = GetComponentLookup<LodgingProvider>(isReadOnly: true);
            m_TouristHouseholds = GetComponentLookup<TouristHousehold>(isReadOnly: true);
            m_CurrentTransports = GetComponentLookup<CurrentTransport>(isReadOnly: true);
            m_CurrentVehicles = GetComponentLookup<Game.Creatures.CurrentVehicle>(isReadOnly: true);
            m_Residents = GetComponentLookup<Game.Creatures.Resident>(isReadOnly: true);
            m_HouseholdMembers = GetComponentLookup<HouseholdMember>(isReadOnly: true);
            m_CruisePassengers = GetComponentLookup<Components.CruisePassenger>(isReadOnly: true);
            m_DeletedTags = GetComponentLookup<Deleted>(isReadOnly: true);
            m_CruiseCalls = GetComponentLookup<Components.CruiseCall>(isReadOnly: true);
            m_HouseholdCitizenBuffers = GetBufferLookup<HouseholdCitizen>(isReadOnly: true);
            m_PassengerBuffers = GetBufferLookup<Passenger>(isReadOnly: true);
            m_CurrentRoutes = GetComponentLookup<CurrentRoute>(isReadOnly: true);
            m_PrefabRefs = GetComponentLookup<PrefabRef>(isReadOnly: true);
            m_TripNeededBuffers = GetBufferLookup<TripNeeded>(isReadOnly: true);
            m_Entities = GetEntityStorageInfoLookup();
            m_PathElements = GetBufferLookup<Game.Pathfind.PathElement>(isReadOnly: true);
            m_CurrentBuildings = GetComponentLookup<CurrentBuilding>(isReadOnly: true);
            m_Humans = GetComponentLookup<Game.Creatures.Human>(isReadOnly: true);
            m_RouteWaypoints = GetBufferLookup<RouteWaypoint>(isReadOnly: true);
            m_Connected = GetComponentLookup<Connected>(isReadOnly: true);
            m_Owners = GetComponentLookup<Owner>(isReadOnly: true);
            m_OutsideConnections = GetComponentLookup<Game.Objects.OutsideConnection>(isReadOnly: true);
            m_StorageProperties = GetComponentLookup<Game.Buildings.StorageProperty>(isReadOnly: true);
            m_Renters = GetBufferLookup<Game.Buildings.Renter>(isReadOnly: true);
            m_SweepResults = new NativeArray<int>((int)SweepResult.Count, Allocator.Persistent);
            m_LastPlaces = new NativeParallelHashMap<Entity, LastPlace>(4096, Allocator.Persistent);
            m_ResetTripArchetype = EntityManager.CreateArchetype(
                ComponentType.ReadWrite<Game.Common.Event>(),
                ComponentType.ReadWrite<Game.Creatures.ResetTrip>());
            m_Observations = new NativeList<VesselObservation>(8, Allocator.Persistent);
            m_HoldRequests = new NativeList<HoldRequest>(8, Allocator.Persistent);
            m_PublicTransportsRW = GetComponentLookup<Game.Vehicles.PublicTransport>(isReadOnly: false);
            m_WaitingPassengersRW = GetComponentLookup<WaitingPassengers>(isReadOnly: false);
            m_TransportStopsRW = GetComponentLookup<Game.Routes.TransportStop>(isReadOnly: false);
            m_BoardingVehiclesRW = GetComponentLookup<BoardingVehicle>(isReadOnly: false);
            m_CruiseManifests = GetComponentLookup<Components.CruiseManifest>(isReadOnly: true);
            m_PathOwners = GetComponentLookup<Game.Pathfind.PathOwner>(isReadOnly: true);
            m_WatercraftLanes = GetComponentLookup<Game.Vehicles.WatercraftCurrentLane>(isReadOnly: true);
            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            // Watercraft only.
            //
            // Without it this query is every public transport vehicle in the city with a route - every bus,
            // tram, train and taxi - and ServeDockedShips walks the lot on every update, asking IsOnCruiseLine
            // about each one. Measured at 17-27 ms per update in a 1.2M city, which was the single most
            // expensive thing this mod did. A cruise line is a ship line, so nothing but a ship can ever serve
            // it and the rest were only ever going to fail that test.
            m_CruiseVehicleQuery = GetEntityQuery(
                ComponentType.ReadWrite<Game.Vehicles.PublicTransport>(),
                ComponentType.ReadOnly<CurrentRoute>(),
                ComponentType.ReadOnly<Game.Vehicles.Watercraft>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_AshoreQuery = GetEntityQuery(
                ComponentType.ReadOnly<Components.CruisePassenger>(),
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_CruiseTaggedQuery = GetEntityQuery(
                ComponentType.ReadOnly<Components.CruisePassenger>(),
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

            // Routes, filtered to this mod's prefab at the point of use. Temp is excluded so a line
            // still being dragged out by the tool is not counted as a second one and deleted under
            // the player's cursor.
            m_CruiseRouteQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Routes.TransportLine>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        // Where an update's time goes, in Stopwatch ticks, summed until the next report.
        //
        // The sweeps are the most expensive thing this mod does in a large city, and "the system costs N ms"
        // is not actionable: the cost could be the snapshot, the vessel service, or any of the three sweeps,
        // and they run at different cadences over different numbers of parties. Reported every
        // kTimingReportUpdates updates so a profiler capture can be read against it.
        private long m_TicksSnapshot;
        private long m_TicksServeShips;
        private long m_TicksWait;
        private long m_TicksQueue;
        private long m_TicksHold;
        private long m_TicksLoading;
        private long m_TicksReturnParties;
        private long m_TicksSweepParties;
        private long m_TicksSweepTerminals;
        private int m_TimedUpdates;
        private int m_TimedSweeps;
        private int m_LastPartiesAshore;
        private int m_LastVehiclesWalked;

        // Shore parties the sweeps looked at, and how many had lost the harbour as their hotel.
        // TouristHouseholdBehaviorSystem nulls a tourist's hotel whenever the household is not in
        // that building's Renter list (:74-92), and cruise parties deliberately are not, so it takes
        // the anchor away from each party every 1024 frames and the sweep puts it back within 64.
        // Restored / swept should therefore sit near 1/16; much higher means the anchor is not holding.
        private int m_PartiesSwept;
        private int m_AnchorsRestored;

        /// <summary>Updates between timing reports. 256 is about a minute of play at the system's cadence.</summary>
        private const int kTimingReportUpdates = 256;

        // Cached lookups for the per-party sweeps.
        //
        // These paths ask about the same handful of components for every party ashore, which can be two
        // thousand of them. Going through EntityManager for each question resolves the type and checks the
        // jobs writing it every single time: measured at 0.075 ms of actual work costing 2.2-4.0 ms per
        // frame, nearly all of it the main thread waiting. A lookup resolves once per update instead.
        private ComponentLookup<HouseholdNeed> m_HouseholdNeeds;
        private ComponentLookup<Components.ExpectsPurchase> m_ExpectsPurchases;
        private ComponentLookup<Game.Pathfind.PathInformation> m_PathInformations;
        private ComponentLookup<TravelPurpose> m_TravelPurposes;
        private ComponentLookup<LodgingSeeker> m_LodgingSeekers;
        private ComponentLookup<Target> m_Targets;
        private ComponentLookup<LodgingProvider> m_LodgingProviders;
        private ComponentLookup<TouristHousehold> m_TouristHouseholds;
        private ComponentLookup<CurrentTransport> m_CurrentTransports;
        private ComponentLookup<Game.Creatures.CurrentVehicle> m_CurrentVehicles;
        private ComponentLookup<Game.Creatures.Resident> m_Residents;
        private ComponentLookup<HouseholdMember> m_HouseholdMembers;
        private ComponentLookup<Components.CruisePassenger> m_CruisePassengers;
        private ComponentLookup<Deleted> m_DeletedTags;
        private ComponentLookup<Components.CruiseCall> m_CruiseCalls;
        private BufferLookup<HouseholdCitizen> m_HouseholdCitizenBuffers;
        private BufferLookup<Passenger> m_PassengerBuffers;
        private ComponentLookup<CurrentRoute> m_CurrentRoutes;
        private ComponentLookup<PrefabRef> m_PrefabRefs;
        private BufferLookup<TripNeeded> m_TripNeededBuffers;
        private EntityStorageInfoLookup m_Entities;
        private BufferLookup<Game.Pathfind.PathElement> m_PathElements;
        private ComponentLookup<CurrentBuilding> m_CurrentBuildings;
        private ComponentLookup<Game.Creatures.Human> m_Humans;
        private BufferLookup<RouteWaypoint> m_RouteWaypoints;
        private ComponentLookup<Connected> m_Connected;
        private ComponentLookup<Owner> m_Owners;
        private ComponentLookup<Game.Objects.OutsideConnection> m_OutsideConnections;
        private ComponentLookup<Game.Buildings.StorageProperty> m_StorageProperties;
        private BufferLookup<Game.Buildings.Renter> m_Renters;

        /// <summary>Event + ResetTrip, as TripNeededSystem creates it, for turning a walking body round.</summary>
        private EntityArchetype m_ResetTripArchetype;

        /// <summary>The shore-party sweep in flight, and what it counted (see <see cref="SweepResult"/>).</summary>
        private JobHandle m_SweepJob;
        private NativeArray<int> m_SweepResults;

        /// <summary>Each shore passenger's last building, for putting a lost one back where it was.</summary>
        private NativeParallelHashMap<Entity, LastPlace> m_LastPlaces;

        // The vessel side. m_Observations is what the last VesselJob saw, read by this update's
        // decisions; m_HoldRequests is what those decisions ask the next VesselJob to apply.
        private JobHandle m_VesselJob;
        private NativeList<VesselObservation> m_Observations;
        private NativeList<HoldRequest> m_HoldRequests;
        private ComponentLookup<Game.Vehicles.PublicTransport> m_PublicTransportsRW;
        private ComponentLookup<WaitingPassengers> m_WaitingPassengersRW;
        private ComponentLookup<Game.Routes.TransportStop> m_TransportStopsRW;
        private ComponentLookup<BoardingVehicle> m_BoardingVehiclesRW;
        private ComponentLookup<Components.CruiseManifest> m_CruiseManifests;
        private ComponentLookup<Game.Pathfind.PathOwner> m_PathOwners;
        private ComponentLookup<Game.Vehicles.WatercraftCurrentLane> m_WatercraftLanes;

        // Which lazy waits have been taken this update; see EnsureHouseholdData and friends.
        private bool m_HouseholdDataReady;
        private bool m_CreatureDataReady;
        private bool m_RouteDataReady;

        /// <summary>Refreshes the cached lookups. Called once per update, and again after a structural change.</summary>
        private void RefreshLookups()
        {
            long waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();

            m_HouseholdNeeds.Update(this);
            m_ExpectsPurchases.Update(this);
            m_PathInformations.Update(this);
            m_TravelPurposes.Update(this);
            m_LodgingSeekers.Update(this);
            m_Targets.Update(this);
            m_LodgingProviders.Update(this);
            m_TouristHouseholds.Update(this);
            m_CurrentTransports.Update(this);
            m_CurrentVehicles.Update(this);
            m_Residents.Update(this);
            m_HouseholdMembers.Update(this);
            m_CruisePassengers.Update(this);
            m_DeletedTags.Update(this);
            m_CruiseCalls.Update(this);
            m_HouseholdCitizenBuffers.Update(this);
            m_PassengerBuffers.Update(this);
            m_CurrentRoutes.Update(this);
            m_PrefabRefs.Update(this);
            m_TripNeededBuffers.Update(this);
            m_Entities.Update(this);
            m_PathElements.Update(this);
            m_CurrentBuildings.Update(this);
            m_Humans.Update(this);
            m_RouteWaypoints.Update(this);
            m_Connected.Update(this);
            m_Owners.Update(this);
            m_OutsideConnections.Update(this);
            m_StorageProperties.Update(this);
            m_Renters.Update(this);
            m_PublicTransportsRW.Update(this);
            m_WaitingPassengersRW.Update(this);
            m_TransportStopsRW.Update(this);
            m_BoardingVehiclesRW.Update(this);
            m_CruiseManifests.Update(this);
            m_PathOwners.Update(this);
            m_WatercraftLanes.Update(this);

            m_HouseholdDataReady = false;
            m_CreatureDataReady = false;
            m_RouteDataReady = false;
        }

        // Lazy waits.
        //
        // A lookup read on the main thread does not wait for the jobs writing it, which
        // EntityManager.GetComponentData did, so each path waits for what it reads before reading it.
        // Waiting for everything on every refresh cost 5-8 ms per update, nearly all of it for the
        // frame's creature jobs (Target, CurrentTransport, CurrentVehicle, Resident), and on most
        // updates nothing on the main thread reads those at all: the shore-party sweep, which reads
        // them for every party, now runs as a job after them. What is left waits only on the paths
        // that need it — a manifest walk, adopting passengers, cancelling a hotel trip.

        /// <summary>Households' own components: the tag, the citizen list, the tourist record.</summary>
        private void EnsureHouseholdData()
        {
            if (m_HouseholdDataReady)
            {
                return;
            }

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            EntityManager.CompleteDependencyBeforeRW<Components.CruisePassenger>();
            EntityManager.CompleteDependencyBeforeRO<HouseholdCitizen>();
            EntityManager.CompleteDependencyBeforeRO<TouristHousehold>();
            m_TicksWait += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            m_HouseholdDataReady = true;
        }

        /// <summary>A vessel's route and the prefab behind it.</summary>
        private void EnsureRouteData()
        {
            if (m_RouteDataReady)
            {
                return;
            }

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            EntityManager.CompleteDependencyBeforeRO<CurrentRoute>();
            EntityManager.CompleteDependencyBeforeRO<PrefabRef>();
            m_TicksWait += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            m_RouteDataReady = true;
        }

        /// <summary>Everything a citizen, its body and a vessel's manifest can say. The expensive one.</summary>
        private void EnsureCreatureData()
        {
            if (m_CreatureDataReady)
            {
                return;
            }

            EnsureHouseholdData();
            EnsureRouteData();

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            EntityManager.CompleteDependencyBeforeRO<Target>();
            EntityManager.CompleteDependencyBeforeRO<CurrentTransport>();
            EntityManager.CompleteDependencyBeforeRO<Game.Creatures.CurrentVehicle>();
            EntityManager.CompleteDependencyBeforeRO<Game.Creatures.Resident>();
            EntityManager.CompleteDependencyBeforeRO<Game.Creatures.Human>();
            EntityManager.CompleteDependencyBeforeRO<HouseholdMember>();
            EntityManager.CompleteDependencyBeforeRO<Passenger>();
            EntityManager.CompleteDependencyBeforeRO<TripNeeded>();
            EntityManager.CompleteDependencyBeforeRO<CurrentBuilding>();
            EntityManager.CompleteDependencyBeforeRO<Game.Pathfind.PathInformation>();
            m_TicksWait += System.Diagnostics.Stopwatch.GetTimestamp() - started;
            m_CreatureDataReady = true;
        }

        /// <summary>
        /// The shared helpers, for the main thread. Waits for everything they can read first, because
        /// on the main thread nothing else orders them after the jobs writing it.
        /// </summary>
        private ShorePartyAccess ShoreAccess()
        {
            EnsureCreatureData();
            return BuildShoreAccess();
        }

        private ShorePartyAccess BuildShoreAccess()
        {
            return new ShorePartyAccess
            {
                m_Entities = m_Entities,
                m_HouseholdNeeds = m_HouseholdNeeds,
                m_ExpectsPurchases = m_ExpectsPurchases,
                m_PathInformations = m_PathInformations,
                m_PathElements = m_PathElements,
                m_TravelPurposes = m_TravelPurposes,
                m_LodgingSeekers = m_LodgingSeekers,
                m_Targets = m_Targets,
                m_LodgingProviders = m_LodgingProviders,
                m_TouristHouseholds = m_TouristHouseholds,
                m_CurrentTransports = m_CurrentTransports,
                m_CurrentBuildings = m_CurrentBuildings,
                m_CurrentVehicles = m_CurrentVehicles,
                m_Humans = m_Humans,
                m_CruiseCalls = m_CruiseCalls,
                m_HouseholdCitizens = m_HouseholdCitizenBuffers,
                m_TripNeeded = m_TripNeededBuffers,
                m_CurrentRoutes = m_CurrentRoutes,
                m_RouteWaypoints = m_RouteWaypoints,
                m_Connected = m_Connected,
                m_Owners = m_Owners,
                m_OutsideConnections = m_OutsideConnections,
                m_StorageProperties = m_StorageProperties,
                m_Renters = m_Renters,
                m_Deleted = m_DeletedTags,
                m_ResetTripArchetype = m_ResetTripArchetype,
                m_TerminalRooms = Mod.Settings != null
                    ? math.clamp(Mod.Settings.CruiseShipCapacity, 100, 5000)
                    : 2000,
            };
        }

        protected override void OnDestroy()
        {
            m_SweepJob.Complete();
            m_VesselJob.Complete();
            m_SweepResults.Dispose();
            m_LastPlaces.Dispose();
            m_Observations.Dispose();
            m_HoldRequests.Dispose();
            base.OnDestroy();
        }

        protected override void OnGameLoadingComplete(
            Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_ReportHoldsAfterLoad = mode == GameMode.Game;
        }

        protected override void OnUpdate()
        {
            // The last sweep was scheduled at least sixteen frames ago, so this is not a wait; it makes
            // the tags it wrote readable here and logs what it counted.
            // Both jobs were scheduled at least sixteen frames ago, so this is not a wait. It makes
            // what they wrote readable here: the sweep's tags and counts, the vessels' observations.
            m_SweepJob.Complete();
            m_VesselJob.Complete();
            ReportLastSweep();

            RefreshLookups();

            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            m_TimedUpdates++;

            // Before the early returns, so a panel reading the published count never sees figures
            // left over from a line that has since been deleted.
            SnapshotPassengersAshore();
            m_TicksSnapshot += System.Diagnostics.Stopwatch.GetTimestamp() - started;

            if (m_CruiseLineSystem == null || !m_CruiseLineSystem.LineCreated)
            {
                m_CruiseConnections.Clear();
                m_Observations.Clear();
                return;
            }

            Entity cruiseLinePrefab = m_CruiseLineSystem.LinePrefabEntity;

            if (cruiseLinePrefab == Entity.Null)
            {
                m_CruiseConnections.Clear();
                m_Observations.Clear();
                return;
            }

            // EnforceOneCruiseLine can add Locked to the line prefab, which is a structural change and
            // invalidates the lookups taken above.
            EnforceOneCruiseLine(cruiseLinePrefab);
            RefreshLookups();
            RefreshCruiseConnections(cruiseLinePrefab);
            ReportHoldsAfterLoad();

            started = System.Diagnostics.Stopwatch.GetTimestamp();
            m_LastVehiclesWalked = m_Observations.Length;
            ServeDockedShips(cruiseLinePrefab);
            m_TicksServeShips += System.Diagnostics.Stopwatch.GetTimestamp() - started;

            // The shore party is swept on its own, slower cadence.
            //
            // Only the vessel needs sixteen frames — TransportBoardingHelpers:368 gives a ship that
            // was not already EnRoute a departure sixty frames out, so a hold has to be written
            // inside that window. Nothing about a party ashore moves that fast: a lodging anchor,
            // a recall and a deadline are all measured in in-game hours.
            //
            // Running the sweep at the vessel's rate meant walking every ashore household — two
            // thousand of them on a full call, several EntityManager lookups each — sixteen times a
            // second for state that changes over hours. At a quarter of the rate the behaviour is
            // indistinguishable and three quarters of the work is gone.
            if (m_SimulationSystem.frameIndex % kShorePartyInterval
                < (uint)GetUpdateInterval(SystemUpdatePhase.GameSimulation))
            {
                m_TimedSweeps++;

                started = System.Diagnostics.Stopwatch.GetTimestamp();

                // Buffers are created in the order the three sweeps always recorded into them, so
                // playback order is unchanged: the party sweep's commands, then the orphaned parties',
                // then the terminals'. The two small sweeps run here on the main thread first, and the
                // party sweep is scheduled last — the small ones only read a tag's ship and terminal,
                // which the party sweep never changes.
                EntityCommandBuffer partyCommands = m_EndFrameBarrier.CreateCommandBuffer();
                EntityCommandBuffer orphanCommands = m_EndFrameBarrier.CreateCommandBuffer();
                EntityCommandBuffer terminalCommands = m_EndFrameBarrier.CreateCommandBuffer();

                SweepOrphanedParties(orphanCommands);
                long afterParties = System.Diagnostics.Stopwatch.GetTimestamp();
                m_TicksSweepParties += afterParties - started;

                SweepOrphanedTerminals(terminalCommands);
                long afterTerminals = System.Diagnostics.Stopwatch.GetTimestamp();
                m_TicksSweepTerminals += afterTerminals - afterParties;

                ScheduleShorePartySweep(partyCommands);
                m_TicksReturnParties += System.Diagnostics.Stopwatch.GetTimestamp() - afterTerminals;
            }

            ReportTimingIfDue();
        }

        /// <summary>
        /// Schedules the shore-party sweep (<see cref="ShorePartyJob"/>) after the jobs that write what
        /// it reads. Nothing on the main thread waits for it; the next update collects its counts.
        /// </summary>
        private void ScheduleShorePartySweep(EntityCommandBuffer commandBuffer)
        {
            if (m_AshoreQuery.IsEmptyIgnoreFilter)
            {
                // Nobody ashore: the remembered places belong to parties that have gone.
                m_LastPlaces.Clear();
                return;
            }

            for (int i = 0; i < m_SweepResults.Length; i++)
            {
                m_SweepResults[i] = 0;
            }

            m_SweepJob = new ShorePartyJob
            {
                m_EntityType = GetEntityTypeHandle(),
                m_PassengerType = GetComponentTypeHandle<Components.CruisePassenger>(isReadOnly: false),
                m_Access = BuildShoreAccess(),
                m_CommandBuffer = commandBuffer,
                m_Results = m_SweepResults,
                m_LastPlaces = m_LastPlaces,
                m_Frame = m_SimulationSystem.frameIndex,
                m_LastCall = LastCallFrames(),
                m_UpdateInterval = (uint)GetUpdateInterval(SystemUpdatePhase.GameSimulation),
            }.Schedule(m_AshoreQuery, Dependency);

            m_EndFrameBarrier.AddJobHandleForProducer(m_SweepJob);
            Dependency = m_SweepJob;
        }

        /// <summary>
        /// Logs the last sweep's summary, on the same conditions as when the sweep logged it itself:
        /// bursts of recalls or boardings, anyone left behind or gone another way, or every 8192
        /// frames while anyone is still out.
        ///
        /// Stranded is the number that says the return is not working: called back, never aboard,
        /// sent out of the city when the vessel sailed. The figures after it say which stage lost
        /// them — walking means the journey is not finishing in time, queued means the pathfinder has
        /// not answered, idle means the trip was dropped and nothing is bringing them back.
        /// </summary>
        private void ReportLastSweep()
        {
            if (m_SweepResults[(int)SweepResult.Ran] == 0)
            {
                return;
            }

            int R(SweepResult r) => m_SweepResults[(int)r];

            int recalled = R(SweepResult.Recalled);
            int boarded = R(SweepResult.Boarded);
            int stranded = R(SweepResult.Stranded);
            int leftOtherWay = R(SweepResult.LeftOtherWay);
            int walking = R(SweepResult.Walking);
            int queued = R(SweepResult.Queued);
            int idle = R(SweepResult.Idle);
            uint frame = (uint)R(SweepResult.Frame);

            m_SweepResults[(int)SweepResult.Ran] = 0;
            m_PartiesSwept += R(SweepResult.Swept);
            m_AnchorsRestored += R(SweepResult.AnchorsRestored);

            bool periodic = walking + queued + idle > 0
                            && frame % 8192u < (uint)GetUpdateInterval(SystemUpdatePhase.GameSimulation);

            int redirected = R(SweepResult.Redirected);
            int rehomed = R(SweepResult.Rehomed);
            int rehomedAtTerminal = R(SweepResult.RehomedAtTerminal);

            if (recalled > 5 || boarded > 5 || stranded > 0 || leftOtherWay > 0 || redirected > 20
                || rehomed + rehomedAtTerminal > 0 || periodic)
            {
                Mod.Log.Info(
                    $"Cruise shore leave: {recalled} recalled, {redirected} turned round mid-errand, "
                    + $"{rehomed} put back where they were, {rehomedAtTerminal} at the terminal, {boarded} aboard, "
                    + $"{stranded} left behind, {leftOtherWay} reached the sea by another route; "
                    + $"still ashore: {walking} walking, "
                    + $"{queued} queued ({R(SweepResult.AwaitingPath)} waiting on the pathfinder, "
                    + $"{R(SweepResult.BusyIndoors)} busy indoors, {R(SweepResult.NowhereAtAll)} nowhere), {idle} idle "
                    + $"(walking: {R(SweepResult.WalkingToShip)} bound for the ship, "
                    + $"{R(SweepResult.RidingOther)} riding another vehicle, {R(SweepResult.WalkingElsewhere)} elsewhere).");
            }
        }

        /// <summary>
        /// Writes where the last <see cref="kTimingReportUpdates"/> updates went, then starts again.
        ///
        /// Milliseconds per update, so the figures can be compared directly with a profiler capture's
        /// ms-per-frame for this system, plus the number of parties the sweeps had to walk, which is what
        /// the sweep costs scale with.
        /// </summary>
        private void ReportTimingIfDue()
        {
            if (m_TimedUpdates < kTimingReportUpdates)
            {
                return;
            }

            double ToMs(long ticks) =>
                ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency / m_TimedUpdates;

            Mod.Log.Info(
                $"CruiseVoyage timing over {m_TimedUpdates} updates ({m_TimedSweeps} with sweeps), "
                + $"{m_LastPartiesAshore} parties ashore, {m_LastVehiclesWalked} vessels walked: "
                + $"snapshot {ToMs(m_TicksSnapshot):0.000} ms, "
                + $"serve ships {ToMs(m_TicksServeShips):0.000} ms (decisions and scheduling; the vessels are a job) "
                + $"(queue {ToMs(m_TicksQueue):0.000}, "
                + $"hold {ToMs(m_TicksHold):0.000}, loading {ToMs(m_TicksLoading):0.000}), "
                + $"waiting for jobs {ToMs(m_TicksWait):0.000} ms, "
                + $"schedule party sweep {ToMs(m_TicksReturnParties):0.000} ms (the sweep itself is a job), "
                + $"sweep parties {ToMs(m_TicksSweepParties):0.000} ms, "
                + $"sweep terminals {ToMs(m_TicksSweepTerminals):0.000} ms; "
                + $"harbour anchors restored {m_AnchorsRestored} of {m_PartiesSwept} parties swept "
                + $"(~1/16 is the game's own 1024-frame reset) "
                + "(per update, averaged over all of them)");

            m_TicksSnapshot = 0;
            m_TicksServeShips = 0;
            m_TicksWait = 0;
            m_TicksQueue = 0;
            m_TicksHold = 0;
            m_TicksLoading = 0;
            m_TicksReturnParties = 0;
            m_TicksSweepParties = 0;
            m_TicksSweepTerminals = 0;
            m_TimedUpdates = 0;
            m_TimedSweeps = 0;
            m_PartiesSwept = 0;
            m_AnchorsRestored = 0;
        }

        /// <summary>
        /// Keeps the city to one cruise line, running between one map edge and one quay.
        ///
        /// Both limits are enforced here rather than in the route tool, because the tool has no idea
        /// this prefab is special — it is an ordinary TransportLinePrefab as far as the game is
        /// concerned, and a player can draw as many as they like with as many stops as they like.
        /// This is the only place that knows better.
        ///
        /// The rules exist because everything the feature does assumes them. The queue, the waiting
        /// figures and the boarding flags are written to *stops*, not to lines, so two cruise lines
        /// sharing a harbour would fight over the same values every update. And a call is a round
        /// trip between exactly two places: a third stop has no meaning in it — the vessel would
        /// load at the map edge, land its complement at whichever quay it reached first, and sail
        /// past the other with nobody aboard for it.
        ///
        /// The oldest line is the one kept. It is the one the player has been running, and its ship
        /// may be mid-call with a complement ashore; deleting that to keep a line drawn five seconds
        /// ago would throw away a working voyage.
        /// </summary>
        /// <summary>
        /// Outside connections the cruise line calls at. Ordinary arrivals must not be spawned at
        /// these — see <see cref="ServesCruiseLine"/>.
        /// </summary>
        private readonly HashSet<Entity> m_CruiseConnections = new HashSet<Entity>();

        /// <summary>
        /// Whether a map-edge connection is one the cruise line calls at.
        ///
        /// ClearOutsideConnectionWait holds that stop's advertised wait at zero so the cruise
        /// complement created there routes onto the ship. Any ordinary visitor spawned at the same
        /// connection sees the same free ride and queues for it too: with ship arrivals at 40%, a
        /// rebuilt line gathered a backlog of 9,000 visitors waiting for a vessel that calls rarely
        /// and sits at the quay for hours. The arrival spawner and routing therefore skip these,
        /// leaving the connection to the cruise line alone. Main thread only.
        /// </summary>
        internal bool ServesCruiseLine(Entity connection) => m_CruiseConnections.Contains(connection);

        /// <summary>Re-derives the cruise line's map-edge connections from its route waypoints.</summary>
        private void RefreshCruiseConnections(Entity cruiseLinePrefab)
        {
            m_CruiseConnections.Clear();

            if (m_CruiseRouteQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> routes = m_CruiseRouteQuery.ToEntityArray(Allocator.Temp);

            for (int r = 0; r < routes.Length; r++)
            {
                Entity route = routes[r];

                if (!EntityManager.HasComponent<PrefabRef>(route)
                    || EntityManager.GetComponentData<PrefabRef>(route).m_Prefab != cruiseLinePrefab
                    || !EntityManager.HasBuffer<RouteWaypoint>(route))
                {
                    continue;
                }

                DynamicBuffer<RouteWaypoint> waypoints =
                    EntityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);

                for (int i = 0; i < waypoints.Length; i++)
                {
                    Entity waypoint = waypoints[i].m_Waypoint;

                    if (waypoint == Entity.Null || !EntityManager.Exists(waypoint)
                        || !EntityManager.HasComponent<Connected>(waypoint))
                    {
                        continue;
                    }

                    Entity stop = EntityManager.GetComponentData<Connected>(waypoint).m_Connected;

                    if (StopIsOutsideConnection(stop))
                    {
                        Entity connection = OutsideConnectionOf(stop);

                        if (connection != Entity.Null)
                        {
                            m_CruiseConnections.Add(connection);
                        }
                    }
                }
            }

            routes.Dispose();
        }

        private void EnforceOneCruiseLine(Entity cruiseLinePrefab)
        {
            if (m_CruiseRouteQuery.IsEmptyIgnoreFilter)
            {
                // No routes at all, so the tool is free.
                SetToolAvailable(cruiseLinePrefab, true);
                return;
            }

            NativeArray<Entity> routes = m_CruiseRouteQuery.ToEntityArray(Allocator.Temp);
            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();

            try
            {
                Entity keep = Entity.Null;

                for (int i = 0; i < routes.Length; i++)
                {
                    if (!EntityManager.HasComponent<PrefabRef>(routes[i])
                        || EntityManager.GetComponentData<PrefabRef>(routes[i]).m_Prefab
                           != cruiseLinePrefab)
                    {
                        continue;
                    }

                    // Lowest index is the earliest created, so the incumbent wins.
                    if (keep == Entity.Null || routes[i].Index < keep.Index)
                    {
                        if (keep != Entity.Null)
                        {
                            RejectExtraLine(keep, commandBuffer);
                        }

                        keep = routes[i];
                        continue;
                    }

                    RejectExtraLine(routes[i], commandBuffer);
                }

                // Take the tool away while a line exists, rather than only cleaning up after one is
                // drawn. Deleting a player's line the instant they finish it is a poor way to say
                // "only one of these", and it costs them the drawing.
                SetToolAvailable(cruiseLinePrefab, keep == Entity.Null);

                if (keep != Entity.Null)
                {
                    WarnIfNotAPairOfStops(keep);
                    UseCruiseShip(keep);
                }
            }
            finally
            {
                routes.Dispose();
            }
        }

        /// <summary>
        /// Shows or hides the cruise line tool, so a second one cannot be drawn in the first place.
        ///
        /// Game.Prefabs.Locked is how the game keeps an unavailable prefab out of the toolbar, and
        /// it is IEnableableComponent — so availability is a flag to toggle rather than a component
        /// to add and remove, and toggling it causes no structural change at all. That matters for
        /// something evaluated every update.
        ///
        /// This is the half of the limit the player actually experiences. EnforceOneCruiseLine
        /// removing a surplus line is the backstop for a save that already has two, or for a line
        /// that appears by some route this does not cover; on its own it would mean letting someone
        /// draw a line and then deleting it, which is a poor way to say "only one of these".
        /// </summary>
        private void SetToolAvailable(Entity prefab, bool available)
        {
            if (prefab == Entity.Null || !EntityManager.Exists(prefab))
            {
                return;
            }

            if (!EntityManager.HasComponent<Locked>(prefab))
            {
                if (available)
                {
                    return;
                }

                EntityManager.AddComponent<Locked>(prefab);
            }

            if (EntityManager.IsComponentEnabled<Locked>(prefab) == available)
            {
                EntityManager.SetComponentEnabled<Locked>(prefab, !available);

                Mod.Log.Info(
                    available
                        ? "Cruise line tool available again; no cruise line is drawn."
                        : "Cruise line tool hidden; a city runs one cruise line.");
            }
        }

        /// <summary>
        /// Names the mod's cruise ship as the cruise route's vehicle model, so the line runs it instead of
        /// the stock passenger ship. See CruiseLineSystem.CreateCruiseShip for why the ship exists.
        ///
        /// Setting the model is all it takes: TransportLineSystem.CheckVehicles treats a vehicle on the
        /// line that is not of the named model as not continuing, abandons it and requests one that is.
        /// That swap must not strand anyone, so the model is only set between voyages — no call open
        /// and no party tagged anywhere, ashore or homeward — and once set it stays set.
        /// </summary>
        private void UseCruiseShip(Entity route)
        {
            Entity ship = m_CruiseLineSystem.ShipPrefabEntity;

            if (ship == Entity.Null || !EntityManager.HasBuffer<VehicleModel>(route))
            {
                return;
            }

            DynamicBuffer<VehicleModel> models = EntityManager.GetBuffer<VehicleModel>(route, isReadOnly: true);

            if (models.Length == 1 && models[0].m_PrimaryPrefab == ship && models[0].m_SecondaryPrefab == Entity.Null)
            {
                return;
            }

            if (!m_ActiveCallQuery.IsEmptyIgnoreFilter || !m_CruiseTaggedQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            models = EntityManager.GetBuffer<VehicleModel>(route);
            models.Clear();
            models.Add(new VehicleModel { m_PrimaryPrefab = ship, m_SecondaryPrefab = Entity.Null });

            Mod.Log.Info(
                $"Cruise line {route.Index} now runs the cruise ship; the game replaces its current vessel "
                + "with one on its next check.");
        }

        /// <summary>Deletes a surplus cruise line and says why, once.</summary>
        private void RejectExtraLine(Entity route, EntityCommandBuffer commandBuffer)
        {
            if (route == m_RejectedLine)
            {
                return;
            }

            m_RejectedLine = route;

            commandBuffer.AddComponent<Deleted>(route);

            Mod.Log.Warn(
                $"A second cruise line ({route.Index}) was found and has been removed. A city runs "
                + "one cruise line: the stop settings the feature depends on belong to the harbour "
                + "rather than to the line, so two of them would overwrite each other. The tool is "
                + "hidden while a line exists, so this should only happen on a save made before "
                + "that limit.");
        }

        /// <summary>
        /// Says so when the kept line is not a map edge and a quay, without touching it.
        ///
        /// Warned rather than deleted, deliberately. A line is drawn a stop at a time, so a
        /// half-finished one legitimately has one waypoint or three for a moment, and removing it
        /// mid-draw would be indistinguishable from the tool not working.
        /// </summary>
        private void WarnIfNotAPairOfStops(Entity route)
        {
            if (route == m_WarnedTopology || !EntityManager.HasBuffer<RouteWaypoint>(route))
            {
                return;
            }

            DynamicBuffer<RouteWaypoint> waypoints =
                EntityManager.GetBuffer<RouteWaypoint>(route, isReadOnly: true);

            if (waypoints.Length < 2)
            {
                return;
            }

            int outside = 0;

            for (int i = 0; i < waypoints.Length; i++)
            {
                Entity waypoint = waypoints[i].m_Waypoint;

                if (waypoint == Entity.Null
                    || !EntityManager.Exists(waypoint)
                    || !EntityManager.HasComponent<Connected>(waypoint))
                {
                    continue;
                }

                if (StopIsOutsideConnection(
                        EntityManager.GetComponentData<Connected>(waypoint).m_Connected))
                {
                    outside++;
                }
            }

            if (waypoints.Length == 2 && outside == 1)
            {
                return;
            }

            m_WarnedTopology = route;

            Mod.Log.Warn(
                $"Cruise line {route.Index} has {waypoints.Length} stops, {outside} of them at a "
                + "map edge. It wants exactly two: one sea outside connection to load at, and one "
                + "harbour to call at. Anything else and the ship lands its passengers at whichever "
                + "quay it reaches first and sails past the rest.");
        }

        /// <summary>
        /// Scrubs the entity references this mod persists, immediately before the game writes them.
        ///
        /// THIS IS A SAVE-CRASH FIX, and the hazard is the mod's own doing.
        ///
        /// CruiseCall.m_Terminal, CruisePassenger.m_Ship and CruisePassenger.m_Terminal are Entity
        /// fields written into the save by our own Serialize methods. An Entity is not a value a
        /// save can take at face value: SerializerSystem.CreateQuery builds the set of entities that
        /// will be written and puts Temp and Deleted in its None list, so the file is a closed world
        /// and every reference in it has to name something inside that world. A reference to a
        /// vessel the player has just deleted, or a harbour marked Deleted this frame, names
        /// something that is not going to be there — and it is the write of that reference, during
        /// the Serialize phase, that takes the game down. Which is why it happens only on save, only
        /// with this mod installed, and sooner in a city running a cruise line.
        ///
        /// The runtime already knew these references could dangle — SailingFrameFor and the
        /// shore-party sweep both test Exists() before trusting them — so the values were
        /// understood to be untrustworthy while being saved as though they were not.
        ///
        /// Nulling rather than removing, deliberately. Entity.Null is always representable, this
        /// runs inside the Serialize phase where a structural change is the last thing wanted, and
        /// SweepOrphanedParties has already taken the component off anything genuinely orphaned on
        /// an ordinary update. What reaches here is the narrow case of a reference that went stale
        /// since that sweep last ran, and for that, blanking the field is enough.
        /// </summary>
        public void PreSerialize(Colossal.Serialization.Entities.Context context)
        {
            m_SweepJob.Complete();
            m_VesselJob.Complete();

            int scrubbed = 0;

            if (!m_ActiveCallQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> ships = m_ActiveCallQuery.ToEntityArray(Allocator.Temp);

                try
                {
                    for (int i = 0; i < ships.Length; i++)
                    {
                        Components.CruiseCall call =
                            EntityManager.GetComponentData<Components.CruiseCall>(ships[i]);

                        if (IsSavable(call.m_Terminal))
                        {
                            continue;
                        }

                        call.m_Terminal = Entity.Null;
                        EntityManager.SetComponentData(ships[i], call);
                        scrubbed++;
                    }
                }
                finally
                {
                    ships.Dispose();
                }
            }

            if (!m_CruiseTaggedQuery.IsEmptyIgnoreFilter)
            {
                NativeArray<Entity> parties = m_CruiseTaggedQuery.ToEntityArray(Allocator.Temp);

                try
                {
                    for (int i = 0; i < parties.Length; i++)
                    {
                        Components.CruisePassenger passenger =
                            EntityManager.GetComponentData<Components.CruisePassenger>(parties[i]);

                        bool ship = IsSavable(passenger.m_Ship);
                        bool terminal = IsSavable(passenger.m_Terminal);

                        if (ship && terminal)
                        {
                            continue;
                        }

                        if (!ship)
                        {
                            passenger.m_Ship = Entity.Null;
                        }

                        if (!terminal)
                        {
                            passenger.m_Terminal = Entity.Null;
                        }

                        EntityManager.SetComponentData(parties[i], passenger);
                        scrubbed++;
                    }
                }
                finally
                {
                    parties.Dispose();
                }
            }

            if (scrubbed > 0)
            {
                Mod.Log.Warn(
                    $"Blanked {scrubbed} cruise reference(s) naming entities this save will not "
                    + "contain. The save is sound; something was deleted between the last sweep and "
                    + "the write.");
            }
        }

        /// <summary>
        /// Whether an entity reference can be written into a save.
        ///
        /// Entity.Null is fine — it is the absence of a reference, and always representable.
        /// Anything else has to exist and has to be inside the set SerializerSystem will write,
        /// which is what rules out Deleted and Temp: both are in that query's None list, so an
        /// entity carrying either is not in the file however alive it looks from here.
        /// </summary>
        private bool IsSavable(Entity entity)
        {
            return entity == Entity.Null
                || (EntityManager.Exists(entity)
                    && !EntityManager.HasComponent<Deleted>(entity)
                    && !EntityManager.HasComponent<Temp>(entity));
        }

        /// <summary>
        /// Releases shore parties whose ship has gone, so none is left tagged for ever.
        ///
        /// The tag is what holds a party out of the hotel system and counts it against a vessel, and
        /// every path that ends a visit needs that vessel: the recall targets the ship's connection,
        /// and the write-off reads the ship's departure frame. So a party whose ship the player has
        /// deleted is not merely stale — it is stuck, permanently ashore, permanently swept, and
        /// permanently holding a reference this mod then writes into every save.
        ///
        /// Returning them to ordinary visitors is the honest recovery: the tag comes off, the
        /// harbour anchor comes off with it, and TouristTargetSearchSystem gives them a hotel to
        /// look for on its next pass, the same as any other arrival. They came ashore, their ship
        /// left without them, and now they need a room.
        ///
        /// A party still at sea is not orphaned and must not be caught here. Its ship is real and
        /// its terminal is deliberately null until the vessel docks — the sentinel
        /// the shore-party sweep reads for exactly that — so the ship, not the terminal, is what
        /// this tests.
        /// </summary>
        private void SweepOrphanedParties(EntityCommandBuffer commandBuffer)
        {
            EnsureHouseholdData();

            if (m_CruiseTaggedQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> parties = m_CruiseTaggedQuery.ToEntityArray(Allocator.Temp);
            NativeArray<Components.CruisePassenger> passengers =
                m_CruiseTaggedQuery.ToComponentDataArray<Components.CruisePassenger>(Allocator.Temp);

            int released = 0;

            try
            {
                for (int i = 0; i < parties.Length; i++)
                {
                    Entity ship = passengers[i].m_Ship;

                    if (ship != Entity.Null
                        && EntityManager.Exists(ship)
                        && !m_DeletedTags.HasComponent(ship))
                    {
                        continue;
                    }

                    commandBuffer.RemoveComponent<Components.CruisePassenger>(parties[i]);

                    // Their anchor was the terminal, and it is no longer theirs to hold. Cleared so
                    // TouristHouseholdBehaviorSystem marks them LodgingSeeker and they look for a
                    // room like any other visitor, rather than keeping a hotel that is a harbour.
                    if (m_TouristHouseholds.HasComponent(parties[i]))
                    {
                        TouristHousehold tourist =
                            m_TouristHouseholds[parties[i]];

                        if (tourist.m_Hotel == passengers[i].m_Terminal)
                        {
                            tourist.m_Hotel = Entity.Null;
                            commandBuffer.SetComponent(parties[i], tourist);
                        }
                    }

                    released++;
                }
            }
            finally
            {
                passengers.Dispose();
                parties.Dispose();
            }

            if (released > 0)
            {
                Mod.Log.Info(
                    $"Released {released} shore parties whose ship no longer exists; they are "
                    + "ordinary visitors again and will look for a hotel.");
            }
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
        private void SweepOrphanedTerminals(EntityCommandBuffer commandBuffer)
        {
            EnsureHouseholdData();

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
                        // Still in use, so nothing is stripped — but this is also the one place
                        // that sees every equipped terminal every update, which makes it where the
                        // utility guard is repaired.
                        //
                        // A call that was already open when this build was installed was equipped
                        // by code that did not know about StorageProperty, and StartCall will not
                        // run again for it, so without this the port keeps drawing renter-scaled
                        // power until the ship sails. Cheap: the component test fails immediately
                        // in the ordinary case, and there are never many terminals.
                        if (!EntityManager.HasComponent<Game.Buildings.StorageProperty>(terminals[i]))
                        {
                            commandBuffer.AddComponent<Game.Buildings.StorageProperty>(terminals[i]);
                            commandBuffer.AddComponent<Components.CruiseTerminalUtilityGuard>(terminals[i]);

                            Mod.Log.Info(
                                $"Cruise terminal {terminals[i].Index} guarded; its utility demand "
                                + "no longer scales with the shore party.");
                        }

                        continue;
                    }

                    StripTerminalEquipment(terminals[i], commandBuffer);

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
        /// Serves the cruise ships: decides, from what the last <see cref="VesselJob"/> saw, which to
        /// start a load or a call for, which load or call has run its course, and what to log; then
        /// schedules this update's VesselJob to apply the holds and look again.
        ///
        /// The decisions read only this mod's own components (CruiseCall, CruiseManifest, the tags)
        /// live, and the game's data through the observation. Live reads are what stop a decision
        /// being taken twice: a call started last update is on the vessel by now, even though the
        /// observation was taken before it was played back.
        /// </summary>
        private void ServeDockedShips(Entity cruiseLinePrefab)
        {
            uint frame = m_SimulationSystem.frameIndex;

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
            NativeList<Entity> created = new NativeList<Entity>(64, Allocator.Temp);
            m_HoldRequests.Clear();

            try
            {
                for (int i = 0; i < m_Observations.Length; i++)
                {
                    VesselObservation seen = m_Observations[i];
                    Entity vehicle = seen.m_Vehicle;

                    if (!EntityManager.Exists(vehicle) || EntityManager.HasComponent<Deleted>(vehicle))
                    {
                        continue;
                    }

                    long queueStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    MaintainCruiseQueue(vehicle, seen, frame, commandBuffer, created);
                    m_TicksQueue += System.Diagnostics.Stopwatch.GetTimestamp() - queueStarted;

                    if (EntityManager.HasComponent<Components.CruiseCall>(vehicle))
                    {
                        long holdStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                        HoldShipUntilReboard(vehicle, seen, frame, commandBuffer);
                        m_TicksHold += System.Diagnostics.Stopwatch.GetTimestamp() - holdStarted;
                        continue;
                    }

                    // A load in progress is followed by its manifest, not by whether the vessel still
                    // looks alongside: the boarding flag flickers mid-load. A spent manifest only
                    // keeps the vessel here while it is still at the connection; anywhere else it
                    // falls through, and StartCall clears it as part of landing the complement.
                    if (EntityManager.HasComponent<Components.CruiseManifest>(vehicle))
                    {
                        bool spent = EntityManager
                            .GetComponentData<Components.CruiseManifest>(vehicle).m_Loaded != 0;

                        if (!spent || (seen.m_Alongside && seen.m_AtOutsideConnection))
                        {
                            long loadStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                            ContinueLoading(vehicle, seen, frame);
                            m_TicksLoading += System.Diagnostics.Stopwatch.GetTimestamp() - loadStarted;
                            continue;
                        }
                    }

                    if (!seen.m_Alongside)
                    {
                        // Boarding somewhere no stop on its route claims: every branch below needs a
                        // resolved stop, so without this the ship would sail with nobody and the log
                        // would stay silent. Written once per vessel until it resolves.
                        if ((seen.m_State & PublicTransportFlags.Boarding) != 0 && m_UnresolvedShip != vehicle)
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

                    if (seen.m_AtOutsideConnection)
                    {
                        long beginStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                        BeginLoading(vehicle, seen, frame, commandBuffer);
                        m_TicksLoading += System.Diagnostics.Stopwatch.GetTimestamp() - beginStarted;
                        continue;
                    }

                    // Only a vessel that has actually loaded at the map edge may start a call, and the
                    // spent manifest is the proof. Without it the ship never leaves a city: ordinary
                    // tourists board at the quay during shore leave, and the moment the call closed
                    // this branch adopted them and opened a fresh one. StartCall consumes the manifest.
                    if (!EntityManager.HasComponent<Components.CruiseManifest>(vehicle))
                    {
                        continue;
                    }

                    StartCall(vehicle, seen.m_Stop, frame, commandBuffer);
                }
            }
            finally
            {
                created.Dispose();
            }

            ScheduleVesselJob(cruiseLinePrefab, frame);
        }

        /// <summary>Asks this update's VesselJob to hold a vessel until at least the given frame.</summary>
        private void RequestHold(Entity vehicle, uint until) =>
            m_HoldRequests.Add(new HoldRequest { m_Vehicle = vehicle, m_Frame = until });

        /// <summary>Asks this update's VesselJob to let a held vessel go now.</summary>
        private void RequestRelease(Entity vehicle, uint frame) =>
            m_HoldRequests.Add(new HoldRequest { m_Vehicle = vehicle, m_Frame = frame, m_Release = true });

        /// <summary>
        /// Schedules the vessel job after the jobs that write what it reads, and ahead of the
        /// shore-party sweep, which reads the tags this job counts before the sweep changes them —
        /// the order the two ran in on the main thread.
        /// </summary>
        private void ScheduleVesselJob(Entity cruiseLinePrefab, uint frame)
        {
            NativeList<Entity> vehicles =
                m_CruiseVehicleQuery.ToEntityListAsync(Allocator.TempJob, out JobHandle vehiclesReady);

            m_VesselJob = new VesselJob
            {
                m_Vehicles = vehicles,
                m_Requests = m_HoldRequests,
                m_Observations = m_Observations,
                m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer(),
                m_CruiseLinePrefab = cruiseLinePrefab,
                m_Frame = frame,
                m_ShoreLeave = ShoreLeaveFrames(),
                m_LastCall = LastCallFrames(),
                m_Grace = BoardingGraceFrames(),
                m_Spread = RecallSpreadFrames(),
                m_Entities = m_Entities,
                m_CurrentRoutes = m_CurrentRoutes,
                m_PrefabRefs = m_PrefabRefs,
                m_RouteWaypoints = m_RouteWaypoints,
                m_Connected = m_Connected,
                m_Owners = m_Owners,
                m_OutsideConnections = m_OutsideConnections,
                m_Targets = m_Targets,
                m_CruiseCalls = m_CruiseCalls,
                m_CruiseManifests = m_CruiseManifests,
                m_Passengers = m_PassengerBuffers,
                m_Residents = m_Residents,
                m_HouseholdMembers = m_HouseholdMembers,
                m_CruisePassengers = m_CruisePassengers,
                m_TouristHouseholds = m_TouristHouseholds,
                m_PathOwners = m_PathOwners,
                m_WatercraftLanes = m_WatercraftLanes,
                m_PublicTransports = m_PublicTransportsRW,
                m_WaitingPassengers = m_WaitingPassengersRW,
                m_TransportStops = m_TransportStopsRW,
                m_BoardingVehicles = m_BoardingVehiclesRW,
            }.Schedule(JobHandle.CombineDependencies(Dependency, vehiclesReady));

            vehicles.Dispose(m_VesselJob);
            m_EndFrameBarrier.AddJobHandleForProducer(m_VesselJob);
            Dependency = m_VesselJob;
        }

        private void StartCall(
            Entity vehicle,
            Entity terminal,
            uint frame,
            EntityCommandBuffer commandBuffer)
        {
            uint shoreLeave = ShoreLeaveFrames();
            uint reboard = frame + shoreLeave;

            // Equip the terminal first, and this ordering is load-bearing.
            //
            // Commands play back in the order they were recorded, and AppendToBuffer throws if the
            // buffer is not there when its turn comes. Adopting first meant every party's append was
            // queued ahead of the AddBuffer that creates the Renter buffer, so playback threw and
            // took the game down inside EndFrameBarrier — valid when recorded, invalid when played,
            // which is the second time that shape has bitten today.
            //
            // Equipping an empty terminal costs nothing if nobody turns out to be aboard: the sweep
            // strips a terminal no live call references.
            ShoreAccess().EquipTerminalWithLodging(terminal, commandBuffer);

            // Whoever the ship carried in is this call's shore party. That is the only thing that
            // starts a call: a cruise call exists because passengers arrived on the vessel, not
            // because a vessel touched a quay.
            // Back before the ship goes, not as it goes — see kBoardingGraceFraction. The vessel
            // still sails at `reboard`; this is only when the party is due at the quay.
            uint ashoreUntil = reboard - BoardingGraceFrames();

            int placed = AdoptCarriedPassengers(
                vehicle, terminal, ashoreUntil, reboard, commandBuffer);

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
            // and vehicle interval keep working around it. Applied by this update's VesselJob, which
            // then re-asserts it every update of the call.
            RequestHold(vehicle, reboard);

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
            VesselObservation seen,
            uint frame,
            EntityCommandBuffer commandBuffer)
        {
            // Last voyage's passengers get off first. They have reached the edge of the map, which
            // is where they came from and where their trip ends.
            int released = LandHomewardPassengers(vehicle, seen.m_Connection, commandBuffer);

            if (released > 0)
            {
                Mod.Log.Info(
                    $"Cruise landed {released} homeward parties at outside connection "
                    + $"{seen.m_Stop.Index}.");
            }

            int capacity = CruiseCapacity(vehicle);

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
                $"Cruise loading at outside connection {seen.m_Stop.Index}: {seen.m_OutboundAboard} aboard of "
                + $"{capacity} the vessel can hold.");

            LogBoardingHolders(vehicle);

            // Held from this update's VesselJob; from the next one on, the job holds to the
            // manifest's deadline itself.
            RequestHold(vehicle, frame + kLoadTimeoutFrames);
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
        private void ContinueLoading(Entity vehicle, VesselObservation seen, uint frame)
        {
            Components.CruiseManifest manifest =
                EntityManager.GetComponentData<Components.CruiseManifest>(vehicle);

            // Already sailed on this manifest. Nothing more to do until it reaches a city and the
            // component is cleared there.
            if (manifest.m_Loaded != 0)
            {
                return;
            }

            // The dwell is the load. Nothing ends it early.
            //
            // Two other tests were tried and both cut it short. Counting everyone aboard cannot tell
            // a cruise complement from the city's commuters — "1332 aboard of 1000" tripped a
            // capacity test on the first check and sailed the ship a tenth of a second in. Waiting
            // for the quay to empty was worse: the queue drains between batches, so an empty quay
            // means "boarding has caught up", not "boarding is finished" — the vessel left eight
            // seconds into a two-hour window with 266 aboard. A cruise ship leaves when its dwell is
            // over; kLoadTimeoutFrames is that dwell.
            //
            // Except when the ship is physically full. Then nobody else can board however long it
            // waits (ResidentAISystem.TryEnterVehicle finds no space), so the rest of the dwell only
            // parks a full ship offshore. This counts the whole passenger buffer against the vessel's
            // own authored capacity — whoever is aboard, locals included — which is what "full"
            // means to boarding, and cannot fire early the way the complement target did.
            int vesselCapacity = VesselCapacity(vehicle);
            bool full = vesselCapacity > 0 && seen.m_PassengersAboard >= vesselCapacity;

            // Or when the complement it was loaded for is aboard: the Cruise ship passengers setting,
            // capped at the vessel's size, as booked on the manifest when the load began. Only visitors
            // count — tourist households outbound — so the city's own residents riding the line, which
            // is what made "1332 aboard of 1000" end a load at once, cannot trip it.
            bool complete = manifest.m_TargetPassengers > 0 && seen.m_TouristsAboard >= manifest.m_TargetPassengers;

            if (frame < manifest.m_LoadDeadline && !full && !complete)
            {
                // Until then the VesselJob holds the ship at the connection every update. A load
                // survives the vessel dropping out of its boarding state — the flag flickers, and a
                // flicker once tore down a booked complement — so only the deadline ends it.
                return;
            }

            // Marked spent, not removed, and that distinction is the whole of it. Removing the
            // manifest let the very next scan start another load at the same connection, and the
            // ship never left the map edge. It is cleared when the ship reaches a city (StartCall,
            // SailOnEmpty).
            manifest.m_Loaded = 1;
            EntityManager.SetComponentData(vehicle, manifest);

            RequestRelease(vehicle, frame);

            Mod.Log.Info(
                frame >= manifest.m_LoadDeadline
                    ? $"Cruise sailing from outside connection after its full dwell with {seen.m_OutboundAboard} "
                      + $"aboard, {seen.m_Waiting} still queued."
                    : full
                        ? $"Cruise sailing from outside connection full ({seen.m_PassengersAboard} of {vesselCapacity}), "
                          + $"{manifest.m_LoadDeadline - frame} frames before its dwell would have ended, {seen.m_Waiting} still queued."
                        : $"Cruise sailing from outside connection with its complement aboard ({seen.m_TouristsAboard} "
                          + $"visitors of {manifest.m_TargetPassengers}), {manifest.m_LoadDeadline - frame} frames before "
                          + $"its dwell would have ended, {seen.m_Waiting} still queued.");

            // The other half of the pair started in BeginLoading.
            LogBoardingHolders(vehicle);
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
            VesselObservation seen,
            uint frame,
            EntityCommandBuffer commandBuffer,
            NativeList<Entity> created)
        {
            // Nothing is ordered while the vessel is alongside a city quay.
            //
            // A call runs for hours, and this was ordering a batch every few hundred frames through
            // all of it — a backlog built at the map edge with no ship there to collect it, and by
            // the time one arrived most of it had given up waiting or wandered inland. Visitors are
            // created for a sailing that is coming, so the sailing has to be the thing that starts
            // them: once the call closes and the vessel leaves, the queue builds through the
            // crossing and is standing on the dock when it ties up.
            if (EntityManager.HasComponent<Components.CruiseCall>(vehicle))
            {
                return;
            }

            if (frame < m_NextBatchFrame || seen.m_Connection == Entity.Null)
            {
                return;
            }

            Entity connection = seen.m_Connection;
            int waiting = seen.m_Waiting;

            // Everyone already queued, plus everyone already aboard, counts against the ship.
            int shortfall = CruiseCapacity(vehicle) - waiting - seen.m_OutboundAboard;

            // Deadband, deliberately independent of the batch size. Tying the two together meant a
            // larger batch stopped topping up earlier — at 500 the ordering stopped once the
            // shortfall fell below 500, which is exactly the last few hundred places on a nearly
            // full ship. Observed: boarding climbed to about 1800 and then starved.
            if (shortfall < kQueueDeadband)
            {
                return;
            }

            m_NextBatchFrame = frame + kBatchIntervalFrames;

            // A brake for the one case the fixed rate does not bound: nobody ever turning up.
            //
            // The rate limit assumes ordered people reach the stop, so that waiting rises and the
            // shortfall closes. When they cannot — observed after a cruise line was rebuilt, until it
            // was rebuilt again — waiting stays at zero and a full complement was ordered every batch:
            // sixty batches, about 45,000 households, in six minutes. After kStalledBatches batches
            // with nobody waiting and nobody aboard, ordering drops to one batch per
            // kStalledIntervalFrames and says so once; the first person to appear restores the normal
            // rate. In working service people are waiting long before that many batches have gone by,
            // so this never engages.
            if (waiting + seen.m_OutboundAboard == 0)
            {
                if (m_FruitlessBatches >= kStalledBatches)
                {
                    m_NextBatchFrame = frame + kStalledIntervalFrames;

                    if (!m_WarnedStalledQueue)
                    {
                        m_WarnedStalledQueue = true;
                        Mod.Log.Warn(
                            $"Cruise queue at outside connection {connection.Index}: {m_FruitlessBatches} "
                            + "batches ordered and nobody has reached the stop, so ordering is slowed to one "
                            + $"batch per {kStalledIntervalFrames} frames until someone does. If it persists, "
                            + "redrawing the cruise line has fixed it before.");
                    }
                }

                m_FruitlessBatches++;
            }
            else
            {
                m_FruitlessBatches = 0;
                m_WarnedStalledQueue = false;
            }

            // Make the stop as attractive as it can be, immediately before anyone is created.
            //
            // A visitor picks a route the moment they exist and does not reconsider, so the price of
            // this stop at the instant of creation is the only one that matters to them. The
            // standing suppression is gated on the vessel being below capacity, and a ship that is
            // full — or over, as happens when two vessels serve one line — switches it off while
            // this method carries on creating. Observed: batches of a thousand ordered against a
            // stop still advertising an average wait of 2500, every one of them routed onto some
            // other line, and the cruise queue never leaving zero.
            //
            // The VesselJob clears it every update, including this one, and it runs before these
            // households are played back into existence, so the people ordered on this pass see a
            // stop that costs what the city quay costs whatever the standing rule is doing.
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
        /// Who is aboard, by kind — the figure that says whether the pier kept its seats for the
        /// complement. Walked once per call, when it closes.
        /// </summary>
        private string ManifestBreakdown(Entity vehicle)
        {
            EnsureCreatureData();

            if (!m_PassengerBuffers.HasBuffer(vehicle))
            {
                return "manifest unavailable";
            }

            DynamicBuffer<Passenger> manifest = m_PassengerBuffers[vehicle];
            int cruise = 0, tourists = 0, residents = 0, other = 0;

            for (int i = 0; i < manifest.Length; i++)
            {
                Entity household = HouseholdOf(manifest[i].m_Passenger);

                if (household == Entity.Null)
                {
                    other++;
                }
                else if (m_CruisePassengers.HasComponent(household))
                {
                    cruise++;
                }
                else if (m_TouristHouseholds.HasComponent(household))
                {
                    tourists++;
                }
                else
                {
                    residents++;
                }
            }

            return $"aboard {manifest.Length}: {cruise} cruise passengers, {tourists} other tourists, "
                + $"{residents} residents, {other} unidentified";
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
        /// <summary>The vessel's authored passenger capacity (PublicTransportVehicleData), or 0 if unknown.</summary>
        private int VesselCapacity(Entity vehicle)
        {
            if (!EntityManager.HasComponent<PrefabRef>(vehicle))
            {
                return 0;
            }

            Entity prefab = EntityManager.GetComponentData<PrefabRef>(vehicle).m_Prefab;

            return prefab != Entity.Null
                   && EntityManager.Exists(prefab)
                   && EntityManager.HasComponent<PublicTransportVehicleData>(prefab)
                ? EntityManager.GetComponentData<PublicTransportVehicleData>(prefab).m_PassengerCapacity
                : 0;
        }

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
            Entity vehicle,
            Entity terminal,
            uint ashoreUntil,
            uint reboard,
            EntityCommandBuffer commandBuffer)
        {
            EnsureCreatureData();

            if (!m_PassengerBuffers.HasBuffer(vehicle))
            {
                return 0;
            }

            DynamicBuffer<Passenger> manifest =
                m_PassengerBuffers[vehicle];

            NativeParallelHashSet<Entity> seen =
                new NativeParallelHashSet<Entity>(64, Allocator.Temp);

            // How much earlier than the ship a party may decide it has seen enough. A third of the
            // stay, so the quayside fills across the whole of last call rather than in one wave.
            uint earlyReturnSpread = RecallSpreadFrames();

            Random random = new Random(
                math.max(1u, m_SimulationSystem.frameIndex * 2654435761u + 1013904223u));

            int adopted = 0;
            ShorePartyAccess access = ShoreAccess();

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
                    uint ownDeadline = ashoreUntil - (uint)random.NextInt(0, (int)earlyReturnSpread);

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

                    access.CancelHotelTrip(household, commandBuffer);

                    // Deliberately *not* listed as a renter of the terminal.
                    //
                    // TouristHouseholdBehaviorSystem nulls m_Hotel unless the household is in that
                    // building's Renter list (:74 needs the buffer, :82-89 the entry), for any tourist
                    // household without a Target building, once every 1024 frames. So the anchor does
                    // not hold on its own: the shore-party sweep's KeepOffTheHotels puts it back
                    // within 64 frames, and at any moment roughly one party in sixteen shows no
                    // accommodation.
                    //
                    // Listing the parties would hold it, and was tried: a building's utility demand
                    // is driven by who rents it, so a harbour holding several hundred households drew
                    // power for all of them and the city's electricity use jumped. A cruise passenger
                    // sleeps on the ship; the terminal is a lodging anchor on paper and should cost
                    // the player nothing.

                    adopted++;
                }
            }
            finally
            {
                seen.Dispose();
            }

            return adopted;
        }

        /// <summary>The household behind a creature, or Entity.Null if the hops do not resolve.</summary>
        private Entity HouseholdOf(Entity creature)
        {
            EnsureCreatureData();

            // Through lookups rather than EntityManager: CountOutboundAboard calls this once per passenger
            // on the manifest, which is two thousand of them on a full ship, on every update while the
            // vessel loads. HasComponent on a lookup also answers the existence question, so the separate
            // Exists calls go with it.
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
            EnsureCreatureData();

            int inBuffer = 0;
            int ours = 0;
            int missing = 0;

            if (m_PassengerBuffers.HasBuffer(vehicle))
            {
                DynamicBuffer<Passenger> manifest =
                    m_PassengerBuffers[vehicle];

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
            EnsureCreatureData();

            if (!m_Residents.HasComponent(creature))
            {
                return false;
            }

            Entity citizen = m_Residents[creature].m_Citizen;

            if (citizen == Entity.Null
                || !EntityManager.Exists(citizen)
                || !m_HouseholdMembers.HasComponent(citizen))
            {
                return false;
            }

            Entity household = m_HouseholdMembers[citizen].m_Household;

            return household != Entity.Null
                   && EntityManager.Exists(household)
                   && m_CruisePassengers.HasComponent(household);
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
        /// <summary>
        /// The vessel with an open call on the same route as the given one, if there is one.
        ///
        /// Selecting a ship in the world does not reliably hand back the entity this mod put the
        /// call on — a vehicle need not be a single entity, and the Controller hop only resolves
        /// upwards, so a click that lands on the controller while the call sits on another part
        /// finds nothing. Measured: "Selected transport vehicle 275382 has no cruise call" while a
        /// call was open and its passengers were ashore.
        ///
        /// The route is the reliable link. Every part of a vessel shares its CurrentRoute, and a
        /// cruise line carries one vessel, so a call on that route is this ship's call whichever
        /// entity the click resolved to.
        /// </summary>
        public Entity FindCallOnSameRoute(Entity vehicle)
        {
            if (vehicle == Entity.Null
                || !EntityManager.HasComponent<CurrentRoute>(vehicle)
                || m_ActiveCallQuery.IsEmptyIgnoreFilter)
            {
                return Entity.Null;
            }

            Entity route = EntityManager.GetComponentData<CurrentRoute>(vehicle).m_Route;

            if (route == Entity.Null)
            {
                return Entity.Null;
            }

            NativeArray<Entity> calls = m_ActiveCallQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < calls.Length; i++)
                {
                    if (EntityManager.HasComponent<CurrentRoute>(calls[i])
                        && EntityManager.GetComponentData<CurrentRoute>(calls[i]).m_Route == route)
                    {
                        return calls[i];
                    }
                }
            }
            finally
            {
                calls.Dispose();
            }

            return Entity.Null;
        }

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
            EnsureHouseholdData();

            m_AshoreByShip.Clear();
            m_LastPartiesAshore = 0;

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

                        // Ashore means still out in the city. A homeward party has reached the quay
                        // and been sent on to the ship, so counting it makes the figure a tally of
                        // everyone the call has ever landed rather than of who is still to come
                        // back — it would then only fall when the vessel reached the map edge, long
                        // after the interesting part.
                        //
                        // They keep the tag because it is what holds their lodging anchor while they
                        // board, which is precisely why the count has to exclude them explicitly.
                        if (passengers[i].m_Homeward != 0)
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
                        m_LastPartiesAshore++;
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
            VesselObservation seen,
            uint frame,
            EntityCommandBuffer commandBuffer)
        {
            Components.CruiseCall call =
                EntityManager.GetComponentData<Components.CruiseCall>(vehicle);

            // Everyone is back: sail now rather than sit out the rest of the shore leave.
            //
            // "Back" is nobody of this call's shore party left ashore — parties aboard are homeward and
            // not counted, and parties already released or written off are no longer this ship's, so
            // there is nobody left to wait for. Not in the first few sweeps of a call: the tags are added
            // by command buffer when the call starts, so the head count is empty until they are played
            // back and swept once.
            if (frame < call.m_ReboardFrame
                && frame >= call.m_DisembarkedFrame + 2u * kShorePartyInterval
                && (!m_AshoreByShip.TryGetValue(vehicle, out AshoreCount stillOut)
                    || (stillOut.m_People == 0 && stillOut.m_EmptyParties == 0)))
            {
                Mod.Log.Info(
                    $"Cruise ship {vehicle.Index}: all its passengers are back aboard, so it sails now, "
                    + $"{call.m_ReboardFrame - frame} frames ahead of schedule.");

                call.m_ReboardFrame = frame;
                EntityManager.SetComponentData(vehicle, call);
            }

            // Physically full: sail now too, whoever is still ashore, because none of them could get on.
            //
            // Only once last call has begun. A ship arriving at the harbour is also full — of the
            // complement that has not stepped off yet — and they are ashore long before the first
            // recall. Whoever is still out when it goes is written off by the shore sweep on this
            // update, as at any other departure, and leaves through the sea connection.
            int vesselCapacity = VesselCapacity(vehicle);

            if (frame < call.m_ReboardFrame
                && frame + RecallLeadFrames() >= call.m_ReboardFrame
                && vesselCapacity > 0
                && seen.m_PassengersAboard >= vesselCapacity)
            {
                int stillAshore = m_AshoreByShip.TryGetValue(vehicle, out AshoreCount fullOut) ? fullOut.m_People : 0;

                Mod.Log.Info(
                    $"Cruise ship {vehicle.Index} is full ({seen.m_PassengersAboard} of {vesselCapacity}), so it "
                    + $"sails now, {call.m_ReboardFrame - frame} frames ahead of schedule, with {stillAshore} "
                    + "passengers still ashore who could not have boarded.");

                call.m_ReboardFrame = frame;
                EntityManager.SetComponentData(vehicle, call);
            }

            // Nobody is sailed away from. While any of the shore party is still ashore — and still
            // coming back — the call is extended a step at a time, which moves the hold and the
            // write-off together: both read m_ReboardFrame.
            //
            // Decided ahead of the deadline, not after it. The hold is the ship's departure frame, set
            // to m_ReboardFrame, and the vessel sails the moment that frame arrives. This system runs
            // every sixteen frames, so waiting until the frame had passed let the ship leave first:
            // measured as "waiting for 1175 passengers" followed one update later by "left terminal
            // before shore leave ended", and 291 parties written off. Extending while the deadline is
            // still two updates away means the VesselJob raises the hold before the ship reaches it.
            uint lookahead = 2u * (uint)GetUpdateInterval(SystemUpdatePhase.GameSimulation);
            int ashorePeople = m_AshoreByShip.TryGetValue(vehicle, out AshoreCount ashore) ? ashore.m_People : 0;

            uint latestSailing = call.m_DisembarkedFrame + ShoreLeaveFrames() + kMaxOverstayFrames;

            if (frame + lookahead >= call.m_ReboardFrame
                && call.m_Escaped == 0
                && ashorePeople > 0
                && !(vesselCapacity > 0 && seen.m_PassengersAboard >= vesselCapacity)
                && call.m_ReboardFrame < latestSailing
                && StillComingBack(vehicle, ashorePeople, frame))
            {
                if (call.m_Overstayed == 0)
                {
                    Mod.Log.Info(
                        $"Cruise ship {vehicle.Index} is waiting at terminal {call.m_Terminal.Index} "
                        + $"for {ashorePeople} passengers still ashore.");
                }

                // Never past the hour: the last step is cut short rather than overshooting it.
                call.m_ReboardFrame = math.max(
                    call.m_ReboardFrame, math.min(frame + kOverstayStepFrames, latestSailing));
                call.m_Overstayed = 1;
                EntityManager.SetComponentData(vehicle, call);
            }

            if (frame >= call.m_ReboardFrame)
            {
                // Shore leave is over. Anyone still ashore is collected by the shore-party sweep
                // (ShorePartyJob) on this same update, so the call can be closed here.
                ReleaseTerminalLodging(call.m_Terminal, commandBuffer);
                commandBuffer.RemoveComponent<Components.CruiseCall>(vehicle);
                m_OverstayProgress.Remove(vehicle);

                // The departure frame is the hold; on an early close it still names the old reboard
                // frame, so bring it back to now.
                RequestRelease(vehicle, frame);

                if (call.m_Overstayed != 0)
                {
                    Mod.Log.Info(
                        $"Cruise ship {vehicle.Index} sails after waiting "
                        + $"{frame - call.m_DisembarkedFrame - ShoreLeaveFrames()} frames past its shore leave, "
                        + $"with {ashorePeople} passengers still ashore"
                        + (ashorePeople > 0 ? " (nobody else got back for a while, or the wait hit its limit)." : "."));
                }

                Mod.Log.Info(
                    $"Cruise call closed at terminal {call.m_Terminal.Index}; "
                    + $"{call.m_PartyCount} parties reboarded; {ManifestBreakdown(vehicle)}.");

                return;
            }

            // The hold itself is the VesselJob's: it is re-asserted every update at any stop but the
            // map edge, because BeginBoarding:388 rewrites it to frame + 60 whenever boarding begins
            // and the boarding flag flickers. What is left here is reporting what the job saw.
            if (seen.m_ClaimRestored)
            {
                Mod.Log.Info(
                    $"Cruise ship {vehicle.Index} had lost its claim on terminal {call.m_Terminal.Index}; "
                    + "restored it so the hold keeps the ship alongside.");
            }

            // Shore leave is running and the ship is not alongside the quay it belongs to. Either it
            // has sailed, or it is still there and the game has stopped recognising it as boarding —
            // and those need different fixes, so they are told apart rather than guessed at.
            if (seen.m_AwayFromTerminal)
            {
                ReportEscape(vehicle, ref call, frame, seen);
            }
        }

        /// <summary>
        /// Whether the shore party is still getting back: the ashore head count has reached a new low
        /// within the last <see cref="kOverstayStallFrames"/>. Boarding comes in clumps as the ship's
        /// doors cycle, so a flat stretch shorter than that is not a stall.
        /// </summary>
        private bool StillComingBack(Entity vehicle, int people, uint frame)
        {
            if (!m_OverstayProgress.TryGetValue(vehicle, out (int People, uint Since) progress)
                || people < progress.People)
            {
                m_OverstayProgress[vehicle] = (people, frame);
                return true;
            }

            return frame - progress.Since < kOverstayStallFrames;
        }

        /// <summary>
        /// Logs every held ship's hold on the first update after a load. The one measured early
        /// departure came in the first minute after one; this records the state it left from.
        /// </summary>
        private void ReportHoldsAfterLoad()
        {
            if (!m_ReportHoldsAfterLoad)
            {
                return;
            }

            m_ReportHoldsAfterLoad = false;

            if (m_ActiveCallQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeArray<Entity> ships = m_ActiveCallQuery.ToEntityArray(Allocator.Temp);

            for (int i = 0; i < ships.Length; i++)
            {
                Entity vehicle = ships[i];
                Components.CruiseCall call =
                    EntityManager.GetComponentData<Components.CruiseCall>(vehicle);
                Game.Vehicles.PublicTransport transport =
                    EntityManager.GetComponentData<Game.Vehicles.PublicTransport>(vehicle);

                string path = EntityManager.HasComponent<Game.Pathfind.PathOwner>(vehicle)
                    ? EntityManager.GetComponentData<Game.Pathfind.PathOwner>(vehicle).m_State.ToString()
                    : "none";
                string lane = EntityManager.HasComponent<Game.Vehicles.WatercraftCurrentLane>(vehicle)
                    ? EntityManager.GetComponentData<Game.Vehicles.WatercraftCurrentLane>(vehicle).m_LaneFlags.ToString()
                    : "none";
                Entity target = EntityManager.HasComponent<Target>(vehicle)
                    ? EntityManager.GetComponentData<Target>(vehicle).m_Target
                    : Entity.Null;

                Mod.Log.Info(
                    $"After load: cruise ship {vehicle.Index} on call at terminal {call.m_Terminal.Index} "
                    + $"(reboard {call.m_ReboardFrame}, now {m_SimulationSystem.frameIndex}): "
                    + $"state {transport.m_State}, departure {transport.m_DepartureFrame}, "
                    + $"path {path}, lane {lane}, target {target.Index}.");

                LogBoardingHolders(vehicle);
            }

            ships.Dispose();
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
            VesselObservation seen)
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
                $"  state {seen.m_State}, departure frame {seen.m_DepartureFrame} "
                + $"(hold wanted {call.m_ReboardFrame}), "
                + $"alongside: {seen.m_Alongside}"
                + (seen.m_Alongside ? $" at stop {seen.m_Stop.Index}, not terminal {call.m_Terminal.Index}" : ""));

            if (seen.m_HasPathOwner)
            {
                Mod.Log.Warn($"  path owner {seen.m_PathState}");
            }

            if (seen.m_HasLane)
            {
                Mod.Log.Warn($"  lane flags {seen.m_LaneFlags}");
            }

            Mod.Log.Warn($"  target {seen.m_Target.Index}, exists: {seen.m_TargetExists}");

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
                    $"  waypoint [{i}] {waypoint.Index} stop {connected.Index}: boarding vehicle {holder.Index}"
                    + (holder == vehicle ? " (ours)" : holder == Entity.Null ? " (none)" : "")
                    + $", {waiting}");
            }
        }

        /// <summary>
        /// Lets a homeward complement off at the map edge — they have left the city.
        ///
        /// Called before loading the next one, so a ship never carries two complements at once.
        /// </summary>
        private int LandHomewardPassengers(Entity vehicle, Entity homePort, EntityCommandBuffer commandBuffer)
        {
            EnsureHouseholdData();

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

                        commandBuffer.RemoveComponent<Components.CruisePassenger>(entities[i]);

                        // And they leave. Last call sends them aboard on a Leisure trip, not
                        // MovingAway (see RecallToHarbour), so nothing else ends the visit: without
                        // this the party is set down in the ship's outside connection as an ordinary
                        // tourist with nowhere to be, and tries to reach an attraction or a leisure
                        // spot from the map edge every few seconds, failing each time at the cost
                        // limit. Measured as ~110 such citizens, each failing 55 times in three
                        // minutes, using 9.6% of all route search work in a 665k city.
                        //
                        // MovingAway to the connection they are already in completes on the spot
                        // (the citizen reaches it, HouseholdMoveAwaySystem removes the household),
                        // exactly as for a party written off at the deadline above.
                        if (!EntityManager.HasComponent<Game.Agents.MovingAway>(entities[i]))
                        {
                            commandBuffer.AddComponent(entities[i], new Game.Agents.MovingAway
                            {
                                m_Target = homePort,
                                m_Reason = Game.Agents.MoveAwayReason.None
                            });
                        }

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

        /// <summary>
        /// How long before sailing the first parties are recalled, in frames: kRecallLeadHours * ln(1 +
        /// stay / 4 hours). See <see cref="kRecallLeadHours"/>.
        /// </summary>
        private uint RecallLeadFrames()
        {
            float stayHours = ShoreLeaveFrames() * 24f / kFramesPerDay;
            float leadHours = kRecallLeadHours * math.log(1f + stayHours / 4f);
            return (uint)math.clamp(leadHours * kFramesPerDay / 24f, kFramesPerDay / 24f, ShoreLeaveFrames());
        }

        /// <summary>
        /// Time between a party's deadline and the ship sailing, reserved for getting aboard: 15% of
        /// the stay, capped at 40% of the recall lead so a long stay does not push every deadline hours
        /// ahead of the departure.
        /// </summary>
        private uint BoardingGraceFrames()
        {
            return (uint)math.max(1f, math.min(
                ShoreLeaveFrames() * kBoardingGraceFraction, RecallLeadFrames() * kGraceCapOfLead));
        }

        /// <summary>How far apart parties' deadlines, and so their recalls, are spread.</summary>
        private uint RecallSpreadFrames() => (uint)math.max(1f, RecallLeadFrames() * kRecallSpreadOfLead);

        /// <summary>
        /// Last call, as frames before each party's own deadline: the lead less the grace and the spread,
        /// so the earliest-due parties are recalled exactly one lead before sailing and the last a fifth
        /// of it later. At least 40% of the lead, since the grace and spread are capped to the rest.
        /// </summary>
        private uint LastCallFrames() => RecallLeadFrames() - BoardingGraceFrames() - RecallSpreadFrames();

        private uint ShoreLeaveFrames()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            int hours = settings != null ? math.clamp(settings.CruiseShoreLeaveHours, 2, 48) : 8;

            return (uint)((ulong)kFramesPerDay * (ulong)hours / 24UL);
        }

        private void ReleaseTerminalLodging(Entity terminal, EntityCommandBuffer commandBuffer)
        {
            if (terminal == Entity.Null
                || !EntityManager.Exists(terminal)
                || !EntityManager.HasComponent<Components.CruiseTerminalLodging>(terminal))
            {
                return;
            }

            StripTerminalEquipment(terminal, commandBuffer);
        }

        /// <summary>
        /// Takes back everything the mod put on a terminal, and nothing else.
        ///
        /// Every component here is native, serialized, and written to a player-owned building, so
        /// the rule the notes record for AttractivenessProvider applies to all of them: whatever
        /// was added has to come off on every path that ends a call, including the ones that are
        /// not clean shutdowns.
        ///
        /// What changed is the "and nothing else". This used to remove the LodgingProvider and the
        /// Renter buffer unconditionally, which is correct for the stock harbour — it has neither —
        /// and destructive for any harbour that does. A DLC port that houses a company would have
        /// lost its renters, and with them the company, the first time a cruise ship sailed. The
        /// markers written at equip time say which of these are ours; anything without its marker
        /// belonged to the building before we arrived and is left alone.
        ///
        /// A terminal saved by an earlier build carries CruiseTerminalLodging and none of the
        /// markers. There is no way to ask that save what we added, so it is treated the way that
        /// build behaved: the provider and the buffer were ours, because on the harbours that build
        /// could equip they always were.
        /// </summary>
        private void StripTerminalEquipment(Entity terminal, EntityCommandBuffer commandBuffer)
        {
            // Deliberately keyed off the equip marker rather than "has no per-component markers".
            // The sweep repairs a legacy terminal's utility guard in place, which would otherwise
            // give it a marker and make it look like one of ours — and then its old provider and
            // renter buffer would never come off.
            bool legacy = !EntityManager.HasComponent<Components.CruiseTerminalEquipped>(terminal);

            if (legacy || EntityManager.HasComponent<Components.CruiseTerminalProvider>(terminal))
            {
                commandBuffer.RemoveComponent<LodgingProvider>(terminal);
                commandBuffer.RemoveComponent<Components.CruiseTerminalProvider>(terminal);
            }

            if (legacy || EntityManager.HasComponent<Components.CruiseTerminalRenters>(terminal))
            {
                commandBuffer.RemoveComponent<Game.Buildings.Renter>(terminal);
                commandBuffer.RemoveComponent<Components.CruiseTerminalRenters>(terminal);
            }

            if (EntityManager.HasComponent<Components.CruiseTerminalUtilityGuard>(terminal))
            {
                commandBuffer.RemoveComponent<Game.Buildings.StorageProperty>(terminal);
                commandBuffer.RemoveComponent<Components.CruiseTerminalUtilityGuard>(terminal);
            }

            commandBuffer.RemoveComponent<Components.CruiseTerminalEquipped>(terminal);
            commandBuffer.RemoveComponent<Components.CruiseTerminalLodging>(terminal);
        }

    }
}
