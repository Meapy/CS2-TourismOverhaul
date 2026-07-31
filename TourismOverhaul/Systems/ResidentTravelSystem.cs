using Game;
using Game.Agents;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Events;
using Game.Simulation;
using Game.Tools;
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

        protected override void OnUpdate()
        {
            CitizensAway = CountCitizensAway();

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

            SendHouseholdsAway(departures, settings.HolidayMinimumSavings);
        }

        /// <summary>
        /// Citizens whose current travel purpose is a trip out of the city. This is the same
        /// purpose LeisureSystem assigns for LeisureType.Travel.
        /// </summary>
        private int CountCitizensAway()
        {
            int away = 0;

            ComponentTypeHandle<TravelPurpose> purposeHandle =
                GetComponentTypeHandle<TravelPurpose>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks =
                m_TravellingCitizenQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    NativeArray<TravelPurpose> purposes = chunk.GetNativeArray(ref purposeHandle);

                    for (int i = 0; i < purposes.Length; i++)
                    {
                        if (purposes[i].m_Purpose == Purpose.Traveling)
                        {
                            away++;
                        }
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }

            return away;
        }

        private void SendHouseholdsAway(int count, int minimumSavings)
        {
            NativeArray<Entity> households = m_EligibleHouseholdQuery.ToEntityArray(Allocator.Temp);
            try
            {
                if (households.Length == 0)
                {
                    return;
                }

                Random random = new Random(math.max(1u, m_SimulationSystem.frameIndex * 1664525u + 1013904223u));

                NativeQueue<AddMeetingSystem.AddMeeting> queue =
                    m_AddMeetingSystem.GetMeetingQueue(out JobHandle deps);
                deps.Complete();

                int sent = 0;
                int attempts = 0;
                int maxAttempts = count * 8;

                while (sent < count && attempts < maxAttempts)
                {
                    attempts++;

                    Entity household = households[random.NextInt(households.Length)];

                    if (!EntityManager.HasBuffer<Game.Economy.Resources>(household))
                    {
                        continue;
                    }

                    // Mirrors vanilla's intent that only households with spare money travel.
                    int money = EconomyUtils.GetResources(
                        Resource.Money, EntityManager.GetBuffer<Game.Economy.Resources>(household, true));

                    if (money < minimumSavings)
                    {
                        continue;
                    }

                    queue.Enqueue(new AddMeetingSystem.AddMeeting
                    {
                        m_Household = household,
                        m_Type = Game.Agents.LeisureType.Travel
                    });

                    sent++;
                }

                m_AddMeetingSystem.AddWriter(default(JobHandle));

                TripsSent += sent;
            }
            finally
            {
                households.Dispose();
            }
        }
    }
}
