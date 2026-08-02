using Game;
using Game.Buildings;
using Game.Common;
using Game.Prefabs;
using Game.Tools;
using Unity.Collections;
using Unity.Entities;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Makes old town buildings attractive in their own right.
    ///
    /// City attractiveness is the sum of AttractivenessProvider.m_Attractiveness over every building
    /// that carries one (TourismSystem:95-97), and in the base game that is essentially parks,
    /// attractions and signature landmarks. Ordinary zoned buildings contribute nothing, so a
    /// beautifully preserved old quarter draws no visitors at all unless a landmark happens to sit
    /// inside it. Tourism ends up bolted onto a handful of placed buildings rather than emerging
    /// from the character of a district.
    ///
    /// The game already lets the player mark a building as historical, which preserves its level.
    /// That is a statement about the building's character, and it is exactly the sort of place
    /// visitors go, so it should draw them. This gives every historical building a small
    /// attractiveness value.
    ///
    /// Reading the player's own flag rather than inferring one from zone or asset names means it
    /// works for any building, in any zone, from any asset pack, and it puts the decision where the
    /// player already expects to make it. Mark a street of townhouses historical and the street
    /// becomes a destination; preserve an old quarter and it draws visitors the way a real one does.
    ///
    /// Small per building on purpose. A single old house should be worth very little; a whole
    /// quarter of them should be worth travelling for. The district earns it, not the building.
    /// </summary>
    public partial class HistoricAttractivenessSystem : GameSystemBase
    {
        /// <summary>Buildings examined per update, so a large city is walked gradually.</summary>
        private const int kMaxPerUpdate = 512;

        private EntityQuery m_BuildingQuery;
        private EndFrameBarrier m_EndFrameBarrier;

        private int m_Cursor;

        /// <summary>Buildings currently granted historic attractiveness. For diagnostics.</summary>
        public int HistoricBuildings { get; private set; }

        // Rarely: buildings do not become historic on their own, and new ones appear slowly.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 4096;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_EndFrameBarrier = World.GetOrCreateSystemManaged<EndFrameBarrier>();

            m_BuildingQuery = GetEntityQuery(
                ComponentType.ReadOnly<Building>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Deleted>(),
                ComponentType.Exclude<Temp>());
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null || m_BuildingQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            int value = settings.HistoricBuildingAttractiveness;

            EntityCommandBuffer commandBuffer = m_EndFrameBarrier.CreateCommandBuffer();

            NativeArray<Entity> buildings = m_BuildingQuery.ToEntityArray(Allocator.Temp);
            try
            {
                // Resume where the last update stopped, so every building is reached in turn
                // without walking the whole city in one go.
                if (m_Cursor >= buildings.Length)
                {
                    m_Cursor = 0;
                    HistoricBuildings = 0;
                }

                int end = math_min(m_Cursor + kMaxPerUpdate, buildings.Length);

                for (int i = m_Cursor; i < end; i++)
                {
                    Apply(buildings[i], value, commandBuffer);
                }

                m_Cursor = end;
            }
            finally
            {
                buildings.Dispose();
            }
        }

        private static int math_min(int a, int b) => a < b ? a : b;

        private void Apply(Entity building, int value, EntityCommandBuffer commandBuffer)
        {
            if (!IsHistoric(building))
            {
                return;
            }

            HistoricBuildings++;

            // Zero means the feature is off; take the value back off rather than leaving a stale
            // contribution behind, so the setting can be turned off without reloading.
            if (value <= 0)
            {
                if (EntityManager.HasComponent<AttractivenessProvider>(building))
                {
                    commandBuffer.RemoveComponent<AttractivenessProvider>(building);
                }

                return;
            }

            if (EntityManager.HasComponent<AttractivenessProvider>(building))
            {
                AttractivenessProvider existing =
                    EntityManager.GetComponentData<AttractivenessProvider>(building);

                // Never lower what a building already provides. A signature building standing in an
                // old town zone keeps its authored value.
                if (existing.m_Attractiveness >= value)
                {
                    return;
                }

                commandBuffer.SetComponent(building, new AttractivenessProvider
                {
                    m_Attractiveness = value
                });

                return;
            }

            commandBuffer.AddComponent(building, new AttractivenessProvider
            {
                m_Attractiveness = value
            });
        }

        /// <summary>
        /// Whether the player has marked this building as historical.
        ///
        /// That is a flag on the building itself — BuildingFlags.Historical (0x10) in
        /// Building.m_Flags — set through the building's own panel. Reading the player's choice is
        /// better than inferring one from zone or asset names: it works for any building in any
        /// zone from any asset pack, and it puts the decision where the player already expects to
        /// make it. Mark a street of townhouses historical and the street becomes a destination.
        /// </summary>
        private bool IsHistoric(Entity building)
        {
            // Fully qualified: Game.Prefabs has its own BuildingFlags, with different values, and
            // both namespaces are in scope here.
            return (EntityManager.GetComponentData<Building>(building).m_Flags
                    & Game.Buildings.BuildingFlags.Historical) != 0;
        }
    }
}
