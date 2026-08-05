# Changelog

All notable changes to CS2 Tourism Overhaul.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning is
[semantic](https://semver.org/spec/v2.0.0.html).

## [1.7.1] — 2026-08-05

### Fixed

- **Hotel spending read 0$ in the Tourism Finance view for anyone who had not raised the hotel room
  multiplier.** `HotelCapacitySystem` treats that multiplier being above 1 as its enable condition,
  and it defaults to 1 — so on a default setup the system stood down, the native
  `LodgingProviderSystem` did the billing, and the counter the ledger drains was never incremented.
  Not hotels versus motels: both were missing equally, and every other category's percentage was
  overstated to match, for as long as the finance view has existed.

  The inactive path now runs an observation-only walk that counts what the native system charges
  without charging anything itself. It is not an estimate: `LodgingProviderJob:145` debits each
  guest `(int)(consumePerUpdate * marketPrice)`, and the guest count is reproduced the way the job
  arrives at it — non-tourist renters dropped, overflow above capacity evicted — both of which are
  deterministic from readable state, so the figure is the same whether the native job has already
  run that frame or is about to.

- **The lodging figure was the hotel's income rather than the guests' outgoings.** `:145` truncates
  the per-guest charge while `:148` rounds the untruncated total once across the whole hotel, so the
  two differ by roughly 2%. The ledger measures money leaving visitors, and now reports that side.

- **The arrivals `/mo.` row reset on reload and climbed for an hour of play before it meant
  anything.** It is now distinct visitors over a trailing in-game month — a companion to *Tourists
  in city*, which is who is present right now — held as a ring of 128 slices on a serialized
  singleton so it survives a reload. The two previous attempts at this row both failed on exactly
  that, because CS2 saves entities and not systems.

- **Visitors went broke and were evicted as `TouristNoMoney` in waves, collapsing tourist numbers.**
  Measured: non-lodging spending ran at ~24,000 per household per in-game day against a budget
  allowing 1,050, with leisure ~71% of it. Wallets emptied in well under an in-game day, so the
  length-of-stay mechanic never got to express itself. Routing and lodging were never involved —
  `NoTarget` and `NoHotel` held flat at 5 and 11-13 throughout.

- **`LeisurePricingSystem` was scaling the wrong field, in the wrong direction.**
  `EconomyUtils.GetServicePriceMultiplier:548-551` is `lerp(0.7, 1.3, saturate(1 - available/max))`,
  so raising a venue's maximum shrinks `available/max` and moves the price *up*, not down — and the
  whole term is clamped to ±30% regardless, which could never account for a 23x overspend. It now
  scales `m_ServiceConsuming`, which is what the charge is actually derived from. Capacity scaling
  is kept, because it lifts the production ceiling and stops venues stalling; it is simply not a
  pricing lever.

### Added

- **Leisure venue cost**, a new setting under Tourist demand, defaulting to **20%** of the base
  game's price. This is the fix for the eviction waves above. It applies to residents as well as
  visitors — the price belongs to the venue, not the customer — so venues earn proportionally less
  per visit, offset by serving more surviving visitors.

  Below about 10% it stops making any difference: `LeisureSystem:102` floors the units taken per
  visit at 1.

### Changed

- **The tourist demand bar now measures visitors who would come and have nowhere to stay**, rather
  than unmet appetite alone. Free rooms are subtracted from the figure itself instead of only
  appearing as a factor beside it, because the old reading ran high while over half the city's rooms
  stood empty — telling the player to build when building was the one thing that would not help.
  *Empty Hotel Rooms* is now reported continuously as a vacancy rate and can sit alongside a
  shortage, since some visitors having nowhere to stay and other rooms going unused are both true at
  once.

- **Arrivals fill a shortfall twice as fast.** The fill horizon halved from 256 updates to 128,
  which doubles the arrival rate for a given deficit. Paired with the leisure repricing deliberately:
  a faster spawner is only a gain if the arrivals survive long enough to be counted.

### Notes

- Saves from 1.7.0 load unchanged. The trailing-month window is a new serialized component, so an
  existing save starts it empty and fills it over the following in-game day.
- `TouristSpendingLedgerSystem` does not subtract the lodging charge from the wallet drop it
  attributes, so lodging money is counted once under *Hotels* and again under *Leisure* or
  *Unattributed*. Pre-existing, and small against the totals, but the category split is slightly
  double-counted until it is addressed.

## [1.7.0] — 2026-08-05

### Added

- **A tourist demand bar, alongside the game's own six.** It appears in the City Information
  panel's Demand page with its own factor list, and as a seventh bar in the toolbar stack, in light
  red.

  The bar reads as how much more tourism the city could carry: the gap between the visitors its
  attractiveness could support and the visitors it currently has. That deliberately uses the
  uncapped figure, because the lodging-capped one would fall to zero the moment hotels filled —
  reporting no demand at exactly the point the player most needs to build.

  The factor list says what is holding tourism back: *Lodging Shortage*, *Attractiveness*, *Empty
  Hotel Rooms*, *Visitors Already Here* and *Ways Into The City*. Signs follow the game's own
  convention, where `+` means the factor is pushing demand up — so a lodging shortage reads `+` for
  the same reason unoccupied buildings read `−` under commercial demand. The weights are indicative
  shares rather than an exact decomposition, which is worth knowing before reading much into small
  differences between them.

  Hovering the section describes tourist demand in the detail pane, as the other six do.

### Notes

- The bar is drawn by the game rather than by the mod. Tourism is registered as a seventh demand
  type, so the arrow geometry, colour, icon plate, hover and selection states, the factor rows and
  the toolbar bar are all the game's own and will follow any future changes to them.
- `DemandFactor.TouristDemand` has existed in the game's own enum since release and is never
  written by anything. It joins the unread departure timer and the uncalled stay-length function as
  a third piece of tourism that was specified and left unfinished.

## [1.6.0] — 2026-08-02

### Added

- **Translations for the eleven other languages the game ships with** — German, Spanish, French,
  Italian, Japanese, Korean, Polish, Brazilian Portuguese, Russian, Simplified Chinese and
  Traditional Chinese. Every settings group heading, option label, the Hotels and Motels zone names
  and every row label in the Tourism and Tourism Finance panels now appear in the player's own
  language.

  The long settings descriptions stay in English on purpose. They run to several paragraphs of
  domain vocabulary — surge pricing, outside connections, attractiveness — and a confidently wrong
  description is worse than an English one, because the player cannot tell that it is wrong. A new
  `LocaleOverlay` fills anything a locale has not translated from the English source, so a partial
  translation never leaves a blank label and contributions can be added a few strings at a time.
  The panel rows do the same on the frontend side: each asks the game to translate its key and
  passes the English text as the fallback, so an untranslated row renders in English rather than
  showing a raw locale key.

## [1.5.1] — 2026-08-02

### Fixed

- **Shopping was almost entirely missed, and the money was landing in the wrong category.** The
  ledger recognised a purchase by finding `ResourceBuyer` on a citizen, but that component exists
  only while a purchase is outstanding, and households are sampled every few thousand frames — so
  most trips started and settled unseen. `TouristShoppingSystem` grants the need in the first place,
  so it now marks the household with `ExpectsPurchase` at that moment and the ledger clears the mark
  with the drop that pays for it. Shops went from 1% of tourist spending to 30%.

  This also corrects the picture 1.5.0 reported. Leisure was never ~90% of tourist spending; it is
  around 46%, with shops at 30%. The earlier figure was mislabelled shopping, and the conclusion
  drawn from it — that tourist behaviour was lopsided toward leisure and would need
  `CitizenBehaviorSystem` replaced to change — was wrong.
- **Saves made with 1.5.0 failed to load** with `ComponentSerializerException: Data size mismatch`
  once a fifth spending category was added, because the ledger component had no version field and
  the reader ran past the end of the older layout. The component is renamed to `TourismLedgerData`
  so stale data is skipped rather than misread, and it now writes a version first. New fields go at
  the end and read conditionally from here on.

### Added

- Leisure and unattributed spending are separate rows. Leisure is read from `Game.Citizens.Leisure`,
  the same kind of positive marker `ResourceBuyer` provides, so "unattributed" is genuine residue
  rather than a label that sounds like an answer.

### Known

- Around 19% of tourist spending is still unattributed. The likely path is parking fees:
  `PersonalCarAISystem:890-902` moves money from the household through a transfer queue with no
  marker left on the citizen, and roughly 30% of visitors arrive with a car.
- `AttractionCrowdingSystem` damps attractiveness, which `SetupAttraction:369` reads when choosing
  an attraction — but leisure trips use `SetupLeisureTarget`, which does not. Crowds at a park from
  leisure traffic are therefore unaffected.

## [1.5.0] — 2026-08-02

### Added

- **Tourism Finance info view**, beside the stock Tourism view, with its own icon drawn to the game's
  own conventions. Shows what visitors spent over the last full month, split into hotels, shops,
  fares, leisure and unattributed, each with its share. Leisure is read from `Game.Citizens.Leisure`
  on a citizen, the same kind of positive marker `ResourceBuyer` provides for shopping, so the
  remaining "unattributed" row is genuine residue rather than a category that sounds like an answer. `TourismFinanceViewSystem` creates the view by
  copying the Tourism one and sharing its infomodes — `InfoviewPrefab.isValid` is false without at
  least one (`:67`), and the map colouring a player wants while reading tourist finances is the same
  one they want while reading tourist numbers.
- **Tourist fares counted separately from residents'.** The game pays fares straight into
  `PlayerMoney` (`ResidentAISystem:3922-3928`), so once the money arrives there is no record of who
  paid it. The fare is a property of the route though, so the ledger reproduces
  `GetTicketPrice` (`:3046-3057`) and counts on the transition onto a vehicle — each ride charged
  once, tourists only.
- **Crowded attractions lose their appeal.** Nothing in the base game notices how full somewhere
  already is, so the most attractive park stays the first choice however packed. Appeal is now
  damped by `1 / (1 + crowd / capacity)`, with capacity from the building's footprint, so small
  squares saturate quickly and large attractions absorb more.
- The spending ledger persists across saves, on a serialized singleton component.

### Fixed

- **Leisure dominated tourist spending at over 90%.** `LeisureSystem:125` charges
  `consumption x market price x GetServicePriceMultiplier(available, max)` — surge pricing that rises
  as a venue's stock is drawn down. Tourists are ideal for driving it, since
  `CitizenBehaviorSystem:446-452` skips the cooldown and leisure-counter checks for them entirely.
  `LeisurePricingSystem` gives venues more service capacity, which lowers the multiplier.
- Lodging spend is counted where guests are billed rather than estimated from elapsed frames. The
  estimate was smaller than the sampling noise, so hotels read zero and everything fell to "other".
- Fares read a structural zero because `CurrentVehicle` was being looked for on the citizen. A
  citizen is a record, not a body — the creature entity carries it, reached through
  `CurrentTransport`.
- The ledger reported zero for a whole in-game day after each load: a freshly created component
  defaulted its month to 0, which is a real month, so the first update banked a set of zeros as the
  reported figure. The sentinel is now explicit, and a banked month is only used if it contains
  something.
- Shopping trips were capped at 200 resource units, which a visitor with a normal wallet exceeded
  eight times over, so the cap bound on essentially every trip. Raising the shopping chance produced
  more trips that each bought the same token amount. The bound is now 2,000 and acts as a sanity
  limit rather than a balance figure.

### Known

- Leisure appeared to account for around 90% of tourist spending. **This was wrong** — see 1.5.1.
  Most of it was shopping the ledger failed to detect.
- `LeisurePricingSystem` matches only 15 prefabs, because parks and attractions are city services
  without `ServiceCompanyData` and so never enter the surge-pricing branch it targets. It is
  harmless but close to inert, and needs a wider query or removal.

### Changed

- Hotel room cost and visitor spending money are now expressed as percentages that scale together,
  replacing a flat daily allowance that had drifted to 78% of every wallet.
- Visitor wealth varies from one full budget upward rather than within a narrow band.

## [1.4.0] — 2026-08-01

### Added

- **Historical buildings add to city attractiveness.** Attractiveness is the sum of
  `AttractivenessProvider.m_Attractiveness` across every building carrying one
  (`TourismSystem:95-97`), which in the base game is parks, attractions and signature landmarks.
  Ordinary buildings contribute nothing, so a preserved old quarter draws nobody unless a landmark
  sits inside it. `HistoricAttractivenessSystem` reads `BuildingFlags.Historical` — the flag the
  player already sets from the building panel — and grants each one a small value, so a district
  earns its appeal collectively rather than a single building carrying it. New "Historical building
  appeal" setting, default 3, 0 to disable.

  Note this raises the tourist target and improves how destinations on those streets score
  (`CitizenPathfindSetup:87-91`). It does not make the historical buildings themselves visitable —
  that would require them to be leisure providers, which has knock-on effects on citizen behaviour
  beyond tourism.

### Fixed

- `BuildingFlags` is ambiguous between `Game.Buildings` and `Game.Prefabs`, which have unrelated
  values. Fully qualified at the use site.

## [1.3.1] — 2026-08-01

### Fixed

- The UI bundle failed to build after the retired hotel district module was deleted, because
  `index.tsx` still imported and registered it. 1.3.0 therefore shipped with a stale `.mjs`.

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
