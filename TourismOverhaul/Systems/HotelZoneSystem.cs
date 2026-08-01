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
    ///   Hotels — EU_CommercialHotel01 and NA_CommercialHotel01  (from zone types 4 and 7)
    ///   Motels — EU_CommercialMotel01 and NA_CommercialMotel01  (from zone types 35 and 36)
    ///
    /// Both themes go into one zone each, as asked. The game keeps its own theme filtering
    /// downstream, and the buildings retain their own BuildingProperties overrides, so their
    /// lodging-only nature survives the move.
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

        private const string kHotelZoneName = "TourismOverhaul Hotels";
        private const string kMotelZoneName = "TourismOverhaul Motels";

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

        private ZonePrefab m_HotelZone;
        private ZonePrefab m_MotelZone;

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

            m_HotelZone = CreateZone(kHotelZoneName, template, kHotelIcon);
            m_MotelZone = CreateZone(kMotelZoneName, template, kMotelIcon);

            m_ZonesCreated = m_HotelZone != null && m_MotelZone != null;

            if (m_ZonesCreated)
            {
                Mod.Log.Info($"Created zones \"{kHotelZoneName}\" and \"{kMotelZoneName}\".");
            }
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

        private ZonePrefab CreateZone(string name, ZonePrefab template, string icon)
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

                // Toolbar placement, borrowed from the zone we copied so it lands in the zoning
                // menu next to the stock commercial zones.
                UIObject templateUI = template.GetComponent<UIObject>();

                if (templateUI != null)
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
            if (!TryGetZoneType(m_HotelZone, out ZoneType hotelZoneType)
                || !TryGetZoneType(m_MotelZone, out ZoneType motelZoneType))
            {
                // Expected during OnGamePreload on a cold start; the caller retries later.
                Mod.Log.Info("Hotel zones have no zone type yet; deferring the building move.");
                return false;
            }

            Entity hotelZoneEntity = m_PrefabSystem.GetEntity(m_HotelZone);
            Entity motelZoneEntity = m_PrefabSystem.GetEntity(m_MotelZone);

            int hotels = 0;
            int motels = 0;

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

                    if (name.Contains(kHotelPrefix))
                    {
                        EntityManager.SetSharedComponentManaged(prefab, new BuildingSpawnGroupData(hotelZoneType));
                        RepointZonePrefab(prefab, hotelZoneEntity);
                        hotels++;
                    }
                    else if (name.Contains(kMotelPrefix))
                    {
                        EntityManager.SetSharedComponentManaged(prefab, new BuildingSpawnGroupData(motelZoneType));
                        RepointZonePrefab(prefab, motelZoneEntity);
                        motels++;
                    }

                    // Signature buildings are left alone — they are placed by hand, not zoned.
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

            AdoptHeightRange(m_HotelZone);
            AdoptHeightRange(m_MotelZone);

            Mod.Log.Info(
                $"Moved {hotels} hotel and {motels} motel building prefabs into their zones " +
                $"(hotel zoneType {hotelZoneType.m_Index}, motel zoneType {motelZoneType.m_Index}).");

            LogPaintedCells(hotelZoneType, motelZoneType);

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
        private void LogPaintedCells(ZoneType hotelZoneType, ZoneType motelZoneType)
        {
            int hotelCells = 0;
            int motelCells = 0;
            int otherZoned = 0;
            int blockCount = 0;

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

                        if (zone.Equals(hotelZoneType))
                        {
                            hotelCells++;
                        }
                        else if (zone.Equals(motelZoneType))
                        {
                            motelCells++;
                        }
                        else if (!zone.Equals(ZoneType.None))
                        {
                            otherZoned++;
                        }
                    }
                }
            }
            catch (System.Exception e)
            {
                Mod.Log.Warn($"Could not count painted cells: {e.Message}");
                return;
            }
            finally
            {
                blocks.Dispose();
            }

            Mod.Log.Info(
                $"Painted cells after load: {hotelCells} hotel, {motelCells} motel, " +
                $"{otherZoned} other zoned, across {blockCount} blocks.");
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
        private void AdoptHeightRange(ZonePrefab zone)
        {
            if (zone == null || m_HeightSource == null)
            {
                return;
            }

            try
            {
                Entity target = m_PrefabSystem.GetEntity(zone);
                Entity source = m_PrefabSystem.GetEntity(m_HeightSource);

                if (target == Entity.Null || source == Entity.Null
                    || !EntityManager.HasComponent<ZoneData>(target)
                    || !EntityManager.HasComponent<ZoneData>(source))
                {
                    return;
                }

                ZoneData sourceData = EntityManager.GetComponentData<ZoneData>(source);
                ZoneData targetData = EntityManager.GetComponentData<ZoneData>(target);

                targetData.m_MinOddHeight = sourceData.m_MinOddHeight;
                targetData.m_MinEvenHeight = sourceData.m_MinEvenHeight;
                targetData.m_MaxHeight = sourceData.m_MaxHeight;
                targetData.m_ZoneFlags = sourceData.m_ZoneFlags;

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
