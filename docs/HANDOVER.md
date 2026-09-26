# Handover — cruise line

Everything below is either **verified in game** (seen in a log or watched happen) or **unverified**
(written, compiled, never observed working). The distinction is the most valuable thing here.

Read `docs/SESSION-NOTES.md` first — it holds the traps that cost real debugging time, most of them
from the session that built the loading and return machinery. `docs/CRUISE-LINE-PLAN.md` has the
original design and its rejected alternatives, and is now largely historical: the implementation
diverged from it in one fundamental way, described below.

## Where the repo stands

Stamped **1.10.0** — `PublishConfiguration.xml`, its `ChangeLog` field and `CHANGELOG.md` all agree.

`CruiseVoyageSystem` is split across three files: the main system (decisions, logging, spawning),
`CruiseVoyageSystem.ShoreParty.cs` (the Burst `ShorePartyJob` and the `ShorePartyAccess` helpers it
shares with the main thread) and `CruiseVoyageSystem.Vessels.cs` (the Burst `VesselJob`, which owns
every read and write of the game's vehicle and stop data). The main thread no longer reads
`PublicTransport`, `Target`, `CurrentTransport` or `CurrentVehicle` on the hot path; it works from the
jobs' observations and sends holds and releases back as requests. Keep it that way: the first
main-thread read of a type the frame's jobs write waits for all of them, and narrowing the wait only
moves it to the next type.

`GameVersion` in `PublishConfiguration.xml` reads `1.6.*` — check it against the build shown on the
main menu before an upload, because Paradox rejects the package on a mismatch. The verb is
`NewVersion`, run from the managed project directory so the media paths resolve.

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
   throughout. The load ends at the deadline, when the vessel is physically full, or when the
   **Cruise ship passengers** setting's worth of tourist households is aboard. The ship is the mod's
   own Cruise Ship prefab (`CruiseLineSystem`), whose capacity is that setting.
4. **At the city quay**, `AdoptCarriedPassengers` makes the shore party out of whoever the vessel
   carried, anchors their lodging to the terminal and cancels the hotel errand they arrived with.
   `StartCall` holds the ship for the shore leave.
5. **At last call** (4.5 h x ln(1 + stay / 4 h) before sailing, spread over a fifth of that) each
   citizen is issued a `TripNeeded` naming the ship's sea connection. Walkers are turned round on the
   spot with a `ResetTrip` event; citizens left with no building and no body are given back the last
   building they were seen in (the terminal if that fails twice), since `TripNeededSystem` serves only
   citizens with a `CurrentBuilding`. The recall is re-asserted every 1024 frames.
6. **At the reboard frame** the call closes and the parties leave via `MovingAway`. The ship sails
   early once the whole party is back, or once it is full after last call, and waits at most one
   in-game hour past its time for anyone still coming back.

## Verified working

- The line tool, its icon, the drawn line, the name, one vessel per line.
- The ship fills at the map edge and sails after its full dwell.
- It holds at the city quay for the shore leave and then leaves.
- The shore party comes ashore, stays out of hotels, and wanders the city.
- Recalled parties walk back to the harbour.
- Locals are heavily discouraged from the pier by cost.
- Recalled parties walk back, run, and board; the ashore count falls as the vessel's rises.
- The selected-vehicle panel shows the sailing time and the ashore count, and the time is accurate.

## Open issues, roughly by value

### 1. The harbour lodging anchor oscillates

`TouristHouseholdBehaviorSystem:82-89` nulls `TouristHousehold.m_Hotel` every 1024 frames when the
household is not in the hotel's `Renter` list, and cruise parties deliberately are not (a building's
utility demand follows its renters). The shore sweep restores it within 64 frames, so about one party
in sixteen briefly shows no accommodation; the `CruiseVoyage timing` log line reports how many anchors
each sweep restored. `TouristRebookSystem` excludes cruise parties, so the gap no longer books them a
real room. Written up in SESSION-NOTES with both candidate fixes and why each needs care.

### 2. Possible orphaned hotel reservations

Adopted passengers arrive having already reserved a real room — `LodgingSeeker` is how they earned a
destination. Adoption overwrites `m_Hotel` with the terminal, which may leave that hotel's
`m_FreeRooms` decremented for a guest who is now at sea. **Unverified.** Watch whether
`free hotel rooms` in the diagnostics drifts down across several voyages without recovering.

### 3. The passenger buffer counts everyone

`CountOutboundAboard` reads the vessel's whole `Passenger` buffer, so commuters riding the line are
counted as complement — measured at "1635 aboard of 1000". It now also returns the tourist households
alone, and that figure is the one the "complement aboard" early sailing uses. "Physically full"
compares the whole buffer with the vessel's capacity, since everyone aboard really does take a place.

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

`LeisureCostPercent`, `CruiseShoreLeaveHours`, `CruiseShipCapacity`, the two panel labels and the
Cruise Ship's name and description are English-only. Keys are appended at the end of `Translations.Keys`, so the locale arrays are simply
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
