using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Companies;
using Game.Pathfind;
using Game.Prefabs;
using Game.Simulation;
using Game.Tools;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Replaces TouristFindTargetSystem so that arrivals which are not standing directly on a lane
    /// can still find somewhere to go.
    ///
    /// THE DEFECT
    ///
    /// The native system asks the pathfinder for a destination using an origin built like this
    /// (TouristFindTargetSystem.cs:99-104):
    ///
    ///     SetupQueueTarget origin = new SetupQueueTarget
    ///     {
    ///         m_Type = SetupTargetType.CurrentLocation,
    ///         m_Methods = PathMethod.Pedestrian,
    ///         m_Entity = entity2
    ///     };
    ///
    /// m_Value2 is left at zero. That field is the origin search radius, and
    /// CommonPathfindSetup.SetupCurrentLocationJob:83 reads it:
    ///
    ///     if ((FindTargets(entity, entity, 0f, ...) != 0 &amp;&amp; flag)
    ///         || ((m_Methods &amp; PathMethod.Flying) == 0 &amp;&amp; num &lt;= 0f))
    ///     {
    ///         return;
    ///     }
    ///
    /// With num = 0 and no Flying method, the job returns before the road fallback (:101), before
    /// the airway lookups (:105-128) and before the radius search (:130). The only thing that can
    /// supply an origin is the zero-radius FindTargets in the condition itself, which succeeds only
    /// where a pedestrian lane lies directly under the household.
    ///
    /// A road outside connection sits on such a lane, so it works. An air or sea connection at the
    /// map edge does not, so no origin is enqueued, the query returns no destination, and the
    /// household is evicted as TouristNoTarget (:144) — on the first and only attempt.
    ///
    /// Measured across a full session: road arrivals failed at 34%, air and sea at 94-98%,
    /// unchanged by free rooms, arrival rate, or which building the tourist was placed in. Only the
    /// entry point mattered.
    ///
    /// THE FIX
    ///
    /// Two changes, both in the request rather than in the pathfinder:
    ///
    ///   m_Value2 = kOriginSearchRadius   lets SetupCurrentLocationJob run its radius search and
    ///                                    find a nearby lane or stop, so the transport hop the
    ///                                    player built can actually be used.
    ///
    ///   retries before eviction          a transient failure no longer ends the visit. The native
    ///                                    single-shot behaviour is harsh for every mode, not just
    ///                                    air and sea.
    ///
    /// NEW ARRIVALS ARE BOOKED, NOT SEARCHED FOR
    ///
    /// Even with the radius, the route search was measured failing from where arrivals stand: 0
    /// found in several thousand from a rail line's city station, about one in five from a road
    /// connection, with ten valid hotels holding 2,520 free rooms. TouristRebookSystem had been
    /// booking every arrival before the search answered, which hid it — and the visitors it booked
    /// did reach their rooms, by their own trips along the line. So an arrival at a connection with
    /// a way into the city (ArrivalConnections) is booked into the hotel nearest where it comes in:
    /// the line's city stop, or the connection itself for a road. The route search still runs when
    /// no hotel has a free room, and for anyone already in the city.
    ///
    /// The booking itself is the native HotelReserveJob's. Turning the setting off restores the
    /// native system at runtime.
    /// </summary>
    public partial class TouristTargetSearchSystem : GameSystemBase
    {
        /// <summary>
        /// How far from the tourist to look for somewhere to start walking from.
        ///
        /// Large enough to reach the access road or transport stop serving a harbour or airport,
        /// small enough that a tourist is not teleported across the district. The native value is
        /// effectively zero, which is the defect.
        /// </summary>
        private const float kOriginSearchRadius = 150f;

        /// <summary>Failed searches tolerated before the visitor gives up and leaves.</summary>
        private const int kMaxAttempts = 3;

        /// <summary>
        /// Households processed per update, so a large backlog cannot stall a frame.
        ///
        /// Raised from 512 for the cruise line, which books a complement in one go: a full ship is
        /// around seven hundred parties created on a single frame, and at 512 per update they were
        /// competing with the city's ordinary arrivals for the same budget. The scan itself is what
        /// costs — a pass over every seeker — and that happens once per update either way, so
        /// lifting the per-update cap adds pathfind requests without adding scans.
        /// </summary>
        private const int kMaxPerUpdate = 2048;

        private EntityQuery m_SeekerQuery;
        private EntityQuery m_OutsideConnectionQuery;
        private ComponentTypeSet m_PathfindTypes;

        private EndFrameBarrier m_EndFrameBarrier;
        private PathfindSetupSystem m_PathfindSetupSystem;
        private TouristFindTargetSystem m_NativeSystem;

        /// <summary>Attempts made per household, so retries can be bounded.</summary>
        private NativeHashMap<Entity, int> m_Attempts;

        private bool m_NativeDisabled;

        private ComponentLookup<PathInformation> m_PathInformation;
        private BufferLookup<HouseholdCitizen> m_HouseholdCitizens;
        private ComponentLookup<CurrentBuilding> m_CurrentBuildings;
        private BufferLookup<Game.Vehicles.OwnedVehicle> m_OwnedVehicles;
        private BufferLookup<Renter> m_Renters;
        private ComponentLookup<LodgingProvider> m_LodgingProviders;
        private ComponentLookup<TouristHousehold> m_TouristHouseholds;

        /// <summary>
        /// Written by the last update's job, indexed by the k-constants below: targets found,
        /// evictions, arrivals booked into the nearest hotel, and route searches that came back
        /// empty.
        /// </summary>
        private NativeArray<int> m_Counts;

        private const int kFound = 0;
        private const int kEvicted = 1;
        private const int kBooked = 2;
        private const int kSearchFailed = 3;

        /// <summary>The same counts, summed between log lines.</summary>
        private readonly int[] m_Totals = new int[4];

        /// <summary>Updates between summary lines: 4096 of 64 frames is one in-game day.</summary>
        private const int kUpdatesPerLog = 4096;

        private int m_UpdatesSinceLog;

        /// <summary>Failed searches by the building the household stood in, between log lines.</summary>
        private NativeHashMap<Entity, int> m_FailedAt;

        /// <summary>A hotel a new arrival can be booked into, and where it stands.</summary>
        private struct HotelSlot
        {
            public Entity m_Company;
            public Entity m_Property;
            public float3 m_Position;
        }

        private EntityQuery m_HotelQuery;

        /// <summary>The entry-stop mapping last logged, so it is written only when it changes.</summary>
        private string m_LastEntryStops = string.Empty;
        private JobHandle m_LastJob;

        /// <summary>Targets found since load. For diagnostics.</summary>
        public int TargetsFound { get; private set; }

        /// <summary>Households evicted after exhausting their retries. For diagnostics.</summary>
        public int Evictions { get; private set; }

        // The native system runs every 16 frames as a Burst-compiled parallel job. This one is a
        // single-threaded Burst job, so the same cadence costs more: a full pass over every seeker
        // in the city, sixteen times per 256 frames.
        //
        // 64 still clears 2,048 households per 256 frames, which is well above the arrival rate any
        // city sustains, and cuts the scan cost to a quarter. A tourist waits a few frames longer
        // for a destination and nothing else changes.
        //
        // Tried at 8 to make a cruise complement appear faster and measured as making no difference
        // at all — the queue at the map edge read 164 before and 169 after. Target assignment was
        // never the limiter, and the arithmetic agrees: 2048 per update every 64 frames is hundreds
        // of thousands of slots across a load window against seven hundred parties. Reverted rather
        // than left in, because it multiplies a full ToEntityArray over every seeker in the city by
        // eight for no measured gain.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_PathfindSetupSystem = World.GetOrCreateSystemManaged<PathfindSetupSystem>();
            m_NativeSystem = World.GetOrCreateSystemManaged<TouristFindTargetSystem>();

            m_PathfindTypes = new ComponentTypeSet(ComponentType.ReadWrite<PathInformation>());
            m_Attempts = new NativeHashMap<Entity, int>(1024, Allocator.Persistent);
            m_Counts = new NativeArray<int>(m_Totals.Length, Allocator.Persistent);
            m_FailedAt = new NativeHashMap<Entity, int>(64, Allocator.Persistent);

            // Hotel companies renting a building, the ones CitizenPathfindSetup's tourist search
            // would offer. Free rooms are read live in the job, since each booking takes one.
            m_HotelQuery = GetEntityQuery(
                ComponentType.ReadOnly<LodgingProvider>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.Exclude<Components.CruiseTerminalLodging>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());

            m_PathInformation = GetComponentLookup<PathInformation>(isReadOnly: true);
            m_HouseholdCitizens = GetBufferLookup<HouseholdCitizen>(isReadOnly: true);
            m_CurrentBuildings = GetComponentLookup<CurrentBuilding>(isReadOnly: true);
            m_OwnedVehicles = GetBufferLookup<Game.Vehicles.OwnedVehicle>(isReadOnly: true);
            m_Renters = GetBufferLookup<Renter>(isReadOnly: false);
            m_LodgingProviders = GetComponentLookup<LodgingProvider>(isReadOnly: false);
            m_TouristHouseholds = GetComponentLookup<TouristHousehold>(isReadOnly: false);

            // Cruise passengers are excluded outright, and this is the fix for them going to hotels
            // rather than another attempt to undo it afterwards.
            //
            // This system replaced the native TouristFindTargetSystem, so it is the only thing that
            // sends a visitor to a room — which means excluding a household here is sufficient, and
            // nothing else has to hold. Everything tried before this was downstream: clearing
            // LodgingSeeker after it was granted, cancelling the Target after it was set, repairing
            // m_Hotel after it was overwritten. Each lost the same race, because this system runs
            // between our updates and only needs to win once.
            //
            // A cruise passenger sleeps aboard and their lodging is settled the moment they come
            // ashore, so there is never a reason for this search to consider them.
            m_OutsideConnectionQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Objects.OutsideConnection>(),
                ComponentType.Exclude<Game.Objects.ElectricityOutsideConnection>(),
                ComponentType.Exclude<Game.Objects.WaterPipeOutsideConnection>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());

            m_SeekerQuery = GetEntityQuery(
                ComponentType.ReadWrite<TouristHousehold>(),
                ComponentType.ReadWrite<LodgingSeeker>(),
                ComponentType.Exclude<Components.CruisePassenger>(),
                ComponentType.Exclude<Target>(),
                ComponentType.Exclude<MovingAway>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnDestroy()
        {
            m_LastJob.Complete();

            if (m_Attempts.IsCreated)
            {
                m_Attempts.Dispose();
            }

            if (m_Counts.IsCreated)
            {
                m_Counts.Dispose();
            }

            if (m_FailedAt.IsCreated)
            {
                m_FailedAt.Dispose();
            }

            RestoreNativeSystem();

            base.OnDestroy();
        }

        private void DisableNativeSystem()
        {
            if (m_NativeDisabled || m_NativeSystem == null)
            {
                return;
            }

            m_NativeSystem.Enabled = false;
            m_NativeDisabled = true;

            Mod.Log.Info(
                "Native TouristFindTargetSystem disabled; target search handled by TourismOverhaul " +
                $"with an origin radius of {kOriginSearchRadius:0} and {kMaxAttempts} attempts.");
        }

        private void RestoreNativeSystem()
        {
            if (!m_NativeDisabled || m_NativeSystem == null)
            {
                return;
            }

            m_NativeSystem.Enabled = true;
            m_NativeDisabled = false;

            Mod.Log.Info("Native TouristFindTargetSystem restored.");
        }

        protected override void OnUpdate()
        {
            // Scheduled 64 frames ago; this only collects its counts.
            m_LastJob.Complete();
            TargetsFound += m_Counts[kFound];
            Evictions += m_Counts[kEvicted];

            for (int i = 0; i < m_Totals.Length; i++)
            {
                m_Totals[i] += m_Counts[i];
                m_Counts[i] = 0;
            }

            if (++m_UpdatesSinceLog >= kUpdatesPerLog)
            {
                m_UpdatesSinceLog = 0;
                LogDailySummary();
            }

            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null || !settings.FixTouristTargetSearch)
            {
                RestoreNativeSystem();
                return;
            }

            DisableNativeSystem();

            if (m_SeekerQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            m_PathInformation.Update(this);
            m_HouseholdCitizens.Update(this);
            m_CurrentBuildings.Update(this);
            m_Renters.Update(this);
            m_LodgingProviders.Update(this);
            m_TouristHouseholds.Update(this);
            m_OwnedVehicles.Update(this);

            NativeList<Entity> seekers = m_SeekerQuery.ToEntityListAsync(Allocator.TempJob, out JobHandle listed);
            NativeParallelHashMap<Entity, Entity> entryStops =
                CollectEntryStops(out NativeParallelHashMap<Entity, float3> entryPositions);
            NativeList<HotelSlot> hotels = CollectHotels();

            JobHandle job = new SearchJob
            {
                m_Seekers = seekers,
                m_EntryStops = entryStops,
                m_EntryPositions = entryPositions,
                m_Hotels = hotels,
                m_PathInformation = m_PathInformation,
                m_HouseholdCitizens = m_HouseholdCitizens,
                m_CurrentBuildings = m_CurrentBuildings,
                m_OwnedVehicles = m_OwnedVehicles,
                m_Renters = m_Renters,
                m_LodgingProviders = m_LodgingProviders,
                m_TouristHouseholds = m_TouristHouseholds,
                m_Attempts = m_Attempts,
                m_FailedAt = m_FailedAt,
                m_Counts = m_Counts,
                m_PathfindTypes = m_PathfindTypes,
                m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer(),
                m_PathfindQueue = m_PathfindSetupSystem.GetQueue(this, 64),
            }.Schedule(JobHandle.CombineDependencies(Dependency, listed));

            seekers.Dispose(job);
            entryStops.Dispose(job);
            entryPositions.Dispose(job);
            hotels.Dispose(job);
            m_PathfindSetupSystem.AddQueueWriter(job);
            m_EndFrameBarrier.AddJobHandleForProducer(job);
            m_LastJob = job;
            Dependency = job;
        }

        /// <summary>
        /// Each rail, air or sea connection mapped to the city-side stop of a line serving it — the
        /// place a visitor set down there really enters the city, and so where a search for them
        /// has to start. See ArrivalConnections.CityEntryStop. A handful of connections, walked on
        /// the main thread once per update.
        /// </summary>
        private NativeParallelHashMap<Entity, Entity> CollectEntryStops(
            out NativeParallelHashMap<Entity, float3> entryPositions)
        {
            NativeArray<Entity> connections = m_OutsideConnectionQuery.ToEntityArray(Allocator.Temp);
            NativeParallelHashMap<Entity, Entity> entryStops =
                new NativeParallelHashMap<Entity, Entity>(math.max(1, connections.Length), Allocator.TempJob);

            // Where an arrival at each usable connection comes into the city, for the nearest-hotel
            // booking: the line's city stop where there is one, the connection itself otherwise.
            entryPositions =
                new NativeParallelHashMap<Entity, float3>(math.max(1, connections.Length), Allocator.TempJob);

            // Sorted, so the line below is rewritten only when the connections change and not
            // whenever the query hands them back in another order.
            connections.Sort(new EntityIndexComparer());

            System.Text.StringBuilder described = new System.Text.StringBuilder();

            for (int i = 0; i < connections.Length; i++)
            {
                // The cruise line's connection is left exactly as it was — its arrivals are the
                // cruise complement, which CruiseVoyageSystem manages — so it gets neither a city
                // stop to search from nor the nearest-hotel booking.
                if (ServesCruiseLine(connections[i]))
                {
                    continue;
                }

                OutsideConnectionTransferType usable = ArrivalConnections.UsableTypes(
                    EntityManager, connections[i], out OutsideConnectionTransferType declared);

                if (usable == OutsideConnectionTransferType.None)
                {
                    continue;
                }

                Entity stop = ArrivalConnections.CityEntryStop(EntityManager, connections[i]);

                if (stop != Entity.Null)
                {
                    entryStops.TryAdd(connections[i], stop);
                }

                if (TryGetPosition(stop != Entity.Null ? stop : connections[i], out float3 position))
                {
                    entryPositions.TryAdd(connections[i], position);
                }

                described.Append(described.Length > 0 ? "; " : string.Empty)
                    .Append($"{connections[i].Index} ({declared}) from ")
                    .Append(stop != Entity.Null ? $"city stop {stop.Index}" : "itself");
            }

            string summary = described.ToString();

            if (summary != m_LastEntryStops)
            {
                m_LastEntryStops = summary;
                Mod.Log.Info($"Arrivals enter the city at: {summary}.");
            }

            connections.Dispose();
            return entryStops;
        }

        /// <summary>
        /// One line per in-game day: arrivals booked, route searches found and failed, and the
        /// three places failing searches stood most often. A failure at a connection usually means
        /// a party left there before it stopped taking arrivals; at a usable one it means the
        /// booking missed it, which is the case worth reading this line for. The job is complete
        /// when this runs, so the counts are safe to read and clear.
        /// </summary>
        private void LogDailySummary()
        {
            if (m_Totals[kBooked] + m_Totals[kFound] + m_Totals[kSearchFailed] > 0)
            {
                Mod.Log.Info(
                    $"Tourist targets today: {m_Totals[kBooked]} arrivals booked into the nearest hotel; "
                    + $"route searches {m_Totals[kFound] - m_Totals[kBooked]} found, "
                    + $"{m_Totals[kSearchFailed]} failed, {m_Totals[kEvicted]} parties gave up"
                    + DescribeFailurePlaces() + ".");
            }

            System.Array.Clear(m_Totals, 0, m_Totals.Length);
            m_FailedAt.Clear();
        }

        /// <summary>The places with the most failed searches, as "; failing most at …".</summary>
        private string DescribeFailurePlaces()
        {
            if (m_FailedAt.Count == 0)
            {
                return string.Empty;
            }

            NativeKeyValueArrays<Entity, int> failures = m_FailedAt.GetKeyValueArrays(Allocator.Temp);
            System.Text.StringBuilder places = new System.Text.StringBuilder();

            // Three passes for the three largest: the map holds a handful of places, not thousands.
            for (int rank = 0; rank < 3; rank++)
            {
                int best = -1;

                for (int i = 0; i < failures.Length; i++)
                {
                    if (failures.Values[i] > 0 && (best < 0 || failures.Values[i] > failures.Values[best]))
                    {
                        best = i;
                    }
                }

                if (best < 0)
                {
                    break;
                }

                places.Append(places.Length > 0 ? ", " : string.Empty)
                    .Append($"{DescribePlace(failures.Keys[best])} x{failures.Values[best]}");

                failures.Values[best] = 0;
            }

            failures.Dispose();
            return "; failing most at " + places;
        }

        private string DescribePlace(Entity place)
        {
            if (place == Entity.Null || !EntityManager.Exists(place))
            {
                return "nowhere";
            }

            if (!EntityManager.HasComponent<Game.Objects.OutsideConnection>(place))
            {
                return $"building {place.Index}";
            }

            OutsideConnectionTransferType usable = ArrivalConnections.UsableTypes(
                EntityManager, place, out OutsideConnectionTransferType declared);

            return $"connection {place.Index} ({declared}, "
                   + (usable == OutsideConnectionTransferType.None ? "unserved" : "served") + ")";
        }

        /// <summary>
        /// Where an entry point stands. A rail stop or an object connection carries a Transform; a
        /// road connection is a road node at the map edge and carries its position on the Node
        /// instead — reading only the Transform left every road arrival out of the booking.
        /// </summary>
        private bool TryGetPosition(Entity entity, out float3 position)
        {
            if (EntityManager.HasComponent<Game.Objects.Transform>(entity))
            {
                position = EntityManager.GetComponentData<Game.Objects.Transform>(entity).m_Position;
                return true;
            }

            if (EntityManager.HasComponent<Game.Net.Node>(entity))
            {
                position = EntityManager.GetComponentData<Game.Net.Node>(entity).m_Position;
                return true;
            }

            position = default;
            return false;
        }

        /// <summary>Orders entities by index, for a stable log line.</summary>
        private struct EntityIndexComparer : System.Collections.Generic.IComparer<Entity>
        {
            public int Compare(Entity x, Entity y) => x.Index.CompareTo(y.Index);
        }

        private bool ServesCruiseLine(Entity connection)
        {
            CruiseVoyageSystem cruise = World.GetExistingSystemManaged<CruiseVoyageSystem>();
            return cruise != null && cruise.ServesCruiseLine(connection);
        }

        /// <summary>
        /// Hotels renting an active building, with the building's position. Each update, on the
        /// main thread: a city has tens of hotels, not thousands.
        /// </summary>
        private NativeList<HotelSlot> CollectHotels()
        {
            NativeArray<Entity> companies = m_HotelQuery.ToEntityArray(Allocator.Temp);
            NativeList<HotelSlot> hotels = new NativeList<HotelSlot>(math.max(1, companies.Length), Allocator.TempJob);

            for (int i = 0; i < companies.Length; i++)
            {
                Entity property = EntityManager.GetComponentData<PropertyRenter>(companies[i]).m_Property;

                if (property == Entity.Null
                    || !EntityManager.HasComponent<Building>(property)
                    || !EntityManager.HasComponent<Game.Objects.Transform>(property)
                    || BuildingUtils.CheckOption(EntityManager.GetComponentData<Building>(property), BuildingOption.Inactive))
                {
                    continue;
                }

                hotels.Add(new HotelSlot
                {
                    m_Company = companies[i],
                    m_Property = property,
                    m_Position = EntityManager.GetComponentData<Game.Objects.Transform>(property).m_Position
                });
            }

            companies.Dispose();
            return hotels;
        }

        /// <summary>
        /// The whole pass, mirroring the native TouristFindTargetSystem's jobs: request a path for a
        /// new seeker, and act on the answer once it comes back.
        ///
        /// Run as a Burst job rather than on the main thread, where reading PathInformation,
        /// CurrentBuilding and Renter and writing LodgingProvider and TouristHousehold waited for
        /// every job touching any of them: 11 ms per update, every 64 frames, up to 61 ms.
        /// Households are visited in the same order as before, and the same ones consume a slot.
        /// </summary>
        [BurstCompile]
        private struct SearchJob : IJob
        {
            [ReadOnly] public NativeList<Entity> m_Seekers;
            [ReadOnly] public NativeParallelHashMap<Entity, Entity> m_EntryStops;
            [ReadOnly] public NativeParallelHashMap<Entity, float3> m_EntryPositions;
            [ReadOnly] public NativeList<HotelSlot> m_Hotels;
            [ReadOnly] public ComponentLookup<PathInformation> m_PathInformation;
            [ReadOnly] public BufferLookup<HouseholdCitizen> m_HouseholdCitizens;
            [ReadOnly] public ComponentLookup<CurrentBuilding> m_CurrentBuildings;
            [ReadOnly] public BufferLookup<Game.Vehicles.OwnedVehicle> m_OwnedVehicles;
            public BufferLookup<Renter> m_Renters;
            public ComponentLookup<LodgingProvider> m_LodgingProviders;
            public ComponentLookup<TouristHousehold> m_TouristHouseholds;
            public NativeHashMap<Entity, int> m_Attempts;
            public NativeHashMap<Entity, int> m_FailedAt;

            /// <summary>Indexed by the k-constants: found, evicted, booked, search failed.</summary>
            public NativeArray<int> m_Counts;

            public ComponentTypeSet m_PathfindTypes;
            public EntityCommandBuffer m_CommandBuffer;
            public NativeQueue<SetupQueueItem> m_PathfindQueue;

            public void Execute()
            {
                int processed = 0;

                for (int i = 0; i < m_Seekers.Length && processed < kMaxPerUpdate; i++)
                {
                    if (ProcessSeeker(m_Seekers[i]))
                    {
                        processed++;
                    }
                }
            }

            /// <summary>The building the household's citizens are in, as RequestPath reads it.</summary>
            private Entity Location(Entity household)
            {
                Entity location = Entity.Null;

                if (m_HouseholdCitizens.TryGetBuffer(household, out DynamicBuffer<HouseholdCitizen> citizens))
                {
                    for (int i = 0; i < citizens.Length; i++)
                    {
                        if (m_CurrentBuildings.TryGetComponent(citizens[i].m_Citizen, out CurrentBuilding building))
                        {
                            location = building.m_CurrentBuilding;
                        }
                    }
                }

                return location;
            }

            /// <returns>True when the household consumed a slot this update.</returns>
            private bool ProcessSeeker(Entity household)
            {
                if (!m_PathInformation.TryGetComponent(household, out PathInformation path))
                {
                    // A new arrival at a connection with a way into the city is booked into the
                    // nearest hotel with a free room, measured from where it enters. See
                    // TryBookNearest for why this does not wait on the route search.
                    if (m_EntryPositions.TryGetValue(Location(household), out float3 entry)
                        && TryBookNearest(household, entry))
                    {
                        return true;
                    }

                    RequestPath(household);
                    return true;
                }

                // Still searching.
                if ((path.m_State & PathFlags.Pending) != 0)
                {
                    return false;
                }

                if (path.m_Destination != Entity.Null)
                {
                    AcceptTarget(household, path.m_Destination);
                    return true;
                }

                // Counted with where the party stands, for the daily summary.
                m_Counts[kSearchFailed]++;

                Entity failedAt = Location(household);
                m_FailedAt.TryGetValue(failedAt, out int failures);
                m_FailedAt[failedAt] = failures + 1;

                // No destination. Unlike the native system this is not immediately fatal.
                m_Attempts.TryGetValue(household, out int attempts);
                attempts++;

                if (attempts < kMaxAttempts)
                {
                    m_Attempts[household] = attempts;

                    // Dropping PathInformation puts the household back at the start of the cycle, so
                    // the next update issues a fresh request rather than re-reading a stale failure.
                    m_CommandBuffer.RemoveComponent<PathInformation>(household);
                    return true;
                }

                m_Attempts.Remove(household);
                m_Counts[kEvicted]++;

                CitizenUtils.HouseholdMoveAway(m_CommandBuffer, household, MoveAwayReason.TouristNoTarget);

                return true;
            }

            /// <summary>
            /// Issues the pathfind request, identical to the native one but with a usable origin radius.
            /// </summary>
            private void RequestPath(Entity household)
            {
                m_CommandBuffer.AddComponent(household, in m_PathfindTypes);
                m_CommandBuffer.SetComponent(household, new PathInformation { m_State = PathFlags.Pending });

                PathfindParameters parameters = new PathfindParameters
                {
                    m_MaxSpeed = 277.77777f,
                    m_WalkSpeed = 1.6666667f,
                    m_Weights = new PathfindWeights(0.1f, 0.1f, 0.1f, 0.2f),
                    m_Methods = PathMethod.PublicTransportDay | PathMethod.Taxi | PathMethod.PublicTransportNight,
                    m_TaxiIgnoredRules = Game.Vehicles.VehicleUtils.GetIgnoredPathfindRulesTaxiDefaults(),
                    m_PathfindFlags = PathfindFlags.IgnoreFlow | PathfindFlags.Simplified | PathfindFlags.IgnorePath
                };

                Entity location = Location(household);

                // A visitor still at a rail, air or sea connection is searched for from the stop
                // where its line comes into the city, not from the map-edge marker, which has no
                // lane near it for a search to start on.
                if (m_EntryStops.TryGetValue(location, out Entity entryStop))
                {
                    location = entryStop;
                }

                SetupQueueTarget origin = new SetupQueueTarget
                {
                    m_Type = SetupTargetType.CurrentLocation,
                    m_Methods = PathMethod.Pedestrian,
                    m_Entity = location,

                    // The fix. Zero here is what strands every arrival that is not standing on a lane.
                    m_Value2 = kOriginSearchRadius
                };

                SetupQueueTarget destination = new SetupQueueTarget
                {
                    m_Type = SetupTargetType.TouristFindTarget,
                    m_Methods = PathMethod.Pedestrian,
                    m_Entity = household
                };

                PathUtils.UpdateOwnedVehicleMethods(
                    household, ref m_OwnedVehicles, ref parameters, ref origin, ref destination);

                m_PathfindQueue.Enqueue(new SetupQueueItem(household, parameters, origin, destination));
            }

            /// <summary>
            /// Books the room or heads for the attraction. Mirrors the native HotelReserveJob
            /// (TouristFindTargetSystem.cs:170-199) so lodging behaves exactly as before.
            /// </summary>
            /// <summary>
            /// Books a new arrival into the nearest hotel with a free room, without a route search.
            ///
            /// The search was measured failing from where arrivals enter the city: 0 found in
            /// several thousand from a rail line's city station, about one in five from a road
            /// connection, with ten valid hotels holding 2,520 free rooms. The rebooking pass had
            /// been booking every arrival before the search answered, which is why that never
            /// showed — and the visitors it booked did reach their hotels, by their own trips,
            /// which the game routes along the line. So arrivals are booked the same way, but only
            /// at a connection with a way into the city (ArrivalConnections), and into the hotel
            /// nearest where they come in rather than whichever came first. With no free room
            /// anywhere the route search still runs, and can still find an attraction to visit.
            /// </summary>
            private bool TryBookNearest(Entity household, float3 entry)
            {
                int best = -1;
                float bestDistance = float.MaxValue;

                for (int i = 0; i < m_Hotels.Length; i++)
                {
                    HotelSlot hotel = m_Hotels[i];

                    if (!m_LodgingProviders.TryGetComponent(hotel.m_Company, out LodgingProvider provider)
                        || provider.m_FreeRooms <= 0)
                    {
                        continue;
                    }

                    float distance = math.distancesq(entry, hotel.m_Position);

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = i;
                    }
                }

                if (best < 0)
                {
                    return false;
                }

                m_Counts[kBooked]++;
                AcceptTarget(household, m_Hotels[best].m_Property);
                return true;
            }

            private void AcceptTarget(Entity household, Entity destination)
            {
                m_Attempts.Remove(household);
                m_Counts[kFound]++;

                Entity hotel = Entity.Null;

                if (m_Renters.TryGetBuffer(destination, out DynamicBuffer<Renter> renters)
                    && renters.Length > 0
                    && m_LodgingProviders.HasComponent(renters[0].m_Renter))
                {
                    hotel = renters[0].m_Renter;
                }

                if (hotel != Entity.Null && m_TouristHouseholds.HasComponent(household))
                {
                    LodgingProvider provider = m_LodgingProviders[hotel];

                    if (provider.m_FreeRooms > 0 && m_Renters.HasBuffer(hotel))
                    {
                        provider.m_FreeRooms--;
                        m_LodgingProviders[hotel] = provider;

                        m_Renters[hotel].Add(new Renter { m_Renter = household });

                        TouristHousehold tourist = m_TouristHouseholds[household];
                        tourist.m_Hotel = hotel;
                        m_TouristHouseholds[household] = tourist;

                        m_CommandBuffer.RemoveComponent<LodgingSeeker>(household);
                    }
                }

                m_CommandBuffer.AddComponent(household, new Target(destination));
            }
        }
    }
}
