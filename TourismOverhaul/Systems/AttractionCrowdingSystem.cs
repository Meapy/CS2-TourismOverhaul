using Game;
using Game.Buildings;
using Game.Citizens;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Makes a crowded attraction less appealing, so visitors spread out instead of piling into the
    /// same few places.
    ///
    /// Tourists pick a destination by score, and CitizenPathfindSetup:87-91 folds the attractiveness
    /// available on the target's road edge into that score. Nothing in that calculation notices how
    /// many people are already there, so the most attractive park in the city stays the most
    /// attractive park no matter how packed it is, and everywhere else stays empty.
    ///
    /// This scales each attraction's contribution by how full it is relative to its size:
    ///
    ///     factor = 1 / (1 + crowd / capacity)
    ///
    /// The same asymptotic damping TouristRoutingSystem uses for connection backlog. A busy
    /// attraction becomes less appealing without ever becoming worthless, so crowds redistribute
    /// smoothly rather than oscillating between full and abandoned.
    ///
    /// Capacity comes from the building's footprint, so a small square saturates after a handful of
    /// visitors while a large attraction absorbs many before it starts to feel crowded — which is
    /// what makes big attractions worth building.
    ///
    /// SAFETY: m_Attractiveness is serialized, so a system that lowers it is writing to the save. It
    /// would be very easy to ratchet a city's attractiveness permanently downward, and the player
    /// would have no way to recover it. Two rules prevent that, and both matter:
    ///
    ///   1. The authored value is captured once, on first sight, and kept outside the component.
    ///      Every write is base x factor. This never reads the live value back, so an error cannot
    ///      compound.
    ///   2. Every base value is restored when the feature is switched off and when the system is
    ///      destroyed, so unloading or disabling the mod leaves the city exactly as it was.
    /// </summary>
    public partial class AttractionCrowdingSystem : GameSystemBase
    {
        /// <summary>
        /// Visitors a single lot cell can absorb before the place feels busy.
        ///
        /// A 2x2 square is 4 cells and so tolerates 4x this many; a large attraction on a 10x10 lot
        /// tolerates 25 times as many. That ratio is the point of the feature.
        /// </summary>
        private const int kVisitorsPerLotCell = 4;

        /// <summary>Floor, so a crowded attraction still beats an ordinary building.</summary>
        private const float kMinimumFactor = 0.1f;

        private EntityQuery m_AttractionQuery;
        private EntityQuery m_VisitorQuery;

        /// <summary>Authored attractiveness per building. The only copy of the real value.</summary>
        private NativeHashMap<Entity, int> m_BaseAttractiveness;

        private bool m_Applied;

        /// <summary>Attractions currently damped. For diagnostics.</summary>
        public int CrowdedAttractions { get; private set; }

        // Often enough to feel responsive as crowds move, rarely enough that the crowd count is not
        // a per-tick cost.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 1024;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_BaseAttractiveness = new NativeHashMap<Entity, int>(256, Allocator.Persistent);

            m_AttractionQuery = GetEntityQuery(
                ComponentType.ReadWrite<AttractivenessProvider>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            // Tourists already heading somewhere. Target names the destination they chose.
            m_VisitorQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.ReadOnly<Target>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnDestroy()
        {
            RestoreAll();

            if (m_BaseAttractiveness.IsCreated)
            {
                m_BaseAttractiveness.Dispose();
            }

            base.OnDestroy();
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // Entities differ between sessions, so last session's captures mean nothing. The values
            // in the save are the authored ones, since they were restored on unload.
            m_BaseAttractiveness.Clear();
            m_Applied = false;
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null || !settings.EnableAttractionCrowding)
            {
                if (m_Applied)
                {
                    RestoreAll();
                }

                return;
            }

            if (m_AttractionQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            NativeHashMap<Entity, int> crowds = new NativeHashMap<Entity, int>(256, Allocator.Temp);

            try
            {
                CountVisitors(crowds);
                ApplyCrowding(crowds, math.max(1, settings.AttractionCrowdTolerance));
            }
            finally
            {
                crowds.Dispose();
            }
        }

        /// <summary>
        /// Tallies visitors by the destination they are heading for.
        ///
        /// Counted in citizens rather than parties, since a family of four crowds a small square as
        /// much as four separate visitors do.
        /// </summary>
        private void CountVisitors(NativeHashMap<Entity, int> crowds)
        {
            ComponentTypeHandle<Target> targetHandle = GetComponentTypeHandle<Target>(isReadOnly: true);
            BufferTypeHandle<HouseholdCitizen> citizenHandle =
                GetBufferTypeHandle<HouseholdCitizen>(isReadOnly: true);

            NativeArray<ArchetypeChunk> chunks = m_VisitorQuery.ToArchetypeChunkArray(Allocator.Temp);
            try
            {
                for (int c = 0; c < chunks.Length; c++)
                {
                    ArchetypeChunk chunk = chunks[c];

                    if (!chunk.Has(ref citizenHandle))
                    {
                        continue;
                    }

                    NativeArray<Target> targets = chunk.GetNativeArray(ref targetHandle);
                    BufferAccessor<HouseholdCitizen> citizens = chunk.GetBufferAccessor(ref citizenHandle);

                    for (int i = 0; i < targets.Length; i++)
                    {
                        Entity destination = targets[i].m_Target;

                        if (destination == Entity.Null)
                        {
                            continue;
                        }

                        crowds.TryGetValue(destination, out int running);
                        crowds[destination] = running + citizens[i].Length;
                    }
                }
            }
            finally
            {
                chunks.Dispose();
            }
        }

        private void ApplyCrowding(NativeHashMap<Entity, int> crowds, int tolerance)
        {
            int crowded = 0;

            NativeArray<Entity> attractions = m_AttractionQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < attractions.Length; i++)
                {
                    Entity attraction = attractions[i];

                    int baseValue = CaptureBase(attraction);

                    if (baseValue <= 0)
                    {
                        continue;
                    }

                    crowds.TryGetValue(attraction, out int crowd);

                    int capacity = math.max(1, Capacity(attraction) * tolerance);
                    float factor = math.max(kMinimumFactor, 1f / (1f + (float)crowd / capacity));

                    if (factor < 0.999f)
                    {
                        crowded++;
                    }

                    // base x factor, never a read-modify-write of the live value.
                    int damped = math.max(1, (int)math.round(baseValue * factor));

                    AttractivenessProvider provider =
                        EntityManager.GetComponentData<AttractivenessProvider>(attraction);

                    if (provider.m_Attractiveness != damped)
                    {
                        provider.m_Attractiveness = damped;
                        EntityManager.SetComponentData(attraction, provider);
                    }
                }
            }
            finally
            {
                attractions.Dispose();
            }

            CrowdedAttractions = crowded;
            m_Applied = true;
        }

        /// <summary>
        /// The authored value, captured once. Later calls return the stored copy, so a damped value
        /// can never be mistaken for the original.
        /// </summary>
        private int CaptureBase(Entity attraction)
        {
            if (m_BaseAttractiveness.TryGetValue(attraction, out int stored))
            {
                return stored;
            }

            int authored = EntityManager.GetComponentData<AttractivenessProvider>(attraction).m_Attractiveness;

            m_BaseAttractiveness[attraction] = authored;

            return authored;
        }

        /// <summary>
        /// How many visitors a place absorbs before it feels busy, from its footprint.
        ///
        /// Lot area rather than any authored capacity, because attractions do not carry one. It is
        /// the right shape regardless: a bigger place holds more people.
        /// </summary>
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

        /// <summary>
        /// Puts every authored value back. Called when the feature is switched off and when the
        /// system is destroyed, so disabling or unloading the mod leaves the city as it was.
        /// </summary>
        private void RestoreAll()
        {
            if (!m_BaseAttractiveness.IsCreated || m_BaseAttractiveness.IsEmpty)
            {
                m_Applied = false;
                return;
            }

            NativeArray<Entity> entities = m_BaseAttractiveness.GetKeyArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    Entity attraction = entities[i];

                    if (!EntityManager.Exists(attraction)
                        || !EntityManager.HasComponent<AttractivenessProvider>(attraction))
                    {
                        continue;
                    }

                    EntityManager.SetComponentData(attraction, new AttractivenessProvider
                    {
                        m_Attractiveness = m_BaseAttractiveness[attraction]
                    });
                }
            }
            finally
            {
                entities.Dispose();
            }

            m_BaseAttractiveness.Clear();
            m_Applied = false;
            CrowdedAttractions = 0;

            Mod.Log.Info("Attraction crowding removed; authored attractiveness restored.");
        }
    }
}
