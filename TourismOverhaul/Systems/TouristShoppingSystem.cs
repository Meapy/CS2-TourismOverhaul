using Game;
using Game.Agents;
using Game.Citizens;
using Game.Common;
using Game.Economy;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Gives tourist households a reason to go shopping.
    ///
    /// Tourists loiter in parks because of how the behaviour decision is ordered.
    /// CitizenBehaviorSystem checks shopping before leisure (:759-774):
    ///
    ///     if (age is Adult or Elderly)
    ///     {
    ///         HouseholdNeed need = m_HouseholdNeeds[household];
    ///         if (need.m_Resource != Resource.NoResource && ...)
    ///         {
    ///             GoShopping(...);   // wins over leisure
    ///             continue;
    ///         }
    ///     }
    ///     if (!chunk.Has(ref m_LeisureType) && DoLeisure(...)) ...
    ///
    /// So a household with a need shops, and one without does leisure. Needs are produced by
    /// HouseholdBehaviorSystem from the household's own stock running down — it zeroes
    /// m_Resources, checks spendable money, then rolls for a resource (:260-314). A tourist
    /// household has no home, no stored goods and no consumption, so that path never generates
    /// anything and m_Resource stays NoResource for the whole visit.
    ///
    /// The result is a visitor who never once goes near a shop. Worse, it also means tourist
    /// spending never reaches commercial companies, so the tourism economy is largely fictional:
    /// visitors pay for lodging and nothing else.
    ///
    /// This fills that gap directly rather than trying to make the native path apply to tourists,
    /// which would require them to hold and consume goods they have nowhere to store. A tourist
    /// with money and no pending need periodically acquires one, and the native machinery takes it
    /// from there — GoShopping, ResourceBuyer, pathfinding, the till at the other end. Nothing
    /// about the purchase itself is reimplemented.
    /// </summary>
    public partial class TouristShoppingSystem : GameSystemBase
    {
        /// <summary>Below this, a tourist is saving for the hotel rather than browsing.</summary>
        private const int kMinimumShoppingMoney = 200;

        /// <summary>
        /// Upper bound on a single shopping trip, in resource units.
        ///
        /// A sanity bound, not a balance lever: it stops one visitor clearing a shop's entire stock
        /// in a single visit. What a tourist actually buys is set by what they are carrying, which
        /// is the figure that should govern it.
        /// </summary>
        private const int kMaxShoppingAmount = 2000;

        private EntityQuery m_TouristQuery;
        private EndFrameBarrier m_EndFrameBarrier;

        // 262144 frames per in-game day. At 2048 this runs 128 times a day, so the per-update
        // chance below converts to a handful of shopping trips per visitor per day.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 2048;

        /// <summary>Shopping trips started since load. For diagnostics.</summary>
        public int TripsStarted { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            m_TouristQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.ReadOnly<Household>(),
                ComponentType.ReadWrite<HouseholdNeed>(),
                ComponentType.ReadOnly<Game.Economy.Resources>(),
                ComponentType.Exclude<MovingAway>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null || settings.TouristShoppingChance <= 0
                || m_TouristQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            int chance = math.clamp(settings.TouristShoppingChance, 0, 100);

            NativeArray<Resource> options = BuildShoppingBasket(Allocator.Temp);

            try
            {
                if (options.Length == 0)
                {
                    return;
                }

                Random random = new Random(math.max(1u, World.Time.ElapsedTime > 0
                    ? (uint)(World.Time.ElapsedTime * 1000d) * 747796405u + 2891336453u
                    : 1u));

                AssignNeeds(chance, options, ref random);
            }
            finally
            {
                options.Dispose();
            }
        }

        /// <summary>
        /// The resources a visitor might plausibly buy.
        ///
        /// Commercial resources only, which is what EconomyUtils.IsCommercialResource already
        /// defines and what CommercialDemandSystem uses to decide what shops sell. Lodging is
        /// dropped because tourists acquire that through the hotel, not by shopping for it.
        /// </summary>
        private static NativeArray<Resource> BuildShoppingBasket(Allocator allocator)
        {
            NativeList<Resource> basket = new NativeList<Resource>(16, allocator);

            ResourceIterator iterator = ResourceIterator.GetIterator();

            while (iterator.Next())
            {
                if (iterator.resource == Resource.Lodging || iterator.resource == Resource.NoResource)
                {
                    continue;
                }

                if (EconomyUtils.IsCommercialResource(iterator.resource))
                {
                    basket.Add(iterator.resource);
                }
            }

            NativeArray<Resource> result = basket.ToArray(allocator);
            basket.Dispose();

            return result;
        }

        private void AssignNeeds(int chance, NativeArray<Resource> options, ref Random random)
        {
            EntityTypeHandle entityHandle = GetEntityTypeHandle();
            ComponentTypeHandle<HouseholdNeed> needHandle = GetComponentTypeHandle<HouseholdNeed>();
            ComponentTypeHandle<Household> householdHandle =
                GetComponentTypeHandle<Household>(isReadOnly: true);
            BufferTypeHandle<Game.Economy.Resources> resourceHandle =
                GetBufferTypeHandle<Game.Economy.Resources>(isReadOnly: true);
            BufferTypeHandle<HouseholdCitizen> citizenHandle =
                GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();

            NativeArray<ArchetypeChunk> chunks = m_TouristQuery.ToArchetypeChunkArray(Allocator.Temp);

            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    NativeArray<Entity> entities = chunk.GetNativeArray(entityHandle);
                    NativeArray<HouseholdNeed> needs = chunk.GetNativeArray(ref needHandle);
                    BufferAccessor<Game.Economy.Resources> resources =
                        chunk.GetBufferAccessor(ref resourceHandle);

                    bool hasCitizens = chunk.Has(ref citizenHandle);
                    BufferAccessor<HouseholdCitizen> citizens =
                        hasCitizens ? chunk.GetBufferAccessor(ref citizenHandle) : default;

                    for (int i = 0; i < chunk.Count; i++)
                    {
                        // Already shopping for something; leave it alone.
                        if (needs[i].m_Resource != Resource.NoResource)
                        {
                            continue;
                        }

                        int occupants = hasCitizens ? citizens[i].Length : 0;

                        // A household with nobody in it cannot go anywhere.
                        if (occupants == 0)
                        {
                            continue;
                        }

                        int money = EconomyUtils.GetResources(Resource.Money, resources[i]);

                        if (money < kMinimumShoppingMoney)
                        {
                            continue;
                        }

                        if (random.NextInt(100) >= chance)
                        {
                            continue;
                        }

                        // Spend a slice of what they are carrying, scaled by party size, so a
                        // family buys more than a lone traveller and nobody is asked to spend
                        // money they do not have.
                        //
                        // The ceiling used to be 200, which a visitor with a normal wallet exceeded
                        // eight times over — so it bound on essentially every trip and every
                        // shopper bought the same token amount. Raising the shopping chance then
                        // did nothing useful: more trips, each still capped at 200. Shops came to
                        // 1% of tourist spending against 94% for leisure, and the cap was most of
                        // the reason.
                        //
                        // The remaining ceiling is a sanity bound rather than a balance figure: it
                        // stops a single trip clearing out a company's entire stock.
                        int amount = math.clamp(money / 20 * occupants, 5, kMaxShoppingAmount);

                        needs[i] = new HouseholdNeed
                        {
                            m_Resource = options[random.NextInt(options.Length)],
                            m_Amount = amount
                        };

                        // Tell the ledger this household is going shopping. Catching ResourceBuyer
                        // later is unreliable — it exists only while a purchase is outstanding, and
                        // sampling misses most of them.
                        commandBuffer.AddComponent<Components.ExpectsPurchase>(entities[i]);

                        TripsStarted++;
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
