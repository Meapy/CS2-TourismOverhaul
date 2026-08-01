# Changelog

All notable changes to CS2 Tourism Overhaul.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning is
[semantic](https://semver.org/spec/v2.0.0.html).

## [1.3.0] — 2026-08-01

### Fixed

- **Arrivals now settle by themselves.** The fill rate was `deficit / 16`, which had a unit error —
  the deficit counts citizens while arrivals counts households, and a household is a party of two or
  three — and took no account of running 1,024 times per in-game month. A shortfall of 20,000 asked
  for 1,250 parties per update, so the spawner pinned at its ceiling and stayed there, dispatching a
  quarter of a million visitors a month to sustain eighteen thousand. Cutting the rate sixfold left
  the tourist population unchanged, which showed the surplus was pure waste: entities created,
  initialised, emptied and cleaned up, costing simulation time across the whole game for no
  visitors. The rate is now proportional to the gap, so numbers still climb whenever rooms and
  attractiveness allow, and fade to a trickle as the city fills.
- **Performance.** The replacement target search rebuilt a citywide lookup once per household rather
  than once per update, and ran on the native system's cadence despite being single-threaded rather
  than a Burst job. Room reclamation now runs four times less often, which costs a few idle seconds
  per vacated room and nothing else.

### Changed

- Arrival speed is a ceiling for pacing rather than the thing driving the rate, which is what the
  name always implied.
- Room reclamation only logs when it hits its per-update ceiling, which is the case worth knowing
  about — at steady state it was reporting every pass and burying everything else.

### Removed

- The retired hotel district experiment and the one-shot hotel asset survey, which had served its
  purpose and re-ran the same scan on every load.

## [1.2.0] — 2026-08-01

### Fixed

- **Tourists arriving by plane, ferry or train left again within minutes.** The base game asks the
  pathfinder for a destination using an origin search radius of zero
  (`TouristFindTargetSystem:99-104`), and `CommonPathfindSetup.SetupCurrentLocationJob:83` returns
  early when that radius is zero, skipping the road fallback, the airway lookups and the radius
  search. The only thing left that can supply a starting point is a lane lying directly under the
  visitor. Road connections sit on one; air and sea connections at the map edge do not, so no origin
  was found, no destination came back, and the household was evicted as `TouristNoTarget` on its
  first and only attempt. Measured at 94-98% failure for air and sea against 34% for road, unchanged
  by free rooms, arrival rate or arrival building. `TouristTargetSearchSystem` replaces the native
  system with a 150m origin radius and three attempts before giving up. Hotel reservation is copied
  from the native `HotelReserveJob:170-199`, so booking behaviour is unchanged.
- **Hotel rooms were never released.** Availability is computed as rooms minus the length of the
  hotel's renter list, so a household that emptied without leaving that list held its room forever.
  Occupancy climbed while guests left, free rooms fell to 475 of 35,163, and 32,000 dead households
  accumulated. `HotelRoomReclaimSystem` releases those rooms and deletes the husks.
- **Hotels in signature buildings never received opening stock.** Both welcome queries required
  `PropertyRenter`, which signature buildings do not carry, so the game's flagship hotels opened with
  an empty larder while every zoned hotel opened stocked. Opening stock now also scales to the
  hotel's storage capacity, since a flat 200 units is a rounding error in a building renting
  thousands of rooms.
- The hotel welcome boost no longer clamps arrivals below the player's own setting.

### Added

- **Tourists go shopping.** `CitizenBehaviorSystem` picks shopping over leisure whenever a household
  wants something, but those wants come from household supplies running down — which a visitor with
  no home never has. Tourists sightsee for their entire stay and their money never reaches
  commercial companies. New "Shopping over sightseeing" setting, default 25%.
- **Arrivals by mode** in the Tourism info view: road, train, plane and sea, in cims per month with
  each mode's share.
- Diagnostics report arrivals against departures over the same window, departures by reason, and
  whether a departing household ever held anyone — which is what separates "arrivals are failing"
  from "visitors are leaving too soon".

### Changed

- Arrivals are reported as a smoothed rate rather than a running total. As a calendar bucket the
  figure climbed for an hour of real play before it meant anything, and reset on load.
- Departures are counted on transition rather than sampled. The old instantaneous count showed
  around 30 while thousands passed through the leaving state between snapshots.

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
