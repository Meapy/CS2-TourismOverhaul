using Game;
using Game.City;
using Game.Economy;
using Game.Prefabs;
using Game.Simulation;
using Unity.Collections;
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
        private EntityQuery m_CityQuery;

        private ResourceSystem m_ResourceSystem;
        private TouristDemandSystem m_DemandSystem;

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
            m_DemandSystem = World.GetOrCreateSystemManaged<TouristDemandSystem>();

            m_EconomyParameterQuery = GetEntityQuery(ComponentType.ReadWrite<EconomyParameterData>());
            m_DemandParameterQuery = GetEntityQuery(ComponentType.ReadWrite<DemandParameterData>());
            m_LeisureParameterQuery = GetEntityQuery(ComponentType.ReadOnly<LeisureParametersData>());
            m_CityQuery = GetEntityQuery(ComponentType.ReadOnly<Tourism>());

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
        /// Occupancy at which full hotels ask for more rooms regardless of party size. 85%, not
        /// 92% as in 2.0.0: parties check out and in all the time, so a city's hotels hover a few
        /// points short of full and rarely touch 92%. One sat between 82% and 92% for twenty
        /// minutes with the demand bar at 99% and never built; it crossed 92% only when hotels
        /// were closing and taking their rooms with them.
        /// </summary>
        internal const float kFullOccupancy = 0.85f;

        /// <summary>The last requirement and reason logged, so the log line only repeats on real change.</summary>
        private string m_LastLoggedRoomReason;
        private float m_LastLoggedRoomRequirement;

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

            // The game's test multiplies tourist CITIZENS by the requirement (TourismSystem:78) and
            // compares against ROOMS (:90), and a room holds a whole household. The vanilla 0.5
            // (DemandPrefab:91) is one room per two-person party. The setting has always meant
            // rooms per party — its description says above 1.0 leaves spare capacity — but was
            // applied per citizen, demanding about two rooms per party: hotels were built forever,
            // 40% of rooms stood empty, and every new hotel's welcome bonus lifted the tourist
            // target until MaximumTourists caught it. Dividing by the measured party size makes it
            // per party. Rounded to 0.1 so ordinary drift in party size does not rewrite the
            // demand parameter every update.
            float partySize = m_DemandSystem != null ? m_DemandSystem.AveragePartySize : 0f;
            partySize = partySize >= 1f ? math.clamp(math.round(partySize * 10f) / 10f, 1f, 4f) : 2f;

            // Clamped well above the per-tourist ceiling because this is a compensated figure, not
            // a player-facing one — at 3 rooms per party and a 10x multiplier it reaches 30.
            float requirement = math.clamp(perTourist * multiplier / partySize, 0.05f, 30f);

            // The first hotel or motel should still be buildable when the city has no lodging
            // capacity yet. That prevents the very first zoned lodging building from being blocked
            // by the normal "rooms per tourist" demand threshold.
            bool hasLodgingCapacity = true;
            if (!m_CityQuery.IsEmptyIgnoreFilter)
            {
                int2 lodging = m_CityQuery.GetSingleton<Tourism>().m_Lodging;
                hasLodgingCapacity = lodging.y > 0;
            }

            bool occupancyTrigger = false;

            if (!hasLodgingCapacity)
            {
                // The game's own count, as in the trigger below: it is what its test multiplies.
                int tourists = m_CityQuery.IsEmptyIgnoreFilter ? 0 : m_CityQuery.GetSingleton<Tourism>().m_CurrentTourists;
                if (tourists > 0)
                {
                    requirement = math.clamp(1f / tourists + 1e-3f, 0.01f, 30f);
                }
                else
                {
                    requirement = 4f;
                }
            }
            else if (m_DemandSystem != null && !m_CityQuery.IsEmptyIgnoreFilter)
            {
                // Full hotels with visitors still to come. The per-party figure above divides by
                // the measured party size, but that size is counted over every tourist household
                // and the rooms hold fewer: one city ran 57,689 tourists in 36,218 occupied rooms
                // (1.6 per room) against a measured party of 2.3, so the requirement asked for 30%
                // too few rooms. It read "hotels will spawn: NO" with 98.5% of rooms taken and the
                // tourist target 40,000 above the current count.
                //
                // So once rooms are nearly full, ask for just enough that the game's test
                // (tourists x requirement > rooms) holds, and stop as soon as a new hotel pulls
                // occupancy back under the threshold.
                //
                // Full hotels always ask. This used to require tourists below IntrinsicTarget
                // (population and attractiveness only), against the 1.9.1 runaway, where each new
                // hotel's rooms drew the visitors that justified the next. But rooms draw visitors
                // by design, so in an attractive city tourists sat above that target for good: one
                // ran 1,851 tourists against a target of 1,050 with its hotels 95% full, and never
                // built again. Growth is still bounded: rooms draw visitors only in proportion to
                // attractiveness, and Maximum tourists caps the lot.
                //
                // Occupancy is the live count; the game's m_Lodging lags by up to three in-game
                // hours. Rooms and tourists are the game's own, because they are what its test
                // reads: a raise computed on this mod's tourist count could fall short of it.
                Tourism tourism = m_CityQuery.GetSingleton<Tourism>();
                int2 lodging = tourism.m_Lodging;
                int tourists = tourism.m_CurrentTourists;
                int roomsTotal = m_DemandSystem.RoomsTotal;
                float occupancy = roomsTotal > 0 ? m_DemandSystem.RoomsOccupied / (float)roomsTotal : 0f;

                if (tourists > 0 && occupancy >= kFullOccupancy)
                {
                    float needed = math.clamp((lodging.y * 1.02f + 1f) / tourists, 0.05f, 30f);

                    if (needed > requirement)
                    {
                        requirement = needed;
                        occupancyTrigger = true;
                    }
                }
            }

            if (math.abs(requirement - m_LastWrittenRoomRequirement) < 0.001f)
            {
                return;
            }

            Entity demandEntity = m_DemandParameterQuery.GetSingletonEntity();
            DemandParameterData demand = EntityManager.GetComponentData<DemandParameterData>(demandEntity);

            demand.m_HotelRoomPercentRequirement = requirement;

            EntityManager.SetComponentData(demandEntity, demand);

            m_LastWrittenRoomRequirement = requirement;

            string reason = !hasLodgingCapacity
                ? "(first hotel/motel build exception)"
                : occupancyTrigger
                    ? $"(rooms at least {kFullOccupancy:P0} full)"
                    : $"({perTourist:0.00} rooms wanted per party of {partySize:0.0} x {multiplier:0} room multiplier)";

            // Logged when the reason changes or the figure moves by a tenth. Under the occupancy
            // trigger it is recomputed from live counts every update, and logging each write
            // filled the log with near-identical lines.
            // The kind of reason, not its text: the per-party text carries the measured party size,
            // which flips between 2.2 and 2.3 and made the line repeat every few seconds.
            string reasonKind = !hasLodgingCapacity ? "first" : occupancyTrigger ? "full" : "party";

            if (reasonKind != m_LastLoggedRoomReason
                || math.abs(requirement - m_LastLoggedRoomRequirement) > 0.1f * math.max(m_LastLoggedRoomRequirement, 0.01f))
            {
                m_LastLoggedRoomReason = reasonKind;
                m_LastLoggedRoomRequirement = requirement;
                Mod.Log.Info(
                    $"Hotel room requirement set to {requirement:0.00} per tourist citizen {reason}");
            }
        }
    }

    /// <summary>
    /// Whether the game will build lodging, read from the game itself.
    ///
    /// The tourist demand bar and the diagnostics line both read this, and it is the number the
    /// game builds on, so the bar works exactly as the native residential and commercial bars do:
    /// CityInfoUISystem shows each system's building demand (0-100, smoothed), and ZoneSpawnSystem
    /// builds while it is above zero. For hotels that number is CommercialDemandSystem's per-resource
    /// building demand for Lodging, which ZoneSpawnSystem checks against m_MinDemand for every
    /// lodging prefab (:318-319, EvaluateDemandAndAvailability). It already folds in the rooms test,
    /// taxes and the rule that empty hotel buildings are re-let before new ones go up.
    ///
    /// The bar used to be the mod's own estimate beside the game's decision: occupancy alone, and
    /// then a progress figure. It could read 99% for twenty minutes while nothing was built.
    /// </summary>
    internal static class LodgingOutlook
    {
        /// <summary>
        /// CommercialDemandSystem:187 verbatim: the rooms test inside the Lodging demand, while
        /// (int)(currentTourists x requirement) exceeds the rooms the game counts.
        /// </summary>
        internal static bool RoomsTestPasses(Tourism tourism, float requirement)
        {
            return (int)(tourism.m_CurrentTourists * requirement) - tourism.m_Lodging.y > 0;
        }

        /// <summary>
        /// The game's building demand for Lodging, 0 when there is none. Waits for the demand job
        /// that writes it, so callers read it on a slow cadence, not every frame.
        /// </summary>
        internal static int BuildingDemand(CommercialDemandSystem commercial)
        {
            if (commercial == null)
            {
                return 0;
            }

            NativeArray<int> demands = commercial.GetBuildingDemands(out Unity.Jobs.JobHandle deps);
            deps.Complete();

            int index = EconomyUtils.GetResourceIndex(Resource.Lodging);
            return demands.IsCreated && index >= 0 && index < demands.Length ? math.max(0, demands[index]) : 0;
        }
    }
}
