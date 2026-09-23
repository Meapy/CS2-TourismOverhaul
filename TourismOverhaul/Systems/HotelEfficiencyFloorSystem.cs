using Game;
using Game.Buildings;
using Game.Common;
using Game.Companies;
using Game.Tools;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Puts a floor under the "lack of resources" efficiency penalty for hotels.
    ///
    /// ProcessingCompanySystem treats an empty larder as all or nothing (:198):
    ///
    ///     BuildingUtils.SetEfficiencyFactor(bufferData, EfficiencyFactor.LackResources,
    ///                                       (num != 0) ? 1 : 0);
    ///
    /// A zero multiplier drops the building to 0% efficiency, so a hotel that runs out of food
    /// stops serving guests entirely, earns nothing, and folds — while still paying its staff. It
    /// cannot trade its way back out, because it needs income to buy the supplies that would
    /// restore it. A hotel with no food in the kitchen ought to be a hotel with unhappy guests, not
    /// a hotel that ceases to exist.
    ///
    /// This raises that one factor to a configurable floor for lodging companies only, leaving
    /// every other efficiency factor and every other kind of business untouched. The value is
    /// rewritten each pass because ProcessingCompanySystem resets it whenever it runs.
    /// </summary>
    public partial class HotelEfficiencyFloorSystem : GameSystemBase
    {
        private EntityQuery m_HotelQuery;

        // Applying the floor writes each hotel's Efficiency buffer, and a write has to wait for every
        // job still reading or writing that buffer, which on the main thread was 6-10 ms per update
        // against almost no work. The same pass now runs as a Burst job scheduled after them, still
        // straight after ProcessingCompanySystem's job that writes the zero, and ahead of any later
        // reader, which the job system orders behind it.
        private EntityStorageInfoLookup m_Entities;
        private ComponentLookup<PropertyRenter> m_PropertyRenters;
        private BufferLookup<Efficiency> m_EfficiencyBuffers;

        /// <summary>Hotels the last job raised to the floor.</summary>
        private NativeReference<int> m_Supported;
        private JobHandle m_LastJob;

        /// <summary>Hotels currently held above the floor, as of the last pass. For diagnostics.</summary>
        public int HotelsSupported { get; private set; }

        /// <summary>
        /// Matches ProcessingCompanySystem exactly: 262144 / (kCompanyUpdatesPerDay * 16), which is
        /// 262144 / (256 * 16) = 64 frames.
        ///
        /// This has to be the same interval, not merely a frequent one. Running every 128 frames
        /// corrected only every second write, so the factor sat at 0 half the time and the panel
        /// flickered between -100% and -50%. Registered with UpdateAfter so the correction lands in
        /// the same frame the zero is written, rather than a frame later.
        /// </summary>
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_Entities = GetEntityStorageInfoLookup();
            m_PropertyRenters = GetComponentLookup<PropertyRenter>(isReadOnly: true);
            // Read-write: the floor is applied by writing into this buffer (BuildingUtils.SetEfficiencyFactor).
            m_EfficiencyBuffers = GetBufferLookup<Efficiency>(isReadOnly: false);
            m_Supported = new NativeReference<int>(Allocator.Persistent);
            m_HotelQuery = GetEntityQuery(
                ComponentType.ReadOnly<LodgingProvider>(),
                ComponentType.ReadOnly<PropertyRenter>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnDestroy()
        {
            m_LastJob.Complete();
            m_Supported.Dispose();
            base.OnDestroy();
        }

        protected override void OnUpdate()
        {
            // Finished 64 frames ago; this only reads its count.
            m_LastJob.Complete();
            HotelsSupported = m_Supported.Value;

            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null || !settings.EnableHotelEfficiencyFloor || m_HotelQuery.IsEmptyIgnoreFilter)
            {
                HotelsSupported = 0;
                m_Supported.Value = 0;
                return;
            }

            m_Entities.Update(this);
            m_PropertyRenters.Update(this);
            m_EfficiencyBuffers.Update(this);

            NativeList<Entity> hotels = m_HotelQuery.ToEntityListAsync(Allocator.TempJob, out JobHandle hotelsReady);

            JobHandle job = new FloorJob
            {
                m_Hotels = hotels,
                // A setting of 50 means "worst case 50% efficiency", i.e. a -50% penalty rather than
                // the -100% the base game applies.
                m_Floor = math.clamp(settings.HotelEfficiencyFloor, 0, 100) / 100f,
                m_Entities = m_Entities,
                m_PropertyRenters = m_PropertyRenters,
                m_EfficiencyBuffers = m_EfficiencyBuffers,
                m_Supported = m_Supported,
            }.Schedule(JobHandle.CombineDependencies(Dependency, hotelsReady));

            hotels.Dispose(job);
            m_LastJob = job;
            Dependency = job;
        }

        [BurstCompile]
        private struct FloorJob : IJob
        {
            [ReadOnly] public NativeList<Entity> m_Hotels;
            public float m_Floor;
            [ReadOnly] public EntityStorageInfoLookup m_Entities;
            [ReadOnly] public ComponentLookup<PropertyRenter> m_PropertyRenters;
            public BufferLookup<Efficiency> m_EfficiencyBuffers;
            public NativeReference<int> m_Supported;

            public void Execute()
            {
                int supported = 0;

                for (int i = 0; i < m_Hotels.Length; i++)
                {
                    Entity property = m_PropertyRenters[m_Hotels[i]].m_Property;

                    if (property == Entity.Null
                        || !m_Entities.Exists(property)
                        || !m_EfficiencyBuffers.HasBuffer(property))
                    {
                        continue;
                    }

                    DynamicBuffer<Efficiency> efficiencies = m_EfficiencyBuffers[property];

                    for (int e = 0; e < efficiencies.Length; e++)
                    {
                        if (efficiencies[e].m_Factor != EfficiencyFactor.LackResources)
                        {
                            continue;
                        }

                        if (efficiencies[e].m_Efficiency < m_Floor)
                        {
                            BuildingUtils.SetEfficiencyFactor(
                                efficiencies, EfficiencyFactor.LackResources, m_Floor);
                            supported++;
                        }

                        break;
                    }
                }

                m_Supported.Value = supported;
            }
        }
    }
}
