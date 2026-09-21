using System.Collections.Generic;
using Game;
using Game.Citizens;
using Game.Common;
using Game.Creatures;
using Game.Net;
using Game.Pathfind;
using Game.Prefabs;
using Game.Tools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Random = Unity.Mathematics.Random;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Stops too many visitors choosing the same park, and spreads the ones inside across its lawns.
    ///
    /// THE UNDERLYING DEFECT
    ///
    /// Residents and tourists alike pick a leisure type in LeisureSystem.SelectLeisureType
    /// (:521-559) and then a destination through CitizenPathfindSetup.SetupLeisureTargetJob, which
    /// offers every LeisureProvider of that type at cost 0 (:164). Only shops and restaurants get a
    /// fullness term (:169-184). So the nearest park wins every search however packed it is — a
    /// single lawn was measured holding 1,487 visitors. AttractionCrowdingSystem cannot help: the
    /// attractiveness it damps feeds only SetupTargetType.Attraction (TripNeededSystem:1442).
    ///
    /// THE FIX
    ///
    /// The one per-park lever in that search is its candidate query, which is simply "has
    /// Game.Buildings.LeisureProvider" (CitizenPathfindSetup:835). A park over its visitor limit has
    /// the tag removed, so new leisure trips pick somewhere else; once it has thinned below 75% of
    /// the limit the tag goes back. Nobody is removed or hidden — the crowd drains as visitors
    /// finish and leave in the ordinary way.
    ///
    /// The tag is serialized and RequiredComponentSystem:1120-1128 only restores it for companies,
    /// not parks, so a save written while a park is gated would keep it gated for good. Every tag
    /// is therefore put back in PreSerialize, on disable and on destroy; the next update re-gates
    /// whatever is still full. No save ever contains a gated park.
    ///
    /// Inside a park, a visitor on an over-full lawn re-rolls its spot (PathFlags.Obsolete, as
    /// ReachTarget:2241-2242 does — FindNewPath keeps the same target building), and settled
    /// visitors occasionally drift, because the native re-roll trigger works out at over ten
    /// minutes of real time and CannotIgnore can pin a visitor for its whole stay.
    ///
    /// WHO COUNTS
    ///
    /// Settled is a lane fact: CreatureLaneFlags.Hangaround | EndReached. ResidentFlags.Arrived is
    /// set only by ReachTarget, which TickGroupMemberWalking never calls; Divert is removed on
    /// arrival (ReachDivert:2476); PathOwner and Target are read by the game with TryGet
    /// (:862-864). Only Resident and HumanCurrentLane are dependable. Derivations, including the
    /// wrong turns, are in docs/SESSION-NOTES.md.
    /// </summary>
    public partial class ParkVisitorSpreadSystem : GameSystemBase, Game.Serialization.IPreSerialize
    {
        private const int kMinimumSpotCapacity = 6;
        private const int kMaximumSpotCapacity = 400;

        /// <summary>A gated park reopens below this fraction of its limit, so it does not flicker.</summary>
        private const float kReopenFraction = 0.75f;

        /// <summary>Mid-journey, aboard something, or stepping off it. Not to be disturbed.</summary>
        private const ResidentFlags kBusy =
            ResidentFlags.WaitingTransport | ResidentFlags.InVehicle | ResidentFlags.Disembarking;

        /// <summary>A search already in flight, or one that has just failed and is being handled.</summary>
        private const PathFlags kPathBusy =
            PathFlags.Pending | PathFlags.Failed | PathFlags.Obsolete | PathFlags.Stuck
            | PathFlags.Divert | PathFlags.DivertObsolete;

        private const CreatureLaneFlags kParked =
            CreatureLaneFlags.Hangaround | CreatureLaneFlags.EndReached;

        /// <summary>16 x 512 frames is 32 lines a day, the cadence TourismDiagnosticsSystem uses.</summary>
        private const int kLogEvery = 16;

        /// <summary>
        /// Counter slots. Every creature walked lands in exactly one of Settled or a Rejected slot,
        /// so they sum to Examined and one log line says where the population goes.
        /// </summary>
        private static class Counter
        {
            public const int RejectedNotParked = 0;
            public const int RejectedBusy = 1;
            public const int RejectedPathBusy = 2;
            public const int RejectedDivert = 3;
            public const int RejectedTarget = 4;
            public const int Settled = 5;
            public const int SettledOnArea = 6;
            public const int SettledFollowers = 7;
            public const int Relocated = 8;
            public const int OnFullSpots = 9;
            public const int WorstOccupancy = 10;
            public const int Examined = 11;
            public const int Length = 12;
        }

        /// <summary>Classify's verdict for a creature that qualifies.</summary>
        private const int kAccepted = Counter.Settled;

        private EntityQuery m_Query;
        private ComponentLookup<Curve> m_CurveData;

        /// <summary>Written by the jobs, read one update later so nothing ever waits on them.</summary>
        private NativeArray<int> m_Counters;
        private NativeHashMap<Entity, int> m_ParkOccupancy;

        /// <summary>Parks whose LeisureProvider tag this system has removed. The only copy.</summary>
        private readonly HashSet<Entity> m_Gated = new HashSet<Entity>();

        private int m_UpdatesSinceLog;
        private int m_BusiestPark;

        /// <summary>Citizens on a leisure trip, and citizens who searched for one and found nothing.</summary>
        private EntityQuery m_LeisureQuery;
        private EntityQuery m_CooldownQuery;
        private Game.Simulation.SimulationSystem m_Simulation;

        /// <summary>False while the feature is off or the query is empty, so silence is unambiguous.</summary>
        private bool m_Ran;

        // 262144 frames an in-game day, so 512 updates a day.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 512;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_CurveData = GetComponentLookup<Curve>(isReadOnly: true);
            m_Counters = new NativeArray<int>(Counter.Length, Allocator.Persistent);
            m_ParkOccupancy = new NativeHashMap<Entity, int>(256, Allocator.Persistent);

            m_LeisureQuery = GetEntityQuery(
                ComponentType.ReadOnly<Leisure>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
            m_CooldownQuery = GetEntityQuery(
                ComponentType.ReadOnly<LeisureSeekerCooldown>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
            m_Simulation = World.GetOrCreateSystemManaged<Game.Simulation.SimulationSystem>();

            // Only the two dependable components. GroupMember is deliberately not excluded, unlike
            // ResidentAISystem:4536, because followers are part of the crowd.
            m_Query = GetEntityQuery(
                ComponentType.ReadWrite<Game.Creatures.Resident>(),
                ComponentType.ReadOnly<HumanCurrentLane>(),
                ComponentType.Exclude<Stumbling>(),
                ComponentType.Exclude<Game.Objects.Unspawned>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnDestroy()
        {
            // The native containers are in a job's hands; freeing them under it would crash.
            CompleteDependency();
            ReopenAll();

            if (m_Counters.IsCreated)
            {
                m_Counters.Dispose();
            }

            if (m_ParkOccupancy.IsCreated)
            {
                m_ParkOccupancy.Dispose();
            }

            base.OnDestroy();
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // A new world: last session's entities mean nothing, and the save is clean because
            // PreSerialize reopened everything before it was written. The occupancy map must go
            // too — its keys name entities in the old world, and Gate reads them on the next update.
            CompleteDependency();
            m_Gated.Clear();
            m_ParkOccupancy.Clear();
        }

        /// <summary>Puts every removed tag back before the game writes the save. See the class doc.</summary>
        public void PreSerialize(Colossal.Serialization.Entities.Context context)
        {
            CompleteDependency();

            // Logged unconditionally: this is the one step whose failure would outlive the mod,
            // so it should be visible in every log that contains a save.
            Mod.Log.Info($"Park gate: reopened {m_Gated.Count} park(s) before saving.");
            ReopenAll();
        }

        protected override void OnUpdate()
        {
            Report();

            TourismOverhaulSetting settings = Mod.Settings;
            m_Ran = settings != null && settings.SpreadParkVisitors && !m_Query.IsEmptyIgnoreFilter;

            if (!m_Ran)
            {
                ReopenAll();
                return;
            }

            // Structural changes first: they invalidate type handles, so they must precede the jobs.
            Gate(math.max(0.1f, settings.MaxParkVisitorsPerCell));

            m_CurveData.Update(this);
            m_ParkOccupancy.Clear();

            // Keyed by spot, not visitor, so a few hundred entries covers a city. It grows if not.
            NativeHashMap<Entity, int> occupancy = new NativeHashMap<Entity, int>(1024, Allocator.TempJob);

            Handles handles = new Handles
            {
                m_Resident = GetComponentTypeHandle<Game.Creatures.Resident>(),
                m_Lane = GetComponentTypeHandle<HumanCurrentLane>(isReadOnly: true),
                m_Path = GetComponentTypeHandle<PathOwner>(),
                m_Target = GetComponentTypeHandle<Target>(isReadOnly: true),
                m_Divert = GetComponentTypeHandle<Divert>(isReadOnly: true),
                m_Group = GetComponentTypeHandle<GroupMember>(isReadOnly: true)
            };

            EntityStorageInfoLookup entities = GetEntityStorageInfoLookup();

            // Single-threaded: counting must finish before the policy can read it, and exact
            // budgets beat a second thread on a job that runs once every 512 frames.
            JobHandle counted = JobChunkExtensions.Schedule(
                new CountJob
                {
                    m_Handles = handles,
                    m_Entities = entities,
                    m_Occupancy = occupancy,
                    m_ParkOccupancy = m_ParkOccupancy
                },
                m_Query, Dependency);

            JobHandle applied = JobChunkExtensions.Schedule(
                new ApplyJob
                {
                    m_Handles = handles,
                    m_Entities = entities,
                    m_CurveData = m_CurveData,
                    m_Occupancy = occupancy,
                    m_Counters = m_Counters,
                    m_DriftChance = math.clamp(settings.ParkVisitorMoveChance, 0, 100),
                    m_Spacing = math.max(1f, settings.ParkVisitorSpacingMetres),
                    m_RelocateBudget = math.max(0, settings.MaxParkVisitorMovesPerUpdate),
                    m_Random = new Random(math.max(1u,
                        (uint)(World.Time.ElapsedTime * 1000d) * 747796405u + 2891336453u))
                },
                m_Query, counted);

            Dependency = occupancy.Dispose(applied);
        }

        /// <summary>
        /// Closes parks over their limit to new leisure trips and reopens ones that have thinned,
        /// from last update's counts. Parks number in the hundreds, so a main-thread pass over
        /// them every 512 frames is cheap.
        /// </summary>
        private void Gate(float perCell)
        {
            m_BusiestPark = 0;

            NativeArray<Entity> parks = m_ParkOccupancy.GetKeyArray(Allocator.Temp);

            for (int i = 0; i < parks.Length; i++)
            {
                Entity park = parks[i];
                int visitors = m_ParkOccupancy[park];
                m_BusiestPark = math.max(m_BusiestPark, visitors);

                // Cheap filters first, and existence before any component read: a key can name a
                // park bulldozed since last update, or a destination that is not a park at all.
                if (!m_Gated.Contains(park) && EntityManager.Exists(park)
                    && EntityManager.HasComponent<Game.Buildings.Park>(park)
                    && EntityManager.HasComponent<Game.Buildings.LeisureProvider>(park)
                    && visitors > Limit(park, perCell))
                {
                    EntityManager.RemoveComponent<Game.Buildings.LeisureProvider>(park);
                    m_Gated.Add(park);
                }
            }

            parks.Dispose();

            m_Gated.RemoveWhere(park =>
            {
                if (!EntityManager.Exists(park))
                {
                    return true;
                }

                m_ParkOccupancy.TryGetValue(park, out int visitors);

                if (visitors >= Limit(park, perCell) * kReopenFraction)
                {
                    return false;
                }

                EntityManager.AddComponent<Game.Buildings.LeisureProvider>(park);
                return true;
            });
        }

        /// <summary>A park's visitor limit, from its lot area — the same idiom AttractionCrowdingSystem uses.</summary>
        private int Limit(Entity park, float perCell)
        {
            int2 lot = new int2(4, 4);

            if (EntityManager.HasComponent<PrefabRef>(park))
            {
                Entity prefab = EntityManager.GetComponentData<PrefabRef>(park).m_Prefab;

                if (EntityManager.HasComponent<BuildingData>(prefab))
                {
                    lot = EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize;
                }
            }

            return math.max(kMinimumSpotCapacity, (int)(lot.x * lot.y * perCell));
        }

        /// <summary>Puts back every tag this system removed. Safe to call at any time.</summary>
        private void ReopenAll()
        {
            foreach (Entity park in m_Gated)
            {
                if (EntityManager.Exists(park)
                    && !EntityManager.HasComponent<Game.Buildings.LeisureProvider>(park))
                {
                    EntityManager.AddComponent<Game.Buildings.LeisureProvider>(park);
                }
            }

            m_Gated.Clear();
        }

        /// <summary>
        /// Logs the previous update's counters and clears them. Read one update late on purpose:
        /// completing the job in the update that scheduled it would be a stall bought for a log
        /// line, and by now 512 frames have passed.
        /// </summary>
        private void Report()
        {
            CompleteDependency();

            if (Mod.Settings != null && Mod.Settings.DiagnosticLogging
                && ++m_UpdatesSinceLog >= kLogEvery)
            {
                m_UpdatesSinceLog = 0;
                Mod.Log.Info(Summary());

                if (m_Ran)
                {
                    Mod.Log.Info(LeisureCensus());
                }
            }

            for (int i = 0; i < m_Counters.Length; i++)
            {
                m_Counters[i] = 0;
            }
        }

        /// <summary>
        /// Where leisure trips are going, by the type of the building each citizen is headed for,
        /// and how many searches came back empty. This is what shows whether a closed park's
        /// visitors went to another park, chose other leisure, or stayed home: a search that finds
        /// no open destination drops the trip and sets LeisureSeekerCooldown (LeisureSystem:414-425).
        ///
        /// Diagnostic only, and only 32 times a day, so a main-thread pass is proportionate.
        /// </summary>
        private string LeisureCensus()
        {
            ComponentLookup<PrefabRef> prefabs = GetComponentLookup<PrefabRef>(isReadOnly: true);
            ComponentLookup<LeisureProviderData> providers =
                GetComponentLookup<LeisureProviderData>(isReadOnly: true);

            NativeArray<Leisure> trips = m_LeisureQuery.ToComponentDataArray<Leisure>(Allocator.Temp);

            int[] byType = new int[(int)Game.Agents.LeisureType.Count];
            int searching = 0;
            int elsewhere = 0;
            int toClosedPark = 0;

            for (int i = 0; i < trips.Length; i++)
            {
                Entity target = trips[i].m_TargetAgent;

                if (target == Entity.Null)
                {
                    searching++;
                }
                else if (prefabs.TryGetComponent(target, out PrefabRef prefab)
                    && providers.TryGetComponent(prefab.m_Prefab, out LeisureProviderData data))
                {
                    byType[(int)data.m_LeisureType]++;
                }
                else
                {
                    // Travel and meetings target something that is not a leisure building.
                    elsewhere++;
                }

                toClosedPark += m_Gated.Contains(target) ? 1 : 0;
            }

            trips.Dispose();

            System.Text.StringBuilder line = new System.Text.StringBuilder(
                $"Leisure census: {trips.Length} on a leisure trip —");

            for (int t = 0; t < byType.Length; t++)
            {
                if (byType[t] > 0)
                {
                    line.Append($" {(Game.Agents.LeisureType)t} {byType[t]},");
                }
            }

            line.Append($" non-leisure destination {elsewhere}, still searching {searching};"
                + $" {toClosedPark} bound for a closed park (chosen before it closed);"
                + $" {ActiveCooldowns()} citizens found nowhere open in the last"
                + $" {Game.Simulation.CitizenBehaviorSystem.kLeisureSeekerCooldownFrames} frames.");

            return line.ToString();
        }

        /// <summary>
        /// Cooldowns still in force. The component is removed only when a later search succeeds
        /// (LeisureSystem:404) and never on expiry — CitizenBehaviorSystem:456 just compares frames —
        /// and it is serialized, so counting the component counts every failed search in the save's
        /// history. Only those stamped within the window are citizens actually being held back.
        /// </summary>
        private int ActiveCooldowns()
        {
            uint now = m_Simulation.frameIndex;
            uint window = Game.Simulation.CitizenBehaviorSystem.kLeisureSeekerCooldownFrames;

            NativeArray<LeisureSeekerCooldown> cooldowns =
                m_CooldownQuery.ToComponentDataArray<LeisureSeekerCooldown>(Allocator.Temp);

            int active = 0;

            for (int i = 0; i < cooldowns.Length; i++)
            {
                active += now < cooldowns[i].m_SimulationFrame + window ? 1 : 0;
            }

            cooldowns.Dispose();
            return active;
        }

        private string Summary()
        {
            if (!m_Ran)
            {
                return Mod.Settings.SpreadParkVisitors
                    ? "Park spread: idle, no resident creatures matched the query."
                    : "Park spread: switched off.";
            }

            return $"Park spread: walked {m_Counters[Counter.Examined]} creatures -> "
                + $"{m_Counters[Counter.Settled]} settled "
                + $"({m_Counters[Counter.SettledOnArea]} on lawns, "
                + $"{m_Counters[Counter.SettledFollowers]} followers); rejected "
                + $"not-parked {m_Counters[Counter.RejectedNotParked]}, "
                + $"busy {m_Counters[Counter.RejectedBusy]}, "
                + $"path {m_Counters[Counter.RejectedPathBusy]}, "
                + $"divert {m_Counters[Counter.RejectedDivert]}, "
                + $"target {m_Counters[Counter.RejectedTarget]}; "
                + $"{m_Counters[Counter.OnFullSpots]} on full spots, "
                + $"{m_Counters[Counter.Relocated]} relocated, "
                + $"busiest spot {m_Counters[Counter.WorstOccupancy]}; "
                + $"{m_Gated.Count} park(s) closed to new visitors, busiest park {m_BusiestPark}.";
        }

        /// <summary>The type handles both jobs need, so the chunk setup is written once.</summary>
        private struct Handles
        {
            public ComponentTypeHandle<Game.Creatures.Resident> m_Resident;
            public ComponentTypeHandle<HumanCurrentLane> m_Lane;
            public ComponentTypeHandle<PathOwner> m_Path;
            public ComponentTypeHandle<Target> m_Target;
            public ComponentTypeHandle<Divert> m_Divert;
            public ComponentTypeHandle<GroupMember> m_Group;

            public Crowd Read(in ArchetypeChunk chunk) => new Crowd
            {
                m_Residents = chunk.GetNativeArray(ref m_Resident),
                m_Lanes = chunk.GetNativeArray(ref m_Lane),
                m_Paths = chunk.Has(ref m_Path) ? chunk.GetNativeArray(ref m_Path) : default,
                m_Targets = chunk.Has(ref m_Target) ? chunk.GetNativeArray(ref m_Target) : default,
                m_Diverts = chunk.Has(ref m_Divert) ? chunk.GetNativeArray(ref m_Divert) : default,
                m_Groups = chunk.Has(ref m_Group) ? chunk.GetNativeArray(ref m_Group) : default
            };
        }

        /// <summary>
        /// One chunk's creatures, with the optional components resolved once. An uncreated array
        /// means the component is absent from this chunk, which reads as "nothing in the way".
        /// </summary>
        private struct Crowd
        {
            public NativeArray<Game.Creatures.Resident> m_Residents;
            public NativeArray<HumanCurrentLane> m_Lanes;
            public NativeArray<PathOwner> m_Paths;
            public NativeArray<Target> m_Targets;
            public NativeArray<Divert> m_Diverts;
            public NativeArray<GroupMember> m_Groups;

            public int Length => m_Residents.Length;

            /// <summary>
            /// Follows a leader, so cannot re-path for itself: TickGroupMemberWalking never calls
            /// FindNewPath. Its leader moving is what moves it.
            /// </summary>
            public bool IsFollower(int i) => m_Groups.IsCreated && m_Groups[i].m_Leader != Entity.Null;

            /// <summary>kAccepted, or the Counter slot naming why this creature was passed over.</summary>
            public int Classify(int i, in EntityStorageInfoLookup entities)
            {
                HumanCurrentLane lane = m_Lanes[i];

                if ((lane.m_Flags & kParked) != kParked || lane.m_Lane == Entity.Null)
                {
                    return Counter.RejectedNotParked;
                }

                if ((m_Residents[i].m_Flags & kBusy) != 0)
                {
                    return Counter.RejectedBusy;
                }

                if (m_Paths.IsCreated && (m_Paths[i].m_State & kPathBusy) != 0)
                {
                    return Counter.RejectedPathBusy;
                }

                // An errand or evacuation owns the destination while it lasts.
                if (m_Diverts.IsCreated && m_Diverts[i].m_Purpose != Game.Citizens.Purpose.None)
                {
                    return Counter.RejectedDivert;
                }

                // A bulldozed destination is ResidentAISystem's to handle. No Target at all is
                // fine — there is then nothing stale to trip over.
                if (m_Targets.IsCreated
                    && (m_Targets[i].m_Target == Entity.Null || !entities.Exists(m_Targets[i].m_Target)))
                {
                    return Counter.RejectedTarget;
                }

                return kAccepted;
            }
        }

        /// <summary>Tallies settled visitors per spot and per destination building.</summary>
        [BurstCompile]
        private struct CountJob : IJobChunk
        {
            public Handles m_Handles;
            [ReadOnly] public EntityStorageInfoLookup m_Entities;
            public NativeHashMap<Entity, int> m_Occupancy;
            public NativeHashMap<Entity, int> m_ParkOccupancy;

            public void Execute(in ArchetypeChunk chunk, int index, bool useMask, in v128 mask)
            {
                Crowd crowd = m_Handles.Read(in chunk);

                for (int i = 0; i < crowd.Length; i++)
                {
                    if (crowd.Classify(i, m_Entities) != kAccepted)
                    {
                        continue;
                    }

                    Tally(ref m_Occupancy, crowd.m_Lanes[i].m_Lane);

                    if (crowd.m_Targets.IsCreated)
                    {
                        Tally(ref m_ParkOccupancy, crowd.m_Targets[i].m_Target);
                    }
                }
            }

            private static void Tally(ref NativeHashMap<Entity, int> map, Entity key)
            {
                map.TryGetValue(key, out int running);
                map[key] = running + 1;
            }
        }

        /// <summary>Re-rolls visitors off over-full lawns, and lets settled visitors drift.</summary>
        [BurstCompile]
        private struct ApplyJob : IJobChunk
        {
            public Handles m_Handles;
            [ReadOnly] public EntityStorageInfoLookup m_Entities;
            [ReadOnly] public ComponentLookup<Curve> m_CurveData;
            [ReadOnly] public NativeHashMap<Entity, int> m_Occupancy;

            public NativeArray<int> m_Counters;

            public int m_DriftChance;
            public float m_Spacing;
            public int m_RelocateBudget;
            public Random m_Random;

            public void Execute(in ArchetypeChunk chunk, int index, bool useMask, in v128 mask)
            {
                Crowd crowd = m_Handles.Read(in chunk);

                for (int i = 0; i < crowd.Length; i++)
                {
                    m_Counters[Counter.Examined]++;

                    int verdict = crowd.Classify(i, m_Entities);

                    if (verdict != kAccepted)
                    {
                        m_Counters[verdict]++;
                        continue;
                    }

                    HumanCurrentLane lane = crowd.m_Lanes[i];
                    bool onLawn = (lane.m_Flags & CreatureLaneFlags.Area) != 0;
                    bool follower = crowd.IsFollower(i);

                    m_Counters[Counter.Settled]++;
                    m_Counters[Counter.SettledOnArea] += onLawn ? 1 : 0;
                    m_Counters[Counter.SettledFollowers] += follower ? 1 : 0;

                    m_Occupancy.TryGetValue(lane.m_Lane, out int occupants);
                    m_Counters[Counter.WorstOccupancy] =
                        math.max(m_Counters[Counter.WorstOccupancy], occupants);

                    if (follower || !crowd.m_Paths.IsCreated)
                    {
                        continue;
                    }

                    // Only lawns overfill; a bench is already capacity-limited by the game.
                    int overflow = onLawn ? occupants - Capacity(lane.m_Lane) : 0;
                    m_Counters[Counter.OnFullSpots] += overflow > 0 ? 1 : 0;

                    // On a full lawn each occupant re-rolls with probability overflow / occupants,
                    // so in expectation exactly the excess moves; otherwise an occasional drift.
                    bool move = overflow > 0
                        ? m_Random.NextInt(occupants) < overflow
                        : m_Random.NextInt(100) < m_DriftChance;

                    if (move)
                    {
                        Relocate(ref crowd, i);
                    }
                }
            }

            /// <summary>
            /// How many visitors a lawn holds, from its connection lane's length squared over a
            /// spacing figure squared: the lane runs across the triangle pair it was generated from
            /// (AreaConnectionSystem:309-347), so its length is the patch's size.
            /// </summary>
            private int Capacity(Entity spot) =>
                m_CurveData.TryGetComponent(spot, out Curve curve)
                    ? math.clamp((int)(curve.m_Length * curve.m_Length / (m_Spacing * m_Spacing)),
                        kMinimumSpotCapacity, kMaximumSpotCapacity)
                    : kMinimumSpotCapacity;

            /// <summary>
            /// Re-rolls which spot in the same park the visitor takes, within this update's search
            /// budget. Clearing CannotIgnore with the two ignore flags reopens every spot; without
            /// it ReachTarget's bench-then-lawn alternation can pin the visitor in place.
            /// </summary>
            private void Relocate(ref Crowd crowd, int i)
            {
                if (m_Counters[Counter.Relocated] >= m_RelocateBudget)
                {
                    return;
                }

                Game.Creatures.Resident resident = crowd.m_Residents[i];
                resident.m_Flags &= ~(ResidentFlags.IgnoreBenches | ResidentFlags.IgnoreAreas
                    | ResidentFlags.CannotIgnore);
                crowd.m_Residents[i] = resident;

                // The native re-path verbatim (ReachTarget:2241-2242).
                PathOwner path = crowd.m_Paths[i];
                path.m_State &= ~PathFlags.Failed;
                path.m_State |= PathFlags.Obsolete;
                crowd.m_Paths[i] = path;

                m_Counters[Counter.Relocated]++;
            }
        }
    }
}
