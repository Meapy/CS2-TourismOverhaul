using Game;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Makes a crowded attraction less appealing, so tourists spread out instead of piling into the
    /// same few places — and makes a park the park gate has closed unappealing too.
    ///
    /// WHY IT MATTERS
    ///
    /// A tourist rolls LeisureType.Attractions 30% of the time before anything else
    /// (LeisureSystem.SelectLeisureType:524-527). That trip goes through meetings to
    /// Purpose.VisitAttractions and SetupAttractionJob, whose candidates are every Building with an
    /// AttractivenessProvider (CitizenPathfindSetup:853) — parks included — scored at
    /// -100 x attractiveness x random (:369). It never consults LeisureProvider, so it bypasses
    /// ParkVisitorSpreadSystem's gate entirely, and nothing in it notices how full a place is.
    ///
    /// WHY THE PREVIOUS VERSION DID NOTHING
    ///
    ///   1. It counted crowds by the tourist *household's* Target, which is the hotel, so it
    ///      almost never saw anyone at a park.
    ///   2. The game recomputes m_Attractiveness from prefab, efficiency, maintenance and terrain
    ///      (AttractionSystem:85-122) every 256 frames — interval 16 over 16 update groups. A
    ///      damped value written every 1,024 frames was overwritten within 256, so the damping was
    ///      live for an eighth of the time at best.
    ///
    /// HOW IT WORKS NOW
    ///
    /// Every 1,024 frames the factors are rebuilt from the visitors ParkVisitorSpreadSystem
    /// actually counts standing at each building:
    ///
    ///     factor = 1 / (1 + visitors / capacity)      (capacity from lot area x tolerance)
    ///     factor = floor                              (a park currently closed to new trips)
    ///
    /// Every frame, ordered after AttractionSystem, a Burst job multiplies the values the game has
    /// just rewritten. The query's change filter limits it to those chunks, and a value is only
    /// damped if it differs from the one this system last wrote there, so a chunk flagged changed
    /// without a real rewrite can never be damped twice. Because the game supplies a fresh base
    /// every 256 frames there is no base to capture, nothing ratchets, and switching the setting
    /// off restores every value within 256 frames with no cleanup.
    ///
    /// Damped values also feed the city's total attractiveness, so a city whose attractions are
    /// packed reads as slightly less attractive. That was always the intent.
    /// </summary>
    public partial class AttractionCrowdingSystem : GameSystemBase
    {
        /// <summary>
        /// Visitors a single lot cell can absorb before the place feels busy. A 2x2 square holds
        /// 4x this, a 10x10 attraction 25x as many — which is what makes big attractions worth it.
        /// </summary>
        private const int kVisitorsPerLotCell = 4;

        /// <summary>Floor, and the value for a closed park: still beats an ordinary building.</summary>
        private const float kMinimumFactor = 0.1f;

        /// <summary>Frames between factor rebuilds. The counts behind them change every 512.</summary>
        private const int kRebuildFrames = 1024;

        /// <summary>Change-filtered: only chunks the game has rewritten since the last run.</summary>
        private EntityQuery m_AttractionQuery;

        /// <summary>Unfiltered: every attraction, for the factor rebuild.</summary>
        private EntityQuery m_AllAttractionsQuery;
        private ParkVisitorSpreadSystem m_Parks;

        /// <summary>Attractions currently damped, and their factor. Absent means untouched.</summary>
        private NativeHashMap<Entity, float> m_Factors;

        /// <summary>The value this system last wrote to each attraction, so it never damps twice.</summary>
        private NativeHashMap<Entity, int> m_Written;

        private int m_FramesSinceRebuild = kRebuildFrames;
        private int m_RebuildsSinceLog;

        /// <summary>Attractions currently damped. For diagnostics.</summary>
        public int CrowdedAttractions { get; private set; }

        protected override void OnCreate()
        {
            base.OnCreate();

            m_Parks = World.GetOrCreateSystemManaged<ParkVisitorSpreadSystem>();
            m_Factors = new NativeHashMap<Entity, float>(256, Allocator.Persistent);
            m_Written = new NativeHashMap<Entity, int>(256, Allocator.Persistent);

            m_AttractionQuery = GetEntityQuery(
                ComponentType.ReadWrite<AttractivenessProvider>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            // Only chunks whose attractiveness was rewritten since this system last ran.
            m_AttractionQuery.SetChangedVersionFilter(ComponentType.ReadWrite<AttractivenessProvider>());

            m_AllAttractionsQuery = GetEntityQuery(
                ComponentType.ReadOnly<AttractivenessProvider>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnDestroy()
        {
            CompleteDependency();

            if (m_Factors.IsCreated)
            {
                m_Factors.Dispose();
            }

            if (m_Written.IsCreated)
            {
                m_Written.Dispose();
            }

            base.OnDestroy();
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // Keys name entities in the old world.
            CompleteDependency();
            m_Factors.Clear();
            m_Written.Clear();
            m_FramesSinceRebuild = kRebuildFrames;
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            if (++m_FramesSinceRebuild >= kRebuildFrames)
            {
                m_FramesSinceRebuild = 0;
                CompleteDependency();
                Rebuild(settings);
            }

            if (m_Factors.IsEmpty)
            {
                return;
            }

            Dependency = JobChunkExtensions.Schedule(new DampJob
            {
                m_EntityType = GetEntityTypeHandle(),
                m_ProviderType = GetComponentTypeHandle<AttractivenessProvider>(),
                m_Factors = m_Factors,
                m_Written = m_Written
            }, m_AttractionQuery, Dependency);
        }

        /// <summary>
        /// Recomputes every attraction's factor from the visitors on site. Main thread, every
        /// 1,024 frames, over a few hundred attractions at most.
        /// </summary>
        private void Rebuild(TourismOverhaulSetting settings)
        {
            m_Factors.Clear();
            CrowdedAttractions = 0;

            if (settings == null || !settings.EnableAttractionCrowding)
            {
                // The game rewrites every value within 256 frames; nothing else to undo. m_Written
                // is otherwise kept across rebuilds — it is the guard against damping a value twice.
                m_Written.Clear();
                return;
            }

            int tolerance = math.max(1, settings.AttractionCrowdTolerance);

            NativeArray<Entity> attractions = m_AllAttractionsQuery.ToEntityArray(Allocator.Temp);
            int total = attractions.Length;
            int closed = 0;
            float strongest = 1f;

            for (int i = 0; i < attractions.Length; i++)
            {
                Entity attraction = attractions[i];
                float factor;

                if (m_Parks.IsClosed(attraction))
                {
                    factor = kMinimumFactor;
                    closed++;
                }
                else if (m_Parks.TryGetVisitorsOnSite(attraction, out int visitors) && visitors > 0)
                {
                    int capacity = math.max(1, Capacity(attraction) * tolerance);
                    factor = math.max(kMinimumFactor, 1f / (1f + (float)visitors / capacity));
                }
                else
                {
                    continue;
                }

                if (factor < 0.999f)
                {
                    m_Factors[attraction] = factor;
                    CrowdedAttractions++;
                    strongest = math.min(strongest, factor);
                }
            }

            attractions.Dispose();

            if (settings.DiagnosticLogging && ++m_RebuildsSinceLog >= 8)
            {
                m_RebuildsSinceLog = 0;
                Mod.Log.Info($"Attraction crowding: {CrowdedAttractions} of {total} "
                    + $"attractions damped, {closed} of them closed parks at the floor; "
                    + $"strongest damping to {strongest:P0} of normal appeal.");
            }
        }

        /// <summary>How many visitors a place absorbs before it feels busy, from its footprint.</summary>
        private int Capacity(Entity attraction)
        {
            Entity prefab = EntityManager.GetComponentData<PrefabRef>(attraction).m_Prefab;

            if (!EntityManager.HasComponent<BuildingData>(prefab))
            {
                return kVisitorsPerLotCell;
            }

            int2 lot = EntityManager.GetComponentData<BuildingData>(prefab).m_LotSize;

            return math.max(1, lot.x * lot.y) * kVisitorsPerLotCell;
        }

        /// <summary>Damps freshly rewritten attractiveness by each attraction's factor.</summary>
        [BurstCompile]
        private struct DampJob : IJobChunk
        {
            [ReadOnly] public EntityTypeHandle m_EntityType;
            public ComponentTypeHandle<AttractivenessProvider> m_ProviderType;
            [ReadOnly] public NativeHashMap<Entity, float> m_Factors;
            public NativeHashMap<Entity, int> m_Written;

            public void Execute(in ArchetypeChunk chunk, int index, bool useMask, in v128 mask)
            {
                NativeArray<Entity> entities = chunk.GetNativeArray(m_EntityType);
                NativeArray<AttractivenessProvider> providers = chunk.GetNativeArray(ref m_ProviderType);

                for (int i = 0; i < entities.Length; i++)
                {
                    if (!m_Factors.TryGetValue(entities[i], out float factor))
                    {
                        continue;
                    }

                    int current = providers[i].m_Attractiveness;

                    // Nothing to damp — and max(1, ...) below would otherwise raise a zero. Or
                    // already damped by this system, and not rewritten since.
                    if (current <= 1
                        || (m_Written.TryGetValue(entities[i], out int written) && written == current))
                    {
                        continue;
                    }

                    int damped = math.max(1, (int)math.round(current * factor));
                    providers[i] = new AttractivenessProvider { m_Attractiveness = damped };
                    m_Written[entities[i]] = damped;
                }
            }
        }
    }
}
