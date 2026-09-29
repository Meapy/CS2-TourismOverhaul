using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Events;
using Game.Simulation;
using Game.Tools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Sends resident households out of the city on holiday more often than vanilla does.
    ///
    /// Vanilla behaviour (traced): LeisureSystem.SelectLeisureType picks from ten leisure types by
    /// weight, and LeisureType.Travel is heavily suppressed (LeisureSystem.cs:505-524):
    ///   - its scale factor is 1f while every other selectable type uses 10f;
    ///   - its wealth gate is the strictest in the game, smoothstep(0.5, 1, (wealth+5000)/10000),
    ///     so households with no spendable money never travel at all;
    ///   - CitizenBehaviorSystem.cs:465-470 additionally cancels most leisure outright in large
    ///     cities via min(80, 10000/sqrt(population)).
    /// Net result is roughly 1-3% of leisure trips, and only for well-off households.
    ///
    /// GetWeight is a private method inside a Burst job, so the weight itself cannot be changed.
    /// Instead this complements the native path: it enqueues exactly the same
    /// AddMeetingSystem.AddMeeting{ m_Type = LeisureType.Travel } that
    /// LeisureSystem.FindLeisure enqueues (LeisureSystem.cs:567-571). From there the game's own
    /// machinery takes over — the coordinated meeting resolves to Purpose.Traveling and
    /// CitizenBehaviorSystem.GoToOutsideConnection routes the household to a real outside
    /// connection (CitizenBehaviorSystem.cs:543-546, :370-409).
    ///
    /// Nothing is patched or disabled; native travel continues to happen on top of this.
    /// </summary>
    public partial class ResidentTravelSystem : GameSystemBase
    {
        /// <summary>Simulation frames in one in-game day.</summary>
        private const float kFramesPerDay = 262144f;

        private EntityQuery m_EligibleHouseholdQuery;
        private EntityQuery m_TravellingCitizenQuery;

        private AddMeetingSystem m_AddMeetingSystem;
        private SimulationSystem m_SimulationSystem;

        /// <summary>Fractional carry so low rates still produce trips over time.</summary>
        private float m_Accumulator;

        private BufferLookup<Game.Economy.Resources> m_Resources;

        /// <summary>Written by the last update's jobs: [0] citizens away, [1] households sent.</summary>
        private NativeArray<int> m_Results;
        private JobHandle m_LastJob;
        private bool m_CountPending;

        /// <summary>Citizens currently away from the city on holiday.</summary>
        public int CitizensAway { get; private set; }

        /// <summary>Households sent away by this system since load. For diagnostics.</summary>
        public int TripsSent { get; private set; }

        // 262144 / 512 = 512 updates per in-game day.
        private const int kUpdatesPerDay = 512;

        public override int GetUpdateInterval(SystemUpdatePhase phase) => (int)(kFramesPerDay / kUpdatesPerDay);

        protected override void OnCreate()
        {
            base.OnCreate();

            m_AddMeetingSystem = World.GetOrCreateSystemManaged<AddMeetingSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();
            m_Resources = GetBufferLookup<Game.Economy.Resources>(isReadOnly: true);
            m_Results = new NativeArray<int>(2, Allocator.Persistent);

            // Resident households only: moved in, not visitors, not already at an event.
            m_EligibleHouseholdQuery = GetEntityQuery(
                ComponentType.ReadOnly<Household>(),
                ComponentType.ReadOnly<HouseholdCitizen>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.ReadOnly<Game.Economy.Resources>(),
                ComponentType.Exclude<TouristHousehold>(),
                ComponentType.Exclude<CommuterHousehold>(),
                ComponentType.Exclude<AttendingEvent>(),
                ComponentType.Exclude<MovingAway>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_TravellingCitizenQuery = GetEntityQuery(
                ComponentType.ReadOnly<TravelPurpose>(),
                ComponentType.ReadOnly<HouseholdMember>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            RequireForUpdate(m_EligibleHouseholdQuery);
        }

        protected override void OnDestroy()
        {
            m_LastJob.Complete();
            m_Results.Dispose();
            base.OnDestroy();
        }

        /// <summary>
        /// Both passes run as Burst jobs after the jobs that write what they read; on the main thread
        /// they waited for every writer of TravelPurpose and Resources first (58 ms per update, up to
        /// 153 ms). Their counts are collected on the next update, 512 frames later.
        /// </summary>
        protected override void OnUpdate()
        {
            m_LastJob.Complete();

            if (m_CountPending)
            {
                CitizensAway = m_Results[0];
                TripsSent += m_Results[1];
            }

            m_Results[0] = 0;
            m_Results[1] = 0;

            JobHandle job = new CountAwayJob
            {
                m_PurposeType = GetComponentTypeHandle<TravelPurpose>(isReadOnly: true),
                m_Results = m_Results,
            }.Schedule(m_TravellingCitizenQuery, Dependency);

            m_CountPending = true;
            m_LastJob = job;
            Dependency = job;

            TourismOverhaulSetting settings = Mod.Settings;
            if (settings == null || !settings.EnableResidentHolidays)
            {
                m_Accumulator = 0f;
                return;
            }

            int rate = math.max(0, settings.HolidayTripsPerThousandHouseholds);
            if (rate == 0)
            {
                return;
            }

            int eligible = m_EligibleHouseholdQuery.CalculateEntityCount();
            if (eligible == 0)
            {
                return;
            }

            // rate is trips per 1000 households per in-game day.
            m_Accumulator += eligible / 1000f * rate / kUpdatesPerDay;

            int departures = (int)m_Accumulator;
            if (departures <= 0)
            {
                return;
            }

            m_Accumulator -= departures;

            // Keep a single tick from flooding the pathfinder.
            departures = math.min(departures, 32);

            m_Resources.Update(this);

            NativeList<Entity> households =
                m_EligibleHouseholdQuery.ToEntityListAsync(Allocator.TempJob, out JobHandle listed);
            NativeQueue<AddMeetingSystem.AddMeeting> meetings =
                m_AddMeetingSystem.GetMeetingQueue(out JobHandle meetingDeps);

            job = new SendAwayJob
            {
                m_Households = households,
                m_Resources = m_Resources,
                m_Meetings = meetings,
                m_Results = m_Results,
                m_Random = new Random(math.max(1u, m_SimulationSystem.frameIndex * 1664525u + 1013904223u)),
                m_Count = departures,
                m_MinimumSavings = settings.HolidayMinimumSavings,
            }.Schedule(JobHandle.CombineDependencies(job, listed, meetingDeps));

            households.Dispose(job);
            m_AddMeetingSystem.AddWriter(job);
            m_LastJob = job;
            Dependency = job;
        }

        /// <summary>
        /// Counts citizens whose current travel purpose is a trip out of the city — the same purpose
        /// LeisureSystem assigns for LeisureType.Travel — into m_Results[0], for the next update.
        /// </summary>
        [BurstCompile]
        private struct CountAwayJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<TravelPurpose> m_PurposeType;
            public NativeArray<int> m_Results;

            public void Execute(
                in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<TravelPurpose> purposes = chunk.GetNativeArray(ref m_PurposeType);

                for (int i = 0; i < purposes.Length; i++)
                {
                    if (purposes[i].m_Purpose == Purpose.Traveling)
                    {
                        m_Results[0]++;
                    }
                }
            }
        }

        /// <summary>
        /// Picks households at random and sends the ones with spare money on holiday, counting them
        /// into m_Results[1]. A job because reading a household's money on the main thread waited
        /// for every job that touches any Resources buffer.
        /// </summary>
        [BurstCompile]
        private struct SendAwayJob : IJob
        {
            [ReadOnly] public NativeList<Entity> m_Households;
            [ReadOnly] public BufferLookup<Game.Economy.Resources> m_Resources;
            public NativeQueue<AddMeetingSystem.AddMeeting> m_Meetings;
            public NativeArray<int> m_Results;
            public Random m_Random;
            public int m_Count;
            public int m_MinimumSavings;

            public void Execute()
            {
                if (m_Households.Length == 0)
                {
                    return;
                }

                int sent = 0;
                int attempts = 0;
                int maxAttempts = m_Count * 8;

                while (sent < m_Count && attempts < maxAttempts)
                {
                    attempts++;

                    Entity household = m_Households[m_Random.NextInt(m_Households.Length)];

                    if (!m_Resources.TryGetBuffer(household, out DynamicBuffer<Game.Economy.Resources> resources))
                    {
                        continue;
                    }

                    // Mirrors vanilla's intent that only households with spare money travel.
                    if (EconomyUtils.GetResources(Resource.Money, resources) < m_MinimumSavings)
                    {
                        continue;
                    }

                    m_Meetings.Enqueue(new AddMeetingSystem.AddMeeting
                    {
                        m_Household = household,
                        m_Type = Game.Agents.LeisureType.Travel
                    });

                    sent++;
                }

                m_Results[1] += sent;
            }
        }
    }
}
