# Passenger Cruise Line — plan and analysis

> **Historical.** This is the design as it stood before implementation, kept for the alternatives it
> rejects and the reasoning behind them. The build diverged from it on the central point: the plan
> assumed the mod would own a cohort and move it, and it cannot — bodies and routes belong to the
> game. What was actually built, and why, is in `docs/HANDOVER.md`; the traps are in
> `docs/SESSION-NOTES.md`.


Target: 1.8.0. Nothing here is implemented. Everything marked **traced** was read out of the
decompiled assemblies; everything marked **unverified** needs a measurement before it is designed
around. No part of this has been run in game.

## What was asked for

A new line tool beside *Passenger Ship Line*, called **Passenger Cruise Line**. It behaves like the
passenger ship line, except that the ship:

1. arrives already full of tourists,
2. unloads them into the city,
3. lets them spend a day or two doing tourist things,
4. takes the same passengers back aboard,
5. and departs with them to where it came from.

## What a passenger ship line actually is

**Traced.** `TransportLinePrefab.cs:13-129`, a subclass of `RoutePrefab`. The whole line is a prefab
plus four runtime archetypes. The interesting authored fields:

| Field | Meaning |
|---|---|
| `m_TransportType` | which vehicle family serves it |
| `m_AccessConnectionType` / `m_RouteConnectionType` | how passengers reach it, how the vehicle travels |
| `m_DefaultVehicleInterval` | spacing between vehicles |
| `m_StopDuration` | dwell at a stop, default `1f` (**unverified**: units) |
| `m_SizeClass` | vessel size bracket |
| `m_PassengerTransport` / `m_CargoTransport` | which of the two behaviours applies |
| `m_PathfindPrefab` | the pathfinding profile |

`GetArchetypeComponents` (`:60-99`) composes four different archetypes from the same prefab — the
route, the waypoint, the connected waypoint, and the segment. Two details matter later:

- `WaitingPassengers` is added to waypoints **only** when `m_PassengerTransport` is true (`:87-90`).
- The route archetype carries `TransportLine`, `VehicleModel`, `RouteVehicle`, `RouteModifier`,
  `DispatchedRequest`, `RouteNumber` and `Policy` (`:63-72`).

`RoutePrefab.LateInitialize` (`:54-89`) builds all four archetypes with `CreateArchetype` and stores
them on `RouteData`. That is the part that makes a runtime-created line prefab plausible at all: the
archetypes are derived from the prefab's own components, not hard-coded.

**Precedent in this repo.** `HotelZoneSystem` already creates prefabs at runtime —
`ScriptableObject.CreateInstance<ZonePrefab>()` at `:246`, `m_PrefabSystem.AddPrefab(zone)` at
`:288`, and it handles the rejection case. The same shape should work for a `TransportLinePrefab`.

## The load-bearing constraint

**The game has no concept of a passenger cohort.** A citizen boards a vehicle because that vehicle's
route serves the destination their pathfinding chose. Nothing anywhere says "these 500 people belong
to this ship and must leave on it."

What does exist:

- `Passenger` (`Game.Vehicles/Passenger.cs:8`) is a buffer of passenger entities on the vehicle —
  so a vehicle does know who is aboard *right now*.
- It is `IEmptySerializable`, i.e. **not saved**. `Game.Serialization/PassengerSystem.cs:36-53`
  rebuilds it after load by walking every entity with `CurrentVehicle` or `CurrentTransport` and
  pushing it back into the vehicle's buffer. So membership survives a reload, but only because the
  passenger side is authoritative. Anything the mod wants to persist must live on the passengers,
  not on the ship.
- `BoardingVehicle` (`Game.Routes/BoardingVehicle.cs`) sits on a waypoint and names the vehicle
  currently accepting boarders. This is the hook that decides who gets on, and it is a single
  vehicle — which is convenient, because a cruise call is a single vehicle too.
- `PublicTransportFlags` (`Game.Vehicles/PublicTransportFlags.cs`) has `Boarding`, `Arriving`,
  `EnRoute`, `Returning`, `Launched`, `Full` and `RouteSource`. The state machine exists; it just
  has no cohort semantics.

**Conclusion: the cohort has to be mod-owned state.** There is no native mechanism to borrow. This
is the single biggest decision in the feature and everything below follows from it.

## Designs considered

### A. Ride the native line machinery, cohort tracked by the mod — *recommended*

Add a `TransportLinePrefab` for the cruise line. On each call, a mod system creates the tourist
households, tags them with the voyage, and manages the return.

- **For:** the ship, route, docking and rendering are all the game's own. Reuses
  `TouristDemandSystem.SpawnTouristHouseholds`, which is a proven creation path that already resolves
  the outside connection before creating the entity.
- **Against:** every existing tourist system has to learn about a tourist that has no hotel and a
  fixed departure. That is five separate exclusions (below), and each one is a place to get it wrong.

### B. Cruise terminal as a building, no line tool — *viable fallback*

Skip the route tool. Ship a cruise terminal building that periodically receives a ship.

- **For:** avoids the entire question of whether a runtime-created route prefab shows up in the
  toolbar, which is the largest unverified risk in A. Much smaller surface.
- **Against:** the player cannot draw the route, which is most of what was asked for.

Worth keeping in reserve: if the toolbar integration turns out not to work, B delivers the
simulation half of the feature without it.

### C. Model the ship as a moving hotel — *rejected*

Give the ship `LodgingProvider` and a `Renter` buffer so passengers "live" aboard and the existing
return-to-hotel behaviour carries them back.

Rejected because pathfinding to a moving vehicle is not a thing the game does, and because
`HotelCapacitySystem` and `LodgingProviderSystem` would both start billing the ship's guests nightly
for rooms — which is not just wrong, it would feed the hotel spending figure that was only just made
correct in 1.7.1.

### D. Teleport passengers on and off — *rejected*

Rejected on the skill's own terms: use native shopping trips, pathfinding and vehicles rather than
inventing a second model. It would also look wrong, which is the whole point of the feature.

## Proposed design (A)

### New serialized state

```
CruiseVoyage      (on a mod-owned singleton-per-call entity)
  m_Version, m_Ship, m_Terminal, m_ArrivedFrame, m_DepartureFrame, m_Passengers (count)

CruisePassenger   (on each tourist household)
  m_Version, m_Voyage, m_DepartureFrame
```

Both need a version int written first — non-negotiable, see the `TourismLedger` entry in
SESSION-NOTES.md. `CruisePassenger` is what makes the cohort survive a reload, because the passenger
side is the authoritative side (see `PassengerSystem` above).

### Lifecycle

1. **Call scheduled.** A system picks the next arrival on the line.
2. **Ship docks.** Native route machinery; the mod watches for `PublicTransportFlags.Boarding` at
   the terminal waypoint.
3. **Disembark.** Create N tourist households through the existing spawn path, placed at the
   terminal rather than at a map-edge connection. Tag each with `CruisePassenger`, set
   `m_DepartureFrame`, and set `TouristHousehold.m_LeavingTime` to match.
4. **Ashore.** They behave as ordinary tourists — `TouristShoppingSystem`, leisure, attractions —
   except that they never seek lodging.
5. **Reboard.** At `m_DepartureFrame`, route them back to the terminal.
6. **Sail.** The households are deleted as a normal departure and the ship leaves.

"The same passengers return" is then true by construction, because the mod owns the cohort from
disembark to reboard. Worth being honest in the changelog that this is the same *entities*, not a
simulation of individual identity across the voyage — but that distinction is invisible to the
player, who sees the same count of people go back aboard.

### Interactions with existing systems — each one a hazard

1. **No hotel, no eviction.** A `TouristHousehold` with `m_Hotel == Entity.Null` is evicted as
   `TouristNoHotel`. Cruise passengers sleep aboard, so the eviction path, `TouristRebookSystem` and
   `HotelRoomReclaimSystem` all need to skip them.
2. **Demand target.** `TouristDemandSystem.ComputeTarget` caps the target at `hotelRooms` and drives
   arrivals from the deficit. Cruise passengers occupy no rooms, so counting them in
   `CurrentTourists` would suppress ordinary arrivals and drag the demand bar down — the bar now
   reads as *visitors who would come but have nowhere to stay*, and cruise passengers by definition
   have somewhere. They should be excluded from both, and probably reported as their own row.
3. **Wallets.** The budget is sized for `AverageStayDays` (6 nights). A 1-2 day passenger needs a
   smaller wallet, or every cruise arrival injects six nights of spending money for two nights of
   spending. With leisure now at 20% this is less dangerous than it was, but it is still a separate
   budget.
4. **Arrival counting.** Cruise arrivals would land in the new trailing-month ring buffer under the
   ship mode. Probably right, but it changes what the `/mo.` row means, and the panel should
   probably separate them.
5. **Per-party versus per-person.** Ship capacity is people; households are parties of 1-4. The
   notes already record this class of error twice (hotel rooms, `HotelRoomsPerTourist`). Convert
   once, deliberately, and say which unit each figure is in.
6. **The zero-radius origin — this design mostly dodges it.** `TouristFindTargetSystem`'s missing
   origin radius strands arrivals that are not standing on a pedestrian lane, which is exactly why
   sea connections at the map edge evicted 94-98%. A cruise terminal is a building on a road, so its
   passengers start on a lane. `TouristTargetSearchSystem` covers it regardless.

## Progress

**Step 1 done and confirmed in game.** The runtime-created `TransportLinePrefab` reaches the
toolbar, uses our icon, and draws a working line. Answers to two of the open questions below fell
out of it:

- Question 1 is **yes**. Copying `UIObject` from the stock passenger ship line — group
  `TransportationShip`, priority 300, ours at 310 — is sufficient. No separate UI registration.
- The line's name in the transport list comes from `NameSystem:544`,
  `Name.FormattedName(m_LocaleID + "[" + prefab.name + "]", "NUMBER", …)`. `m_LocaleID` is the
  shared string `"Assets.ROUTE_NAME"`, so the key is
  `Assets.ROUTE_NAME[TourismOverhaul PassengerCruiseLine]` — **including the space in the prefab
  name** — and the value needs a `{NUMBER}` token, as the stock "Ship line 3" does. Without the
  entry the list shows the raw key.

**Question 4 is moot.** `m_StopDuration` never had to be solved.
`Game.Vehicles.PublicTransport.m_DepartureFrame` is the frame the vessel is scheduled to leave, and
pushing it forward holds the ship through the game's own scheduling rather than against it. It has
to be re-asserted while the ship is boarding, because the native scheduler moves it as boarding
proceeds.

**Steps 3 and 4 written, unverified.** `CruiseVoyageSystem` puts a cohort ashore, holds the ship,
and closes the call. The eviction problem resolved better than the plan expected — see below.

### The eviction exclusion turned out not to need exclusions

The plan assumed five systems would each need to learn about hotel-less tourists. In fact
`TouristLeaveSystem:68` is the whole of tourist eviction:

```csharp
bool num = m_LodgingProviders.HasComponent(touristHousehold.m_Hotel);
reason = (!num && m_Time > 0.8f) ? TouristNoHotel
       : ((num3 < num2 && m_Time > 0.7f) ? TouristNoMoney : None);
```

Both branches key off `m_Hotel` naming an entity with a `LodgingProvider`, and the money test
compares the wallet against that provider's `m_Price`. So pointing a cruise passenger's `m_Hotel` at
the terminal, and giving the terminal a stand-in `LodgingProvider` with `m_Price = 0`, makes them
immune to both — through the game's own condition, with no native system disabled or mirrored.

The stand-in does not make the terminal a hotel, because every query that treats a `LodgingProvider`
as one asks for more: `LodgingProviderSystem` and `HotelCapacitySystem` require `PropertyRenter`,
`ServiceAvailable` and `ProcessingCompany`; the room counts require `PropertyRenter` and `Renter`. A
harbour has none of those. `CruiseTerminalLodging` marks which buildings we equipped so the provider
can be removed again without stripping one from a building that legitimately has it.

## What survives a save, and what does not

`SimulationSystem.cs:135-146` serializes `frameIndex`, so every absolute frame the cruise stores
still means the same thing after a reload. That is the assumption the whole design rests on and it
holds — it is also why the game's own `TouristHousehold.m_LeavingTime` works across saves.

Saved, and correct on reload mid-call:

| State | Where | How |
|---|---|---|
| The call — terminal, disembark and reboard frames, party count | `CruiseCall` on the ship | mod, versioned |
| Cohort membership, reboard frame, recalled flag | `CruisePassenger` on each household | mod, versioned |
| Which terminal we equipped | `CruiseTerminalLodging` | mod, empty-serializable tag |
| The stand-in provider | `LodgingProvider` on the terminal | native |
| Lodging anchor and leaving time | `TouristHousehold` | native |
| The held sailing time | `PublicTransport.m_DepartureFrame` | native |
| A recalled party's walk back | `Target` | native |

Not saved, and deliberately: `CallsServed` and `PassengersAshore` are system fields and reset on
load. They are diagnostics counters, not simulation state.

Nothing needs re-deriving in `OnGameLoadingComplete`, because every piece of state lives on an
entity rather than in a system field. That was the point of putting the cohort on the passengers.

### Two leaks the audit found

**Orphaned terminal lodging — fixed.** `ReleaseTerminalLodging` runs when a call ends normally, but
the `CruiseCall` that names the terminal lives on the ship. Delete the line or the vessel while it
is alongside and the component vanishes with it, leaving the terminal with a `LodgingProvider`
written into the save for good. `SweepOrphanedTerminals` now strips any equipped terminal no live
call references. Same discipline as `AttractivenessProvider`: anything that writes serialized state
must be able to take it back on every path, not just the clean one.

**Uninstall residue — known, not fixed.** `LodgingProvider` is a *native* component. Remove the mod
mid-voyage and `CruisePassenger` and `CruiseTerminalLodging` are skipped as unknown types, but the
provider stays on the harbour, and the ashore households keep `m_Hotel` pointing at it. Since
`TouristLeaveSystem` evicts only tourists whose hotel lacks a provider, those visitors become
immortal — and `m_LeavingTime` will not save them either, because reading it is this mod's
behaviour and the native field is an unimplemented stub.

The window is small (the provider exists only while a ship is docked) but it is real, and it breaks
the README's "safe to add and remove freely" claim in the same way the zones do. Removing the mod
between voyages is clean. This should be stated on the mod page before 1.8.0 ships.

## Open questions — measure before designing further

1. **Does a runtime-created `TransportLinePrefab` appear in the Routes toolbar?** The zones needed
   `UIObjectData` and an SVG icon, and their absence was silent. Assume routes are the same and
   expect it to take several build-and-look rounds. *This is the largest single risk.* Log the
   prefab list first — the frontend cannot be read, only measured.
2. **Is there a cruise ship asset in the base game?** The passenger ship line uses ferries and
   passenger ships. If no suitably large vessel exists, the feature either reuses a passenger ship
   or needs an authored model, which is a different kind of work entirely.
3. **Can a line waypoint be an outside connection**, so the ship genuinely arrives from off-map, and
   what does the existing passenger ship line do at its outside end?
4. **What are the units of `m_StopDuration`?** Default `1f`. A cruise needs a dwell of one to two
   in-game days — 262,144 to 524,288 frames — which is far outside anything the field was authored
   for. If it cannot express that, the ship has to be held at the stop by the mod, or leave and come
   back.
5. **Does the game cope with a vehicle stopped for an in-game day?** Unbunching, vehicle interval
   and the maintenance/refuel flags all assume vehicles keep moving.

Question 4 is the one most likely to force a redesign: if the ship cannot simply wait, the cleanest
answer is that it leaves and returns for a second call, and the cohort waits ashore for a named
future arrival rather than for a vehicle sitting at the dock.

## Suggested order

Each step should be observable in the log before the next begins.

1. Create the prefab, get it into `PrefabSystem`, and confirm whether it reaches the toolbar. Stop
   here and reassess if it does not — that is the fork to design B.
2. Draw a line in game and confirm a vehicle serves it at all, ignoring passengers entirely.
3. Add `CruiseVoyage` / `CruisePassenger` with version fields, and the disembark step. Verify a
   cohort appears and survives a save/reload.
4. Add the exclusions (1-3 above) and confirm no cruise passenger is evicted as `NoHotel` or
   distorts the demand bar.
5. Add reboarding and departure.
6. Panel row, diagnostics counters, locale strings for all twelve languages, settings.

## Scope

Steps 1-2 are genuinely exploratory and could invalidate the rest. I would not commit to 1.8.0
containing the cruise line until step 1 has been seen working — and note that no build has run in
this session at all, so 1.7.1 itself is still unverified.
