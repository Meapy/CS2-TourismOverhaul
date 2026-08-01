# Changelog

All notable changes to CS2 Tourism Overhaul.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning is
[semantic](https://semver.org/spec/v2.0.0.html).

## [1.1.0] — 2026-08-01

### Added

- **Hotel and motel zones.** Two new commercial zones in the zoning toolbar. The game's 80
  lodging-only building prefabs — `EU_`/`NA_CommercialHotel01` and `Motel01`, every level and lot
  size — move into them, hotels into one and motels into the other, both theme variants together.
  Hotels no longer appear in ordinary commercial zones, so they get built where you zone for them
  rather than roughly one lot in four at the game's choosing.
- Custom isometric toolbar icons for both zones, drawn to the game's own 32x32 zone-icon
  conventions and palette.
- `MaxArrivalsPerUpdate` is now a visible setting, and its ceiling is raised from 64 to 256.
- Diagnostic logging reports the lodging demand inequality directly — rooms occupied, rooms total,
  rooms the city wants, and whether hotels will spawn — so a stalled hotel economy no longer has to
  be inferred.
- Diagnostic logging counts painted hotel and motel cells after each load, which distinguishes
  zoning lost from the save from zoning that never resolved.

### Fixed

- **Hotel construction stalling once room capacity was raised.** `CommercialDemandSystem` compares
  tourists against raw `LodgingProvider` capacity, so the hotel room multiplier tripled apparent
  supply, drove lodging demand to zero, and switched hotel construction off everywhere. The room
  requirement now scales by the same multiplier.
- **Hotels condemned the moment they were built.** Zone membership is stored in two fields, and only
  `BuildingSpawnGroupData` was being moved. `SpawnableBuildingData.m_ZonePrefab` now moves with it,
  so `ZoneCheckSystem` judges a building against the zone it actually stands in.
- **Every hotel in the city condemned on load.** The repoint ran on a 4096-frame update interval,
  leaving roughly forty seconds in which cells claimed one zone and buildings another.
  `CondemnedBuildingSystem` deletes a quarter of all condemned buildings every 64 frames, so this
  was reliably destructive rather than merely risky. Repointing now completes before the
  simulation's first tick, with a retry at 64 frames if zone indices are not ready.
- **Condemned markers that could only be cleared by bulldozing and repainting.** `ZoneCheckSystem`
  only inspects buildings inside recently changed zoning bounds, so it never revisits a building
  condemned during load. Affected lodging buildings are now cleared once at load — component *and*
  notification icon, since removing the component alone strands the icon permanently.
- **Freshly zoned hotel and motel land staying empty.** The early repoint attempt read
  `ZoneData.m_ZoneType` before `ZoneSystem` had assigned it, got 0 — which is `ZoneType.None`, not
  "zone zero" — and assigned all 80 prefabs to no zone at all. Index 0 is now correctly treated as
  "not ready yet".
- Newly created zones no longer inherit an empty height range, which had left them paintable but
  permanently unbuildable.
- Zone names and descriptions appear in the toolbar. They need `Assets.NAME[...]` and
  `Assets.DESCRIPTION[...]` keys, which are separate from the settings locale keys.
- The hotel welcome boost no longer *reduces* arrivals. Its ceiling was a hardcoded 64, which capped
  the rate below the player's own setting once that setting could exceed 64.

### Changed

- Arrival fill damping softened from `deficit / 64` to `deficit / 16`, so `MaxArrivalsPerUpdate`
  behaves as the name implies across the whole range rather than only near the target.

## [1.0.0]

Initial release.

### Added

- Adaptive outside-connection routing, so tourists stop being discarded when the city has no airport
  or harbour, and arrivals shift away from congested entrances.
- Population-scaled tourist demand in place of the flat ~1,500 ceiling.
- A real length of stay, implemented on the timer the game declares, saves and never reads.
- Honest reporting, plus three Tourism info view rows: tourists in city, hotel rooms free, and local
  cims away.
- Optional hotel room multiplier, 1x to 10x, for zoned commercial hotels.
- Optional resident holidays out of the city.
- Optional coloured ring marker on tourists and their vehicles.
