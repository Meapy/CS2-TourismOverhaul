using Game;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Tools;
using Game.Vehicles;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Tracks where tourist money goes, split by what it was spent on.
    ///
    /// HOW THIS WORKS, AND WHY IT IS INFERRED
    ///
    /// Money leaves a tourist household through systems this mod does not own and cannot hook:
    /// LodgingProviderSystem charges for the room, ResourceBuyerSystem settles shopping,
    /// ResidentAISystem.GetTicketPrice takes transport fares, LeisureSystem spends at venues. None
    /// of them announces what it took or why.
    ///
    /// So this samples each household's wallet, and when the balance falls it attributes the drop to
    /// whatever that household was doing at the time:
    ///
    ///   holds a hotel and is not out shopping   -> lodging
    ///   a member carries ResourceBuyer          -> goods
    ///   a member is aboard a transport vehicle  -> fares
    ///   anything else                           -> leisure and other
    ///
    /// That is attribution, not accounting. A guest who shops on the way back from the museum has
    /// the whole drop counted as goods. The totals are exact — every coin that leaves a wallet is
    /// counted once — but the split between categories is a best reading of intent, and the panel
    /// says so.
    ///
    /// Wallets only ever fall, since tourists have no income, so a rise means arrivals rather than
    /// earnings and is ignored rather than counted as negative spending.
    /// </summary>
    public partial class TouristSpendingLedgerSystem : GameSystemBase
    {
        /// <summary>Households sampled per update. Bounds the cost in a large city.</summary>
        private const int kMaxPerUpdate = 1024;

        private EntityQuery m_TouristQuery;
        private EntityQuery m_LedgerQuery;
        private TouristDemandSystem m_DemandSystem;

        /// <summary>Last balance and the frame it was taken, per household.</summary>
        private NativeHashMap<Entity, int2> m_LastSample;

        private HotelCapacitySystem m_HotelSystem;

        private int m_Cursor;
        private int m_TrackedMonth = -1;

        // Spent this month, by category, in money.
        private long m_Lodging;
        private long m_Goods;
        private long m_Fares;
        private long m_Other;

        // The last complete month, which is what the panel shows.
        private long m_LastLodging;
        private long m_LastGoods;
        private long m_LastFares;
        private long m_LastOther;
        private bool m_HasCompleteMonth;

        /// <summary>
        /// Whether to report the last completed month or the one in progress.
        ///
        /// A settled month is the better figure, but only once there is one worth showing. A save
        /// made moments into a month banks almost nothing, and reporting that would leave the panel
        /// reading zero for a whole in-game day. So a banked month is used only if it actually
        /// contains something.
        /// </summary>
        private bool UseCompleteMonth =>
            m_HasCompleteMonth && (m_LastLodging + m_LastGoods + m_LastFares + m_LastOther) > 0;

        /// <summary>Spent on hotel rooms over the last full month.</summary>
        public long Lodging => UseCompleteMonth ? m_LastLodging : m_Lodging;

        /// <summary>Spent in shops over the last full month.</summary>
        public long Goods => UseCompleteMonth ? m_LastGoods : m_Goods;

        /// <summary>Spent on transport fares over the last full month.</summary>
        public long Fares => UseCompleteMonth ? m_LastFares : m_Fares;

        /// <summary>Spent on leisure and anything unattributed, over the last full month.</summary>
        public long Other => UseCompleteMonth ? m_LastOther : m_Other;

        /// <summary>Everything above.</summary>
        public long Total => Lodging + Goods + Fares + Other;

        /// <summary>
        /// Times a household was seen mid-purchase, and times a ride was charged. For diagnostics.
        ///
        /// These separate "the signal never appears" from "the signal appears but no money follows".
        /// Shops reading zero could mean either, and the two need opposite fixes: the first would
        /// say ResourceBuyer is on an entity we are not looking at, the second that purchases settle
        /// outside the window we watch.
        /// </summary>
        public int GoodsSignalsSeen { get; private set; }

        /// <summary>Rides charged since load. For diagnostics.</summary>
        public int RidesCharged { get; private set; }

        // Frequent enough that a wallet rarely falls twice between samples, which would blur two
        // categories into one; rare enough not to matter.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 128;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_DemandSystem = World.GetOrCreateSystemManaged<TouristDemandSystem>();
            m_HotelSystem = World.GetOrCreateSystemManaged<HotelCapacitySystem>();

            m_LastSample = new NativeHashMap<Entity, int2>(4096, Allocator.Persistent);

            m_LedgerQuery = GetEntityQuery(ComponentType.ReadWrite<Components.TourismLedger>());

            m_TouristQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.ReadOnly<Game.Economy.Resources>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnDestroy()
        {
            if (m_LastSample.IsCreated)
            {
                m_LastSample.Dispose();
            }

            base.OnDestroy();
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // Entities differ between sessions, so last session's balances mean nothing. The
            // totals, however, do — they are restored below.
            m_LastSample.Clear();
            m_Cursor = 0;

            LoadTotals();
        }

        /// <summary>
        /// Reads the saved totals back, or starts a fresh ledger if this save has none.
        ///
        /// The figures live on a singleton entity rather than in fields because CS2 saves entities,
        /// not systems. Without this the panel reset to zero on every load and, since a reported
        /// month is a whole in-game day, would have spent most of its life blank.
        /// </summary>
        private void LoadTotals()
        {
            if (m_LedgerQuery.IsEmptyIgnoreFilter)
            {
                Entity ledger = EntityManager.CreateEntity(
                    ComponentType.ReadWrite<Components.TourismLedger>());

                // -1, not the default 0, because 0 is a real month. RollOverMonthIfNeeded uses -1
                // to mean "nothing has been counted yet" and skips banking on the first turnover.
                // With 0 it treated the very first update as a completed month, banked a set of
                // zeros as the reported figure, and the panel then showed nothing until the next
                // month came round — an entire in-game day later.
                EntityManager.SetComponentData(ledger, new Components.TourismLedger
                {
                    m_Month = -1
                });

                m_TrackedMonth = -1;
                return;
            }

            Components.TourismLedger saved = m_LedgerQuery.GetSingleton<Components.TourismLedger>();

            m_Lodging = saved.m_Lodging;
            m_Goods = saved.m_Goods;
            m_Fares = saved.m_Fares;
            m_Other = saved.m_Other;

            m_LastLodging = saved.m_LastLodging;
            m_LastGoods = saved.m_LastGoods;
            m_LastFares = saved.m_LastFares;
            m_LastOther = saved.m_LastOther;

            m_HasCompleteMonth = saved.m_HasCompleteMonth;
            m_TrackedMonth = saved.m_Month;
        }

        /// <summary>
        /// Writes the totals back to the saved singleton.
        ///
        /// Called on every update rather than only at save time, because there is no hook that
        /// reliably precedes a save. The cost is a single component write against work that already
        /// walks a thousand households.
        /// </summary>
        private void StoreTotals()
        {
            if (m_LedgerQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            m_LedgerQuery.SetSingleton(new Components.TourismLedger
            {
                m_Lodging = (int)m_Lodging,
                m_Goods = (int)m_Goods,
                m_Fares = (int)m_Fares,
                m_Other = (int)m_Other,
                m_LastLodging = (int)m_LastLodging,
                m_LastGoods = (int)m_LastGoods,
                m_LastFares = (int)m_LastFares,
                m_LastOther = (int)m_LastOther,
                m_HasCompleteMonth = m_HasCompleteMonth,
                m_Month = m_TrackedMonth
            });
        }

        protected override void OnUpdate()
        {
            if (m_TouristQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            RollOverMonthIfNeeded();

            // Drain the exact lodging charges accumulated by the hotel system since last update.
            if (m_HotelSystem != null)
            {
                m_Lodging += m_HotelSystem.LodgingChargedSinceReset;
                m_HotelSystem.LodgingChargedSinceReset = 0;
            }

            // Chunks, not entities.
            //
            // ToEntityArray allocated and filled an array of every tourist household — tens of
            // thousands — on every update, to then touch at most a thousand of them. The chunk
            // array is two orders of magnitude smaller, and entities are read straight out of the
            // chunks that are actually visited.
            EntityTypeHandle entityHandle = GetEntityTypeHandle();

            NativeArray<ArchetypeChunk> chunks = m_TouristQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                int index = 0;
                int taken = 0;

                for (int c = 0; c < chunks.Length && taken < kMaxPerUpdate; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    // Skip whole chunks that fall before the cursor without reading them.
                    if (index + chunk.Count <= m_Cursor)
                    {
                        index += chunk.Count;
                        continue;
                    }

                    NativeArray<Entity> entities = chunk.GetNativeArray(entityHandle);

                    int start = math.max(0, m_Cursor - index);

                    for (int i = start; i < entities.Length && taken < kMaxPerUpdate; i++)
                    {
                        Sample(entities[i]);
                        taken++;
                    }

                    index += chunk.Count;
                }

                // Wrap when the sweep reaches the end, so every household is visited in turn.
                m_Cursor = taken < kMaxPerUpdate ? 0 : m_Cursor + taken;
            }
            finally
            {
                chunks.Dispose();
            }

            StoreTotals();
        }

        /// <summary>
        /// Banks the month when the calendar turns over, so the panel reports a settled figure
        /// rather than a total that climbs all month. Uses the same boundary as the arrival
        /// counters, so the two are comparable.
        /// </summary>
        private void RollOverMonthIfNeeded()
        {
            int month = m_DemandSystem != null ? m_DemandSystem.MonthIndex : -1;

            if (month == m_TrackedMonth)
            {
                return;
            }

            if (m_TrackedMonth != -1)
            {
                m_LastLodging = m_Lodging;
                m_LastGoods = m_Goods;
                m_LastFares = m_Fares;
                m_LastOther = m_Other;
                m_HasCompleteMonth = true;
            }

            m_Lodging = 0;
            m_Goods = 0;
            m_Fares = 0;
            m_Other = 0;
            m_TrackedMonth = month;
        }

        private void Sample(Entity household)
        {
            if (!EntityManager.HasBuffer<Game.Economy.Resources>(household))
            {
                return;
            }

            int balance = EconomyUtils.GetResources(
                Resource.Money,
                EntityManager.GetBuffer<Game.Economy.Resources>(household, isReadOnly: true));

            if (!m_LastSample.TryGetValue(household, out int2 previous))
            {
                // First sight. Record it; the next sample measures against this one.
                m_LastSample[household] = new int2(balance, Observe(household));
                return;
            }

            // Fares are counted exactly, on the transition onto a vehicle, and taken out of the
            // drop before anything else is attributed so they are not counted twice.
            int fare = FareForNewBoarding(household, previous.y, out int aboardFlag);

            if (fare > 0)
            {
                m_Fares += fare;
                RidesCharged++;
            }

            int observed = Observe(household);

            if ((observed & kFlagGoods) != 0)
            {
                GoodsSignalsSeen++;
            }

            int seen = observed | aboardFlag;

            int spent = previous.x - balance - fare;

            // Signals carried forward from earlier samples, plus whatever is visible now.
            //
            // This is the part that makes shops and fares measurable at all. Both states are
            // momentary — a citizen carries ResourceBuyer only while a purchase is outstanding, and
            // rides a vehicle only for the journey — so requiring the signal to be visible at the
            // instant the money moves almost never worked, and the spending fell to "other".
            // Remembering that a household was shopping until its next drop is what connects the
            // two, since a wallet that falls after we saw someone buying almost certainly fell
            // because of it.
            int flags = previous.y | seen;

            // Wallets only fall. A rise means a recycled entity, not income.
            if (spent <= 0)
            {
                m_LastSample[household] = new int2(balance, flags);
                return;
            }

            // Lodging is not inferred here at all — HotelCapacitySystem counts it exactly, where
            // guests are billed. What is measured here is the rest: the wallet fell by more than
            // the room can explain, so something else was bought.
            if ((flags & kFlagGoods) != 0)
            {
                m_Goods += spent;
            }
            else
            {
                m_Other += spent;
            }

            // Shopping signals are consumed by the drop they explain, so one trip is not credited
            // with every later purchase. The aboard flag is kept, since it tracks a ride in
            // progress rather than an unexplained payment.
            m_LastSample[household] = new int2(balance, flags & kFlagAboard);
        }

        private const int kFlagGoods = 1;

        /// <summary>Set while a member is aboard, so a single ride is charged once.</summary>
        private const int kFlagAboard = 2;

        /// <summary>
        /// The fare a household owes for any ride it has just started, and whether it is riding.
        ///
        /// Fares cannot be observed the way lodging can, because the game charges them inside
        /// ResidentAISystem's boarding queue (:3922-3928) — one instant, no lingering state, and the
        /// money goes straight to the city budget rather than to a company, so residents' and
        /// visitors' fares are indistinguishable at the point of payment.
        ///
        /// They can be reconstructed, though. The price is a property of the route
        /// (ResidentAISystem.GetTicketPrice:3046-3057 reads CurrentRoute on the vehicle and then
        /// TransportLine.m_TicketPrice), so seeing a tourist newly aboard a vehicle is enough to
        /// know what they just paid. Counting on the transition from not-aboard to aboard charges
        /// each ride exactly once, and only for tourist households — which is precisely the split
        /// the city budget cannot give us.
        /// </summary>
        private int FareForNewBoarding(Entity household, int previousFlags, out int aboardFlag)
        {
            aboardFlag = 0;

            if (!EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                return 0;
            }

            DynamicBuffer<HouseholdCitizen> citizens =
                EntityManager.GetBuffer<HouseholdCitizen>(household, isReadOnly: true);

            int fare = 0;

            for (int i = 0; i < citizens.Length; i++)
            {
                Entity vehicle = PublicVehicleOf(citizens[i].m_Citizen);

                if (vehicle == Entity.Null)
                {
                    continue;
                }

                aboardFlag = kFlagAboard;

                // Already counted when this ride began.
                if ((previousFlags & kFlagAboard) != 0)
                {
                    continue;
                }

                fare += TicketPrice(vehicle);
            }

            return fare;
        }

        /// <summary>
        /// The vehicle a citizen is riding, if it is one they had to pay for.
        ///
        /// A Citizen is a record, not a body. The thing that physically moves is a separate
        /// creature entity, and CurrentVehicle lives on that — which is why looking for it on the
        /// citizen found nothing and fares read zero. CurrentTransport is the link between the two
        /// (the same hop CommonPathfindSetup:88-95 makes when resolving where someone is).
        /// </summary>
        private Entity PublicVehicleOf(Entity citizen)
        {
            Entity traveller = citizen;

            if (EntityManager.HasComponent<Game.Citizens.CurrentTransport>(citizen))
            {
                Entity transport =
                    EntityManager.GetComponentData<Game.Citizens.CurrentTransport>(citizen).m_CurrentTransport;

                if (transport != Entity.Null && EntityManager.Exists(transport))
                {
                    traveller = transport;
                }
            }

            if (!EntityManager.HasComponent<Game.Creatures.CurrentVehicle>(traveller))
            {
                return Entity.Null;
            }

            Entity vehicle = EntityManager.GetComponentData<Game.Creatures.CurrentVehicle>(traveller).m_Vehicle;

            if (vehicle == Entity.Null || !EntityManager.Exists(vehicle))
            {
                return Entity.Null;
            }

            // Their own car carries no PublicTransport, which is the distinction that matters:
            // driving is free, riding is not.
            return EntityManager.HasComponent<PublicTransport>(vehicle) ? vehicle : Entity.Null;
        }

        /// <summary>Mirrors ResidentAISystem.GetTicketPrice (:3046-3057).</summary>
        private int TicketPrice(Entity vehicle)
        {
            if (!EntityManager.HasComponent<Game.Routes.CurrentRoute>(vehicle))
            {
                return 0;
            }

            Entity route = EntityManager.GetComponentData<Game.Routes.CurrentRoute>(vehicle).m_Route;

            if (route == Entity.Null || !EntityManager.HasComponent<Game.Routes.TransportLine>(route))
            {
                return 0;
            }

            return EntityManager.GetComponentData<Game.Routes.TransportLine>(route).m_TicketPrice;
        }

        /// <summary>
        /// What the household appears to be doing, as flags.
        ///
        /// Both signals are positive rather than inferred: a citizen carrying ResourceBuyer is
        /// definitely buying something, and one aboard a vehicle marked PublicTransport is
        /// definitely paying to ride it. Their own car is not, which is the distinction that
        /// matters — driving is free, riding is not.
        ///
        /// Stops at the first citizen that shows either signal, since one is enough to explain the
        /// household's next drop and the buffers are walked for every sampled household.
        /// </summary>
        private int Observe(Entity household)
        {
            if (!EntityManager.HasBuffer<HouseholdCitizen>(household))
            {
                return 0;
            }

            DynamicBuffer<HouseholdCitizen> citizens =
                EntityManager.GetBuffer<HouseholdCitizen>(household, isReadOnly: true);

            for (int i = 0; i < citizens.Length; i++)
            {
                Entity citizen = citizens[i].m_Citizen;

                if (EntityManager.HasComponent<Game.Companies.ResourceBuyer>(citizen))
                {
                    return kFlagGoods;
                }
            }

            return 0;
        }

    }
}
