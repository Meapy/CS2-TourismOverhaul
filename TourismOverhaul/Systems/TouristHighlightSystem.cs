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
    /// Optional view feature — outline tourists in the world so they can be told apart from
    /// residents at a glance.
    ///
    /// This reuses the game's own outline path rather than inventing a rendering mechanism.
    /// Game.Rendering.BatchInstanceSystem.UpdateObjectInstances checks
    ///     m_ErrorData || m_WarningData || m_OverrideData || m_HighlightedData
    /// and, when any is present, ORs MeshLayer.Outline and SubMeshFlags.OutlineOnly into the mesh
    /// batch (BatchInstanceSystem.cs:425-430). Pedestrians and vehicles are both dispatched
    /// through that same object path (BatchInstanceSystem.cs:344-347, PreCullingFlags.Object), so
    /// adding Game.Tools.Highlighted gives either the standard selection outline. Every add and
    /// remove is paired with Game.Common.BatchesUpdated to force a re-batch, matching what the
    /// game does in ToolClearSystem.cs:111-112 and TransportationOverviewUISystem.ToggleHighlight.
    ///
    /// Entity chain, traced from the game:
    ///     TouristHousehold -> HouseholdCitizen -> Citizen
    ///     Citizen.CurrentTransport.m_CurrentTransport -> Resident creature
    ///         (ObjectEmergeSystem.cs:341, TripNeededSystem.cs:1615)
    ///     Resident.CurrentVehicle.m_Vehicle -> the vehicle it is riding, when aboard one
    ///
    /// Cost is driven by the number of tourist households, not the number of pedestrians in the
    /// city: the desired set is built by walking tourist households only, and the removal pass
    /// walks only what this mod has already marked.
    /// </summary>
    public partial class TouristHighlightSystem : GameSystemBase
    {
        private EntityQuery m_TouristHouseholdQuery;
        private EntityQuery m_OutlinedQuery;

        private EndFrameBarrier m_EndFrameBarrier;

        /// <summary>Number of entities currently outlined. Exposed for diagnostics.</summary>
        public int OutlinedCount { get; private set; }

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

            // Only entities this mod outlined, so tool and selection highlights are never touched.
            m_OutlinedQuery = GetEntityQuery(
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
                // Setting was turned off: clear everything we added, then stay idle.
                if (!m_OutlinedQuery.IsEmptyIgnoreFilter)
                {
                    ClearAll(commandBuffer);
                }
                return;
            }

            NativeParallelHashSet<Entity> desired = new NativeParallelHashSet<Entity>(256, Allocator.Temp);
            try
            {
                CollectTouristEntities(desired, settings.HighlightTouristVehicles);
                RemoveStaleOutlines(commandBuffer, desired);
                AddMissingOutlines(commandBuffer, desired);
            }
            finally
            {
                desired.Dispose();
            }
        }

        /// <summary>
        /// Every entity that should currently be outlined: each tourist's creature, plus the
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

        /// <summary>
        /// Drop outlines from anything no longer in the desired set — the tourist left, the
        /// household was deleted, or they got out of a vehicle.
        /// </summary>
        private void RemoveStaleOutlines(EntityCommandBuffer commandBuffer, NativeParallelHashSet<Entity> desired)
        {
            NativeArray<Entity> outlined = m_OutlinedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < outlined.Length; i++)
                {
                    if (desired.Contains(outlined[i]))
                    {
                        continue;
                    }

                    RemoveOutline(commandBuffer, outlined[i]);
                }
            }
            finally
            {
                outlined.Dispose();
            }
        }

        private void AddMissingOutlines(EntityCommandBuffer commandBuffer, NativeParallelHashSet<Entity> desired)
        {
            int count = 0;

            NativeArray<Entity> entities = desired.ToNativeArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    count++;

                    if (EntityManager.HasComponent<TouristOutline>(entities[i]))
                    {
                        continue;
                    }

                    commandBuffer.AddComponent<Highlighted>(entities[i]);
                    commandBuffer.AddComponent<TouristOutline>(entities[i]);
                    commandBuffer.AddComponent<BatchesUpdated>(entities[i]);
                }
            }
            finally
            {
                entities.Dispose();
            }

            OutlinedCount = count;
        }

        private void ClearAll(EntityCommandBuffer commandBuffer)
        {
            NativeArray<Entity> outlined = m_OutlinedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < outlined.Length; i++)
                {
                    RemoveOutline(commandBuffer, outlined[i]);
                }
            }
            finally
            {
                outlined.Dispose();
            }

            OutlinedCount = 0;
        }

        private static void RemoveOutline(EntityCommandBuffer commandBuffer, Entity entity)
        {
            commandBuffer.RemoveComponent<Highlighted>(entity);
            commandBuffer.RemoveComponent<TouristOutline>(entity);
            commandBuffer.AddComponent<BatchesUpdated>(entity);
        }
    }
}
