using Game;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Fix E — tourist budgets and hotel construction demand.
    ///
    /// Vanilla defect 1: tourists cannot afford to stay.
    ///
    ///   LodgingProviderSystem charges a nightly rate of
    ///       m_Price = m_TouristLodgingConsumePerDay * marketPrice(Lodging)     (:157)
    ///   which is 100 x the market price of Lodging — commonly well over 1000.
    ///
    ///   HouseholdInitializeSystem gives a tourist household a single one-off wallet of
    ///       random(m_TouristInitialWealthRange) - range/2 + m_TouristInitialWealthOffset  (:162)
    ///   which with the shipped 2000/1000 is 0..2000, mean 1000. They never earn more.
    ///
    ///   So a large share of arrivals cannot afford even one night, and TouristLeaveSystem evicts
    ///   them as TouristNoMoney once the day passes 70% (:68). Tourist population then sits in an
    ///   equilibrium between arrivals and money-evictions, regardless of attractiveness or free
    ///   rooms, and the length-of-stay mechanic never gets a chance to run.
    ///
    /// Vanilla defect 2: hotels are under-demanded.
    ///
    ///   CommercialDemandSystem only raises Lodging demand when
    ///       currentTourists * m_HotelRoomPercentRequirement > totalRooms                (:187)
    ///   and that factor ships at 0.5 — the city only ever wants rooms for half its tourists.
    ///
    /// Both values live on singleton components, so both are plain component writes. The budget is
    /// derived from the live Lodging market price rather than hardcoded, so it tracks the economy
    /// instead of drifting out of date.
    /// </summary>
    public partial class TouristEconomySystem : GameSystemBase
    {
        /// <summary>
        /// Mirrors HouseholdBehaviorSystem.kMinimumShoppingMoney (:433). A household whose
        /// spendable money falls below this stops shopping altogether (:267), so a tourist must
        /// stay above it for the whole visit or they stop contributing to the economy partway
        /// through — while still occupying a hotel room.
        /// </summary>
        private const int kMinimumShoppingMoney = 1000;

        private EntityQuery m_EconomyParameterQuery;
        private EntityQuery m_DemandParameterQuery;
        private EntityQuery m_LeisureParameterQuery;

        private ResourceSystem m_ResourceSystem;

        private int m_LastWrittenBudget = -1;
        private float m_LastWrittenRoomRequirement = -1f;

        /// <summary>Nightly hotel rate at the last update, for diagnostics.</summary>
        public int NightlyHotelPrice { get; private set; }

        /// <summary>Wallet a newly arrived tourist household receives, for diagnostics.</summary>
        public int TouristBudget { get; private set; }

        // Market prices move slowly; 64 updates per in-game day is ample.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 4096;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_ResourceSystem = World.GetOrCreateSystemManaged<ResourceSystem>();

            m_EconomyParameterQuery = GetEntityQuery(ComponentType.ReadWrite<EconomyParameterData>());
            m_DemandParameterQuery = GetEntityQuery(ComponentType.ReadWrite<DemandParameterData>());
            m_LeisureParameterQuery = GetEntityQuery(ComponentType.ReadOnly<LeisureParametersData>());

            RequireForUpdate(m_EconomyParameterQuery);
            RequireForUpdate(m_DemandParameterQuery);
            RequireForUpdate(m_LeisureParameterQuery);
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // Parameters are rebuilt from prefabs on load, so forget what we think we wrote.
            m_LastWrittenBudget = -1;
            m_LastWrittenRoomRequirement = -1f;
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;
            if (settings == null)
            {
                return;
            }

            UpdateTouristBudget(settings);
            UpdateHotelRoomRequirement(settings);
        }

        /// <summary>
        /// Sizes the arrival wallet so a tourist can actually pay for the stay they came for.
        ///
        /// The nightly rate is recomputed the same way LodgingProviderSystem does, from the live
        /// Lodging market price, so this stays correct as the economy moves.
        /// </summary>
        private void UpdateTouristBudget(TourismOverhaulSetting settings)
        {
            LeisureParametersData leisure = m_LeisureParameterQuery.GetSingleton<LeisureParametersData>();
            ResourcePrefabs resourcePrefabs = m_ResourceSystem.GetPrefabs();

            float marketPrice = EconomyUtils.GetMarketPrice(Resource.Lodging, resourcePrefabs, EntityManager);

            // The nightly rate is lodging consumed per day times the market price of lodging. The
            // game ships 30 units a day, so at a market price of 50 a room costs 1,500 a night —
            // steep next to what the rest of the economy charges for anything.
            //
            // Scaling the consumption rather than the price is the right lever: the market price is
            // set by supply and demand and writing to it would fight the economy, whereas how much
            // lodging a visitor uses is a property of the visitor. Hotels sell less lodging per
            // guest and earn proportionally less, which is the honest consequence of cheaper rooms.
            float consumePerDay =
                leisure.m_TouristLodgingConsumePerDay * math.max(1, settings.LodgingCostPercent) / 100f;

            int nightlyPrice = (int)(consumePerDay * marketPrice);

            NightlyHotelPrice = nightlyPrice;

            if (!settings.FixTouristBudget || nightlyPrice <= 0)
            {
                return;
            }

            // Never fund fewer nights than TouristStaySystem will keep them for, or they would be
            // evicted for lack of money before their stay elapsed — the two features would fight
            // each other.
            int nights = math.max(settings.TouristBudgetDays, settings.AverageStayDays + 1);

            // A tourist wallet has to cover three separate things, all drawn from the same pot:
            //
            //   1. Lodging — LodgingProviderSystem charges the nightly rate every day.
            //   2. Everything else — HouseholdBehaviorSystem generates shopping trips for tourist
            //      households the same way it does for residents, LeisureSystem spends at venues,
            //      and ResidentAISystem.GetTicketPrice charges real fares on public transport.
            //   3. A reserve, because dropping under kMinimumShoppingMoney stops them shopping
            //      for the rest of the visit even though they are still in the city.
            // Spending is expressed against the room rate rather than as a flat sum of money, so
            // the two stay in proportion. A flat figure drifts out of step the moment lodging is
            // repriced: at a 6,000 daily allowance against a 1,500 room, 78% of every wallet was a
            // number nobody had related to anything the economy charges for. Deriving it means
            // lowering the cost of a room lowers what visitors carry, automatically.
            int dailySpending = nightlyPrice * math.max(0, settings.SpendingPerNightPercent) / 100;
            int budget = nights * (nightlyPrice + dailySpending) + kMinimumShoppingMoney;

            if (budget == m_LastWrittenBudget)
            {
                return;
            }

            Entity economyEntity = m_EconomyParameterQuery.GetSingletonEntity();
            EconomyParameterData economy = EntityManager.GetComponentData<EconomyParameterData>(economyEntity);

            // HouseholdInitializeSystem draws random(range) - range/2 + offset. With offset 1.5x
            // and range 1x the budget, wallets land uniformly in [budget, 2 x budget], so even the
            // poorest arrival can afford the full stay.
            // HouseholdInitializeSystem:162 rolls each arrival's wallet as
            //
            //     random.NextInt(range) - range / 2 + offset
            //
            // so wallets are uniform across [offset - range/2, offset + range/2]. Setting range to
            // the budget and offset to 1.5x it gave everyone between one and two full budgets —
            // varied, but every visitor comfortably solvent, which is why they all behaved alike.
            //
            // The spread is now a setting, anchored so the poorest arrival still carries a full
            // budget. Widening it adds well-off visitors above that floor rather than adding
            // paupers below it, because a tourist who cannot afford the stay is not an interesting
            // traveller, just one who leaves early as TouristNoMoney.
            float spread = math.max(0f, settings.WealthVariationPercent) / 100f;
            int range = math.max(1, (int)(budget * spread));

            economy.m_TouristInitialWealthRange = range;
            economy.m_TouristInitialWealthOffset = budget + range / 2;

            EntityManager.SetComponentData(economyEntity, economy);

            m_LastWrittenBudget = budget;
            TouristBudget = budget;

            Mod.Log.Info(
                $"Tourist budget set to {budget} = {nights} nights x ({nightlyPrice} lodging + " +
                $"{dailySpending} spending) + {kMinimumShoppingMoney} reserve " +
                $"(lodging market price {marketPrice:0.00}, room cost at " +
                $"{settings.LodgingCostPercent}%, spending at {settings.SpendingPerNightPercent}% " +
                $"of the room rate).");
        }

        /// <summary>
        /// Raises how many rooms per tourist the city considers necessary, which is the trigger
        /// CommercialDemandSystem uses to push Lodging demand to maximum and get hotels zoned.
        ///
        /// The room multiplier has to be compensated for here, or it silently switches hotel
        /// construction off. The test CommercialDemandSystem applies is
        ///
        ///     currentTourists * m_HotelRoomPercentRequirement > m_Lodging.y     (:187)
        ///
        /// and TourismSystem fills m_Lodging.y from LodgingProvider capacity (:90), which is the
        /// figure HotelCapacitySystem multiplies. So a 3x multiplier triples the apparent room
        /// supply, the inequality stops holding, Lodging demand sits at zero, and ZoneSpawnSystem
        /// rejects every hotel prefab because EvaluateDemandAndAvailability returns 0 against a
        /// m_MinDemand of 1. Nothing is built, in the hotel zone or anywhere else, however much
        /// land the player zones.
        ///
        /// Multiplying the requirement by the same factor cancels the inflation exactly: the
        /// multiplier then changes how many tourists a single hotel holds, which is what it is for,
        /// without also claiming the city is oversupplied.
        /// </summary>
        private void UpdateHotelRoomRequirement(TourismOverhaulSetting settings)
        {
            if (!settings.FixHotelDemand)
            {
                return;
            }

            float perTourist = math.clamp(settings.HotelRoomsPerTourist, 0.1f, 3f);
            float multiplier = math.max(1, settings.HotelRoomMultiplier);

            // Clamped well above the per-tourist ceiling because this is a compensated figure, not
            // a player-facing one — at 3 rooms per tourist and a 10x multiplier it reaches 30.
            float requirement = math.clamp(perTourist * multiplier, 0.1f, 30f);

            if (math.abs(requirement - m_LastWrittenRoomRequirement) < 0.001f)
            {
                return;
            }

            Entity demandEntity = m_DemandParameterQuery.GetSingletonEntity();
            DemandParameterData demand = EntityManager.GetComponentData<DemandParameterData>(demandEntity);

            demand.m_HotelRoomPercentRequirement = requirement;

            EntityManager.SetComponentData(demandEntity, demand);

            m_LastWrittenRoomRequirement = requirement;

            Mod.Log.Info(
                $"Hotel room requirement set to {requirement:0.00} rooms per tourist " +
                $"({perTourist:0.00} wanted x {multiplier:0} room multiplier).");
        }
    }
}
