using Game;
using Game.Citizens;
using Game.Common;
using Game.Creatures;
using Game.Tools;
using TourismOverhaul.Components;
using Unity.Collections;
using Unity.Entities;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Tracking half of the tourist marker feature: maintains the <see cref="TouristOutline"/>
    /// marker on every entity that currently represents a tourist, so
    /// <see cref="TouristMarkerRenderSystem"/> can draw them with a single cheap query each frame
    /// instead of walking households at render rate.
    ///
    /// Entity chain, traced from the game:
    ///     TouristHousehold -> HouseholdCitizen -> Citizen
    ///     Citizen.CurrentTransport.m_CurrentTransport -> Resident creature
    ///         (ObjectEmergeSystem.cs:341, TripNeededSystem.cs:1615)
    ///     Resident.CurrentVehicle.m_Vehicle -> the vehicle it is riding, when aboard one
    ///
    /// Note this deliberately does NOT use Game.Tools.Highlighted. That component does produce
    /// the native outline, but its colour is not per-entity: BatchDataSystem.cs:768 picks from
    /// the five global colours in RenderingSettingsData, and a plain Highlighted always resolves
    /// to m_HoveredColor. Recolouring it would change hover for the whole game, so the marker is
    /// drawn through OverlayRenderSystem instead, which takes an arbitrary colour per call.
    ///
    /// Cost is driven by the number of tourist households, not the number of pedestrians.
    /// </summary>
    public partial class TouristHighlightSystem : GameSystemBase
    {
        private EntityQuery m_TouristHouseholdQuery;
        private EntityQuery m_MarkedQuery;

        private EndFrameBarrier m_EndFrameBarrier;

        /// <summary>Number of entities currently marked. Exposed for diagnostics.</summary>
        public int MarkedCount { get; private set; }

        // 262144 frames per day; 128 gives 2048 updates/day, responsive enough for tourists
        // boarding and leaving vehicles without scanning anything large.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 128;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            m_TouristHouseholdQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristHousehold>(),
                ComponentType.ReadOnly<HouseholdCitizen>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());

            m_MarkedQuery = GetEntityQuery(
                ComponentType.ReadOnly<TouristOutline>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;
            if (settings == null)
            {
                return;
            }

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();

            if (!settings.HighlightTourists)
            {
                if (!m_MarkedQuery.IsEmptyIgnoreFilter)
                {
                    ClearAll(commandBuffer);
                }
                return;
            }

            NativeParallelHashSet<Entity> desired = new NativeParallelHashSet<Entity>(256, Allocator.Temp);
            try
            {
                CollectTouristEntities(desired, settings.HighlightTouristVehicles);
                RemoveStaleMarkers(commandBuffer, desired);
                AddMissingMarkers(commandBuffer, desired);
            }
            finally
            {
                desired.Dispose();
            }
        }

        /// <summary>
        /// Every entity that should currently be marked: each tourist's creature, plus the
        /// vehicle it is riding when that is enabled.
        /// </summary>
        private void CollectTouristEntities(NativeParallelHashSet<Entity> desired, bool includeVehicles)
        {
            NativeArray<Entity> households = m_TouristHouseholdQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < households.Length; i++)
                {
                    if (!EntityManager.HasBuffer<HouseholdCitizen>(households[i]))
                    {
                        continue;
                    }

                    DynamicBuffer<HouseholdCitizen> citizens =
                        EntityManager.GetBuffer<HouseholdCitizen>(households[i], isReadOnly: true);

                    for (int j = 0; j < citizens.Length; j++)
                    {
                        Entity citizen = citizens[j].m_Citizen;

                        if (citizen == Entity.Null
                            || !EntityManager.Exists(citizen)
                            || !EntityManager.HasComponent<CurrentTransport>(citizen))
                        {
                            continue;
                        }

                        Entity creature = EntityManager
                            .GetComponentData<CurrentTransport>(citizen)
                            .m_CurrentTransport;

                        if (creature == Entity.Null || !EntityManager.Exists(creature))
                        {
                            continue;
                        }

                        desired.Add(creature);

                        if (!includeVehicles || !EntityManager.HasComponent<CurrentVehicle>(creature))
                        {
                            continue;
                        }

                        Entity vehicle = EntityManager.GetComponentData<CurrentVehicle>(creature).m_Vehicle;

                        if (vehicle != Entity.Null && EntityManager.Exists(vehicle))
                        {
                            desired.Add(vehicle);
                        }
                    }
                }
            }
            finally
            {
                households.Dispose();
            }
        }

        private void RemoveStaleMarkers(EntityCommandBuffer commandBuffer, NativeParallelHashSet<Entity> desired)
        {
            NativeArray<Entity> marked = m_MarkedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < marked.Length; i++)
                {
                    if (!desired.Contains(marked[i]))
                    {
                        commandBuffer.RemoveComponent<TouristOutline>(marked[i]);
                    }
                }
            }
            finally
            {
                marked.Dispose();
            }
        }

        private void AddMissingMarkers(EntityCommandBuffer commandBuffer, NativeParallelHashSet<Entity> desired)
        {
            int count = 0;

            NativeArray<Entity> entities = desired.ToNativeArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    count++;

                    if (!EntityManager.HasComponent<TouristOutline>(entities[i]))
                    {
                        commandBuffer.AddComponent<TouristOutline>(entities[i]);
                    }
                }
            }
            finally
            {
                entities.Dispose();
            }

            MarkedCount = count;
        }

        private void ClearAll(EntityCommandBuffer commandBuffer)
        {
            commandBuffer.RemoveComponent<TouristOutline>(m_MarkedQuery, EntityQueryCaptureMode.AtPlayback);
            MarkedCount = 0;
        }
    }
}
