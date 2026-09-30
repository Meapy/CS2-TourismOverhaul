using System.Collections.Generic;
using System.Text.RegularExpressions;
using Game.Economy;
using Game.Prefabs;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// The Hotel Skyscrapers zones: high-rise hotels in the game's own tower buildings.
    ///
    /// The game has no high-rise lodging buildings. Its hotel towers are ordinary high-density
    /// commercial buildings (EU_/NA_CommercialHigh..) rented by a lodging company, which is why a
    /// tower with a rooftop pool and a hotel brand sign can appear anywhere commercial is zoned and
    /// nowhere on request.
    ///
    /// So each theme gets a Hotel Skyscrapers zone holding hotel-only copies of its towers:
    ///
    ///   * Every high-density commercial family with a member at least
    ///     <see cref="kMinTowerHeight"/> tall is copied — all of its levels, because ZoneSpawnSystem only places level 1 (:304) and
    ///     BuildingUpkeepSystem.SelectSpawnableBuilding (:783) levels up within the same lot size,
    ///     zone and property data. Copying only the tall levels would leave nothing to spawn.
    ///   * A copy keeps the original's meshes, props, brand signs, rooftop pool and ThemeObject;
    ///     only its zone changes, and any BuildingProperties override is replaced with lodging-only
    ///     values. The zone's own ZoneProperties are lodging-only (CreateZone), so copies without
    ///     an override become hotels from the zone alone. The originals stay in ordinary
    ///     commercial zones.
    ///   * Copies are named "TourismOverhaul HotelTower " + the original name and are created in
    ///     OnGamePreload, before a save is read, so saved towers find their prefab by name.
    ///   * The tower zones are created after all the hotel and motel zones. Zone cells store a bare
    ///     index handed out in creation order (ZoneSystem.GetNextIndex :212), so appending keeps the
    ///     indices of zoning painted with earlier versions.
    ///
    /// The zones copy their colour, toolbar group and space multiplier from a vanilla high-density
    /// commercial zone, and their height range from it too, so towers fit.
    /// </summary>
    public partial class HotelZoneSystem
    {
        private const string kTowerIcon = "coui://ui-mods/images/tourism-overhaul-hotel-towers.svg";
        private const string kTowerPrefix = "TourismOverhaul HotelTower ";

        /// <summary>
        /// A family qualifies if any of its levels is at least this tall, in metres. The first build
        /// used 50 m and also required the originals to allow Lodging, and found no tower at all;
        /// every family's tallest height is now logged so the cut-off can be set from measurement.
        /// The Lodging requirement was dropped: a copy becomes a hotel from the zone's lodging-only
        /// properties whatever the original sold.
        /// </summary>
        private const float kMinTowerHeight = 30f;

        private static readonly Regex kHighCommercial = new Regex(
            @"^(EU|NA)_CommercialHigh\d+_L([1-5])_", RegexOptions.CultureInvariant);

        private static string TowerZoneName(string tag) => "TourismOverhaul Hotel Towers " + tag;

        /// <summary>The vanilla high-density commercial zone the tower zones copy.</summary>
        private ZonePrefab m_TowerTemplate;

        /// <summary>theme tag -> that theme's Hotel Skyscrapers zone.</summary>
        private readonly Dictionary<string, ZonePrefab> m_TowerZones = new Dictionary<string, ZonePrefab>();

        private bool m_TowersCreated;

        /// <summary>Hotel-only tower prefabs created for the skyscraper zones. For diagnostics.</summary>
        public int TowerBuildingsCreated { get; private set; }

        /// <summary>
        /// Creates one tower zone per theme and the tower copies, once per session. Called from
        /// OnGamePreload after <see cref="CreateZones"/>.
        /// </summary>
        private void CreateTowerZones()
        {
            if (m_TowersCreated)
            {
                return;
            }

            m_TowersCreated = true;
            m_TowerTemplate = FindHighCommercialZone();

            if (m_TowerTemplate == null)
            {
                Mod.Log.Warn("No high-density commercial zone found; the Hotel Skyscrapers zones are unavailable.");
                return;
            }

            for (int i = 0; i < kThemes.Length; i++)
            {
                ThemePrefab theme = FindTheme(kThemes[i].m_ThemePrefab);
                ZonePrefab zone = CreateZone(TowerZoneName(kThemes[i].m_Tag), m_TowerTemplate, kTowerIcon, theme);

                if (zone != null)
                {
                    m_TowerZones[kThemes[i].m_Tag] = zone;
                }
            }

            TowerBuildingsCreated = CreateTowerCopies();

            Mod.Log.Info(
                $"Created {m_TowerZones.Count} Hotel Skyscrapers zone(s) from \"{m_TowerTemplate.name}\" "
                + $"with {TowerBuildingsCreated} hotel tower prefabs.");
        }

        /// <summary>Gives each tower zone the height range of the high-density zone it copies.</summary>
        private void AdoptTowerHeightRanges()
        {
            foreach (ZonePrefab zone in m_TowerZones.Values)
            {
                AdoptHeightRange(zone, m_TowerTemplate);
            }
        }

        /// <summary>
        /// A vanilla high-density commercial zone. Zone prefabs carry no density flag, so this goes by
        /// the stock names ("EU Commercial High", "NA Commercial High"), which are stable.
        /// </summary>
        private ZonePrefab FindHighCommercialZone()
        {
            EntityQuery zoneQuery = GetEntityQuery(
                ComponentType.ReadOnly<ZoneData>(),
                ComponentType.ReadOnly<PrefabData>());

            NativeArray<Entity> zones = zoneQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity zone in zones)
                {
                    if (EntityManager.GetComponentData<ZoneData>(zone).m_AreaType != AreaType.Commercial)
                    {
                        continue;
                    }

                    if (m_PrefabSystem.TryGetPrefab(zone, out ZonePrefab prefab) && prefab != null
                        && prefab.name.IndexOf("Commercial High", System.StringComparison.OrdinalIgnoreCase) >= 0
                        && prefab.Has<UIObject>())
                    {
                        return prefab;
                    }
                }
            }
            finally
            {
                zones.Dispose();
            }

            return null;
        }

        private int CreateTowerCopies()
        {
            // family (name without its level) -> members, and the tallest height seen in it.
            var families = new Dictionary<string, List<BuildingPrefab>>();
            var tallest = new Dictionary<string, float>();
            var lodging = new HashSet<string>();

            NativeArray<Entity> prefabs = m_BuildingPrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                foreach (Entity entity in prefabs)
                {
                    if (!m_PrefabSystem.TryGetPrefab(entity, out BuildingPrefab building) || building == null)
                    {
                        continue;
                    }

                    Match m = kHighCommercial.Match(building.name);

                    if (!m.Success || !building.Has<SpawnableBuilding>()
                        || !m_TowerZones.ContainsKey(m.Groups[1].Value))
                    {
                        continue;
                    }

                    float height = EntityManager.HasComponent<ObjectGeometryData>(entity)
                        ? EntityManager.GetComponentData<ObjectGeometryData>(entity).m_Size.y
                        : 0f;

                    // EU_CommercialHigh01_L3_4x4 -> EU_CommercialHigh01__4x4: one key per family and lot.
                    string family = building.name.Remove(m.Groups[2].Index - 1, 2);

                    if (!families.TryGetValue(family, out List<BuildingPrefab> members))
                    {
                        members = new List<BuildingPrefab>();
                        families.Add(family, members);
                        tallest.Add(family, 0f);
                    }

                    members.Add(building);
                    tallest[family] = UnityEngine.Mathf.Max(tallest[family], height);

                    if ((EntityManager.GetComponentData<BuildingPropertyData>(entity).m_AllowedSold & Resource.Lodging) != 0)
                    {
                        lodging.Add(family);
                    }
                }
            }
            finally
            {
                prefabs.Dispose();
            }

            int created = 0;
            var report = new System.Text.StringBuilder();

            foreach (KeyValuePair<string, List<BuildingPrefab>> family in families)
            {
                report.Append(report.Length > 0 ? "; " : string.Empty)
                    .Append($"{family.Key} {tallest[family.Key]:F0} m x{family.Value.Count}")
                    .Append(lodging.Contains(family.Key) ? " (lodging)" : string.Empty);

                if (tallest[family.Key] < kMinTowerHeight)
                {
                    continue;
                }

                ZonePrefab zone = m_TowerZones[family.Key.Substring(0, 2)];

                foreach (BuildingPrefab original in family.Value)
                {
                    if (CopyAsHotelTower(original, zone))
                    {
                        created++;
                    }
                }
            }

            Mod.Log.Info(
                $"High-density commercial families ({families.Count}, cut-off {kMinTowerHeight:F0} m): {report}");

            return created;
        }

        private bool CopyAsHotelTower(BuildingPrefab original, ZonePrefab zone)
        {
            try
            {
                var copy = (BuildingPrefab)original.Clone(kTowerPrefix + original.name);
                copy.Remove<ObsoleteIdentifiers>();

                copy.GetComponent<SpawnableBuilding>().m_ZoneType = zone;

                if (copy.TryGet(out BuildingProperties properties))
                {
                    properties.m_ResidentialProperties = 0;
                    properties.m_AllowedSold = new[] { ResourceInEditor.Lodging };
                    properties.m_AllowedInput = new[] { ResourceInEditor.Food };
                    properties.m_AllowedManufactured = new ResourceInEditor[0];
                    properties.m_AllowedStored = new ResourceInEditor[0];
                }

                return m_PrefabSystem.AddPrefab(copy);
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not copy {original.name} as a hotel tower: {e.Message}");
                return false;
            }
        }
    }
}
