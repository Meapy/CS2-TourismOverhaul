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
    /// Everything else mirrors the native system exactly, including the hotel reservation, so
    /// booking behaviour is unchanged. Turning the setting off restores the native system at
    /// runtime.
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

        /// <summary>Written by the last update's job: [0] targets found, [1] evictions.</summary>
        private NativeArray<int> m_Counts;
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
            m_Counts = new NativeArray<int>(2, Allocator.Persistent);

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
            TargetsFound += m_Counts[0];
            Evictions += m_Counts[1];
            m_Counts[0] = 0;
            m_Counts[1] = 0;

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

            JobHandle job = new SearchJob
            {
                m_Seekers = seekers,
                m_PathInformation = m_PathInformation,
                m_HouseholdCitizens = m_HouseholdCitizens,
                m_CurrentBuildings = m_CurrentBuildings,
                m_OwnedVehicles = m_OwnedVehicles,
                m_Renters = m_Renters,
                m_LodgingProviders = m_LodgingProviders,
                m_TouristHouseholds = m_TouristHouseholds,
                m_Attempts = m_Attempts,
                m_Counts = m_Counts,
                m_PathfindTypes = m_PathfindTypes,
                m_CommandBuffer = m_EndFrameBarrier.CreateCommandBuffer(),
                m_PathfindQueue = m_PathfindSetupSystem.GetQueue(this, 64),
            }.Schedule(JobHandle.CombineDependencies(Dependency, listed));

            seekers.Dispose(job);
            m_PathfindSetupSystem.AddQueueWriter(job);
            m_EndFrameBarrier.AddJobHandleForProducer(job);
            m_LastJob = job;
            Dependency = job;
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
            [ReadOnly] public ComponentLookup<PathInformation> m_PathInformation;
            [ReadOnly] public BufferLookup<HouseholdCitizen> m_HouseholdCitizens;
            [ReadOnly] public ComponentLookup<CurrentBuilding> m_CurrentBuildings;
            [ReadOnly] public BufferLookup<Game.Vehicles.OwnedVehicle> m_OwnedVehicles;
            public BufferLookup<Renter> m_Renters;
            public ComponentLookup<LodgingProvider> m_LodgingProviders;
            public ComponentLookup<TouristHousehold> m_TouristHouseholds;
            public NativeHashMap<Entity, int> m_Attempts;

            /// <summary>[0] targets found, [1] evictions.</summary>
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

            /// <returns>True when the household consumed a slot this update.</returns>
            private bool ProcessSeeker(Entity household)
            {
                if (!m_PathInformation.TryGetComponent(household, out PathInformation path))
                {
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
                m_Counts[1]++;

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
            private void AcceptTarget(Entity household, Entity destination)
            {
                m_Attempts.Remove(household);
                m_Counts[0]++;

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
