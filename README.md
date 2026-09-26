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
| E | Hotels cannot be zoned for — they appear at random inside mixed commercial groups | `HotelZoneSystem` |
| F | Lodging demand is measured against raw room capacity, so adding rooms suppresses hotel construction | `TouristEconomySystem` |
| G | Tourist target search uses an origin radius of zero, evicting any arrival not standing on a lane | `TouristTargetSearchSystem` |
| H | Hotel rooms are never released, so availability decays permanently | `HotelRoomReclaimSystem` |
| I | Tourists never shop — needs come from household stock they do not have | `TouristShoppingSystem` |
| J | Ordinary buildings contribute no attractiveness, so historic districts draw nobody | `HistoricAttractivenessSystem` |

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

### A3 — Park crowding

Residents and tourists pick a park through `CitizenPathfindSetup.SetupLeisureTargetJob`, which offers
every `LeisureProvider` of the chosen leisure type at cost 0 (`:164`) — only shops and restaurants
get a fullness term (`:169-184`). The nearest park wins every search however packed it is, and one
lawn was measured holding 1,487 cims.

`ParkVisitorSpreadSystem` closes a park that is over its limit (lot cells ×
**Park visitor limit**) by removing its `Game.Buildings.LeisureProvider` tag, the one per-park
filter in that search's query (`:835`), and reopens it below three quarters. Visitors already
inside keep their leisure — `LeisureSystem.SpendLeisure` reads the prefab's `LeisureProviderData`,
not the tag — and nobody is removed; the crowd thins as they leave. The tag is serialized and
not restored on load for parks, so every removed tag is put back in `PreSerialize`, on disable
and on destroy. Inside a park, visitors on an over-full lawn re-roll their spot with the native
re-path.

Tourists also reach parks through `Attractions` leisure (30% of their rolls,
`SelectLeisureType:524-527`), which scores every building with an `AttractivenessProvider` by
attractiveness and never looks at `LeisureProvider`. `AttractionCrowdingSystem` covers that route:
it damps each attraction by the visitors actually counted on site, drops a closed park to a 10%
floor, and applies the factor in a Burst job ordered after the game's `AttractionSystem`, which
rewrites every value every 256 frames. The park system also re-closes parks within 16 frames of a
save rather than on its next 512-frame pass.

Deciding who is standing in a park is a lane fact (`CreatureLaneFlags.Hangaround | EndReached`):
`ResidentFlags.Arrived` is never set for group members, and `Divert` is removed on arrival.

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

### E — Hotel and motel zones

The game ships 80 lodging-only building prefabs (`EU_`/`NA_CommercialHotel01` and `Motel01`, every
level and lot size), each overriding `m_AllowedSold` to `Lodging` alone. They are real hotel assets,
but they sit inside the ordinary commercial spawn groups — 20 among 55-120 general buildings in each
of zone types 4, 7, 35 and 36 — so zoning commercial produces a hotel roughly one time in four,
wherever the game feels like putting it. There is no vanilla way to ask for one.

`HotelZoneSystem` adds two commercial zones, **Hotels** and **Motels**, and moves those prefabs into
them. Both theme variants go into one zone each. Hotels stop appearing in ordinary commercial zones,
which is the point: they appear where you zone for them.

Getting this right meant satisfying three constraints that are easy to miss (findings 9, 10 and 12
in the diagnosis):

- Zone membership lives in **two** fields. `BuildingSpawnGroupData` controls what may be built where;
  `SpawnableBuildingData.m_ZonePrefab` controls where a building may stand. Setting only the first
  makes hotels spawn and then condemn instantly, because `ZoneCheckSystem` judges them against the
  zone they still claim.
- The repoint must finish **before the simulation's first tick**. `CondemnedBuildingSystem` deletes a
  quarter of all condemned buildings every 64 frames, so a late repoint isn't a race — it's a
  guaranteed demolition.
- A new zone starts with an empty height range and an unassigned index. Index 0 means
  `ZoneType.None`, not "zone zero", and reading it early silently assigns every building to no zone.

**Save compatibility:** zone cells store a bare `ushort` index, so hotel zoning painted into a save
resolves to nothing if the mod is later removed, and can shift if your mod list changes. That's
inherent to any custom zone, not specific to this mod.

### F — Lodging demand versus room capacity

`CommercialDemandSystem` raises lodging demand only when
`currentTourists * m_HotelRoomPercentRequirement > m_Lodging.y` (`:187`), and `m_Lodging.y` is raw
`LodgingProvider` capacity. The shipped requirement is 0.5, so the city only ever wants rooms for
half its tourists.

This also makes the hotel room multiplier self-defeating: tripling capacity triples apparent supply,
demand falls to zero, and `ZoneSpawnSystem:319` then rejects every hotel prefab because
`EvaluateDemandAndAvailability` returns 0 against a `m_MinDemand` of 1. `TouristEconomySystem` scales
the requirement by the same multiplier, so the setting changes how many guests a hotel holds without
also claiming the city is oversupplied.

## Tourist demand

A seventh demand bar, in the Demand page and the toolbar stack. `TouristDemandUISystem` mirrors
`CityInfoUISystem`: it publishes a 0–1 float advanced every frame with the native
`AdvanceSmoothDemand` constants (`CityInfoUISystem.cs:225-228` — note it falls five times faster
than it rises), and a factor array refreshed on a 256-tick `UIUpdateState`, capped at five and
sorted by absolute weight as `FactorInfo.CompareTo` does.

Demand is `max(0, (IntrinsicTarget - CurrentTourists) - roomsFree) / IntrinsicTarget` — visitors who
would come and have nowhere to sleep. `IntrinsicTarget` rather than `TargetTourists` because the
latter is lodging-capped and would read zero exactly when hotels fill, which is the opposite error.

Free rooms are subtracted from the figure itself, not merely listed as a factor beside it. Measuring
unmet appetite alone read high while over half the city's rooms stood empty — telling the player to
build when building was the one thing that would not help. So the bar peaks when appetite is high
and every room is taken, and reaches zero once a room is waiting for everyone who would come.

Nothing is drawn by the mod. Tourism is registered as a seventh member of the game's `DemandType`
enum with entries in `demandColors` and `demandIcons`, and the native `DemandSection` and
`DemandBars` render it — the latter takes its bars as an `items` prop, so one appended entry is the
whole toolbar integration. `docs/DEMAND-UI-PLAN.md` has the full trace.

Two strings the game does not supply for an unregistered type — the section title and the detail
pane text — are written into the elements it renders empty. See `docs/SESSION-NOTES.md`.

## Cruise line

A `TransportLinePrefab` created at runtime from the passenger ship line, appearing in the ship tab.
Drawn from a sea outside connection to a harbour, one per city — `Game.Prefabs.Locked` is toggled on
the prefab so the tool disappears while a line exists.

The line runs its own **Cruise Ship**, a `PrefabBase.Clone` of the largest stock passenger ship with
its capacity set from the **Cruise ship passengers** setting. The stock ship's capacity is authored on
a prefab every ordinary ferry line shares, so it could not be changed for the cruise line alone. The
route names the copy as its vehicle model, which `TransportLineSystem.CheckVehicles` enforces; the
model is only switched between voyages, so no shore party is stranded. The copy requires a theme the
city is not using, so ordinary ferry lines never select it, and it drops the stock ship's
`ObsoleteIdentifiers` so the two prefabs do not claim the same old names.

**No body is moved by hand.** Three attempts to place bodies directly all failed, each more loudly
than the last, and the rule they establish is in `docs/SESSION-NOTES.md`: bodies and routes belong to
the game, and a mod's job is to give citizens reasons. So the cycle is built out of the game's own
mechanisms — `LodgingSeeker` to earn a destination, `TripNeeded` to earn a body and a journey,
`MovingAway` to leave.

The two stops are deliberately priced against each other through
`PathUtils.GetTransportStopSpecification`. The map edge is held maximally attractive so the
complement chooses the ship; the pier is priced out so the city's commuters do not fill a vessel that
sits with its doors open for hours, and opened again for the whole window in which any party of the
call may be walking back. `WaitingPassengers` is self-healing and safe to write; `m_ComfortFactor` is
authored data and is not. Writing the figure is not enough on its own: the game rebuilds a stop's
average from its history and tags the waypoint `PathfindUpdated` only when that rebuild changes the
value, and the pathfinder reads the cost only after such a tag, so the mod writes a history that
reproduces the figure and tags the waypoint itself.

The return is the hard half, and it is built out of three parts that each failed alone. A party is
given one ordinary trip to the ship's sea connection, so the legs come out as walk, wait, board. The
recall is re-asserted periodically for parties that have wandered off, but never for one already
walking back or waiting on a route — restarting those meant only the parties that finished inside one
refresh ever reached the ship. A party held by a `TravelPurpose` is recalled even so, because
`TripNeededSystem` excludes those citizens from its query entirely and their queued journey cannot
start until the purpose comes off.

Last call starts 4.5 h x ln(1 + stay / 4 h) before sailing, so it scales with the shore leave without
growing out of hand on a long one. A recalled citizen already walking somewhere is turned round on the
spot with a `ResetTrip` event, the game's own way of re-targeting a body. A citizen whose body the game
deleted without giving it a building (no path home, in `ResidentAISystem`) can never start a trip,
because `TripNeededSystem` only serves citizens with a `CurrentBuilding`. The recall gives it back the
last building it was seen in, so it is seen walking out of that shop or attraction to the ship; if it
goes missing a second time, it is given the harbour terminal instead. Only the bodiless citizen is
placed. No body is moved.

The vessel is held at the quay by `PublicTransport.m_DepartureFrame`, which `StopBoarding` honours
only while the stop's `BoardingVehicle` names it — a field the game blanks after a load and on
unrelated waypoint changes, so the mod puts its claim back rather than letting the ship sail. If
shore leave ends with passengers still out, the call is extended in steps while they are still coming
back, at most one in-game hour past the scheduled departure. The ship sails early once the whole
shore party is back, or once it is physically full after last call; at the map edge it sails as soon
as the setting's worth of visitors is aboard. A party that reaches the sea connection by some other line leaves the city there: it can never
board, so it is released rather than counted ashore and waited for. Giving the cruise line a sea
connection no other line serves is what makes every passenger come back to the ship itself.

Passengers ashore are anchored to the terminal by a zero-price `LodgingProvider` and an empty
`Renter` buffer. The game checks that the household is in that buffer
(`TouristHouseholdBehaviorSystem:82-89`) and clears the anchor every 1024 frames when it is not; the
shore sweep restores it within 64. The buffer stays empty because a building's utility demand follows
its renters.

Almost none of this runs on the simulation thread. The shore-party sweep (`ShorePartyJob`) and
everything that reads or writes the game's vehicle and stop data (`VesselJob`) are Burst jobs scheduled
after the creature and vehicle jobs that write what they read. The main thread keeps only the
decisions that log, spawn tourists or open and close a call, made from the jobs' last observations.

## Translations

Labels are translated into all eleven other locales the game ships with. `Translations.cs` holds one
positional string array per locale against a shared key list built from `nameof(...)`, so a key
cannot drift from the property it names. Three key shapes share the table: `#` prefixes a settings
group, `@` an info-panel row, and a bare name a settings label.

`LocaleOverlay` merges English first and the locale on top, so an absent or blank entry falls back
to readable English rather than a blank label. The panel rows do the same on the frontend side,
passing their English text as the `translate()` fallback. Both properties make a translation additive
and safe to contribute a few strings at a time.

The long settings descriptions stay in English deliberately — they are paragraphs of simulation
vocabulary, and a confidently wrong description is worse than an English one because the player
cannot tell that it is wrong. Pull requests from native speakers are welcome, for those as well.

## Design constraints

- No Harmony patches, no reflection.
- Native shopping trips, pathfinding, outside connections, hotels, costs and vehicles used as-is.
- Safe to add to and remove from an existing save.
- Everything is individually toggleable; everything is off or at vanilla values by default except
  the arrival fixes and leisure pricing (below).

The second exception is `LeisureCostPercent`, which ships at 20% rather than vanilla 100%. Measured
in a live city, non-lodging spending ran at ~24,000 per household per in-game day against a budget
allowing 1,050, so wallets emptied in under an in-game day and visitors were evicted as
`TouristNoMoney` in waves long before their stay elapsed. At vanilla pricing the length-of-stay
mechanic cannot express itself, so a vanilla default would ship a feature that does not work. The
slider returns it to 100% for anyone who wants the base game's economics.

The lever is `ServiceCompanyData.m_ServiceConsuming`, which `LeisureSystem:102` divides by
`kUpdateInterval` to get the units a visit takes, and `:125` multiplies by market price to get the
charge — the same derived-output relationship as lodging. Note `EconomyUtils:548-551` clamps the
surge multiplier to `[0.7, 1.3]`, so venue capacity can never move a price more than ±30% and is not
a pricing lever despite looking like one.

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
  src/images/             zone toolbar icons, emitted to coui://ui-mods/images/
docs/TOURISM-DIAGNOSIS.md full technical trace of every defect
CHANGELOG.md              release history
```

The two zone icons are authored to the game's own conventions — 32x32, a 2:1 isometric plate, and
the stock zone palette read out of `ZoneCommercialLow.svg`. They reach the game through webpack's
`asset/resource` rule, which emits them under `publicPath: coui://ui-mods/`; `HotelZoneSystem` puts
that URL on the zone prefab's `UIObject` and falls back to the template zone's stock icon if the
file is missing, so a skipped UI build degrades to a plain tile rather than a blank one.
