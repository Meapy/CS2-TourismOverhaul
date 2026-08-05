using Game;
using Game.Economy;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Makes leisure venues affordable, by scaling what a visit consumes.
    ///
    /// WHY LEISURE DOMINATES TOURIST SPENDING
    ///
    /// LeisureSystem:102 and :125 charge a visit as
    ///
    ///     num2 = max((int)(m_ServiceConsuming / kUpdateInterval), 1);   // units taken per visit
    ///     cost = num2 x marketPrice x GetServicePriceMultiplier(available, m_MaxService);
    ///
    /// Tourists are the ideal customer for driving that: CitizenBehaviorSystem:446-452 skips the
    /// cooldown and leisure-counter checks for them entirely, so a visitor is always willing to go
    /// out. Measured in a live city, non-lodging spending ran at roughly 24,000 per household per
    /// in-game day against a budget that allowed 1,050 — wallets emptied in under a day and the
    /// visitors were evicted as TouristNoMoney long before their stay was up. Leisure was ~71% of
    /// it.
    ///
    /// THE SURGE TERM IS NOT THE LEVER, AND IT POINTS THE OTHER WAY
    ///
    /// An earlier version of this system scaled m_MaxService, on the reasoning that more capacity
    /// means a lower surge multiplier. That is backwards. EconomyUtils:548-551 is
    ///
    ///     lerp(0.7f, 1.3f, saturate(1f - serviceAvailable / (float)maxServiceAvailable))
    ///
    /// so for a given stock, *raising* the maximum shrinks available/max and moves the multiplier
    /// toward 1.3 — dearer, not cheaper. Raising it does also lift the production ceiling, so stock
    /// can grow to meet it, which pulls the other way; the net effect is genuinely ambiguous. What
    /// is not ambiguous is the size: the whole term is clamped to [0.7, 1.3], so it can move a
    /// price by at most ±30% in either direction and could never account for a 23x overspend. The
    /// city in question already had capacity scaled x4 and leisure still dominated.
    ///
    /// WHAT THIS CHANGES
    ///
    /// m_ServiceConsuming is the input the charge is actually derived from, so scaling it scales
    /// the price directly and without a ceiling. This is the same relationship the notes record for
    /// rooms — LodgingProvider.m_Price is derived output, and the way to reprice a room is to scale
    /// consumption rather than fight the market price, which belongs to the resource economy.
    ///
    /// It helps twice over: a visit that draws less stock also leaves availability higher, which
    /// moves the surge term toward 0.7 on its own.
    ///
    /// Note the floor. num2 is max(..., 1), so once m_ServiceConsuming falls below kUpdateInterval
    /// (5) every visit takes one unit regardless and lowering the setting further does nothing.
    /// That is what bounds the useful range of LeisureCostPercent at the low end.
    ///
    /// Capacity scaling is kept, unchanged in effect, because it raises the production ceiling and
    /// so keeps venues from stalling — the same reason HotelCapacitySystem scales it for hotels.
    /// It is simply not the thing that makes leisure cheap.
    ///
    /// SCOPE: this affects every customer at these venues, not only tourists. Residents get cheaper
    /// leisure too. That is unavoidable — the price is a property of the venue, not the visitor —
    /// and arguably right, since the crowding that caused the surge was never meant to be there.
    /// Lodging is excluded because HotelCapacitySystem owns it and would fight over the same field.
    ///
    /// The authored figures are held in a map rather than recovered by dividing out the last factor
    /// applied, so repeated changes always scale from the original and never compound a rounding
    /// error. Prefab data is rebuilt from source on load, so the map is cleared there.
    /// </summary>
    public partial class LeisurePricingSystem : GameSystemBase
    {
        private EntityQuery m_ServicePrefabQuery;

        private int m_AppliedMultiplier;
        private int m_AppliedCostPercent;

        /// <summary>
        /// Authored m_MaxService (x) and m_ServiceConsuming (y) per venue prefab.
        ///
        /// Kept outside the component so every write is base x factor rather than a
        /// read-modify-write, which is what stops repeated setting changes from compounding.
        /// </summary>
        private NativeHashMap<Entity, int2> m_Authored;

        /// <summary>Prefabs adjusted, for diagnostics.</summary>
        public int AdjustedVenues { get; private set; }

        // Prefab data, so this only needs to run when the setting changes.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 4096;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_Authored = new NativeHashMap<Entity, int2>(64, Allocator.Persistent);

            m_ServicePrefabQuery = GetEntityQuery(
                ComponentType.ReadWrite<Game.Companies.ServiceCompanyData>(),
                ComponentType.ReadOnly<IndustrialProcessData>(),
                ComponentType.ReadOnly<PrefabData>());
        }

        protected override void OnDestroy()
        {
            if (m_Authored.IsCreated)
            {
                m_Authored.Dispose();
            }

            base.OnDestroy();
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // Prefab data is rebuilt from source on load, so the values sitting there are the
            // authored ones again and anything remembered about them is stale. Forget both the
            // recorded originals and the factors applied to them, and reapply from scratch.
            m_Authored.Clear();
            m_AppliedMultiplier = 0;
            m_AppliedCostPercent = 0;
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null)
            {
                return;
            }

            int multiplier = math.clamp(settings.LeisureCapacityMultiplier, 1, 10);
            int costPercent = math.clamp(settings.LeisureCostPercent, 5, 200);

            // Nothing to do unless one of the two factors has actually moved.
            if ((multiplier == m_AppliedMultiplier && costPercent == m_AppliedCostPercent)
                || m_ServicePrefabQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            int adjusted = 0;

            NativeArray<Entity> prefabs = m_ServicePrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];

                    // Hotels are HotelCapacitySystem's business. Two systems scaling the same field
                    // would multiply each other's work.
                    if (EntityManager.GetComponentData<IndustrialProcessData>(prefab)
                            .m_Output.m_Resource == Resource.Lodging)
                    {
                        continue;
                    }

                    Game.Companies.ServiceCompanyData data =
                        EntityManager.GetComponentData<Game.Companies.ServiceCompanyData>(prefab);

                    if (data.m_MaxService <= 0)
                    {
                        continue;
                    }

                    // First sight of this prefab since the last load is the authored state, because
                    // prefab data is rebuilt from source and nothing has been written to it yet.
                    if (!m_Authored.TryGetValue(prefab, out int2 authored))
                    {
                        authored = new int2(data.m_MaxService, data.m_ServiceConsuming);
                        m_Authored[prefab] = authored;
                    }

                    data.m_MaxService = (int)math.min((long)authored.x * multiplier, int.MaxValue);

                    // Consumption per visit, which is what the charge is derived from. Floored at 1
                    // so a venue never serves for nothing; the game floors the per-visit amount at
                    // 1 anyway once this drops below kUpdateInterval.
                    if (authored.y > 0)
                    {
                        data.m_ServiceConsuming =
                            (int)math.max(1L, (long)authored.y * costPercent / 100L);
                    }

                    EntityManager.SetComponentData(prefab, data);
                    adjusted++;
                }
            }
            finally
            {
                prefabs.Dispose();
            }

            m_AppliedMultiplier = multiplier;
            m_AppliedCostPercent = costPercent;
            AdjustedVenues = adjusted;

            Mod.Log.Info(
                $"Leisure venues: visit cost scaled to {costPercent}% of authored consumption and " +
                $"service capacity x{multiplier}, across {adjusted} prefab(s).");
        }
    }
}
