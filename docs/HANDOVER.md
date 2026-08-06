# Handover — cruise line

Everything below is either **verified in game** (seen in a log or watched happen) or **unverified**
(written, compiled, never observed working). The distinction is the most valuable thing here.

Read `docs/SESSION-NOTES.md` first — it holds the traps that cost real debugging time, most of them
from the session that built the loading and return machinery. `docs/CRUISE-LINE-PLAN.md` has the
original design and its rejected alternatives, and is now largely historical: the implementation
diverged from it in one fundamental way, described below.

## Where the repo stands

`PublishConfiguration.xml` says **1.7.1**. The working tree carries unreleased **1.8.0** cruise-line
work on top of it. Nothing has been published.

## How the feature actually works

The plan assumed the mod would own a passenger cohort and move it. It does not, and cannot — three
separate attempts to move bodies by hand failed, each louder than the last (silently at the map edge,
as a `NativeQuadTree` crash at the quay, as a stale count at the connection). **Bodies and routes
belong to the game; the mod's job is to give citizens reasons.**

The cycle as built:

1. **Between sailings**, `MaintainCruiseQueue` creates tourist households at the line's map-edge
   connection — a fixed batch on a fixed interval — and marks each `LodgingSeeker`. That marker is
   what earns them a destination from `TouristTargetSearchSystem`, and a destination is what makes
   the game give them bodies. They walk to the stop and queue.
2. **The map-edge stop is held maximally attractive** and the city pier maximally unattractive, both
   through `PathUtils.GetTransportStopSpecification`. See SESSION-NOTES for every term.
3. **The ship arrives, `BeginLoading` holds it** for `kLoadTimeoutFrames`. The queue boards natively
   throughout. Only the deadline ends the load.
4. **At the city quay**, `AdoptCarriedPassengers` makes the shore party out of whoever the vessel
   carried, anchors their lodging to the terminal and cancels the hotel errand they arrived with.
   `StartCall` holds the ship for the shore leave.
5. **At last call** (`kLastCallFraction`, 33%) each citizen is issued a `TripNeeded` naming the
   terminal, re-issued every update to anyone not already walking.
6. **At the reboard frame** the call closes and the parties leave via `MovingAway`.

## Verified working

- The line tool, its icon, the drawn line, the name, one vessel per line.
- The ship fills at the map edge and sails after its full dwell.
- It holds at the city quay for the shore leave and then leaves.
- The shore party comes ashore, stays out of hotels, and wanders the city.
- Recalled parties walk back to the harbour.
- Locals are heavily discouraged from the pier by cost.

## Open issues, roughly by value

### 1. The harbour lodging anchor oscillates

`TouristHouseholdBehaviorSystem:74` nulls `TouristHousehold.m_Hotel` when the hotel has no `Renter`
buffer, which a harbour has not got. `KeepOffTheHotels` rewrites it every update. Parties are
therefore intermittently re-marked `LodgingSeeker`. This is the root of anything hotel-related that
looks flaky. Written up in SESSION-NOTES with both candidate fixes and why each needs care.

### 2. Possible orphaned hotel reservations

Adopted passengers arrive having already reserved a real room — `LodgingSeeker` is how they earned a
destination. Adoption overwrites `m_Hotel` with the terminal, which may leave that hotel's
`m_FreeRooms` decremented for a guest who is now at sea. **Unverified.** Watch whether
`free hotel rooms` in the diagnostics drifts down across several voyages without recovering.

### 3. The passenger buffer counts everyone

`CountOutboundAboard` reads the vessel's whole `Passenger` buffer, so commuters riding the line are
counted as complement — measured at "1635 aboard of 1000". Nothing depends on it now that the load
ends on a deadline, but any future logic keyed to it will be wrong.

### 4. Queue yield is roughly a fifth

Batches of a thousand produced a plateau around three hundred. The rest are lost between creation and
the stop. The plateau responds to the rate sub-linearly, so it is an equilibrium between arrivals and
people giving up waiting, not a ceiling on ordering.

### 5. Shared terminals

Everything the mod writes to a stop — waiting time, comfort factor, flags — applies to that stop, not
to the line. A harbour shared between the cruise line and an ordinary passenger ship line gets both.
A dedicated terminal avoids it.

### 6. `CruisePassenger.m_Aboard` is vestigial

Serialized but no longer written. Remove at the next component version bump, not before — the version
scheme handles appended fields, not deleted ones.

### 7. Translations

`LeisureCostPercent`, `CruiseShoreLeaveHours`, `CruiseShipCapacity` and the two panel labels are
English-only. Keys are appended at the end of `Translations.Keys`, so the locale arrays are simply
shorter and fall back to English.

## Working rules that earned their place

- **Log before guessing at the frontend.** The UI ships packed in `.cok` archives.
- **Confirm which entity carries a component.** A missing component reads identically to a behaviour
  that never fires.
- **Zero is a real value.** Sentinels need a field with no valid zero.
- **Measure, don't predict** — but only where the measurement moves before you measure again.
- **A fix in `ServeDockedShips` can starve a branch below it, and it will look like silence.**

## Build

```powershell
cd ui; npm run build; cd ..
dotnet build .\TourismOverhaul\TourismOverhaul.csproj -c Release
```

A UI build failure is blocking — webpack can fail while the C# build succeeds, shipping a stale
`.mjs` beside a fresh `.dll` with no error anywhere.
