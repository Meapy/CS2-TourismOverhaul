# Changelog

All notable changes to CS2 Tourism Overhaul.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versioning is
[semantic](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.10.0] — 2026-09-26

### Added

- **The cruise line runs its own Cruise Ship.** The stock passenger ship's capacity is authored on its
  prefab, which every ordinary passenger ship line shares, so the cruise line could not carry more than
  2,300-2,800 without changing them all. `CruiseLineSystem` now copies that ship with the game's own
  `PrefabBase.Clone` into a "Cruise Ship" prefab — same meshes and materials, since a mod cannot author
  new 3D art — whose capacity is the **Cruise ship passengers** setting, kept in step on the baked prefab
  so a change applies to the ship already sailing. The cruise route names it as its vehicle model, which
  the game's selection always prefers and `TransportLineSystem.CheckVehicles` enforces by replacing the
  line's current ship; that is only set between voyages (no call open, nobody tagged), so no shore party
  is stranded. It requires the theme the city is not using, and `VehicleSelectRequirementData` passes that
  only for a line that has chosen it, so ordinary ferry lines never pick it. The copy drops the stock
  ship's `ObsoleteIdentifiers` (its old names, kept so older saves still find it), which it would
  otherwise have claimed as well.

### Performance

- **The cruise shore-party sweep runs as a Burst job.** Everything `ReturnFinishedParties` did for each
  party ashore — keeping them off the hotels, detecting who is aboard, last call and its refresh,
  nudging idle citizens, writing off stragglers, releasing parties that reached the sea another way —
  is now `CruiseVoyageSystem.ShorePartyJob`. On the main thread it first had to wait for the frame's
  creature and vehicle jobs, which write `Target`, `CurrentTransport`, `CurrentVehicle` and `Resident`:
  5-8 ms per update against well under a millisecond of work, and narrowing the wait to fewer
  component types only moved it to the next one. The job is scheduled after those same writers, so it
  reads the same world; its commands go into an `EndFrameBarrier` buffer created at the same point in
  the update as before, so they play back in the same order; and chunks are walked in query order on
  one thread, as the loop did. Only the summary log line moves, to the next update.

  `CancelHotelTrip`, `EquipTerminalWithLodging` and `ClearCitizenPathState` have one implementation, in
  `ShorePartyAccess`, shared by the job and the main thread.

- **Serving the cruise ships runs as a Burst job too.** Moving the sweep alone only moved the wait: the
  vessel code's first read of `PublicTransport` waited for every vehicle and creature job in the frame,
  and "serve ships" went from ~0.02 to 4-6 ms per update. `CruiseVoyageSystem.VesselJob` now does, in the
  same frame and after those jobs, everything that reads or writes the game's vehicle and stop data:
  applying and re-asserting every hold on `m_DepartureFrame` (so a hold still lands inside the 60
  frames `TransportBoardingHelpers:368` gives a boarding ship), keeping the stops priced
  (`ClearOutsideConnectionWait`), reclaiming a lost quay (`ReclaimQuay`), counting outbound passengers,
  and recording one observation per ship.

  The main thread keeps the decisions that log, spawn tourists or start and end things, and makes them
  from the last observation plus this mod's own components read live. Deadlines (a load's timeout, a
  call's close and overstay) need only the frame and those components, so they still happen on the same
  update, with the job applying the release. Starting a load or a call happens one update (16 frames)
  after the ship is seen: detection plus hold still fits inside the 60-frame window, and reading the
  mod's components live means nothing is started twice. Holds and releases decided on the main thread
  reach the job as requests.

- **`TouristRebookSystem` runs as a Burst job.** It walks every tourist household to find the few
  without a room: 6-10 ms per update on the main thread, spiking to 40 ms.

### Fixed

- **A cruise ship waits for its shore party again.** The overstay (holding the ship while passengers
  are still ashore) was decided only once the reboard frame had passed, but the hold *is* the ship's
  departure frame, set to that same reboard frame, and the vessel sails the moment it arrives. With the
  system running every sixteen frames the ship could leave first: measured as "waiting for 1175
  passengers" followed one update later by "left terminal before shore leave ended", and 291 parties
  written off. The extension is now decided two updates ahead of the deadline and rolls forward, so the
  hold is always raised before the ship reaches it.

  It waits only while it helps, and never more than one in-game hour past its scheduled departure, for as
  long as the ashore head count keeps reaching new lows, and it sails once nobody else has got back for
  8192 frames (about 45 in-game minutes), so a party with no way to the quay cannot hold it indefinitely.
  The ship's departure logs how long it waited and how many were still ashore.

  At the measured deadline the stragglers were mostly finishing errands elsewhere before their recall
  trip could start (about 150 parties) or riding other vehicles (about 30); the wait is what brings
  those back. About 100 more were "nowhere" — a trip queued, but no building and no body, so no trip can
  start; see the next entry.

- **Passengers stuck "nowhere" are put back where they were last.** At the last measured deadline 1,100+
  were still ashore, and they were not walking: walkers fell from 454 to 39 in time. 435 parties (about
  1,300 people) had a trip queued but no building and no body, a number that grew through last call.
  `TripNeededSystem` only serves citizens with a `CurrentBuilding` (its query requires one), so their
  trip could never start however often the recall queued it. A citizen lands there when its body is
  deleted other than by arriving — `ResidentAISystem.ReturnHome` deletes a body with no path home, and
  `ReferencesSystem` then drops the citizen's `CurrentTransport` without giving it a building. The
  recall now gives such a citizen a building again before issuing the trip: the last building it was
  seen in during shore leave, so its body comes out of that shop or attraction and is seen walking back
  to the ship. Only once — a citizen that goes nowhere again has no path from there, so the second
  time (or if that building is gone) it is put at the harbour terminal instead, a short sure walk
  aboard. Nothing visible vanishes, as it had no body. The full
  recall is also re-asserted every 1024 frames instead of 2048. The shore-leave log counts how many were
  put back where they were and how many at the terminal.

- **Last call turns passengers round where they are.** The recall used to queue a trip to the ship,
  which the game serves only once the current errand ends, so parties finished their shopping or
  sightseeing across the city first: about 150 parties at the measured deadline. A recalled citizen out
  in the world on foot is now re-targeted on the spot with a `ResetTrip` event — the game's own
  mechanism (`TripNeededSystem.ResetTrip` uses it to give a walking body a new trip; `TripResetSystem`
  drops any detour, clears the arrived and hang-around flags, marks the path obsolete and sets the new
  target and travel purpose) — and its queued errands are cleared. The body re-paths with pedestrian,
  taxi and public transport methods (`ResidentAISystem.FindNewPath`), walks to the pier and boards.
  Citizens indoors already had their activity cancelled by the recall. A citizen riding a vehicle is
  left to the vehicle, as every game system that emits `ResetTrip` leaves it, and is turned round on
  the first sweep after it steps off. The shore-leave log now counts how many were turned round.

- **A full ship sails straight away.** At a city quay, once none of the call's shore party is left
  ashore (parties aboard, released or written off no longer count), the ship sails at once instead of
  sitting out the rest of the shore leave; not in the call's first two sweeps, while the new tags are
  still being counted. At the map edge, a load ends early when the passenger buffer reaches the
  vessel's own authored capacity, since nobody else can board — the physical figure, deliberately not
  the complement target, which counts locals riding through and once sailed a ship a tenth of a second
  into its load. Closing a call now also releases the hold, so an early close leaves at once rather than
  at the old departure frame.

  A load also ends as soon as the **Cruise ship passengers** setting's worth of visitors is aboard (the
  target booked on the manifest: the setting capped at the vessel's own size). Only tourist households
  outbound count, so residents riding the line cannot trip it. The setting is therefore the ship's size in
  effect, up to the model's own capacity; raising it past that would need a vessel prefab of the mod's
  own, since the stock passenger ship's capacity is shared by every ordinary ferry line. Its description
  no longer promises a ±500 swing by city attractiveness, which the code never did.

  At the harbour too: once last call has begun, a ship whose passenger buffer has reached its authored
  capacity (2800/2800) sails at once, and the overstay no longer holds a full ship, since nobody still
  ashore could board it. Not before last call, because a ship arriving at the harbour is full of the
  complement still to step off. Anyone still ashore is written off as at any departure and leaves
  through the sea connection; the log says how many.

- **Last call scales with the stay on a log curve.** It was half the shore leave before each party's
  own deadline, with deadlines spread over a third of the stay, which suited a recall that had to wait
  for errands to end: with walkers turned round on the spot, a ten-hour call recalled its first parties
  40 minutes in and had most back with five hours to go. A fixed 20% then proved too late. The first
  parties are now recalled 4.5 h x ln(1 + stay / 4 h) before sailing — 8 h: 4.9 h, 12 h: 6.2 h,
  24 h: 8.8 h, 48 h: 11.5 h — and the rest over the next fifth of that lead; the boarding grace is
  capped at 40% of it, so no part of the timing grows without bound on a long stay. The pier's boarding
  window opens with the first recall, and the overstay still holds the ship for anyone on the way.

- **The cruise queue no longer runs away when nobody reaches the stop.** Its only bound was one batch
  per 512 frames, which assumes the people ordered turn up and close the shortfall. After a cruise line
  was rebuilt, twice in a row nobody did until the line was rebuilt again, and the queue ordered a full
  complement every batch — about 45,000 households in six minutes. After four batches with nobody
  waiting and nobody aboard, it now slows to one batch per 4096 frames and logs a warning once; the first
  person to appear restores the normal rate. In working service people are waiting within a batch or
  two (measured: 0, 59, 132, 693), so it never engages.

- **Cruise parties are no longer booked into hotels.** `TouristRebookSystem` treated any tourist whose
  hotel was not a live lodging provider as displaced, cruise parties included. The game takes each
  party's harbour anchor away every 1024 frames (they are deliberately not in the terminal's Renter
  list) and the shore sweep restores it within 64, so a rebooking pass in that window gave the party a
  real room, a renter entry and a walk to the hotel. The sweep then cancelled the walk and re-anchored
  them to the harbour, but the room and the renter entry were never given back. Cruise parties are now
  excluded.

- **Main-thread readers wait for the data they read.** `TouristShoppingSystem`, `HotelCapacitySystem`,
  `HotelWelcomeSystem`, `TourismPanelUISystem`, `TouristDemandSystem`, `TouristDemandUISystem`,
  `TourismDiagnosticsSystem` and `TouristStaySystem` read `Renter`, `LodgingProvider`,
  `TouristHousehold`, `HouseholdNeed`, `Resources` and the cruise tag through chunk handles or lookups on
  the main thread without waiting for the jobs writing them — the game's own, and now this mod's
  rebooking, room-reclaim and cruise jobs. Each now waits for exactly the types it touches. All of them
  run every 256-512 frames or less, so the waits are rare.

- **Stale comments corrected.** Several named methods that no longer exist (`ReturnFinishedParties`,
  `RecallToShip`, `TopUpCall`), and the cruise capacity setting's code comment still promised a ±500
  swing by attractiveness. An unused constant (`kPierComfortFactor`) is gone.

- **Corrected a comment that misdescribed the harbour anchor.** It said the game only checks that the
  terminal has a Renter buffer; it also checks that the household is in it
  (`TouristHouseholdBehaviorSystem:82-89`), which is why about one party in sixteen briefly shows no
  accommodation until the shore sweep restores it. The `CruiseVoyage timing` line now reports how many
  anchors each sweep restored.

## [1.9.1] — 2026-09-23

### Fixed

- **Cruise passengers who sailed home now actually leave.** `LandHomewardPassengers` only removed the
  `CruisePassenger` tag when the ship reached its outside connection, on the assumption that last call
  had already sent them away. It no longer does: last call is a Leisure trip to the connection, so
  nothing ended the visit and the party sat in the connection as ordinary tourists, trying to reach an
  attraction or leisure spot from the map edge every few seconds and failing at the cost limit each
  time. CS2 Performance measured ~110 such citizens failing 55 times each in three minutes, 9.6% of all
  route search work in a 665k city. They now get `MovingAway` to the connection they are in, the same
  as a party written off at the deadline. Parties already stuck in an existing save lost their tag
  before this fix, so it does not reach them. The new `StrandedVisitorSystem` does: every 4096
  frames it looks for tourist parties with a hotel (not a `LodgingSeeker`), not moving away and not
  cruise-tagged, whose citizens are all inside one outside connection, and sends home any still there
  after 1.5 in-game hours. Queued cruise arrivals are `LodgingSeeker`s until they get a hotel and then
  leave for it, so they never match.

- **The cruise pier's prices now actually reach the pathfinder, and each closed call reports who
  was aboard.** `WaitingPassengersSystem` (`:177-192`) rebuilds a stop's average wait every 256
  frames from its history — `max(ongoing ÷ count, concluded ÷ successes)` — and tags the waypoint
  `PathfindUpdated` only when that rebuild changes it; the pathfinder reads a stop's cost only after
  such a tag. The mod wrote the figure alone and cleared the history, so whatever the game rebuilt
  was the only value that ever reached the graph. It now writes a history that reproduces the
  figure (concluded = figure, one success) and tags the waypoint whenever the live value differs.

  Each closed call now logs the manifest, split into cruise passengers, other tourists and
  residents. That is how the rest of this was found: a call closed with 2,678 of 2,800 aboard —
  **1,072 of them residents** — and 270 parties left behind.

  Pricing last call to keep residents off was tried and measured, and does not work. Where the
  harbour is also served by an ordinary ferry to the same sea connection, the shore party has an
  alternative too, and the price sent them to it: 1 of 1,015 parties reboarded. Price cannot tell
  two groups apart when both have somewhere else to go, and restricting the ship to guests of the
  harbour is not available either — pathfinding authorizes only a household's `PropertyRenter`
  home (`TripNeededSystem:1100`, `ResidentAISystem:3096`), and a tourist's accommodation is
  `TouristHousehold.m_Hotel`, which is never an authorization. Last call stays free. Room for
  outsiders comes from the complement size: a **Cruise ship capacity** below the vessel's own
  leaves seats for them, where one that fills the vessel means every resident who boards displaces
  a returning passenger. Parties that miss the ship are not stranded — they leave the city through
  the same connection by the ferry.

- **Ordinary visitors no longer queue for the cruise ship.** Rebuilding a cruise line sent every
  sea arrival onto it — a backlog of 9,000 visitors waiting for a vessel that calls rarely and sits
  at the quay for hours. `ClearOutsideConnectionWait` holds the line's map-edge stop at zero
  advertised wait so the cruise complement created there routes onto the ship; but ordinary
  arrivals spawn at that same outside connection, and to them a free ride is the cheapest way to
  their hotel. With ship arrivals at 40% that was a large share of all visitors. The arrival
  spawner and arrival routing now skip any connection the cruise line calls at
  (`CruiseVoyageSystem.ServesCruiseLine`), so ordinary visitors use the city's other connections;
  a city whose only sea connection is the cruise line's gets its ship share spread over the other
  modes.

- **Hotels are no longer built without end, and tourists no longer run away to the maximum.** The
  game's hotel test multiplies tourist *citizens* by a requirement (`TourismSystem:78`) and compares
  it with *rooms* (`:90`), and a room holds a whole household; the vanilla 0.5
  (`DemandPrefab:91`) is one room per two-person party. The mod's "rooms per tourist" setting was
  meant per party — its description says above 1.0 leaves spare capacity — but was applied per
  citizen, demanding about two rooms per party. Measured: 22,000 of 52,000 rooms empty while every
  snapshot read `hotels will spawn: YES`. Endless hotels meant a permanent opening bonus, which adds
  each new hotel's rooms to the tourist target for a week, so tourists climbed until
  `MaximumTourists` caught them at 100,000. The requirement is now divided by the measured party
  size.

- **Tourists no longer walk into a park the park limit has closed.** In a session at the tourist
  target (~99,000) the busiest park climbed to 721 visitors while *closed*, with 30 parks shut and
  failed leisure searches nearly tripling — the worst of both. Two leaks.

  **Tourists reach parks through a path the limit did not cover.** `SelectLeisureType:524-527`
  sends a tourist to `Attractions` 30% of the time before any other roll; that goes through meetings
  to `VisitAttractions` and `SetupAttractionJob`, whose candidates are every building with an
  `AttractivenessProvider` (`CitizenPathfindSetup:853`), parks included, scored at
  `-100 × attractiveness × random` (`:369`). It never looks at `LeisureProvider`, so closing a park
  did nothing to it.

  The only brake on that path, `AttractionCrowdingSystem`, never worked. It counted crowds by the
  tourist *household's* `Target` — the hotel — so it barely saw anyone at a park; and the game
  recomputes `m_Attractiveness` every 256 frames (`AttractionSystem:85-122`, interval 16 over 16
  update groups), so a damped value written every 1,024 frames was overwritten within 256. That is
  why "How busy a place gets before it puts people off" never visibly did anything.

  It is rebuilt. Factors come from the visitors `ParkVisitorSpreadSystem` actually counts on site,
  and a closed park drops to the 10% floor so the attraction path passes it over too. A Burst job
  ordered after `AttractionSystem` applies them to exactly the chunks the game has just rewritten
  (change filter), and damps a value only if it differs from the one it last wrote, so it can never
  damp twice. The game supplies a fresh base every 256 frames, so there is no base to capture and
  nothing to restore.

  **Every save reopened every closed park for up to 512 frames.** The reopen is what keeps saves
  clean, but the game has no post-save hook and the limit only re-closed on its next pass — so each
  autosave opened every full park to every leisure search in that window, about 7,000 trips per
  census. The park system now wakes every 16 frames and re-closes immediately after a save, while
  still doing its full work every 512.

  Crowded attractions now read as less attractive in the city's total too, so a city whose
  attractions are packed draws slightly fewer new tourists. That was always the design; it simply
  never took effect before.

- **A cruise ship no longer sails while its own passengers are still walking back.** The hold on a
  docked vessel is `PublicTransport.m_DepartureFrame`, and `StopBoarding`
  (`TransportWatercraftAISystem:797-807`) reads it only while the stop's `BoardingVehicle` still
  names that vessel. `BoardingVehicleSystem` blanks that field on every stop in the city after a
  load, and whenever any waypoint is `Updated`, if it cannot match the vessel's `Target` back to the
  stop — and with the field blank `:850` clears `Boarding` whatever the hold says. Measured: a ship
  left its quay 62,613 frames into a call, leaving 1,100 ashore queuing for a vessel that had gone.
  A ship that is still boarding, still targeting a waypoint connected to its call's terminal, is
  alongside whatever that field says, so the claim is put back rather than the ship reported gone.
  The state of every held ship is now logged on the first update after a load, since that is where
  this was caught.

- **The ship now waits for stragglers.** When shore leave ends with passengers still ashore, the
  call is extended in steps rather than closed. The hold, the passengers' deadline and the sailing
  time shown on the vessel panel all read the same frame, so all three move together. Capped at half
  the shore leave, because a party that can never reach the quay would otherwise keep the ship in
  port for ever; anyone still out after that is written off as before. Logged once per call.

- **The quay is open for boarding from the moment the first party is called back.** Each party's
  last call is measured from its own deadline, which is set up to a boarding grace plus an
  early-return spread ahead of the ship's, so a window measured from the ship's departure opened
  hours after the first passengers were already walking. This never showed while the pier's price
  was not reaching the pathfinder; once it did, early returners found the quay priced out of reach,
  walked to it and stood there — 0 of 1,033 parties aboard while the count of people waiting at the
  pier climbed. The window is now the union of the parties' own.

- **Returning passengers are no longer sent back to the start of their journey every few minutes.**
  The recall is re-asserted periodically, and it used to clear the path of everyone it touched,
  including parties already walking back or waiting at the pier. Any walk or route search that took
  longer than the refresh was wiped and restarted, so only the few that finished inside one window
  ever reached the ship: 678 of 1,070 parties were still ashore an hour before sailing. A party
  already on its way is now left to finish.

- **Passengers held indoors by an activity are recalled properly.** `TripNeededSystem` excludes any
  citizen carrying a `TravelPurpose` from its query outright, so a tourist sitting in a museum with
  the journey home already queued goes nowhere until that purpose is taken off — which is what the
  recall does. Leaving alone everyone who had a trip queued, as the fix above first did, included
  exactly these: about 280 parties per call sat indoors holding a trip that could never run, and the
  log showed it — a queue that never moved, with none of them waiting on the pathfinder. "Already on
  the way" now means walking to the ship, or idle with the trip ready to run; a party held by an
  activity is recalled again.

- **A party that reaches the sea another way counts as gone, not as ashore.** Boarding the cruise
  ship is priced from the line's long vehicle interval — the same figure that holds the line to one
  vessel — so where an ordinary ferry serves the same sea connection, it is far cheaper and some
  parties take it. Their trip is to that connection, so it completes; they never board, and they
  used to keep their tag, stay in the ashore count, be recalled to where they already stood, and
  hold the ship's extended wait for nobody. They now leave the city properly and drop out of the
  count. To have every passenger return to the ship itself, the cruise line needs a sea connection
  no other line serves.

- **The shore-leave log says where the parties actually are.** Each line now splits those still
  ashore into walking, queued and idle; the walkers into bound for the ship, riding another vehicle
  and elsewhere; and the queued into waiting on the pathfinder, busy indoors and nowhere. It is
  written on a slow cadence as well as on recalls and boardings, so a return that is merely slow is
  visible. Every one of the fixes above was found in these figures.

- **The log says which build is running.** `OnLoad` now records the mod assembly's build time.
  Copying a new build into the Mods folder while the game is running does nothing until the next
  start, and several rounds of measurement were spent on builds that were never loaded.

### Performance

- **`TouristSpendingLedgerSystem` and `HotelEfficiencyFloorSystem` run as Burst jobs.** Timing each wait
  separately showed each was almost entirely one component: the ledger waited 10-12 ms per update for
  `Resources` (tourist wallets, written by the economy jobs) against 1-1.6 ms of work, and the floor
  waited 6-10 ms for `Efficiency`, which it writes and so must wait for every reader too. Both passes
  now run in jobs scheduled after those writers, reading and writing exactly what they did before. The
  ledger publishes each sample's totals to the main thread on its next update, 128 frames later, so a
  save made in between leaves out that one sample.

- **`HotelRoomReclaimSystem` runs as a Burst job.** It checks every guest in every hotel (~51k) and every
  tourist household (~57k): 27.7 ms per update on the main thread, felt as a hitch every 2048 frames.
  The same two passes, in the same order with the same caps, now run in a job scheduled after the jobs
  writing what it reads, so the main thread does no work and no waiting for it.

## [1.9.0] — 2026-09-21

### Added

- **A full park turns new visitors away.** A popular park would fill without limit — one lawn was
  measured holding 1,487 cims — while others nearby stayed empty.

  The cause is in how the game picks a park. Residents and tourists alike roll a leisure type in
  `LeisureSystem.SelectLeisureType` (`:521-559`), then `CitizenPathfindSetup.SetupLeisureTargetJob`
  offers every `LeisureProvider` of that type at **cost 0** (`:164`). Only shops and restaurants get
  a fullness term (`:169-184`). So the nearest park wins every search however packed it is.
  `AttractionCrowdingSystem` never reached this: the attractiveness it damps feeds only
  `SetupTargetType.Attraction` (`TripNeededSystem:1442`).

  The one per-park lever in that search is its candidate query — simply "has
  `Game.Buildings.LeisureProvider`" (`:835`). A park over its limit has the tag removed, so new
  leisure trips pick the next park; below three quarters of the limit it goes back. The tag is only
  a search filter: `LeisureSystem.SpendLeisure` (`:283-360`) reads the prefab's
  `LeisureProviderData`, so visitors already inside keep their leisure. **Nobody is moved out or
  hidden** — the crowd thins as visitors finish and go home.

  The tag is serialized, and `RequiredComponentSystem:1120-1128` restores it on load only for
  companies, not parks. Every removed tag is therefore put back in `PreSerialize` (registered ahead
  of `SerializerSystem`, as the cruise system does), when the feature is switched off, and on
  destroy. The next update re-closes whatever is still full, so no save ever contains a closed park.

  The limit is **Park visitor limit (per lot cell)**, 1.00 by default: a 12×12 park is full at 144.

  Inside a park, a visitor on an over-full lawn re-rolls its spot (`PathFlags.Obsolete`, as
  `ReachTarget:2241-2242` does), and settled visitors occasionally drift — the native re-roll works
  out at over ten minutes of real time and `CannotIgnore` can pin a visitor for its whole stay.

  Counting who is actually standing in a park took four attempts; the test is now
  `CreatureLaneFlags.Hangaround | EndReached` on the lane, because `ResidentFlags.Arrived` is never
  set for group members and `Divert` is removed on arrival. Two Burst jobs, once every 512 frames,
  plus a main-thread pass over the parks. A **Diagnostic logging** line reports the population, the
  busiest lawn and park, and how many parks are closed.

- **"How busy a place gets before it puts people off" is now a visible setting.** It was hidden as a
  development tuning value, which was wrong: it is the only dial in the mod that acts on the
  *arrival* side. Everything the park systems do rearranges the visitors already present;
  `AttractionCrowdTolerance` is what decides how many turn up. It multiplies an attraction's
  footprint-derived capacity, so **lower spreads visitors sooner** — at 5 a place absorbs five times
  as many before losing any appeal, which keeps one park heaving while others sit empty.
  `AttractionCrowdingSystem` re-reads it every update, so the slider takes effect without a reload.

### Performance

- **Cruise passenger bookkeeping no longer goes through `EntityManager` per entity.** Profiling a
  1.2M-citizen city (CS2 Performance, main-thread capture) put `CruiseVoyageSystem` at 2.2-5.1 ms per
  rendered frame, with single updates as long as 250 ms, making it the most expensive mod system in the
  city by some way. `TouristSpendingLedgerSystem` (0.8-1.1 ms), `HotelEfficiencyFloorSystem` (0.5-0.7 ms)
  and `TouristRebookSystem` (0.3-0.4 ms) followed the same pattern.

  The cause was the access path, not the logic. Every `EntityManager.HasComponent`/`GetComponentData`
  resolves the component type afresh and checks the jobs writing it, and these paths asked one question
  per entity they walked. Those reads now go through `ComponentLookup`/`BufferLookup` fields refreshed
  once per update (and again after `EnforceOneCruiseLine`, the one path that makes a structural change).
  Same components, same order, same results.

  Per-phase timing added to `CruiseVoyageSystem` found the real hot spot, which was not where it looked:
  the shore-party sweeps cost 0.02-0.8 ms per update, while **serving docked ships cost 17-27 ms**.

  That phase walked the wrong set of vehicles. `m_CruiseVehicleQuery` asked for public transport with a
  route, which in a large city is every bus, tram, train and taxi running, and `ServeDockedShips` then
  asked `IsOnCruiseLine` about each one - four `EntityManager` calls apiece - on every update, only ever
  to reject them. A cruise line is a ship line, so the query now asks for `Watercraft` as well and the
  loop sees the handful of vessels that could actually be on it. `IsOnCruiseLine` and the manifest walk
  in `CountOutboundAboard` (two thousand passengers on a full ship, five `EntityManager` calls each
  through `HouseholdOf`) use lookups too.

  The timing report now also prints how many vessels the loop walked, which is the number that made the
  cause obvious.

  The timing report stays in, logged every 256 updates, so the next regression is one log line away
  instead of a guess. Measured on a 665k-citizen save with 8 vessels: serve ships 1.1-1.7 ms per update.

  An `EntityManager` read waits for the jobs writing that component; a lookup read on the main thread
  does not. So each converted system waits (`EntityManager.CompleteDependencyBeforeRO/RW<T>`) for
  exactly the types whose data it reads, which is the wait the old calls did. `CompleteDependency()`
  was tried first and waited for every registered type, presence-only ones included: 5-16 ms per update.
  In `CruiseVoyageSystem`, timing each type showed the wait was almost entirely `Target` (about 3 ms per
  refresh, since every creature and vehicle job writes it), whose data is read through the lookup in one
  place, `KeepOffTheHotels`. That wait is now taken there, on sweep updates with a party that has a
  `Target`, instead of on both refreshes of every update. Measured afterwards, the total did not fall
  (5.7-8 ms per update): the same creature jobs write `Resident`, `CurrentTransport` and
  `CurrentVehicle` too, so the wait moved to the next of those types. It is a wait for the frame's
  creature jobs as a whole, and only running this work in a job, or later in the frame, would avoid it.

## [1.8.3] — 2026-09-14

### Fixed

- **Saving no longer crashes while the trailing-month arrivals window is written.** Every save,
  manual or automatic, could die with a `NullReferenceException` inside the game's serializer,
  followed by a native crash with no managed stack — the exception escaped a job and left the job
  system unrecoverable.

  The arrivals window kept its counts in a buffer with a hand-written serializer. Burst cannot
  compile a job parameterised on a type from an assembly loaded at runtime, so that serializer ran
  on the managed fallback path, which the game's own buffers never touch because theirs are all
  compiled. The type gained nothing from being there in the first place: it wrote four integers in
  order, which is exactly what the plain path writes by itself. It now uses the plain path.

  The saved bytes are unchanged, so existing saves are unaffected.

- **Saving no longer crashes in a city running a cruise line.** The mod wrote raw entity references
  into the save — the terminal a call belongs to, and the ship and terminal each shore party belongs
  to — without ever checking they still named anything. A save file is a closed world: the game's
  serializer excludes deleted and in-progress entities from it, so a reference to a vessel or harbour
  that has just been removed names something the file will not contain, and writing it takes the game
  down as the file is written. That is why it only ever happened on save, only with this mod
  installed, and sooner the more you used the cruise line.

  Those references are now checked immediately before the game writes, using the game's own
  pre-serialization hook, and blanked if they name anything that will not be in the file.

- **A shore party whose ship is deleted is no longer stranded for ever.** Every way a visit can end
  needs the vessel — the walk back aims at its map-edge connection, and the deadline comes from its
  departure — so deleting a cruise line left its passengers ashore permanently, still tagged, still
  swept every update, and still holding the dead reference that went into every later save. They are
  now released as ordinary visitors and go looking for a hotel.

## [1.8.2] — 2026-09-08

### Fixed

- **Cruise passengers now actually get back on the ship.** A call could end with most of the
  complement still ashore, sent out of the city on foot instead of sailing.

  The return was a relay in two steps: walk to the harbour building, and then — only once a sweep
  happened to catch a citizen standing inside it — issue a second trip to the ship's map-edge
  connection, which is the one that actually boards anyone. Step two was the only step that put a
  passenger aboard, and it was gated on a state the game had no reason to give them: a trip to a
  harbour, which provides no leisure, need not park anyone inside it, and the sweep only looked every
  sixty-four frames. Miss that window and the party walked to the quay, stood there, and was written
  off when the vessel left.

  Last call is now one journey to the connection. A citizen routed there is routed over the transport
  network, and from a city pier the only way to a sea connection is the vessel serving it — so the
  legs come out as walk across the city, wait at the terminal, board, under the game's own power. The
  walk back is still the visible half of a cruise call; it is now the first leg of the journey that
  boards them rather than a separate errand that had to be noticed.

  Two supporting changes. A party is now due back a short grace before the ship sails rather than on
  the same frame, so there is time to walk aboard and not merely to arrive. And "aboard" is read from
  the citizen's body carrying the vessel — the same thing the game rebuilds a manifest from — so a
  party that boards between two sweeps is no longer pulled back off the ship and marched out of the
  city when the deadline passes.

  The shore leave log line now reports parties recalled, aboard, and left behind, so a call that goes
  wrong says so.

- **Maximum tourists is now actually a maximum.** The setting was applied inside the demand
  calculation and then the finished figure was multiplied by 1.4, so the real ceiling sat forty per
  cent above whatever the slider said — a limit of 20,000 targeted 28,000 — and the hotel opening
  allowance was added after the clamp as well. The 1.4 is arrival headroom, and it now lives inside
  the demand figure where it belongs; the player's ceiling is applied once, last, after everything
  else. Nothing else about the numbers moves.

  Two related things went with it. The ceiling was `max(vanillaTarget, MaximumTourists)`, so a limit
  below what the base game would have produced could never bind and the bottom of the slider's range
  did nothing; it is now a hard bound. And a ceiling of zero disables the limit rather than emptying
  the city.

  Two known reasons the count can still read above the limit, both by design: cruise passengers are
  counted separately from staying visitors, so a docked ship adds up to its complement on top; and
  the limit throttles arrivals rather than evicting anyone, so lowering it takes effect as visitors
  leave rather than at once.

- **A cruise port no longer draws a thousand megawatts when a ship ties up.** A building's
  electricity and water demand stops being its rated figure once it has renters: the game multiplies
  the prefab's consumption by `5 x citizens / (level + 0.5 x average education)`, with level fixed at
  5 for anything that is not a zoned building. A shore party of several hundred therefore multiplied
  a harbour's demand by two or three hundred, which a 50 MW low-voltage connection cannot carry and a
  400 MW high-voltage one would not have carried either.

  The terminal is now tagged `StorageProperty` for the length of a call — the game's own switch,
  read by nothing but the two consumption systems, for "do not scale this building's utilities by its
  renters". The shore party stays in the renter list, which is what holds the lodging anchor and what
  brings them back to the ship; the port draws what it is rated for.

  The previous fix emptied the renter list instead, and only when a tagged passenger happened to be
  listed at that exact terminal — so it healed the stock harbour and left every other one spiking.
  This one is keyed off the mod's own terminal marker rather than a prefab, so it covers Bridges &
  Ports terminals, asset-mod harbours, and harbours that do not exist yet. A call already open when
  you install this is repaired on the next sweep rather than waiting for the ship to sail.

- **Equipping a terminal no longer damages harbours that come with their own fittings.** Release used
  to strip the `LodgingProvider` and the `Renter` buffer unconditionally, which is correct for the
  stock harbour — it has neither — and would have taken a DLC port's renters, and with them its
  company, the first time a cruise ship sailed. The mod now marks what it adds and removes only that.

## [1.8.0] — 2026-08-06

### Added

- **Passenger Cruise Line.** A new line tool beside Passenger Ship Line. Draw it from a sea outside
  connection to a harbour and a cruise ship works the route: it fills with visitors at the map edge,
  sails in, holds at the quay while its passengers see the city, and leaves with them at the end of
  shore leave. Cruise passengers sleep aboard, so they never take a hotel room and never compete with
  your other visitors for one.

  Nobody is teleported or placed by hand at any point. The mod creates visitors at the connection and
  gives them a reason to travel; the game gives them bodies, routes them, boards them and lands them
  through its own machinery. The cruise line's map-edge stop is made as attractive as the pathfinder
  allows and its city pier as unattractive, which is what keeps the ship full of visitors and free of
  commuters.

- **Cruise shore leave** setting, in hours. How long the ship stays at the quay; passengers are sent
  back for the last third of it. Defaults to 8.

- **Cruise ship passengers** setting. An upper bound on how many a call brings — the vessel's own
  authored capacity applies as well, and the smaller of the two wins.

- **A sailing time on the selected-vehicle panel.** Selecting a docked cruise ship shows when it
  leaves, in city time, and how many of its passengers are still ashore. The clock comes from
  `TimeSystem.normalizedTime` plus the frame gap rather than from the frame index, which ignores the
  map's founding offset and is wrong by a constant in every save.

### Known limitations

- Everything the mod writes to a stop applies to the stop rather than the line, so a harbour shared
  between a cruise line and an ordinary passenger ship line is affected by both.
- Removing the mod while a ship is alongside leaves a stand-in lodging component on the harbour. Do
  it between voyages.

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
