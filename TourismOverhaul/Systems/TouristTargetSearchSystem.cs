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
using Unity.Collections;
using Unity.Entities;
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

        /// <summary>Households processed per update, so a large backlog cannot stall a frame.</summary>
        private const int kMaxPerUpdate = 512;

        private EntityQuery m_SeekerQuery;
        private ComponentTypeSet m_PathfindTypes;

        private EndFrameBarrier m_EndFrameBarrier;
        private PathfindSetupSystem m_PathfindSetupSystem;
        private TouristFindTargetSystem m_NativeSystem;

        /// <summary>Attempts made per household, so retries can be bounded.</summary>
        private NativeHashMap<Entity, int> m_Attempts;

        private bool m_NativeDisabled;

        /// <summary>Rebuilt once per update, not once per household.</summary>
        private BufferLookup<Game.Vehicles.OwnedVehicle> m_OwnedVehicles;

        /// <summary>Targets found since load. For diagnostics.</summary>
        public int TargetsFound { get; private set; }

        /// <summary>Households evicted after exhausting their retries. For diagnostics.</summary>
        public int Evictions { get; private set; }

        // The native system runs every 16 frames as a Burst-compiled parallel job. This one is
        // managed and single-threaded, so the same cadence costs far more: a full ToEntityArray
        // over every seeker in the city, sixteen times per 256 frames, to process at most 512.
        //
        // 64 still clears 2,048 households per 256 frames, which is well above the arrival rate any
        // city sustains, and cuts the scan cost to a quarter. A tourist waits a few frames longer
        // for a destination and nothing else changes.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();
            m_PathfindSetupSystem = World.GetOrCreateSystemManaged<PathfindSetupSystem>();
            m_NativeSystem = World.GetOrCreateSystemManaged<TouristFindTargetSystem>();

            m_PathfindTypes = new ComponentTypeSet(ComponentType.ReadWrite<PathInformation>());
            m_Attempts = new NativeHashMap<Entity, int>(1024, Allocator.Persistent);

            m_SeekerQuery = GetEntityQuery(
                ComponentType.ReadWrite<TouristHousehold>(),
                ComponentType.ReadWrite<LodgingSeeker>(),
                ComponentType.Exclude<Target>(),
                ComponentType.Exclude<MovingAway>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnDestroy()
        {
            if (m_Attempts.IsCreated)
            {
                m_Attempts.Dispose();
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

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();
            NativeQueue<SetupQueueItem> pathfindQueue = m_PathfindSetupSystem.GetQueue(this, 64);

            // Built once per update rather than once per household. This was inside RequestPath,
            // which meant constructing a lookup over every owned-vehicle buffer in the city for
            // every single tourist processed — up to 512 times an update, for a value that does
            // not change within the update.
            m_OwnedVehicles = GetBufferLookup<Game.Vehicles.OwnedVehicle>(isReadOnly: true);

            NativeArray<Entity> seekers = m_SeekerQuery.ToEntityArray(Allocator.Temp);

            try
            {
                int processed = 0;

                for (int i = 0; i < seekers.Length && processed < kMaxPerUpdate; i++)
                {
                    if (ProcessSeeker(seekers[i], commandBuffer, pathfindQueue))
                    {
                        processed++;
                    }
                }
            }
            finally
            {
                seekers.Dispose();
            }

            m_PathfindSetupSystem.AddQueueWriter(Dependency);
        }

        /// <returns>True when the household consumed a slot this update.</returns>
        private bool ProcessSeeker(
            Entity household, EntityCommandBuffer commandBuffer, NativeQueue<SetupQueueItem> queue)
        {
            if (!EntityManager.HasComponent<PathInformation>(household))
            {
                RequestPath(household, commandBuffer, queue);
                return true;
            }

            PathInformation path = EntityManager.GetComponentData<PathInformation>(household);

            // Still searching.
            if ((path.m_State & PathFlags.Pending) != 0)
            {
                return false;
            }

            if (path.m_Destination != Entity.Null)
            {
                AcceptTarget(household, path.m_Destination, commandBuffer);
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
                commandBuffer.RemoveComponent<PathInformation>(household);
                return true;
            }

            m_Attempts.Remove(household);
            Evictions++;

            CitizenUtils.HouseholdMoveAway(commandBuffer, household, MoveAwayReason.TouristNoTarget);

            return true;
        }

        /// <summary>
        /// Issues the pathfind request, identical to the native one but with a usable origin radius.
        /// </summary>
        private void RequestPath(
            Entity household, EntityCommandBuffer commandBuffer, NativeQueue<SetupQueueItem> queue)
        {
            commandBuffer.AddComponent(household, in m_PathfindTypes);
            commandBuffer.SetComponent(household, new PathInformation { m_State = PathFlags.Pending });

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

            if (EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                DynamicBuffer<HouseholdCitizen> citizens =
                    EntityManager.GetBuffer<HouseholdCitizen>(household, isReadOnly: true);

                for (int i = 0; i < citizens.Length; i++)
                {
                    if (EntityManager.HasComponent<CurrentBuilding>(citizens[i].m_Citizen))
                    {
                        location = EntityManager.GetComponentData<CurrentBuilding>(citizens[i].m_Citizen)
                            .m_CurrentBuilding;
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

            queue.Enqueue(new SetupQueueItem(household, parameters, origin, destination));
        }

        /// <summary>
        /// Books the room or heads for the attraction. Mirrors the native HotelReserveJob
        /// (TouristFindTargetSystem.cs:170-199) so lodging behaves exactly as before.
        /// </summary>
        private void AcceptTarget(Entity household, Entity destination, EntityCommandBuffer commandBuffer)
        {
            m_Attempts.Remove(household);
            TargetsFound++;

            Entity hotel = Entity.Null;

            if (EntityManager.HasBuffer<Renter>(destination))
            {
                DynamicBuffer<Renter> renters =
                    EntityManager.GetBuffer<Renter>(destination, isReadOnly: true);

                if (renters.Length > 0 && EntityManager.HasComponent<LodgingProvider>(renters[0].m_Renter))
                {
                    hotel = renters[0].m_Renter;
                }
            }

            if (hotel != Entity.Null && EntityManager.HasComponent<TouristHousehold>(household))
            {
                LodgingProvider provider = EntityManager.GetComponentData<LodgingProvider>(hotel);

                if (provider.m_FreeRooms > 0)
                {
                    provider.m_FreeRooms--;
                    EntityManager.SetComponentData(hotel, provider);

                    EntityManager.GetBuffer<Renter>(hotel).Add(new Renter { m_Renter = household });

                    TouristHousehold tourist = EntityManager.GetComponentData<TouristHousehold>(household);
                    tourist.m_Hotel = hotel;
                    EntityManager.SetComponentData(household, tourist);

                    commandBuffer.RemoveComponent<LodgingSeeker>(household);
                }
            }

            commandBuffer.AddComponent(household, new Target(destination));
        }
    }
}
