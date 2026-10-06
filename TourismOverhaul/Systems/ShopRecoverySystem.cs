using Game;
using Game.Common;
using Game.Companies;
using Game.Economy;
using Game.Prefabs;
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
    /// Eases a city out of the commercial over-building that versions up to 2.0.1 caused.
    ///
    /// THE DAMAGE
    ///
    /// Up to 2.0.1, LeisurePricingSystem raised every commercial company's maximum service stock
    /// (x4 by default), not only leisure venues'. The game builds commercial for a resource while
    /// unsold stock is under 45% of capacity — CommercialDemandSystem:184 takes demand as
    /// 10 x (45 - 100 x stock / capacity), from sums CountCompanyDataSystem:360-361 builds out of
    /// each company's m_MaxService — so with every shop counted four times over, it kept building
    /// until shops held about 1.8 times their real capacity in unsold stock. The same inflated
    /// maximum hid the "not enough customers" flag (ServiceCompanySystem:169, above 90%) and kept
    /// the price near its ceiling (EconomyUtils: lerp(0.7, 1.3, 1 - stock / max)).
    ///
    /// 2.0.2 put ordinary shops back on their real capacity, and in a city grown that way every
    /// one of them is suddenly full: flagged, selling at the 0.7 floor, and commercial wealth
    /// falling. The game corrects it itself — demand is zero, so failing shops close and are not
    /// replaced — but with every shop at the floor at once, slowly and painfully.
    ///
    /// THE RECOVERY
    ///
    /// For <see cref="kRampFrames"/> after such a city loads, an ordinary shop's unsold stock is
    /// capped at a ceiling that rises from <see cref="kStartCeiling"/> of its capacity to all of
    /// it. Stock above the ceiling is discarded, which is what the game already does at the
    /// maximum; this only lowers that line for a while. At half, the price sits near its neutral
    /// 1.0 and the flag is off, and because half is above the game's 45% line, commercial demand
    /// stays at zero throughout: no new shops, and the excess still closes as it fails to sell —
    /// gradually, rather than everything at the floor at once. As the ceiling reaches 90% the
    /// flag returns wherever it is still true, and at 100% the cap ends.
    ///
    /// Measured in a 1,087-shop city that loaded with 158 shops over the line: the icons stood on
    /// 218 shops for the first snapshots after the cap took hold — they clear on each company's own
    /// update, every 1,024 frames, not when the stock falls — and then dropped to 1.
    ///
    /// Raising capacity instead was considered and would not work: it lowers stock / capacity, so
    /// the game would read the city as short of shops and build more, and over-built shops would
    /// refill the larger capacity within days anyway.
    ///
    /// Ordinary shops only. Hotels have their capacity from HotelCapacitySystem, and leisure venues
    /// from LeisurePricingSystem. A city that loads healthy is left alone.
    /// </summary>
    public partial class ShopRecoverySystem : GameSystemBase
    {
        /// <summary>Ten in-game days: 262144 frames each.</summary>
        private const uint kRampFrames = 10u * 262144u;

        /// <summary>Where the ceiling starts, as a share of capacity. Above the game's 45%.</summary>
        private const float kStartCeiling = 0.5f;

        /// <summary>
        /// Share of ordinary shops at or above the game's 90% flag on load that marks a city as
        /// over-built. Measured: a healthy 1,069-shop city loaded with 1% of them there, one grown
        /// on the inflated capacity with 20%, and the city that reported it with most. Ten per cent
        /// is ten times the healthy figure and still catches the milder case — and a recovery
        /// started where it was not needed costs ten days of capped unsold stock, nothing more.
        /// </summary>
        private const float kOverbuiltShare = 0.10f;

        private EntityQuery m_RecoveryQuery;
        private EntityQuery m_ShopQuery;
        private Game.Simulation.SimulationSystem m_SimulationSystem;

        private ComponentLookup<ServiceCompanyData> m_ServiceCompanyDatas;
        private ComponentLookup<LeisureProviderData> m_LeisureProviderDatas;
        private ComponentLookup<IndustrialProcessData> m_ProcessDatas;

        private NativeReference<int> m_Capped;
        private JobHandle m_LastJob;

        /// <summary>Shops capped since the last progress line.</summary>
        private int m_CappedSinceLog;
        private uint m_LastLogDay = uint.MaxValue;

        private bool m_FirstPassPending = true;
        private bool m_FirstPassScheduled;

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_FirstPassPending = true;
            m_FirstPassScheduled = false;
            m_LastLogDay = uint.MaxValue;
        }

        // Often enough that a shop's own update rarely sees stock far above the ceiling: a company
        // adds a sliver of a day's production per update, against a capacity of days.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 256;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_SimulationSystem = World.GetOrCreateSystemManaged<Game.Simulation.SimulationSystem>();
            m_RecoveryQuery = GetEntityQuery(ComponentType.ReadWrite<Components.ShopRecoveryData>());

            m_ShopQuery = GetEntityQuery(
                ComponentType.ReadWrite<ServiceAvailable>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.ReadOnly<CommercialCompany>(),
                ComponentType.Exclude<LodgingProvider>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_ServiceCompanyDatas = GetComponentLookup<ServiceCompanyData>(isReadOnly: true);
            m_LeisureProviderDatas = GetComponentLookup<LeisureProviderData>(isReadOnly: true);
            m_ProcessDatas = GetComponentLookup<IndustrialProcessData>(isReadOnly: true);

            m_Capped = new NativeReference<int>(Allocator.Persistent);
        }

        protected override void OnDestroy()
        {
            m_LastJob.Complete();
            m_Capped.Dispose();
            base.OnDestroy();
        }

        /// <summary>
        /// Whether a company is an ordinary shop — not a hotel, not a leisure venue — by its prefab.
        /// Shared with LeisurePricingSystem's load check, so both count the same companies.
        /// </summary>
        internal static bool IsOrdinaryShop(EntityManager entityManager, Entity prefab)
        {
            return entityManager.HasComponent<ServiceCompanyData>(prefab)
                   && !entityManager.HasComponent<LeisureProviderData>(prefab)
                   && !(entityManager.HasComponent<IndustrialProcessData>(prefab)
                        && (entityManager.GetComponentData<IndustrialProcessData>(prefab).m_Output.m_Resource
                            & Resource.Lodging) != Resource.NoResource);
        }

        /// <summary>
        /// Called from LeisurePricingSystem's load check, which sees every shop's stock before it
        /// trims anything: starts the recovery if this city loads over-built and has not had one.
        /// </summary>
        internal void ConsiderStart(int fullShops, int shops)
        {
            if (!m_RecoveryQuery.IsEmptyIgnoreFilter || shops == 0)
            {
                return;
            }

            float share = fullShops / (float)shops;

            if (share < kOverbuiltShare)
            {
                return;
            }

            Entity recovery = EntityManager.CreateEntity(ComponentType.ReadWrite<Components.ShopRecoveryData>());
            EntityManager.SetComponentData(recovery, new Components.ShopRecoveryData
            {
                m_StartFrame = m_SimulationSystem.frameIndex
            });

            Mod.Log.Info(
                $"Shop recovery started: {fullShops} of {shops} ordinary shops loaded at or above the "
                + "game's 90% \"not enough customers\" line, the mark of a city built while versions up to "
                + "2.0.1 inflated shop capacity. For ten in-game days their unsold stock is capped, rising "
                + $"from {kStartCeiling:P0} of capacity to all of it, so prices stay near normal while the "
                + "excess shops close.");
        }

        protected override void OnUpdate()
        {
            // Scheduled 256 frames ago; this only reads its count.
            m_LastJob.Complete();

            // The first pass after a start or a load is reported at once, so the cap can be seen to
            // take hold without waiting a whole in-game day for the progress line.
            if (m_FirstPassPending && m_FirstPassScheduled)
            {
                m_FirstPassPending = false;
                Mod.Log.Info($"Shop recovery first pass: {m_Capped.Value} shop(s) capped.");
            }

            m_CappedSinceLog += m_Capped.Value;
            m_Capped.Value = 0;

            if (m_RecoveryQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            Entity recoveryEntity = m_RecoveryQuery.GetSingletonEntity();
            Components.ShopRecoveryData recovery =
                EntityManager.GetComponentData<Components.ShopRecoveryData>(recoveryEntity);

            if (recovery.m_Finished != 0)
            {
                return;
            }

            uint elapsed = m_SimulationSystem.frameIndex - recovery.m_StartFrame;

            if (elapsed >= kRampFrames)
            {
                recovery.m_Finished = 1;
                EntityManager.SetComponentData(recoveryEntity, recovery);

                Mod.Log.Info(
                    "Shop recovery finished: shops are on their real capacity again, and any still "
                    + "showing \"not enough customers\" genuinely have too few.");
                return;
            }

            float ceiling = math.lerp(kStartCeiling, 1f, elapsed / (float)kRampFrames);

            LogProgressDaily(elapsed, ceiling);

            m_ServiceCompanyDatas.Update(this);
            m_LeisureProviderDatas.Update(this);
            m_ProcessDatas.Update(this);

            m_LastJob = new CapJob
            {
                m_ServiceAvailableType = GetComponentTypeHandle<ServiceAvailable>(isReadOnly: false),
                m_PrefabType = GetComponentTypeHandle<PrefabRef>(isReadOnly: true),
                m_ServiceCompanyDatas = m_ServiceCompanyDatas,
                m_LeisureProviderDatas = m_LeisureProviderDatas,
                m_ProcessDatas = m_ProcessDatas,
                m_Ceiling = ceiling,
                m_Capped = m_Capped,
            }.Schedule(m_ShopQuery, Dependency);

            Dependency = m_LastJob;
            m_FirstPassScheduled = true;
        }

        /// <summary>One line per in-game day while the recovery runs.</summary>
        private void LogProgressDaily(uint elapsed, float ceiling)
        {
            uint day = elapsed / 262144u;

            if (day == m_LastLogDay)
            {
                return;
            }

            // The first line of a session comes before any pass has run, so it has no count to give;
            // the first-pass line reports that instead.
            bool firstThisSession = m_LastLogDay == uint.MaxValue;
            m_LastLogDay = day;

            Mod.Log.Info(
                $"Shop recovery day {day + 1} of 10: unsold stock capped at {ceiling:P0} of capacity"
                + (firstThisSession ? "." : $"; {m_CappedSinceLog} cap(s) applied since the last line."));

            m_CappedSinceLog = 0;
        }

        /// <summary>Caps each ordinary shop's unsold stock at the ceiling's share of its capacity.</summary>
        [BurstCompile]
        private struct CapJob : IJobChunk
        {
            public ComponentTypeHandle<ServiceAvailable> m_ServiceAvailableType;
            [ReadOnly] public ComponentTypeHandle<PrefabRef> m_PrefabType;
            [ReadOnly] public ComponentLookup<ServiceCompanyData> m_ServiceCompanyDatas;
            [ReadOnly] public ComponentLookup<LeisureProviderData> m_LeisureProviderDatas;
            [ReadOnly] public ComponentLookup<IndustrialProcessData> m_ProcessDatas;
            public float m_Ceiling;
            public NativeReference<int> m_Capped;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                NativeArray<ServiceAvailable> stocks = chunk.GetNativeArray(ref m_ServiceAvailableType);
                NativeArray<PrefabRef> prefabs = chunk.GetNativeArray(ref m_PrefabType);

                for (int i = 0; i < stocks.Length; i++)
                {
                    Entity prefab = prefabs[i].m_Prefab;

                    // The same test as IsOrdinaryShop, through lookups.
                    if (!m_ServiceCompanyDatas.TryGetComponent(prefab, out ServiceCompanyData service)
                        || m_LeisureProviderDatas.HasComponent(prefab)
                        || (m_ProcessDatas.TryGetComponent(prefab, out IndustrialProcessData process)
                            && (process.m_Output.m_Resource & Resource.Lodging) != Resource.NoResource))
                    {
                        continue;
                    }

                    int ceiling = (int)(service.m_MaxService * m_Ceiling);
                    ServiceAvailable stock = stocks[i];

                    if (stock.m_ServiceAvailable > ceiling)
                    {
                        stock.m_ServiceAvailable = ceiling;
                        stocks[i] = stock;
                        m_Capped.Value++;
                    }
                }
            }
        }
    }
}
