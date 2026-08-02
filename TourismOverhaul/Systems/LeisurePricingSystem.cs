using Game;
using Game.Economy;
using Game.Prefabs;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Takes the sting out of leisure prices by giving venues more service capacity.
    ///
    /// WHY LEISURE DOMINATES TOURIST SPENDING
    ///
    /// LeisureSystem:125 charges a visit as
    ///
    ///     cost = serviceConsumed x marketPrice x GetServicePriceMultiplier(available, maxService)
    ///
    /// That last term is surge pricing: as a venue's service stock is drawn down against its
    /// maximum, every visit costs more. Tourists are the ideal customer for driving it, because
    /// CitizenBehaviorSystem:446-452 skips the cooldown and leisure-counter checks for them
    /// entirely — a visitor is always willing to go out. They arrive in numbers, drain venues,
    /// push the multiplier up, and then pay the higher price they created. Measured in a live city:
    /// 93% of all tourist spending went to leisure.
    ///
    /// WHAT THIS CHANGES
    ///
    /// Raising m_MaxService lowers the multiplier for the same amount of custom, so visits cost
    /// closer to the base price. It does not touch the market price, which is set by supply and
    /// demand across the whole city and should not be fought.
    ///
    /// This is the same lever HotelCapacitySystem already applies to hotels, and for the same
    /// reason: the stock figure was authored for a city where tourists barely existed.
    ///
    /// SCOPE: this affects every customer at these venues, not only tourists. Residents get cheaper
    /// leisure too. That is unavoidable — the price is a property of the venue, not the visitor —
    /// and arguably right, since the crowding that caused the surge was never meant to be there.
    /// Lodging is excluded because HotelCapacitySystem owns it and would fight over the same field.
    /// </summary>
    public partial class LeisurePricingSystem : GameSystemBase
    {
        private EntityQuery m_ServicePrefabQuery;

        private int m_AppliedMultiplier;

        /// <summary>Prefabs adjusted, for diagnostics.</summary>
        public int AdjustedVenues { get; private set; }

        // Prefab data, so this only needs to run when the setting changes.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 4096;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_ServicePrefabQuery = GetEntityQuery(
                ComponentType.ReadWrite<Game.Companies.ServiceCompanyData>(),
                ComponentType.ReadOnly<IndustrialProcessData>(),
                ComponentType.ReadOnly<PrefabData>());
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // Prefab data is rebuilt each load, so the multiplier has to be reapplied.
            m_AppliedMultiplier = 0;
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null)
            {
                return;
            }

            int multiplier = math.clamp(settings.LeisureCapacityMultiplier, 1, 10);

            // Idempotent: only the change between the applied and wanted factor is written, so this
            // never compounds if the setting is moved twice.
            if (multiplier == m_AppliedMultiplier || m_ServicePrefabQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            int previous = math.max(1, m_AppliedMultiplier);
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

                    Game.Companies.ServiceCompanyData data = EntityManager.GetComponentData<Game.Companies.ServiceCompanyData>(prefab);

                    if (data.m_MaxService <= 0)
                    {
                        continue;
                    }

                    // Rebase off the authored value rather than the current one.
                    long authored = (long)data.m_MaxService / previous;
                    long scaled = authored * multiplier;

                    data.m_MaxService = (int)math.min(scaled, int.MaxValue);

                    EntityManager.SetComponentData(prefab, data);
                    adjusted++;
                }
            }
            finally
            {
                prefabs.Dispose();
            }

            m_AppliedMultiplier = multiplier;
            AdjustedVenues = adjusted;

            Mod.Log.Info(
                $"Leisure venue service capacity scaled x{multiplier} across {adjusted} prefab(s), " +
                $"which lowers the surge multiplier applied to every visit.");
        }
    }
}
