using System.Text;
using Game;
using Game.Economy;
using Game.Prefabs;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// One-shot survey of the game's zoned building assets, written to the mod log.
    ///
    /// Answers a question that cannot be settled by reading the decompiled code: are there
    /// building assets specific to hotels, or can any commercial building host one?
    ///
    /// It matters because a hotel-only zone is only worth building if a distinct set of hotel
    /// buildings exists to attach to it. BuildingProperties — the component carrying
    /// m_AllowedSold — is an optional override whose tooltip says it "OVERRIDES the value that
    /// would come from the Zone Prefab Zone Properties component", so most buildings inherit
    /// their allowed resources from their zone and are not lodging-specific. Whether *any*
    /// building overrides it to lodging is an empirical question about the shipped assets.
    ///
    /// Runs once per load and logs:
    ///   - every zoned building prefab whose allowed-sold includes Lodging
    ///   - whether Lodging is exclusive on it, which is the definition of a hotel-only asset
    ///   - its zone type index, so a candidate zone can be identified
    /// </summary>
    public partial class HotelAssetSurveySystem : GameSystemBase
    {
        private EntityQuery m_BuildingPrefabQuery;
        private PrefabSystem m_PrefabSystem;

        private bool m_Surveyed;

        // Runs rarely; the survey happens once and then this idles.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 8192;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            m_BuildingPrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<BuildingPropertyData>(),
                ComponentType.ReadOnly<SpawnableBuildingData>(),
                ComponentType.ReadOnly<BuildingData>());

            RequireForUpdate(m_BuildingPrefabQuery);
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);
            m_Surveyed = false;
        }

        protected override void OnUpdate()
        {
            TourismOverhaulSetting settings = Mod.Settings;

            if (settings == null || !settings.DiagnosticLogging || m_Surveyed)
            {
                return;
            }

            m_Surveyed = true;
            Survey();
        }

        private void Survey()
        {
            NativeArray<Entity> prefabs = m_BuildingPrefabQuery.ToEntityArray(Allocator.Temp);

            StringBuilder lodgingOnly = new StringBuilder();
            StringBuilder lodgingAmong = new StringBuilder();

            int total = 0;
            int withLodging = 0;
            int exclusive = 0;

            // Per zone type: how many buildings belong to it, and how many of those are
            // lodging-only. A zone whose buildings are entirely lodging is already a hotel zone;
            // one that mixes them needs its hotel prefabs re-pointed at a new zone.
            NativeParallelHashMap<int, int2> byZone = new NativeParallelHashMap<int, int2>(64, Allocator.Temp);

            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];

                    if (!EntityManager.HasComponent<BuildingSpawnGroupData>(prefab))
                    {
                        continue;
                    }

                    int zone = EntityManager
                        .GetSharedComponentManaged<BuildingSpawnGroupData>(prefab).m_ZoneType.m_Index;

                    bool lodgingOnlyHere =
                        EntityManager.GetComponentData<BuildingPropertyData>(prefab).m_AllowedSold
                        == Resource.Lodging;

                    int2 tally = byZone.TryGetValue(zone, out int2 existing) ? existing : int2.zero;
                    tally.x++;
                    if (lodgingOnlyHere)
                    {
                        tally.y++;
                    }

                    byZone[zone] = tally;
                }
            }
            catch
            {
                // Non-fatal: the per-zone breakdown is a convenience.
            }

            StringBuilder zoneSummary = new StringBuilder();
            try
            {
                NativeArray<int> zones = byZone.GetKeyArray(Allocator.Temp);
                try
                {
                    for (int i = 0; i < zones.Length; i++)
                    {
                        int2 tally = byZone[zones[i]];

                        if (tally.y == 0)
                        {
                            continue;
                        }

                        zoneSummary.Append(
                            $"    zoneType {zones[i]}: {tally.x} buildings, {tally.y} lodging-only" +
                            (tally.x == tally.y ? "  <-- HOTEL-ONLY ZONE\n" : "  (mixed)\n"));
                    }
                }
                finally
                {
                    zones.Dispose();
                }
            }
            finally
            {
                byZone.Dispose();
            }

            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];
                    total++;

                    BuildingPropertyData property =
                        EntityManager.GetComponentData<BuildingPropertyData>(prefab);

                    if ((property.m_AllowedSold & Resource.Lodging) == Resource.NoResource)
                    {
                        continue;
                    }

                    withLodging++;

                    bool isExclusive = property.m_AllowedSold == Resource.Lodging;

                    if (isExclusive)
                    {
                        exclusive++;
                    }

                    string name = GetName(prefab);

                    SpawnableBuildingData spawnable =
                        EntityManager.GetComponentData<SpawnableBuildingData>(prefab);

                    ZoneType zoneType = default;
                    if (EntityManager.HasComponent<BuildingSpawnGroupData>(prefab))
                    {
                        zoneType = EntityManager.GetSharedComponentManaged<BuildingSpawnGroupData>(prefab).m_ZoneType;
                    }

                    BuildingData building = EntityManager.GetComponentData<BuildingData>(prefab);

                    string line =
                        $"    {name}  lot {building.m_LotSize.x}x{building.m_LotSize.y}  " +
                        $"level {spawnable.m_Level}  zoneType {zoneType.m_Index}  sold: {property.m_AllowedSold}\n";

                    if (isExclusive)
                    {
                        lodgingOnly.Append(line);
                    }
                    else
                    {
                        lodgingAmong.Append(line);
                    }
                }
            }
            finally
            {
                prefabs.Dispose();
            }

            // Mod.Log.Info(
            //     "=== hotel asset survey ===\n" +
            //     $"  {total} zoned building prefabs, {withLodging} can host lodging, " +
            //     $"{exclusive} are lodging-only\n" +
            //     "  ZONE TYPES CONTAINING HOTELS:\n" + zoneSummary +
            //     (exclusive > 0
            //         ? "  LODGING-ONLY BUILDINGS (these are true hotel assets):\n" + lodgingOnly
            //         : "  No lodging-only building assets exist — any commercial building can host a\n" +
            //           "  hotel, so there is no distinct set for a hotel zone to draw on.\n") +
            //     (withLodging > exclusive
            //         ? $"  Buildings where lodging is one option among several ({withLodging - exclusive}):\n"
            //           + lodgingAmong
            //         : string.Empty));
        }

        private string GetName(Entity prefab)
        {
            try
            {
                if (m_PrefabSystem.TryGetPrefab(prefab, out PrefabBase prefabBase) && prefabBase != null)
                {
                    return prefabBase.name;
                }
            }
            catch
            {
                // Fall through to the entity id.
            }

            return $"entity {prefab.Index}";
        }
    }
}
