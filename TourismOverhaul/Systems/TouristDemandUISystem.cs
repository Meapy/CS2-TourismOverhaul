using System.Collections.Generic;
using Colossal.Serialization.Entities;
using Colossal.UI.Binding;
using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Simulation;
using Game.Tools;
using Game.UI;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Publishes tourist demand as a seventh demand bar, in the shape the game's own six use.
    ///
    /// Modelled on CityInfoUISystem rather than folded into TourismPanelUISystem: that system runs
    /// at interval 512, around eight seconds of real play, and a demand bar stepping every eight
    /// seconds reads as broken. This one takes the default UI interval and advances the smoothed
    /// value from the frame delta, which is what the native bars do.
    ///
    /// See docs/DEMAND-UI-PLAN.md for the trace behind the numbers.
    /// </summary>
    public partial class TouristDemandUISystem : UISystemBase
    {
        private const string kGroup = "tourismOverhaul";

        /// <summary>
        /// Factor keys, matching the locale entries in LocaleEN and Translations.
        ///
        /// Deliberately not Game.Simulation.DemandFactor. That enum has a TouristDemand member
        /// which the game declares and never writes — see the plan document — but its name would
        /// render through the game's own localisation as a label we do not control, and only one
        /// of these factors has a native counterpart at all.
        /// </summary>
        private const string kFactorNoRooms = "NoRooms";
        private const string kFactorAttractiveness = "Attractiveness";
        private const string kFactorEmptyRooms = "EmptyRooms";
        private const string kFactorAtCeiling = "AtCeiling";
        private const string kFactorConnections = "Connections";

        /// <summary>
        /// People a free hotel room can sleep.
        ///
        /// Rooms are booked per household, not per person (HotelReserveJob:182-189), so this is the
        /// average party size — measured at roughly 2.3 across a mature city, and a property of how
        /// the game composes tourist households rather than anything this mod chooses.
        /// </summary>
        private const float kPeoplePerRoom = 2.3f;

        private ValueBinding<float> m_Demand;
        private RawValueBinding m_Factors;

        private TouristDemandSystem m_DemandSystem;
        private SimulationSystem m_SimulationSystem;
        private EntityQuery m_HotelQuery;

        private UIUpdateState m_UpdateState;

        private float m_Smoothed;
        private uint m_LastFrameIndex;

        /// <summary>
        /// Free and total hotel rooms, refreshed on the factor cadence rather than per frame.
        ///
        /// Demand advances every frame and now depends on the free-room count, but counting them
        /// walks every lodging chunk — far too much to repeat at UI frequency. Rooms change on the
        /// scale of buildings being built and guests checking in, so a value up to 256 ticks old is
        /// well inside what the smoothing would absorb anyway.
        /// </summary>
        private int m_RoomsFree;
        private int m_RoomsTotal;

        /// <summary>
        /// Set on load so the first update snaps to the current value instead of animating up from
        /// zero. Native serializes the smoothed figure to achieve the same thing; we avoid adding a
        /// serialized field to a mod whose save format has already broken once, and the visible
        /// difference is a single frame.
        /// </summary>
        private bool m_SnapNext = true;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_DemandSystem = World.GetOrCreateSystemManaged<TouristDemandSystem>();
            m_SimulationSystem = World.GetOrCreateSystemManaged<SimulationSystem>();

            // Mirrors TourismPanelUISystem.m_HotelQuery — lodging companies renting a property,
            // which is what LodgingProviderSystem maintains m_FreeRooms for.
            m_HotelQuery = GetEntityQuery(
                ComponentType.ReadOnly<LodgingProvider>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.ReadOnly<Renter>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            AddBinding(m_Demand = new ValueBinding<float>(kGroup, "touristDemand", 0f));
            AddBinding(m_Factors = new RawValueBinding(kGroup, "touristDemandFactors", WriteFactors));

            // 256 matches CityInfoUISystem:117. The bar moves every frame; the text under it stays
            // still long enough to read.
            m_UpdateState = UIUpdateState.Create(World, 256);
        }

        protected override void OnGameLoaded(Context serializationContext)
        {
            base.OnGameLoaded(serializationContext);

            m_SnapNext = true;
            m_LastFrameIndex = 0u;
            m_UpdateState.ForceUpdate();
        }

        protected override void OnUpdate()
        {
            base.OnUpdate();

            if (m_DemandSystem == null || m_SimulationSystem == null)
            {
                return;
            }

            uint delta = m_SimulationSystem.frameIndex - m_LastFrameIndex;

            // Refreshed before the first demand figure is computed, and thereafter on the factor
            // cadence, so ComputeDemand never walks the lodging chunks itself.
            if (m_SnapNext)
            {
                CountRooms(out m_RoomsFree, out m_RoomsTotal);
            }

            if (delta != 0)
            {
                m_LastFrameIndex = m_SimulationSystem.frameIndex;

                int target = ComputeDemand();

                m_Smoothed = m_SnapNext
                    ? math.saturate(target / 100f)
                    : AdvanceSmoothDemand(m_Smoothed, target, delta);

                m_SnapNext = false;

                // Snapped to the same 0.001 the native bars publish at, so an idle city stops
                // pushing binding updates the frontend cannot render anyway.
                m_Demand.Update(Colossal.Mathematics.MathUtils.Snap(m_Smoothed, 0.001f));
            }

            if (m_UpdateState.Advance())
            {
                CountRooms(out m_RoomsFree, out m_RoomsTotal);
                m_Factors.Update();
            }
        }

        /// <summary>
        /// Copied verbatim from CityInfoUISystem:225-228. The asymmetry is the point: demand falls
        /// five times faster than it rises, and matching that is most of what makes the bar feel
        /// like it belongs beside the other six.
        /// </summary>
        private static float AdvanceSmoothDemand(float current, int target, uint delta)
        {
            return math.clamp(
                target / 100f,
                current - 0.000625f * delta,
                current + 0.000125f * delta);
        }

        /// <summary>
        /// Demand for more lodging: visitors who would come and have nowhere to sleep.
        ///
        /// Rooms standing empty subtract from it directly. An earlier version measured unmet
        /// appetite alone, which read high while over half the rooms in the city were vacant —
        /// telling the player to build when building was the one thing that would not help. Free
        /// rooms belonged in the number, not only in the factor list beside it.
        ///
        /// IntrinsicTarget rather than TargetTourists throughout, because TargetTourists is itself
        /// capped by lodging: using it would make demand collapse the moment hotels filled, which
        /// is the opposite error.
        ///
        /// So the bar is at its highest when appetite is high and every room is taken, and at zero
        /// once there is a room waiting for everyone who would come.
        /// </summary>
        private int ComputeDemand()
        {
            int ceiling = math.max(0, m_DemandSystem.IntrinsicTarget);

            if (ceiling == 0)
            {
                return 0;
            }

            int appetite = math.max(0, ceiling - m_DemandSystem.CurrentTourists);

            int unhoused = math.max(0, appetite - SleepingSpaceFree());

            return math.clamp(unhoused * 100 / ceiling, 0, 100);
        }

        /// <summary>
        /// Free rooms expressed as the people they can sleep, which is what the demand is in.
        ///
        /// A room is not a person. HotelReserveJob:182-189 decrements m_FreeRooms once per
        /// *household*, so a family of four occupies one room — the trap the notes already record
        /// for hotel occupancy and for HotelRoomsPerTourist, and the demand figure was making it a
        /// third time. Appetite is a head count; subtracting a room count straight off it
        /// undercounted vacancy by the average party size, which is why the bar still read high with
        /// half the city's rooms standing empty.
        ///
        /// The conversion is the same ratio those notes measured — tourists divided by occupied
        /// rooms lands around 2.3, and it is a property of how the game builds households rather
        /// than anything this mod sets. Deliberately not derived live from current occupancy: that
        /// figure is undefined when nothing is occupied, and would make the bar jitter with party
        /// size rather than with vacancy.
        /// </summary>
        private int SleepingSpaceFree()
        {
            return (int)math.round(m_RoomsFree * kPeoplePerRoom);
        }

        /// <summary>
        /// Writes the +/- list, in the game's own format: zero weights dropped, sorted by absolute
        /// weight descending, as FactorInfo.CompareTo does.
        ///
        /// These weights are indicative shares rather than an exact decomposition of the demand
        /// figure. Saying so is better than implying a precision the numbers do not have.
        /// </summary>
        private void WriteFactors(IJsonWriter writer)
        {
            List<KeyValuePair<string, int>> factors = CollectFactors();

            factors.Sort((a, b) =>
            {
                int byWeight = math.abs(b.Value).CompareTo(math.abs(a.Value));
                return byWeight != 0 ? byWeight : string.CompareOrdinal(a.Key, b.Key);
            });

            // Five, as CityInfoUISystem:284 does. The panel has room for that many and the sort
            // has already put the ones worth reading at the top.
            int count = math.min(5, factors.Count);

            writer.ArrayBegin(count);

            for (int i = 0; i < count; i++)
            {
                // The native type name, not ours. The frontend's factor row is handed
                // {"__Type":"Game.UI.InGame.FactorInfo","factor":...,"weight":...} and a different
                // type name makes the payload something it does not recognise.
                writer.TypeBegin("Game.UI.InGame.FactorInfo");
                writer.PropertyName("factor");
                writer.Write(factors[i].Key);
                writer.PropertyName("weight");
                writer.Write(factors[i].Value);
                writer.TypeEnd();
            }

            writer.ArrayEnd();
        }

        private List<KeyValuePair<string, int>> CollectFactors()
        {
            List<KeyValuePair<string, int>> factors = new List<KeyValuePair<string, int>>(5);

            if (m_DemandSystem == null)
            {
                return factors;
            }

            int ceiling = math.max(0, m_DemandSystem.IntrinsicTarget);
            int current = math.max(0, m_DemandSystem.CurrentTourists);
            int appetite = math.max(0, ceiling - current);

            // The same counts the demand figure was built from, so the bar and the factors beside
            // it cannot describe different states.
            int roomsFree = m_RoomsFree;
            int roomsTotal = m_RoomsTotal;

            if (ceiling == 0)
            {
                // Nothing worth visiting yet, so there is exactly one thing to say.
                factors.Add(new KeyValuePair<string, int>(kFactorAttractiveness, -100));
                return factors;
            }

            // Visitors who would come and have nowhere to sleep. The actionable one: this is what
            // painting a Hotels or Motels zone fixes.
            int unhoused = math.max(0, appetite - SleepingSpaceFree());

            if (unhoused > 0)
            {
                factors.Add(new KeyValuePair<string, int>(
                    kFactorNoRooms,
                    math.clamp(unhoused * 100 / math.max(1, appetite), 1, 100)));
            }

            // Rooms standing empty, weighted by vacancy rate.
            //
            // This used to appear only when free rooms exceeded total appetite, which meant a city
            // at 42% occupancy showed nothing at all — even though every one of those empty rooms
            // was subtracting from the demand figure. Vacancy is a continuous pressure, so it is
            // reported continuously, and it can now sit alongside a shortage rather than being
            // mutually exclusive with it: some visitors having nowhere to stay and other rooms
            // going unused are both true at once, and both worth seeing.
            if (roomsFree > 0 && roomsTotal > 0)
            {
                factors.Add(new KeyValuePair<string, int>(
                    kFactorEmptyRooms,
                    -math.clamp(roomsFree * 100 / roomsTotal, 1, 100)));
            }

            // Headroom the city's appeal is generating, against how much of it is already taken up.
            if (appetite > 0)
            {
                factors.Add(new KeyValuePair<string, int>(
                    kFactorAttractiveness,
                    math.clamp(appetite * 100 / ceiling, 1, 100)));
            }

            // Only once visitors are genuinely damping demand. Below half the ceiling this is the
            // exact inverse of the attractiveness weight above — the two always sum to 100 — so
            // showing both everywhere would fill a row with no information in it.
            int taken = current * 100 / ceiling;

            if (taken >= 50)
            {
                factors.Add(new KeyValuePair<string, int>(
                    kFactorAtCeiling, -math.clamp(taken, 1, 100)));
            }

            // Whether the ways into the city are carrying enough traffic to close the gap. Arrivals
            // are a monthly flow and appetite is a population, so this compares like with like only
            // loosely - it is a direction, not a rate.
            int4 arrivals = m_DemandSystem.MonthlyArrivalsByMode;
            int monthlyArrivals = arrivals.x + arrivals.y + arrivals.z + arrivals.w;

            if (appetite > 0)
            {
                int coverage = math.clamp(monthlyArrivals * 100 / appetite, 0, 200);

                // Below half the deficit reads as a way-in problem; well above it reads as the
                // connections doing their part.
                if (coverage < 50)
                {
                    factors.Add(new KeyValuePair<string, int>(
                        kFactorConnections, -math.max(1, 50 - coverage)));
                }
                else if (coverage > 100)
                {
                    factors.Add(new KeyValuePair<string, int>(
                        kFactorConnections, math.min(50, coverage - 100)));
                }
            }

            return factors;
        }

        /// <summary>
        /// Free and total hotel rooms. Same shape as TourismPanelUISystem.CountRooms so the two
        /// panels cannot disagree: total is free rooms plus current renters, because
        /// LodgingProvider tracks only the free count.
        /// </summary>
        private void CountRooms(out int free, out int total)
        {
            free = 0;
            total = 0;

            ComponentTypeHandle<LodgingProvider> providerHandle =
                GetComponentTypeHandle<LodgingProvider>(isReadOnly: true);
            BufferTypeHandle<Renter> renterHandle = GetBufferTypeHandle<Renter>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks =
                m_HotelQuery.ToArchetypeChunkArray(Allocator.Temp);

            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];
                    NativeArray<LodgingProvider> providers = chunk.GetNativeArray(ref providerHandle);
                    BufferAccessor<Renter> renters = chunk.GetBufferAccessor(ref renterHandle);

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        int freeRooms = math.max(0, providers[i].m_FreeRooms);

                        free += freeRooms;
                        total += freeRooms + renters[i].Length;
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }
    }
}
