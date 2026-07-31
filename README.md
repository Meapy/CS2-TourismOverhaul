# CS2 Tourism Overhaul

A Cities: Skylines II code mod that repairs the tourism simulation.

You can max out city attractiveness and still see almost no visitors. This mod fixes the causes —
all of which were found by decompiling and tracing the installed game's own assemblies, not by
guesswork. The full trace, with the file and line behind every finding, is in
[docs/TOURISM-DIAGNOSIS.md](docs/TOURISM-DIAGNOSIS.md). Read that first; the fixes only make sense
against the defects.

## What it fixes

| # | Defect | System |
|---|---|---|
| A | 80% of tourist arrivals discarded when the city has no air or sea connection | `TouristRoutingSystem` |
| A2 | No capacity awareness — a saturated entrance keeps its full share, and arrivals that can't move on are deleted | `TouristRoutingSystem` |
| B | Tourist target is a flat ~1,500 regardless of city size | `TouristDemandSystem` |
| C | Length of stay declared, serialized, and never implemented | `TouristStaySystem` |
| D | Panel reports ~8x the achievable figure; count omits tourists in transit | `TourismReportingSystem` |

Plus optional extras: a hotel room multiplier (`HotelCapacitySystem`), resident holidays
(`ResidentTravelSystem`), three new Tourism info view rows (`TourismPanelUISystem` + the `ui`
module), and a coloured tourist marker (`TouristHighlightSystem`, `TouristMarkerRenderSystem`).

### A — Arrival routing (the big one)

`DemandPrefab.m_TouristOCSpawnParameters` is `(road .1, train .1, air .5, ship .3)`.
`BuildingUtils.GetRandomOutsideConnectionByParameters` makes **one** roll for a transfer type and
filters outside connections to exactly that type. No match means `false`, with no fallback and no
retry — but `TouristSpawnSystem` creates the household anyway, without `CurrentBuilding`.
`HouseholdInitializeSystem`'s query *requires* `CurrentBuilding` to give a household citizens, so
it never gets any people and is later destroyed as `TouristNoTarget`.

A city with no airport and no harbour converts 80% of its tourist arrivals into dead entities and
wasted pathfinding requests — about 13,000 per in-game day.

That this is an oversight rather than a design choice is strongly supported by the *departure*
path in the same utility class: `CitizenBehaviorSystem.GoToOutsideConnection` uses a combined flag
mask **and** an explicit fallback to any connection at all. Both safeguards, both absent from the
arrival path.

### A2 — Congestion

`OutsideConnectionData` carries only `m_Type` and `m_Remoteness`; there is no capacity concept
anywhere in the arrival path. `TouristRoutingSystem` measures tourists still waiting at each
connection without a destination — exactly the ones about to be deleted — and damps that route's
share with `1 / (1 + backlog / sensitivity)`, renormalising the rest. Asymptotic, so a busy route
recovers rather than being abandoned. Backlog is counted per connection, so three airports absorb
three times the traffic before air is throttled.

### B — Demand

`TourismSystem.GetTargetTourists` returns `attractiveness * 15` below 100, and the logistic in
`TourismSystem` caps attractiveness at 100 — a flat 1,500 for every city. Above 100 it grows as
`100 * log10`, so doubling attractiveness buys 200 more tourists.

`TouristDemandSystem` tops arrivals up toward a population-scaled target. It does not disable the
native spawner; it creates additional households with the same archetype, flags and connection
selection, and resolves the connection *before* creating the entity so it can never produce the
zombie households from defect A.

### C — Length of stay

`TouristHousehold.m_LeavingTime` is written as `0`, serialized, and never read.
`TourismSystem.GetTouristRandomStay()` returns one in-game day and is never called. Both are
unfinished stubs. `TouristStaySystem` implements the mechanic using the existing field, so no new
saved state is introduced, and only manages tourists holding a hotel room — day-trippers stay with
the native `TouristLeaveSystem`.

### D — Reporting

`m_AverageTourists = 12500 * probability`, against a target that never exceeds 1,500. And
`CountHouseholdDataSystem` only counts tourist households holding a `Target`, hiding every tourist
in transit.

## Design constraints

- No Harmony patches, no reflection.
- Native shopping trips, pathfinding, outside connections, hotels, costs and vehicles used as-is.
- Safe to add to and remove from an existing save.
- Everything is individually toggleable; everything is off or at vanilla values by default except
  the arrival fixes.

One deliberate exception: the optional hotel room multiplier replaces `LodgingProviderSystem` with
a copy that applies the multiplier. Room count is computed inside a Burst job from values that also
drive rent (`PropertyUtils.cs:399`) and workplace capacity (`BuildingUtils.cs:834`), so no lighter
lever exists. Turning the setting off restores the native system at runtime. See the header comment
in `HotelCapacitySystem.cs` for the maintenance risk.

## Building

Requires the in-game Modding Toolchain (Options → Modding), which sets the `CSII_*` environment
variables the project reads. Windows only — targets `net48` and runs `ModPostProcessor.exe`.

```powershell
# managed half
dotnet build .\TourismOverhaul\TourismOverhaul.csproj -c Release

# UI half — see ui/README.md for first-time setup
cd ui
npm run build
```

Both halves deploy into the same folder, and the toolchain's stock `DeployWIP` target wipes it
before copying. `TourismOverhaul.csproj` overrides that target so the two don't delete each other —
see the comment there before changing it.

## Publishing

See [PUBLISHING.md](PUBLISHING.md).

## Repository layout

```
TourismOverhaul/          managed mod (net48)
  Systems/                one file per fix
  Components/             TouristOutline marker
  Properties/             publish configuration, thumbnail
ui/                       frontend module (.mjs) for the Tourism info view rows
docs/TOURISM-DIAGNOSIS.md full technical trace of every defect
```
