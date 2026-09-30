using System.Collections.Generic;
using Game;
using Game.Economy;
using Game.Prefabs;
using Game.Zones;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

namespace TourismOverhaul.Systems
{
    /// <summary>
    /// Adds dedicated hotel and motel zones, and moves the game's lodging buildings into them.
    ///
    /// The game ships 80 lodging-only zoned building prefabs — EU_CommercialHotel01,
    /// NA_CommercialHotel01, EU_CommercialMotel01, NA_CommercialMotel01, in every level and lot
    /// size. Each carries a BuildingProperties override setting m_AllowedSold to Lodging alone, so
    /// they are true hotel assets rather than ordinary shops that happen to host a hotel.
    ///
    /// The catch is that they sit in mixed zones: 20 of them among 55-120 ordinary commercial
    /// buildings in each of zone types 4, 7, 35 and 36 (the EU and NA theme variants). ZoneSpawnSystem
    /// picks a building from whichever group matches the cell's zone (:288, comparing
    /// BuildingSpawnGroupData.m_ZoneType), so zoning commercial gives a hotel maybe one time in four.
    ///
    /// So this creates two new zones and repoints those prefabs at them:
    ///
    ///   Hotels EU — EU_CommercialHotel01   Hotels NA — NA_CommercialHotel01
    ///   Motels EU — EU_CommercialMotel01   Motels NA — NA_CommercialMotel01
    ///
    /// One zone per theme per kind, which is how the game's own zones work: a stock zone prefab
    /// carries a ThemeObject naming its theme, and the zoning toolbar shows only the zones whose
    /// theme is selected (ToolbarUISystem.BindAssets filters on the ObjectRequirementElement that
    /// ThemeObject adds). Ours do the same, so European and North American lodging zones appear
    /// under their own themes beside the stock zones rather than both appearing under each.
    ///
    /// The buildings keep their own BuildingProperties overrides, so their lodging-only nature
    /// survives the move.
    ///
    /// Repointing also removes hotels from ordinary commercial zones, which is the intended effect:
    /// hotels appear where you zone for them, not at random.
    ///
    /// SAVE COMPATIBILITY: zone cells store the zone's ushort index (GenerateZonesSystem writes
    /// item.m_ZoneType straight into the Cell buffer). A save with hotel zoning painted will hold
    /// indices that resolve to nothing if this mod is later removed. That is inherent to any custom
    /// zone and worth stating plainly in the mod description.
    /// </summary>
    public partial class HotelZoneSystem : GameSystemBase
    {
        /// <summary>Prefab name prefixes that identify the game's lodging buildings.</summary>
        private const string kHotelPrefix = "CommercialHotel";
        private const string kMotelPrefix = "CommercialMotel";

        /// <summary>
        /// The themes the game's lodging assets are authored for, in the order the zones are
        /// created — which is also the order they are handed their zone indices.
        ///
        /// A zone cell stores nothing but that index, so the order is save-visible: cells painted
        /// with the old single Hotels zone land on the first entry's hotel zone, and the old Motels
        /// zone on the first entry's motel zone. European is first so that an existing city keeps
        /// hotel cells as hotel cells and motel cells as motel cells across the split; a North
        /// American city has its lodging cells moved over by <see cref="MigratePaintedCells"/>.
        /// </summary>
        private static readonly ThemeSplit[] kThemes =
        {
            new ThemeSplit { m_Tag = "EU", m_ThemePrefab = "European" },
            new ThemeSplit { m_Tag = "NA", m_ThemePrefab = "North American" }
        };

        /// <summary>A theme, by the prefix its building prefabs use and the theme prefab's name.</summary>
        private struct ThemeSplit
        {
            public string m_Tag;
            public string m_ThemePrefab;
        }

        /// <summary>
        /// The pair of zones belonging to one theme, with what they resolve to once ZoneSystem has
        /// handed them their indices. Resolved once per pass by <see cref="TryResolveZones"/>,
        /// because every lookup here costs a prefab-to-entity round trip and the passes below ask
        /// the same questions for every building prefab and every zone cell on the map.
        /// </summary>
        private sealed class LodgingZones
        {
            public string m_Tag;
            public ZonePrefab m_Hotels;
            public ZonePrefab m_Motels;

            public Entity m_HotelEntity;
            public Entity m_MotelEntity;
            public ZoneType m_HotelType;
            public ZoneType m_MotelType;

            /// <summary>Cells found painted with these zones by the last count.</summary>
            public int m_HotelCells;
            public int m_MotelCells;

            /// <summary>
            /// True when m_Hotels and m_Motels are the Hotels &amp; Motels asset pack's zones. Our own
            /// zones then still exist, hidden, as m_OwnHotels and m_OwnMotels, so that cells a save
            /// painted with them load and can be moved across (<see cref="MigrateOwnCellsToPack"/>).
            /// </summary>
            public bool m_FromPack;
            public ZonePrefab m_OwnHotels;
            public ZonePrefab m_OwnMotels;
            public ZoneType m_OwnHotelType;
            public ZoneType m_OwnMotelType;
        }

        /// <summary>
        /// The Hotels &amp; Motels asset pack's zones, by theme tag: "HotelsAndMotels Hotels EU" and
        /// so on. The pack is code-free and brings its own Hotels and Motels zones; with both
        /// installed, those become the lodging zones here, so players see one set.
        /// </summary>
        private static string PackHotelZoneName(string tag) => "HotelsAndMotels Hotels " + tag;

        private static string PackMotelZoneName(string tag) => "HotelsAndMotels Motels " + tag;

        private static string HotelZoneName(string tag) => "TourismOverhaul Hotels " + tag;

        private static string MotelZoneName(string tag) => "TourismOverhaul Motels " + tag;

        // Served from the mod's own UI folder. The files live in ui/src/images and reach this path
        // because webpack's asset/resource rule emits them to images/ with publicPath coui://ui-mods/.
        // Names are prefixed because ui-mods is a shared namespace across every installed UI mod.
        private const string kHotelIcon = "coui://ui-mods/images/tourism-overhaul-hotels.svg";
        private const string kMotelIcon = "coui://ui-mods/images/tourism-overhaul-motels.svg";

        private EntityQuery m_BuildingPrefabQuery;
        private EntityQuery m_BlockQuery;
        private EntityQuery m_BuildingConfigurationQuery;

        private Game.Notifications.IconCommandSystem m_IconCommandSystem;

        private PrefabSystem m_PrefabSystem;

        /// <summary>One entry per theme, in <see cref="kThemes"/> order.</summary>
        private readonly List<LodgingZones> m_Zones = new List<LodgingZones>();

        /// <summary>The commercial zone the new zones copy their height range from.</summary>
        private ZonePrefab m_HeightSource;

        private bool m_ZonesCreated;
        private bool m_BuildingsMoved;

        /// <summary>Building prefabs moved into the hotel zone. For diagnostics.</summary>
        public int HotelBuildingsMoved { get; private set; }

        /// <summary>Building prefabs moved into the motel zone. For diagnostics.</summary>
        public int MotelBuildingsMoved { get; private set; }

        // The work happens once per load, in OnGameLoadingComplete. This interval only governs how
        // quickly the fallback retries if the zone types were not ready at that point, so it is
        // deliberately short — every update spent waiting is an update in which ZoneCheckSystem can
        // condemn hotels that have not yet been repointed.
        public override int GetUpdateInterval(SystemUpdatePhase phase) => 64;

        protected override void OnCreate()
        {
            base.OnCreate();

            m_PrefabSystem = World.GetOrCreateSystemManaged<PrefabSystem>();

            m_BuildingPrefabQuery = GetEntityQuery(
                ComponentType.ReadOnly<BuildingPropertyData>(),
                ComponentType.ReadOnly<SpawnableBuildingData>(),
                ComponentType.ReadOnly<BuildingSpawnGroupData>());

            m_BlockQuery = GetEntityQuery(
                ComponentType.ReadOnly<Block>(),
                ComponentType.ReadOnly<Cell>());

            m_BuildingConfigurationQuery = GetEntityQuery(
                ComponentType.ReadOnly<BuildingConfigurationData>());

            m_IconCommandSystem = World.GetOrCreateSystemManaged<Game.Notifications.IconCommandSystem>();
        }

        protected override void OnGamePreload(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGamePreload(purpose, mode);

            if (Mod.Settings != null && !Mod.Settings.EnableHotelZones)
            {
                return;
            }

            // Zones must exist before prefabs are baked into entities, so create them here rather
            // than waiting for the first simulation update.
            CreateZones();

            // The Hotel Skyscrapers zones and their hotel-only tower copies
            // (HotelZoneSystem.Towers.cs), after every hotel and motel zone so earlier zones keep
            // their indices, and here, before the save is read, so saved towers find their prefabs.
            if (m_ZonesCreated)
            {
                CreateTowerZones();
            }

            // Try to repoint the buildings straight away too. This usually fails on a cold start,
            // because ZoneSystem has not yet handed our zones their indices, and that is fine — it
            // is a free attempt at the earliest possible moment, and OnGameLoadingComplete covers
            // the normal case. Attempting it twice costs nothing and removes a whole class of
            // ordering assumption.
            m_BuildingsMoved = m_ZonesCreated && MoveBuildingsIntoZones();
        }

        protected override void OnGameLoadingComplete(Colossal.Serialization.Entities.Purpose purpose, GameMode mode)
        {
            base.OnGameLoadingComplete(purpose, mode);

            // Repoint here rather than waiting for the first OnUpdate.
            //
            // Prefab entities and zone indices both exist by now, and — crucially — this runs
            // before the simulation starts. ZoneCheckSystem validates every building as soon as it
            // does, and a hotel standing on one of our zone cells whose prefab still names an
            // ordinary commercial zone fails ValidateZoneBlocks and is condemned (:309).
            //
            // This used to run on a 4096-frame update interval, which left roughly forty seconds
            // after load in which the cells said "hotel zone" and the buildings on them said
            // "commercial" — long enough to condemn every hotel in the city. Because ZoneCheckSystem
            // also *removes* Condemned once a building validates again (:301-305), closing the
            // window is enough on its own; hotels condemned by an earlier version recover.
            if (!m_BuildingsMoved)
            {
                m_BuildingsMoved = m_ZonesCreated && MoveBuildingsIntoZones();
            }

            // After the move, so the zone types are known, and before the simulation starts, so a
            // migrated cell is never judged against the zone it used to name.
            if (m_BuildingsMoved)
            {
                MigrateOwnCellsToPack();
                MigratePaintedCells();
            }

            // Undo any condemnation that a previous session left behind. A building carries
            // Condemned into the save, and CondemnedBuildingSystem deletes it long before
            // ZoneCheckSystem gets round to clearing the flag, so without this a save made during
            // the old window would keep demolishing hotels that are now perfectly valid.
            ClearCondemnedLodging();
        }

        protected override void OnUpdate()
        {
            // Fallback only, for the case where the zone types were not ready during load.
            if (m_BuildingsMoved || !m_ZonesCreated)
            {
                return;
            }

            m_BuildingsMoved = MoveBuildingsIntoZones();
        }

        /// <summary>
        /// Creates the two zone prefabs, copying their look and toolbar placement from an existing
        /// commercial zone so they sit alongside the stock zones rather than needing new art.
        /// </summary>
        private void CreateZones()
        {
            if (m_ZonesCreated)
            {
                return;
            }

            ZonePrefab template = FindCommercialZoneTemplate();

            if (template == null)
            {
                Mod.Log.Warn("No commercial zone template found; hotel zones unavailable.");
                return;
            }

            m_HeightSource = template;

            m_ZonesCreated = true;

            for (int i = 0; i < kThemes.Length; i++)
            {
                ThemeSplit split = kThemes[i];
                ThemePrefab theme = FindTheme(split.m_ThemePrefab);

                if (theme == null)
                {
                    // Created without a theme rather than not at all: an unthemed zone shows under
                    // every theme, which is worse than the stock behaviour but still usable, where
                    // a missing zone would strand every lodging building of that theme.
                    Mod.Log.Warn(
                        $"No theme prefab named \"{split.m_ThemePrefab}\"; the {split.m_Tag} "
                        + "lodging zones will not be filtered by theme.");
                }

                // The asset pack's zones for this theme, if it is installed.
                ZonePrefab packHotels = FindZone(PackHotelZoneName(split.m_Tag));
                ZonePrefab packMotels = FindZone(PackMotelZoneName(split.m_Tag));
                bool fromPack = packHotels != null && packMotels != null;

                // Hotels before motels, and the themes in their declared order: see kThemes for
                // why the order is save-visible. Ours are created even when the pack's are used,
                // hidden from the toolbar, so a save painted with them still finds them.
                ZonePrefab ownHotels = CreateZone(HotelZoneName(split.m_Tag), template, kHotelIcon, theme, hidden: fromPack);
                ZonePrefab ownMotels = CreateZone(MotelZoneName(split.m_Tag), template, kMotelIcon, theme, hidden: fromPack);

                LodgingZones zones = new LodgingZones
                {
                    m_Tag = split.m_Tag,
                    m_Hotels = fromPack ? packHotels : ownHotels,
                    m_Motels = fromPack ? packMotels : ownMotels,
                    m_FromPack = fromPack,
                    m_OwnHotels = fromPack ? ownHotels : null,
                    m_OwnMotels = fromPack ? ownMotels : null
                };

                m_Zones.Add(zones);
                m_ZonesCreated &= ownHotels != null && ownMotels != null;

                if (fromPack)
                {
                    Mod.Log.Info(
                        $"Hotels & Motels asset pack found: its {split.m_Tag} Hotels and Motels zones are the "
                        + "lodging zones; ours are kept hidden for older saves.");
                }
            }

            if (m_ZonesCreated)
            {
                Mod.Log.Info(
                    $"Created hotel and motel zones for {kThemes.Length} theme(s): "
                    + string.Join(", ", System.Array.ConvertAll(kThemes, t => t.m_Tag)) + ".");
            }
        }

        /// <summary>A loaded zone prefab of that name, or null.</summary>
        private ZonePrefab FindZone(string name)
        {
            EntityQuery zoneQuery = GetEntityQuery(
                ComponentType.ReadOnly<ZoneData>(),
                ComponentType.ReadOnly<PrefabData>());

            NativeArray<Entity> zones = zoneQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < zones.Length; i++)
                {
                    if (m_PrefabSystem.TryGetPrefab(zones[i], out ZonePrefab prefab)
                        && prefab != null
                        && prefab.name == name)
                    {
                        return prefab;
                    }
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not look up zone \"{name}\": {e.Message}");
            }
            finally
            {
                zones.Dispose();
            }

            return null;
        }

        /// <summary>The theme prefab of that name, or null if this game does not have it.</summary>
        private ThemePrefab FindTheme(string name)
        {
            EntityQuery themeQuery = GetEntityQuery(
                ComponentType.ReadOnly<ThemeData>(),
                ComponentType.ReadOnly<PrefabData>());

            NativeArray<Entity> themes = themeQuery.ToEntityArray(Allocator.Temp);

            try
            {
                for (int i = 0; i < themes.Length; i++)
                {
                    if (m_PrefabSystem.TryGetPrefab(themes[i], out ThemePrefab prefab)
                        && prefab != null
                        && prefab.name == name)
                    {
                        return prefab;
                    }
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not look up theme \"{name}\": {e.Message}");
            }
            finally
            {
                themes.Dispose();
            }

            return null;
        }

        /// <summary>
        /// An existing commercial zone to copy colour, density and toolbar group from. Preferring a
        /// low-density one keeps the new zones visually distinct from high-rise commercial.
        /// </summary>
        private ZonePrefab FindCommercialZoneTemplate()
        {
            EntityQuery zoneQuery = GetEntityQuery(
                ComponentType.ReadOnly<ZoneData>(),
                ComponentType.ReadOnly<ZonePropertiesData>(),
                ComponentType.ReadOnly<PrefabData>());

            NativeArray<Entity> zones = zoneQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < zones.Length; i++)
                {
                    ZoneData data = EntityManager.GetComponentData<ZoneData>(zones[i]);

                    if (data.m_AreaType != Game.Zones.AreaType.Commercial)
                    {
                        continue;
                    }

                    if (m_PrefabSystem.TryGetPrefab(zones[i], out ZonePrefab prefab)
                        && prefab != null
                        && prefab.Has<UIObject>())
                    {
                        return prefab;
                    }
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not find a commercial zone template: {e.Message}");
            }
            finally
            {
                zones.Dispose();
            }

            return null;
        }

        private ZonePrefab CreateZone(string name, ZonePrefab template, string icon, ThemePrefab theme, bool hidden = false)
        {
            try
            {
                ZonePrefab zone = ScriptableObject.CreateInstance<ZonePrefab>();
                zone.name = name;
                zone.m_AreaType = Game.Zones.AreaType.Commercial;
                zone.m_Color = template.m_Color;
                zone.m_Edge = template.m_Edge;
                zone.m_Office = false;

                // Lodging only. This is what makes the zone a hotel zone: companies that cannot
                // sell Lodging have no reason to take premises here.
                ZoneProperties properties = zone.AddComponent<ZoneProperties>();
                ZoneProperties templateProperties = template.GetComponent<ZoneProperties>();

                properties.m_ScaleResidentials = false;
                properties.m_ResidentialProperties = 0f;
                properties.m_SpaceMultiplier =
                    templateProperties != null ? templateProperties.m_SpaceMultiplier : 1f;
                properties.m_AllowedSold = new[] { ResourceInEditor.Lodging };
                properties.m_AllowedInput = new[] { ResourceInEditor.Food };
                properties.m_AllowedManufactured = new ResourceInEditor[0];
                properties.m_AllowedStored = new ResourceInEditor[0];
                properties.m_FireHazardMultiplier =
                    templateProperties != null ? templateProperties.m_FireHazardMultiplier : 1f;
                properties.m_IgnoreLandValue =
                    templateProperties != null && templateProperties.m_IgnoreLandValue;
                properties.m_LevelUpResources =
                    templateProperties != null ? templateProperties.m_LevelUpResources : null;

                // The theme this zone belongs to. ThemeObject.LateInitialize adds an
                // ObjectRequirementElement naming the theme, which is what the zoning toolbar
                // filters on, so the zone appears under its own theme exactly as a stock zone does.
                if (theme != null)
                {
                    zone.AddComponent<ThemeObject>().m_Theme = theme;
                }

                // Toolbar placement, borrowed from the zone we copied so it lands in the zoning
                // menu next to the stock commercial zones.
                UIObject templateUI = template.GetComponent<UIObject>();

                // A zone with no UIObject has no toolbar entry: the hidden ones only keep old
                // saves' cells resolvable until they are moved to the pack's zones.
                if (templateUI != null && !hidden)
                {
                    UIObject ui = zone.AddComponent<UIObject>();
                    ui.m_Group = templateUI.m_Group;
                    ui.m_Priority = templateUI.m_Priority + 100;
                    // Falls back to the template's stock icon if ours is missing, so a failed UI
                    // build leaves an unlabelled-but-usable tile rather than a blank one.
                    ui.m_Icon = string.IsNullOrEmpty(icon) ? templateUI.m_Icon : icon;
                    ui.m_IsDebugObject = false;
                }

                if (!m_PrefabSystem.AddPrefab(zone))
                {
                    Mod.Log.Warn($"PrefabSystem rejected zone \"{name}\".");
                    return null;
                }

                return zone;
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not create zone \"{name}\": {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Repoints every lodging building prefab at the new zones.
        ///
        /// Two separate fields have to move together, and missing either one produces a convincing
        /// but broken result:
        ///
        ///   BuildingSpawnGroupData (shared, one ZoneType) decides what may be *built* where.
        ///   ZoneSpawnSystem matches on it (:288), so changing it both adds the building to our
        ///   zone and removes it from the commercial zone it used to sit in.
        ///
        ///   SpawnableBuildingData.m_ZonePrefab decides where the building may *stand*.
        ///   ZoneCheckSystem.ValidateZoneBlocks (:337) resolves it to a ZoneType and requires the
        ///   cells underneath to carry that same type, otherwise it attaches Condemned (:309).
        ///
        /// Moving only the spawn group is what made hotels appear and then immediately condemn:
        /// they were built in our zone, then judged against the commercial zone they still claimed
        /// to belong to.
        /// </summary>
        /// <returns>
        /// False if the zone types were not available yet, so the caller can retry on a later
        /// update rather than leaving the buildings pointing at the wrong zone forever.
        /// </returns>
        private bool MoveBuildingsIntoZones()
        {
            if (!TryResolveZones())
            {
                // Expected during OnGamePreload on a cold start; the caller retries later.
                Mod.Log.Info("Hotel zones have no zone type yet; deferring the building move.");
                return false;
            }

            int hotels = 0;
            int motels = 0;
            int unthemed = 0;

            NativeArray<Entity> prefabs = m_BuildingPrefabQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < prefabs.Length; i++)
                {
                    Entity prefab = prefabs[i];

                    // Only true lodging assets. A building that can sell other things would drag
                    // ordinary shops into the hotel zone with it.
                    if (EntityManager.GetComponentData<BuildingPropertyData>(prefab).m_AllowedSold
                        != Resource.Lodging)
                    {
                        continue;
                    }

                    string name = GetName(prefab);
                    bool isHotel = name.Contains(kHotelPrefix);

                    if (!isHotel && !name.Contains(kMotelPrefix))
                    {
                        // Signature buildings are left alone — they are placed by hand, not zoned.
                        continue;
                    }

                    // The theme is in the asset's own name: EU_CommercialHotel01, NA_CommercialMotel01.
                    // A lodging asset from some other theme has no zone of its own to go to, so it
                    // is left in whatever commercial zone it shipped in rather than being dropped
                    // into one of these — a hotel nobody can zone for is worse than a hotel that
                    // still turns up in commercial.
                    LodgingZones zones = ZonesForBuilding(name);

                    if (zones == null)
                    {
                        unthemed++;
                        continue;
                    }

                    EntityManager.SetSharedComponentManaged(
                        prefab,
                        new BuildingSpawnGroupData(isHotel ? zones.m_HotelType : zones.m_MotelType));

                    RepointZonePrefab(prefab, isHotel ? zones.m_HotelEntity : zones.m_MotelEntity);

                    if (isHotel)
                    {
                        hotels++;
                    }
                    else
                    {
                        motels++;
                    }
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not move lodging buildings: {e.Message}");
            }
            finally
            {
                prefabs.Dispose();
            }

            HotelBuildingsMoved = hotels;
            MotelBuildingsMoved = motels;

            System.Text.StringBuilder indices = new System.Text.StringBuilder();

            foreach (LodgingZones zones in m_Zones)
            {
                // The game worked out the pack's zones' heights from the pack's own buildings; the
                // vanilla lodging just moved in must fit as well, so widen rather than replace.
                AdoptHeightRange(zones.m_Hotels, widen: zones.m_FromPack);
                AdoptHeightRange(zones.m_Motels, widen: zones.m_FromPack);

                indices.Append(indices.Length > 0 ? ", " : string.Empty)
                    .Append($"{zones.m_Tag} hotels {zones.m_HotelType.m_Index}, ")
                    .Append($"motels {zones.m_MotelType.m_Index}");
            }

            AdoptTowerHeightRanges();

            Mod.Log.Info(
                $"Moved {hotels} hotel and {motels} motel building prefabs into their themed zones "
                + $"({indices})."
                + (unthemed > 0 ? $" {unthemed} lodging prefab(s) of another theme left in place." : string.Empty));

            LogPaintedCells();

            return true;
        }

        /// <summary>The zones of one theme, by its tag.</summary>
        private LodgingZones ZonesFor(string tag)
        {
            foreach (LodgingZones zones in m_Zones)
            {
                if (zones.m_Tag == tag)
                {
                    return zones;
                }
            }

            return null;
        }

        /// <summary>
        /// The zones a lodging asset belongs in, by the theme tag in its name: a prefix for the
        /// game's own assets (EU_CommercialHotel01_L1_2x2) or a suffix for the Hotels &amp; Motels
        /// pack, which names its buildings HMCommercialHotel01_L1_4x4_EU. Before the suffix was
        /// recognised, all 500 of that pack's buildings were left out of the zones as "another theme".
        /// </summary>
        private LodgingZones ZonesForBuilding(string prefabName)
        {
            foreach (LodgingZones zones in m_Zones)
            {
                if (prefabName.StartsWith(zones.m_Tag + "_", System.StringComparison.Ordinal)
                    || prefabName.EndsWith("_" + zones.m_Tag, System.StringComparison.Ordinal))
                {
                    return zones;
                }
            }

            return null;
        }

        /// <summary>
        /// Fills in each zone's entity and zone type, or reports that ZoneSystem has not handed
        /// them out yet. Everything below reads those fields rather than asking again.
        /// </summary>
        private bool TryResolveZones()
        {
            if (m_Zones.Count == 0)
            {
                return false;
            }

            foreach (LodgingZones zones in m_Zones)
            {
                if (!TryGetZoneType(zones.m_Hotels, out zones.m_HotelType)
                    || !TryGetZoneType(zones.m_Motels, out zones.m_MotelType))
                {
                    return false;
                }

                zones.m_HotelEntity = m_PrefabSystem.GetEntity(zones.m_Hotels);
                zones.m_MotelEntity = m_PrefabSystem.GetEntity(zones.m_Motels);

                if (zones.m_FromPack
                    && (!TryGetZoneType(zones.m_OwnHotels, out zones.m_OwnHotelType)
                        || !TryGetZoneType(zones.m_OwnMotels, out zones.m_OwnMotelType)))
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Counts the map cells carrying our zone types just after a load.
        ///
        /// Zone cells store a bare ushort index (GenerateZonesSystem writes ZoneType straight into
        /// the Cell buffer), so painted zoning only survives a reload if our zones are handed the
        /// same index they had when the save was written. GetNextIndex (ZoneSystem:212) hands out
        /// "first freed slot, else append", which depends on how many zone prefabs exist when ours
        /// are registered — so adding, removing or reordering any mod that ships a zone can shift
        /// ours underneath a save.
        ///
        /// This distinguishes the two failure modes that look identical in game:
        ///   painted 0, index unchanged  -> something cleared the cells during load
        ///   painted 0, index changed    -> index drift; the cells now name a different zone
        /// </summary>
        private void LogPaintedCells()
        {
            if (!CountPaintedCells(out int otherZoned, out int blockCount))
            {
                return;
            }

            System.Text.StringBuilder painted = new System.Text.StringBuilder();

            foreach (LodgingZones zones in m_Zones)
            {
                painted.Append($"{zones.m_HotelCells} {zones.m_Tag} hotel, ")
                    .Append($"{zones.m_MotelCells} {zones.m_Tag} motel, ");
            }

            Mod.Log.Info(
                $"Painted cells after load: {painted}{otherZoned} other zoned, "
                + $"across {blockCount} blocks.");
        }

        /// <summary>
        /// Tallies every zone cell on the map into the per-theme counts on <see cref="m_Zones"/>.
        /// </summary>
        private bool CountPaintedCells(out int otherZoned, out int blockCount)
        {
            otherZoned = 0;
            blockCount = 0;

            foreach (LodgingZones zones in m_Zones)
            {
                zones.m_HotelCells = 0;
                zones.m_MotelCells = 0;
            }

            NativeArray<Entity> blocks = m_BlockQuery.ToEntityArray(Allocator.Temp);

            try
            {
                blockCount = blocks.Length;

                for (int i = 0; i < blocks.Length; i++)
                {
                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blocks[i], isReadOnly: true);

                    for (int c = 0; c < cells.Length; c++)
                    {
                        ZoneType zone = cells[c].m_Zone;

                        if (zone.Equals(ZoneType.None))
                        {
                            continue;
                        }

                        if (!CountLodgingCell(zone))
                        {
                            otherZoned++;
                        }
                    }
                }

                return true;
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not count painted cells: {e.Message}");
                return false;
            }
            finally
            {
                blocks.Dispose();
            }
        }

        /// <summary>Adds a cell to whichever lodging tally owns it; false if none does.</summary>
        private bool CountLodgingCell(ZoneType zone)
        {
            foreach (LodgingZones zones in m_Zones)
            {
                if (zone.Equals(zones.m_HotelType))
                {
                    zones.m_HotelCells++;
                    return true;
                }

                if (zone.Equals(zones.m_MotelType))
                {
                    zones.m_MotelCells++;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Moves cells painted with our own lodging zones onto the asset pack's, when the pack's
        /// are the lodging zones (<see cref="LodgingZones.m_FromPack"/>).
        ///
        /// A save made before the pack was installed has its hotel and motel plots on our zones.
        /// Those zones still load, hidden, but nothing grows there any more: every lodging
        /// building now names the pack's zones, so the plots would sit empty and the hotels on
        /// them would be condemned. Moving the cells keeps the city's zoning as painted.
        /// </summary>
        private void MigrateOwnCellsToPack()
        {
            bool any = false;
            foreach (LodgingZones zones in m_Zones)
            {
                any |= zones.m_FromPack;
            }

            if (!any)
            {
                return;
            }

            NativeArray<Entity> blocks = m_BlockQuery.ToEntityArray(Allocator.Temp);

            try
            {
                int moved = 0;

                for (int i = 0; i < blocks.Length; i++)
                {
                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blocks[i]);
                    bool touched = false;

                    for (int c = 0; c < cells.Length; c++)
                    {
                        Cell cell = cells[c];

                        foreach (LodgingZones zones in m_Zones)
                        {
                            if (!zones.m_FromPack)
                            {
                                continue;
                            }

                            if (cell.m_Zone.Equals(zones.m_OwnHotelType))
                            {
                                cell.m_Zone = zones.m_HotelType;
                            }
                            else if (cell.m_Zone.Equals(zones.m_OwnMotelType))
                            {
                                cell.m_Zone = zones.m_MotelType;
                            }
                            else
                            {
                                continue;
                            }

                            cells[c] = cell;
                            touched = true;
                            moved++;
                            break;
                        }
                    }

                    // As in MigratePaintedCells: revalidate and redraw the block.
                    if (!touched)
                    {
                        continue;
                    }

                    if (!EntityManager.HasComponent<Game.Common.Updated>(blocks[i]))
                    {
                        EntityManager.AddComponent<Game.Common.Updated>(blocks[i]);
                    }

                    if (!EntityManager.HasComponent<Game.Common.BatchesUpdated>(blocks[i]))
                    {
                        EntityManager.AddComponent<Game.Common.BatchesUpdated>(blocks[i]);
                    }
                }

                if (moved > 0)
                {
                    Mod.Log.Info(
                        $"Moved {moved} lodging cell(s) from Tourism Overhaul's zones onto the Hotels & Motels "
                        + "pack's, which are the lodging zones while the pack is installed.");
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not move lodging cells onto the pack's zones: {e.Message}");
            }
            finally
            {
                blocks.Dispose();
            }
        }

        /// <summary>
        /// Moves a city's lodging cells onto its own theme's zones, once.
        ///
        /// A zone cell holds nothing but a zone index, so a city painted before the zones were
        /// split by theme comes back on the first theme's zones — see <see cref="kThemes"/>. In a
        /// North American city that means every hotel and motel plot suddenly belongs to the
        /// European zones, which its toolbar does not even show, and which build European assets.
        ///
        /// The cells are therefore moved to the city's own theme, but only in the one case where
        /// that cannot be a mistake: the city has lodging cells of another theme and none at all of
        /// its own. Once it has any of its own, this never runs again, so a player who deliberately
        /// paints a second theme's zones keeps them.
        /// </summary>
        private void MigratePaintedCells()
        {
            Game.City.CityConfigurationSystem config =
                World.GetExistingSystemManaged<Game.City.CityConfigurationSystem>();

            if (config == null || config.defaultTheme == Entity.Null)
            {
                return;
            }

            if (!m_PrefabSystem.TryGetPrefab(config.defaultTheme, out ThemePrefab cityTheme)
                || cityTheme == null)
            {
                return;
            }

            LodgingZones cityZones = null;

            foreach (ThemeSplit split in kThemes)
            {
                if (split.m_ThemePrefab == cityTheme.name)
                {
                    cityZones = ZonesFor(split.m_Tag);
                }
            }

            if (cityZones == null)
            {
                return;
            }

            // Nothing to do when the city already has lodging cells of its own theme. The count
            // also leaves each theme's tallies on m_Zones, which is what says there is anything to
            // move at all.
            if (!CountPaintedCells(out int _, out int _)
                || cityZones.m_HotelCells > 0
                || cityZones.m_MotelCells > 0)
            {
                return;
            }

            NativeArray<Entity> blocks = m_BlockQuery.ToEntityArray(Allocator.Temp);

            try
            {
                int moved = 0;

                for (int i = 0; i < blocks.Length; i++)
                {
                    DynamicBuffer<Cell> cells = EntityManager.GetBuffer<Cell>(blocks[i]);
                    bool touched = false;

                    for (int c = 0; c < cells.Length; c++)
                    {
                        Cell cell = cells[c];

                        foreach (LodgingZones other in m_Zones)
                        {
                            if (other == cityZones)
                            {
                                continue;
                            }

                            if (cell.m_Zone.Equals(other.m_HotelType))
                            {
                                cell.m_Zone = cityZones.m_HotelType;
                            }
                            else if (cell.m_Zone.Equals(other.m_MotelType))
                            {
                                cell.m_Zone = cityZones.m_MotelType;
                            }
                            else
                            {
                                continue;
                            }

                            cells[c] = cell;
                            touched = true;
                            moved++;
                            break;
                        }
                    }

                    // Both tags, as the zone tool itself adds when it writes cells
                    // (ApplyZonesSystem:101-106): Updated revalidates what stands on the block,
                    // BatchesUpdated redraws it. Without them the cells are right and the map still
                    // shows the old colour until something else touches the block.
                    if (!touched)
                    {
                        continue;
                    }

                    if (!EntityManager.HasComponent<Game.Common.Updated>(blocks[i]))
                    {
                        EntityManager.AddComponent<Game.Common.Updated>(blocks[i]);
                    }

                    if (!EntityManager.HasComponent<Game.Common.BatchesUpdated>(blocks[i]))
                    {
                        EntityManager.AddComponent<Game.Common.BatchesUpdated>(blocks[i]);
                    }
                }

                if (moved > 0)
                {
                    Mod.Log.Info(
                        $"Moved {moved} lodging cell(s) onto the {cityZones.m_Tag} zones, which this city's "
                        + $"theme ({cityTheme.name}) uses. Zoning painted before hotel and motel "
                        + "zones were split by theme comes back on the European zones.");
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not migrate painted lodging cells: {e.Message}");
            }
            finally
            {
                blocks.Dispose();
            }
        }

        /// <summary>
        /// Removes the condemned flag from lodging buildings once at load.
        ///
        /// Condemnation is not a warning — CondemnedBuildingSystem runs every 64 frames and deletes
        /// each condemned building with probability 1/4 per run (:36-40), so a building is gone
        /// within a few hundred frames. That is far quicker than waiting for ZoneCheckSystem to
        /// revalidate and clear the flag itself, which is why a hotel condemned by a mismatch could
        /// be demolished before the mismatch was even fixed.
        ///
        /// Clearing the flag here is safe rather than a cheat: ZoneCheckSystem only inspects
        /// buildings inside recently changed zoning bounds (:475-484), never the whole city, so a
        /// building condemned during load is never revisited on its own — which is why bulldozing
        /// and repainting the zone was the only thing that cleared it.
        ///
        /// The notification icon has to be removed alongside the component. ZoneCheckSystem only
        /// removes that icon on the branch where it finds Condemned still attached (:301-305), so
        /// removing the component on its own strands the icon permanently: the building is fine but
        /// still wears the condemned marker forever.
        /// </summary>
        private void ClearCondemnedLodging()
        {
            EntityQuery condemnedQuery = GetEntityQuery(
                ComponentType.ReadOnly<Game.Buildings.Condemned>(),
                ComponentType.ReadOnly<PrefabRef>(),
                ComponentType.Exclude<Game.Common.Deleted>(),
                ComponentType.Exclude<Game.Tools.Temp>());

            if (condemnedQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            if (m_BuildingConfigurationQuery.IsEmptyIgnoreFilter)
            {
                return;
            }

            Entity condemnedNotification = m_BuildingConfigurationQuery
                .GetSingleton<BuildingConfigurationData>().m_CondemnedNotification;

            Game.Notifications.IconCommandBuffer iconBuffer =
                m_IconCommandSystem.CreateCommandBuffer();

            int cleared = 0;

            NativeArray<Entity> buildings = condemnedQuery.ToEntityArray(Allocator.Temp);
            try
            {
                for (int i = 0; i < buildings.Length; i++)
                {
                    Entity prefab = EntityManager.GetComponentData<PrefabRef>(buildings[i]).m_Prefab;

                    if (!EntityManager.HasComponent<BuildingPropertyData>(prefab)
                        || EntityManager.GetComponentData<BuildingPropertyData>(prefab).m_AllowedSold
                           != Resource.Lodging)
                    {
                        continue;
                    }

                    EntityManager.RemoveComponent<Game.Buildings.Condemned>(buildings[i]);
                    iconBuffer.Remove(buildings[i], condemnedNotification);
                    cleared++;
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not clear condemned lodging buildings: {e.Message}");
            }
            finally
            {
                buildings.Dispose();
            }

            if (cleared > 0)
            {
                Mod.Log.Info($"Cleared the condemned flag from {cleared} lodging building(s) after load.");
            }
        }

        /// <summary>
        /// Points a building prefab's SpawnableBuildingData at one of our zones, so ZoneCheckSystem
        /// judges it against the zone it is actually built in rather than condemning it on sight.
        /// </summary>
        private void RepointZonePrefab(Entity buildingPrefab, Entity zoneEntity)
        {
            if (zoneEntity == Entity.Null)
            {
                return;
            }

            SpawnableBuildingData data =
                EntityManager.GetComponentData<SpawnableBuildingData>(buildingPrefab);

            data.m_ZonePrefab = zoneEntity;

            EntityManager.SetComponentData(buildingPrefab, data);
        }

        /// <summary>
        /// Gives a new zone a usable building height range.
        ///
        /// ZoneSystem creates a zone with an empty range — m_MinOddHeight and m_MinEvenHeight at
        /// ushort.MaxValue and m_MaxHeight at 0 (:180-182) — and the game widens it as buildings
        /// register against the zone. Our buildings are repointed after that has happened, so the
        /// range stays empty and no lot can ever accommodate a building: the zone paints, and then
        /// nothing is ever built on it.
        ///
        /// The buildings we moved came out of ordinary commercial zones, so that zone's range
        /// already covers them. Copying it is both correct and safely permissive.
        /// </summary>
        private void AdoptHeightRange(ZonePrefab zone, ZonePrefab heightSource = null, bool widen = false)
        {
            heightSource = heightSource ?? m_HeightSource;

            if (zone == null || heightSource == null)
            {
                return;
            }

            try
            {
                Entity target = m_PrefabSystem.GetEntity(zone);
                Entity source = m_PrefabSystem.GetEntity(heightSource);

                if (target == Entity.Null || source == Entity.Null
                    || !EntityManager.HasComponent<ZoneData>(target)
                    || !EntityManager.HasComponent<ZoneData>(source))
                {
                    return;
                }

                ZoneData sourceData = EntityManager.GetComponentData<ZoneData>(source);
                ZoneData targetData = EntityManager.GetComponentData<ZoneData>(target);

                if (widen)
                {
                    targetData.m_MinOddHeight = (ushort)System.Math.Min(targetData.m_MinOddHeight, sourceData.m_MinOddHeight);
                    targetData.m_MinEvenHeight = (ushort)System.Math.Min(targetData.m_MinEvenHeight, sourceData.m_MinEvenHeight);
                    targetData.m_MaxHeight = (ushort)System.Math.Max(targetData.m_MaxHeight, sourceData.m_MaxHeight);
                    targetData.m_ZoneFlags |= sourceData.m_ZoneFlags;
                }
                else
                {
                    targetData.m_MinOddHeight = sourceData.m_MinOddHeight;
                    targetData.m_MinEvenHeight = sourceData.m_MinEvenHeight;
                    targetData.m_MaxHeight = sourceData.m_MaxHeight;
                    targetData.m_ZoneFlags = sourceData.m_ZoneFlags;
                }

                EntityManager.SetComponentData(target, targetData);

                Mod.Log.Info(
                    $"Zone \"{zone.name}\" height range set to " +
                    $"{targetData.m_MinOddHeight}/{targetData.m_MinEvenHeight}-{targetData.m_MaxHeight}.");
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not set the height range for \"{zone.name}\": {e.Message}");
            }
        }

        /// <summary>
        /// Resolves a zone prefab to its runtime zone type.
        ///
        /// The index-zero check is the important part. A zone prefab entity carries ZoneData from
        /// the moment it is created, but ZoneSystem does not fill in m_ZoneType until
        /// InitializeZonePrefabs runs, and until then it reads as 0 — which is ZoneType.None, the
        /// value meaning "unzoned". GetNextIndex never returns 0 (ZoneSystem:216, 225), so a real
        /// zone can never legitimately have it.
        ///
        /// Without this check the early OnGamePreload attempt appeared to succeed and pointed all
        /// 80 building prefabs at zone 0, then marked the work done so the correct pass never ran.
        /// The buildings ended up in no zone at all, which is why freshly zoned land stayed empty.
        /// </summary>
        private bool TryGetZoneType(ZonePrefab zone, out ZoneType zoneType)
        {
            zoneType = default;

            if (zone == null)
            {
                return false;
            }

            try
            {
                Entity entity = m_PrefabSystem.GetEntity(zone);

                if (entity == Entity.Null || !EntityManager.HasComponent<ZoneData>(entity))
                {
                    return false;
                }

                ZoneType candidate = EntityManager.GetComponentData<ZoneData>(entity).m_ZoneType;

                if (candidate.m_Index == 0)
                {
                    return false;
                }

                zoneType = candidate;

                return true;
            }
            catch
            {
                return false;
            }
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
                // Fall through.
            }

            return string.Empty;
        }
    }
}
