# Working notes

Findings and decisions that are expensive to rediscover. Everything here was established by reading
the decompiled game or by measurement in a live city; nothing is assumed.

## Frame zero is a real frame, so it cannot mean "not yet"

Cruise passengers loaded at the map edge have no port and no shore leave until the ship docks, so
`CruisePassenger.m_ReboardFrame` was left at zero and filled in on arrival. The shore-side sweep
tests `frame >= m_ReboardFrame`, and every frame is `>= 0` — so an entire inbound complement was
judged overdue the instant it was created:

```
19:12:39,909  Cruise loading at outside connection … created 888 parties
19:12:40,038  Cruise sailing from outside connection with 0 aboard.
19:12:40,040  Cruise shore leave: 0 parties recalled, 888 sailed.
```

Eight hundred and eighty-eight parties moved away a tenth of a second after being created, before a
single one had citizens to put aboard — which then made the ship look like it had failed to load,
sending two rounds of investigation at the loading code, which was working.

The sentinel has to be a field that has no valid zero. Here that is `m_Terminal`, which is
`Entity.Null` until a port is chosen, and every shore-side check now tests that instead. The skill
lists this trap in the abstract — "defaults that are also valid values"; this is what it costs.

## Route state is split between the waypoint and the stop

A transport line has two parallel entities per port of call and it matters which one holds what:

- The **waypoint** belongs to the route. It carries `Connected`, `VehicleTiming`, and
  `WaitingPassengers` when the line is a passenger line (`TransportLinePrefab:73-90`).
- The **stop** belongs to the building. It carries `BoardingVehicle`, `TransportStop` and
  `ConnectedRoute` (`Game.Prefabs/TransportStop.cs:52-86`).

`Connected.m_Connected` is the hop from the first to the second, and the game always makes it before
reading boarding state — `TransportBoardingHelpers` writes through `data.m_Stop` (`:369`), and
`StopBoarding` resolves `Connected` before looking up `BoardingVehicle` (`:797`).

Testing `BoardingVehicle` on the waypoint therefore matches nothing, on every route, forever — and
it fails silently, because the natural shape of the code is a loop that simply never finds its
match. The symptom was a cruise ship that never waited and never landed anybody, which reads like a
scheduling problem rather than a wrong-entity problem.

This is the same trap the notes already record for citizens: a missing component reads identically
to a behaviour that never fires. Confirm which entity carries a component before concluding the
behaviour does not happen.

## `m_StopDuration` is a line's planning figure, not a dwell instruction

It is the obvious-looking way to make a vehicle wait somewhere. It is not one, and using it that way
breaks two things at once.

`TransportLineSystem:470` sums it over the line's waypoints into `stableDuration`, and `:189` turns
that into the fleet size via `CalculateVehicleCount` (`:906-909`):

```csharp
stableDuration += prefabLineData.m_StopDuration;          // :470, per waypoint
int num2 = CalculateVehicleCount(value, stableDuration);  // :189
// :908 — max(1, round(lineDuration / max(1, vehicleInterval)))
```

So setting it to a twelve-hour shore leave told the game the cruise line needed **thirty vessels**.
And because it belongs to the *line* rather than to a stop, the ship also waited twelve hours at the
outside connection — the one place it must not.

Both symptoms, one cause. The lever for parking one vehicle at one stop is that vehicle's own
`PublicTransport.m_DepartureFrame`, applied only where you want it.

Two related facts worth keeping:

- **The fleet size can be pinned at one** by making `m_DefaultVehicleInterval` far larger than any
  plausible `stableDuration`. The division rounds to zero and the `max(1, …)` floor does the rest.
- **The boarding window can be sixty frames.** `TransportBoardingHelpers:368` gives a vessel that was
  not already `EnRoute` a departure at `frame + 60`. Anything that means to extend a dwell has to
  write inside that window, which rules out update intervals of a few hundred frames.

## `m_AccessConnectionType = None` does not exclude locals, it removes passenger transport

It was set to `None` on the cruise line to stop the city's own commuters filling a ship that sits at
the quay with its doors open for twelve in-game hours. It does stop them. It also stops everyone
else, permanently, everywhere on the line:

- `TransportLinePrefab:75` gates whether the **waypoint archetype gets `WaitingPassengers`** on this
  field. No queue is ever built.
- `TransportStop:71` does the same for the stop archetype.
- `WaypointConnectionSystem:1203` passes it to `FindLane` to locate the access lane. Without one
  there is no physical way to reach the vessel.

So the line had no passenger machinery at all. `0 / 2,800` on the panel was not "locals excluded",
it was "boarding does not exist here" — and it is why no cohort could ever be carried in or out, in
either direction, by any means.

The related trap is that **citizens have no bodies until they need a trip.** `CurrentTransport` — the
citizen-to-creature link — is added in exactly two places, `TripNeededSystem:1615` and
`ObjectEmergeSystem:341`, and both require a trip in progress. A household created at an outside
connection holds `CurrentBuilding` and no body (`CitizenTravelPurposeSystem:631` shows the two are
alternatives), so hand-boarding it onto a ship fails for every citizen, always — not intermittently.
That is what `BoardCitizen` was doing, and it could never have worked.

Together these say the same thing: **passengers get onto a vehicle by needing to go somewhere.**
There is no shortcut that puts a body on a ship, and the field that lets the game do it properly was
switched off.

## The selected-info panel builds its sections from bindings, not from module exports

Adding a row to a vehicle's panel took four attempts, three of which registered cleanly through
`moduleRegistry.extend` and were never invoked — no error, no render, nothing. The dump that
explained it:

```
game-ui/game/data-binding/selected-info-bindings.ts
  -> selectedEntity, …, topSections, middleSections, bottomSections, titleSection, …, SectionType
```

The panel assembles its sections from **data C# publishes** and resolves each section's `group`
string — `InfoSectionBase.group`, e.g. `"PublicTransportVehicleSection"` — to a component
internally. Extending `public-transport-vehicle-section.tsx` therefore modifies a module the panel
never reads. So does extending `PanelSpace` from `selected-info-panel.tsx`.

What works is `InfoSection` from
`shared-components/info-section/info-section.tsx`: it is the shared component every section is built
from, so whatever renders a section renders it. The cost is that it renders many times per panel, so
a row appended there needs a guard — a module-level flag claimed during the first section and
cleared on the next microtask covers exactly one render pass.

Two smaller traps from the same hunt:

- **`cs2/ui`'s `InfoRow` renders nothing if handed props it does not expect.** Its shape cannot be
  read from disk, so passing `{left, right, subRow}` is a guess, and a wrong guess is
  indistinguishable from the row not existing. Plain markup with inline styles is uglier and it
  appears.
- **Registration succeeding says nothing about rendering.** Log from inside the component, on value
  *change* rather than once — logging once fires on the first panel opened, which is almost never
  the case being investigated.

## Frame index is not the time of day

`frame % 262144` looks like it should give the clock time and does not. `TimeSystem.GetTicks:89-91`
counts from `TimeData.m_FirstFrame` and adds the map's authored time and date offsets before taking
the remainder, so a raw frame index is out by a constant that depends on when the city was founded —
wrong in every save, by a different amount in each.

Work from the delta instead: ticks advance one per frame, so `TimeSystem.normalizedTime` plus
`(target - now) / 262144` is exact, with the offsets already applied. Floor the hours and minutes to
match `GetCurrentDateTime:191-192` rather than rounding, or the figure sits a minute ahead of the
clock in the corner.

## What a stop actually costs, and the one thing that is not a cost

`PathUtils.GetTransportStopSpecification:1527-1568` builds the whole of a stop's contribution to a
path, and it is worth knowing every term because four of them are writable and one of them is a trap.

```csharp
m_Flags = EdgeFlags.FreeBackward,                                  // :1531 alighting, always
if ((transportStop.m_Flags & StopFlags.Active) != 0)
    result.m_Flags |= EdgeFlags.Forward;                           // :1537 boarding, gated
if ((transportStop.m_Flags & StopFlags.AllowEnter) != 0)
    result.m_Flags |= EdgeFlags.AllowEnter;                        // :1541
float num = math.max(transportLine.m_VehicleInterval * 0.5f,
                     (int)waitingPassengers.m_AverageWaitingTime)
            - stopDuration;
m_StartingCost.m_Value.x += num;                                   // :1562 time
m_StartingCost.m_Value.z *= (int)transportLine.m_TicketPrice;      // :1564 money
m_StartingCost.m_Value.w *= 1f - transportStop.m_ComfortFactor;    // :1565 comfort
```

**To make a stop attractive:** `Active` and `AllowEnter` set, `m_AverageWaitingTime` at 0 so the time
term falls back to the vehicle interval alone, and `m_ComfortFactor` at 1 so the comfort axis is
multiplied out entirely. Used on the cruise line's map-edge stop.

**To make one unattractive:** raise `m_AverageWaitingTime` — it is a `ushort`, so 60000 is available
and dwarfs the interval's contribution — and set `m_ComfortFactor` strongly *negative*, which inverts
`1 - factor` from a discount into a multiplier. Two axes, so a traveller who weights one lightly is
still deterred. Used on the cruise line's city pier.

**Do not clear `StopFlags.Active` to stop boarding.** It works — the edge loses `EdgeFlags.Forward`
and nobody can board — but `FreeBackward` at `:1531` is not the whole of alighting, and an inactive
stop is not served properly. The measured result was a cruise ship whose shore party rode straight
past the city it had come to visit. Only ever set that flag, never clear it.

Note also that `m_AverageWaitingTime` is self-reinforcing and will fight anything written to it: a
rarely-visited stop accumulates a high average, the high average raises the cost, the cost keeps
travellers away, and nobody boards to bring it down. Measured at 2655 on a cruise stop against 185 at
a city quay on the same line. It has to be held, not set once.

## A destination is not a trip

`Target` on a household does not make anyone walk. `TouristHouseholdBehaviorSystem:59-66` reads it,
finds it names a valid building, and `continue`s — it treats the household as already sorted and
issues nothing. A recall written that way records a destination and starts no journey, which looks
exactly like the shore party ignoring it.

What the game acts on is `TripNeeded`, a buffer on the **citizen**. `TripNeededSystem` walks it,
spawns a body at the citizen's current location and hands it the target (`:1614-1615`). One entry
naming a building produces an ordinary walk across the city.

Three further details, each of which cost a round:

- **`Purpose` matters.** `MovingAway` can be satisfied by *placing* the citizen at the destination
  and marking them `Arrived` (`:1583-1599`) instead of travelling — which reads in game as an entire
  shore party vanishing at once. `Purpose.Leisure` always travels.
- **The departure destination lives on `MovingAway.m_Target`**, read at `CitizenBehaviorSystem:675`.
  `CitizenUtils.HouseholdMoveAway` fills only `m_Reason` (`CitizenUtils.cs:274-280`), so the game
  picks the connection unless the component is written directly.
- **A recall must be re-issued.** A citizen is re-evaluated only on its own `UpdateFrame`, so a
  single write lands on a pass some of them never take part in. Re-issue every update, skipping only
  citizens that hold `CurrentTransport` — that is the one unambiguous marker for "already walking",
  and it is the only state a last call must not interrupt. Testing for an absent `TravelPurpose`
  misses anyone busy doing something; testing for `CurrentBuilding` misses visitors inside
  attractions.

## A tourist's hotel must have a Renter buffer

`TouristHouseholdBehaviorSystem:74`:

```csharp
if (hotel == Entity.Null || !m_RenterBufs.HasBuffer(hotel))
{
    value.m_Hotel = Entity.Null;
}
```

A harbour has no `Renter` buffer, so pointing a cruise passenger's `m_Hotel` at the terminal is undone
on every pass of that system. `KeepOffTheHotels` writes it back each update, so the anchor oscillates
rather than holding, and parties are intermittently re-marked `LodgingSeeker` and set off for a real
hotel.

**Still unfixed.** The two candidate fixes both need care: giving the terminal a `Renter` buffer runs
into the reason `HotelWelcomeSystem` crashed on a harbour in the first place, and excluding cruise
passengers from that system's query means touching a native system's behaviour rather than its data.

## A control loop cannot be faster than its feedback

Two runaways in one session, both from ordering work against a measurement that had not moved yet.

The quayside top-up ordered `target - (aboard + queued)` every update. Both figures only change many
updates after a household is created — citizens, then a destination, then a body, then a walk — so
the shortfall read full every pass: ninety parties every 150ms, indefinitely.

Then the same shape in reverse: ending a load when the quay was empty. The queue drains *between*
batches, because the next batch is still becoming people, so an empty quay means "boarding has caught
up", not "boarding is finished". The vessel left eight seconds into a two-hour dwell.

The rule both violate: **when the lag between acting and observing is long, fix the rate and bound
the duration; do not derive either from the error.** The working version orders a fixed batch per
fixed interval and ends the load on a deadline.

## `ServeDockedShips` is a priority chain, and every branch ends in `continue`

Three separate faults this session were branch-ordering mistakes in that one method, and all three
presented as *silence* rather than an error:

- `SailOnEmpty` fired on every scan while a ship sat alongside, because `StartCall` runs every pass —
  twenty full route dumps in three seconds.
- Making `CruiseManifest` outlive its load meant the manifest branch intercepted the vessel at the
  city quay too, so `StartCall` was never reached: no call, no hold, no log.
- Widening adoption to "whoever the ship carried" let a closing call immediately open a new one from
  tourists who had boarded at the pier during shore leave, so the ship never left.

When adding a branch there, check what it starves below it.

## A vehicle carries what its prefab says, and boarding is capped by it

`CruiseShipCapacity` was a figure this mod invented, and loads were targeted at it. Boarding cannot
honour it. `ResidentAISystem.TryEnterVehicle:3744-3790` is the only way a citizen gets onto a public
transport vehicle, and it goes through:

```csharp
vehicle = TryFindVehicle(ref freeSpaceMap, vehicle, Entity.Null, position, isLeader: true, num, out var distance);
if (vehicle == Entity.Null) return;          // no room, nobody boards
...
int3 value = freeSpaceMap[vehicle];
value.x -= num;
```

The free space comes from `PublicTransportVehicleData.m_PassengerCapacity`, authored on the vehicle
prefab (`Game.Prefabs/PublicTransport.cs:18`, default 30). A load aimed at 2000 on a vessel that
holds a few hundred can never complete: the queue is worked down to the ship's real limit, the target
stays unmet, and the hold runs to its timeout every voyage — which is what every observed load did.

The capacity is now read from the vessel and the setting is a ceiling rather than a target. Raising
the real figure is not available: `m_PassengerCapacity` is on the vehicle prefab, a stock passenger
ship shared with every other line in the city.

Two related facts from the same function, both worth keeping:

- **Boarding is distance-gated.** `:3762` refuses to board a leader whose `distance` exceeds
  `m_MaxBoardingDistance`, recording it in `m_MinWaitingDistance` instead. `StopBoarding:805` then
  reopens the gate to `m_MinWaitingDistance + 1`, so a dwell extends itself one nearest-passenger at
  a time. After `BeginBoarding` sets `m_MinWaitingDistance = float.MaxValue`, the first `StopBoarding`
  pass widens the gate to `float.MaxValue` and it stays there.
- **A passenger mid-entry gives up if the vehicle stops boarding.** `:1088-1094` cancels the entry
  outright unless the citizen is already on the vehicle's own lane.

## "Measure, don't predict" only works if the measurement moves before you measure again

The rule that fixed the passenger count at the quay — count who actually arrived and top up — was
applied to loading at the map edge and produced a runaway:

```
Cruise top-up ... 42 aboard and 0 queued of 2000 wanted, created 95 more parties.
Cruise top-up ... 0 aboard and 0 queued of 2000 wanted, created 98 more parties.
Cruise top-up ... 0 aboard and 0 queued of 2000 wanted, created 91 more parties.
```

Ninety-odd parties every 150ms, forever, because the two things being measured — passengers aboard
and passengers queued — only move after a household has been given citizens, a destination, a body,
and time to walk to the stop. That is many updates. The shortfall therefore reads full every pass
and is ordered again.

The quayside version avoided this by refusing to judge the shortfall while any party still lacked
citizens: *measure only when there is nothing in flight*. That guard is the whole difference, and it
was dropped when the pattern was reused. **A control loop needs a signal that moves when work is
ordered, not when it lands** — otherwise the lag between the two becomes the gain of the loop.

Related: `aboard` fell 42 → 12 → 9 → 0 across those same updates, so passengers were leaving the
vessel while it was held at the map edge. Holding a ship far beyond its scheduled departure does not
accumulate a complement.

## A throwing command-buffer playback corrupts whatever walks chunks next

The single most expensive misdiagnosis in this codebase so far. Symptom:

```
System update error during GameSimulation->TouristStaySystem: NullReferenceException
  at Unity.Entities.ArchetypeChunk.GetNativeArray (EntityTypeHandle entityTypeHandle)
  at TourismOverhaul.Systems.TouristStaySystem.OnUpdate ()
```

— and the same `GetNativeArray` NRE in `CruiseVoyageSystem`, and earlier in
`CruiseDepartureUISystem`. Three systems, three unrelated chunk walks, all correct. The NRE is a
null `Archetype*`, meaning the chunk had been freed under a live chunk array.

**The cause was not in any of them.** `CruiseVoyageSystem.AdoptComplementAshore` ran
`commandBuffer.SetComponent(household, new CurrentBuilding {...})` on every party in an arriving
cohort. `EntityCommandBuffer.SetComponent` on an entity that does not have the component **throws at
playback**, and a playback that throws part way through leaves the world half-applied. With a full
complement that is nine hundred throwing commands in one `EndFrameBarrier` buffer, and the wreckage
lands on whichever system iterates chunks next — which is why the crash appeared to move between
systems and appeared to follow whatever had last been edited.

Two things made it hard:

- **The crash never names its cause.** The stack points at the reader, and the reader is fine.
  Correlate on *timing* instead: it fired the moment a ship began boarding at the pier, which is
  when `StartCall` runs, which is the only thing that queues those commands.
- **A plausible wrong answer was available.** `CountAshoreFor` was genuinely borrowing this system's
  type handles from the UIUpdate phase, which is illegal and was worth fixing — and fixing it
  appeared to work, because the crash simply moved. An explanation that accounts for one of three
  occurrences is not an explanation.

The general rule: **`SetComponent` through a command buffer needs the component to be there at
playback, not at recording time.** Guard with `HasComponent`, or use `AddComponent`, or do not write
it. And when a chunk-iteration NRE appears in a system that has not changed, suspect a barrier
playback rather than the walk.

`CurrentBuilding` was the wrong component regardless. On a household it is the *initialisation
marker*: `HouseholdInitializeSystem` requires it and removes it once citizens are assigned, which is
how this mod's own leaked-household query identifies never-initialised households
(`TouristDemandSystem:268-276`). Adding it back to an initialised household would tell the game that
household had never been set up.

## A type handle belongs to one system's update, so a public chunk walk is a crash waiting

`CruiseVoyageSystem.CountAshoreFor` was a thin wrapper over a chunk walk, and
`CruiseDepartureUISystem` called it from the UIUpdate phase to fill the panel's ashore row. Clicking
a docked cruise ship crashed the game:

```
System update error during UIUpdate->CruiseDepartureUISystem: System.NullReferenceException
  at Unity.Entities.LookupCache.Update (Archetype* archetype, TypeIndex typeIndex)
  at Unity.Entities.ArchetypeChunk.GetNativeArray[T] (ComponentTypeHandle`1[T]& typeHandle)
  at TourismOverhaul.Systems.CruiseVoyageSystem.CountPassengersAshore
```

**This was not the cause of that crash** — see the entry above; the real fault was a throwing
command-buffer playback, and the same NRE recurred inside `CruiseVoyageSystem`'s own update after
this was fixed. It is recorded anyway because the pattern is genuinely illegal and the fix is worth
keeping: `GetComponentTypeHandle<T>()` and `GetBufferTypeHandle<T>()` resolve against the calling
system's `SystemState` and are only valid while *that* system is updating.

The fix is the one the skill states for the frontend and which applies just as well between two
managed systems: **publish the answer from the system that knows it.** The count is now taken once
per update where the handles are legal, into a `Dictionary<Entity, AshoreCount>`, and every reader
gets a lookup that touches no ECS state. That is also cheaper than it was — the walk previously ran
once per docked ship per update *and* once per interface frame.

The general rule: a method that walks chunks must be private to its system. If another system needs
the number, it needs the number, not the walk.

## The ship's hold has exactly one lever, and it is gated on a component on the stop

Two facts settle how a vessel can be parked at a quay, both traced.

**`PublicTransport.m_DepartureFrame` has one writer in the whole game** —
`TransportBoardingHelpers.BeginBoarding:378/:388`. So once pushed forward, nothing moves it back
until the vessel begins boarding somewhere again. It is a safe field to hold.

**`StopBoarding` only consults it on one of four paths.** `TransportWatercraftAISystem:797-800`
sets `flag` from `BoardingVehicle.m_Vehicle` on the stop behind the target waypoint, and the hold at
`:807` is `if (flag && frame < m_DepartureFrame) return false`. So:

- `flag` false — `:850` clears `Boarding` and returns true. **The departure frame is never read.**
- `forcedStop: true` from `:255-259` (target gone or pathfind failed) or `:316-318` (no longer at
  path end) — the whole block at `:801-822` is skipped.

That last one matters for "sometimes": `Game.Routes.BoardingVehicleSystem` nulls
`BoardingVehicle.m_Vehicle` on **every stop in the city** whenever any waypoint anywhere is `Updated`
or `Deleted`, or after a load, if the named vehicle's `Target` does not resolve back to that stop. A
ship parked for twelve in-game hours is exposed to that for the whole call, and whether it fires
depends on unrelated player activity.

Which of the four actually fires has not been measured yet. `ReportEscape` logs the readings that
separate them.

## `LodgingProvider` is the mod's definition of "hotel", so adding one has consequences

`CruiseVoyageSystem` gives a cruise terminal a stand-in `LodgingProvider` with `m_Price = 0`, which
is what keeps its passengers out of `TouristLeaveSystem:68`. That immediately crashed
`HotelWelcomeSystem.UpdateBonus` with a `NullReferenceException`, and the reason is worth keeping.

`HotelWelcomeSystem` treats **bare `LodgingProvider` as the definition of a hotel** — deliberately,
per the comment at `:63-71`, widened from `LodgingProvider + PropertyRenter + Renter` so that
signature hotels are not missed. So the harbour read as a hotel that had just opened: it was given a
welcome boost, stocked with lodging it cannot sell, and then indexed for its `Renter` buffer, which
a harbour does not have. `chunk.GetBufferAccessor` returns a *default* accessor for a chunk without
the buffer rather than failing, and the default throws on indexing.

Two lessons, both already in the skill's list and both worth a concrete instance:

**A shared marker is state, not a cause.** Before adding a native component to an entity that would
not normally carry it, search every consumer. Here, three of the mod's own queries key off bare
`LodgingProvider` (`HotelWelcomeSystem` twice, `TourismDiagnosticsSystem` once) and three others
happen to be safe only because they additionally require `PropertyRenter` or `Renter`. That is luck,
not design — anything added later that keys off the bare component will need the same exclusion.
`CruiseTerminalLodging` exists to be excluded, and is.

**`chunk.GetBufferAccessor` on a chunk without the buffer is a latent crash, not an error.** Guard
with `chunk.Has(ref handle)` — `CleanUpLeakedHouseholds` already did. This was a real pre-existing
bug that the cruise terminal merely made reliable: the same query was widened specifically to admit
placed hotels that lack the renting components, so a signature hotel without `Renter` would have
tripped it too.

## The surge multiplier is clamped, and scaling capacity moves it the wrong way

`EconomyUtils.GetServicePriceMultiplier:548-551`:

```csharp
return math.lerp(0.7f, 1.3f, math.saturate(1f - serviceAvailable / (float)maxServiceAvailable));
```

Two things follow, and `LeisurePricingSystem` originally got both backwards.

**It is clamped to [0.7, 1.3].** Whatever else is true, this term can move a price by at most ±30%.
It can never explain a large overspend, so it is never the right place to look for one.

**Raising `m_MaxService` raises the price.** For a given stock, a larger maximum shrinks
`available/max`, which moves the lerp toward 1.3. Raising it also lifts the production ceiling so
stock can grow to meet it, which pulls back the other way — the net is genuinely ambiguous. What is
not ambiguous is that "more capacity means cheaper visits" is not a safe reading of this function.

The lever that does scale the charge is `m_ServiceConsuming`, at `LeisureSystem:102` and `:125`:

```csharp
num2 = math.max((int)((float)serviceCompanyData.m_ServiceConsuming / kUpdateInterval), 1);
int num4 = (int)((float)num2 * marketPrice * num3);
```

Same shape as lodging: the price is derived output, consumption is the input. Note `num2` also
comes out of the venue's stock, so cutting it leaves availability higher and nudges the surge term
toward 0.7 as a second-order effect — and note the `max(..., 1)` floor, which is why scaling below
`kUpdateInterval` (5) does nothing at all.

Measured, before the fix: non-lodging spending ran at ~24,000 per household per in-game day against
a budget allowing 1,050, i.e. ~23x. Leisure was ~71% of it. Wallets emptied in well under one
in-game day and tourists were evicted as `TouristNoMoney` in waves — `NoTarget` and `NoHotel` stayed
flat at 5 and 11-13 throughout, so it was never routing or lodging.

**Revenue caveat.** Money paid and stock consumed both scale with `num2`, so revenue per unit of
stock is unchanged, but revenue per visit falls with the setting. Venues earn proportionally less
unless visit numbers rise to compensate. Watch for leisure company insolvency after a large cut.

## What the guest pays is not what the hotel receives

`LodgingProviderJob.Execute:138-158` settles the two sides with different arithmetic:

```csharp
float num5 = num4 * marketPrice;                                  // per guest, per update
EconomyUtils.AddResources(Money, -(int)num5, m_Resources[renter]);   // :145 — truncated
int amount = Mathf.RoundToInt(num5 * (float)num6);                   // :148
EconomyUtils.AddResources(Money, amount, m_Resources[entity]);       // :150 — rounded once
```

A wallet loses `(int)num5`; the company receives the untruncated total rounded once over the whole
hotel. At the shipped rate (30/32 x 50 = 46.875 → 46) that is about 2% apart. The spending ledger
measures money leaving tourists, so it must take `guests x (int)num5` — the company's receipt is a
different quantity that happens to look like the same one.

`HotelCapacitySystem` was reporting the company figure. Now both its paths report the wallet one.

## Observing a native system instead of replacing it

`HotelRoomMultiplier` defaults to 1, and the multiplier being above 1 is `HotelCapacitySystem`'s
enable condition — so on a default setup it stood down, `LodgingProviderSystem` did the billing,
and `LodgingChargedSinceReset` stayed zero. Hotel spending read 0$ for every player who had not
raised the multiplier, and every other category's share was overstated to match.

The fix is an observation walk on the inactive branch: reproduce the native guest count, multiply
by the traced per-guest charge, write nothing. Two things make it safe to run beside a system that
is live:

- **The count does not depend on ordering.** Non-tourist renters are dropped and the overflow above
  capacity is evicted (`:117-137`) before anyone is billed, and both are deterministic from state
  that can be read. Applying the same filters gives the same number whether the native job has
  already run this frame or is about to.
- **Reads go through `EntityManager`, not chunk handles.** `EntityManager.GetBuffer` settles the
  native job's writes to `Renter` before the read; `ToArchetypeChunkArray` does not, and would race
  it.

The tick that hands the native system back is skipped. On that tick there is no charge to attribute
either way — the native system was disabled when it would have run, or we billed the same guests
ourselves — so counting it would report money nobody paid.

## The /mo. row is a sliding window, and windows have to be saved

Two earlier attempts at this row failed the same way: a calendar accumulator, then a smoothed rate,
both held in system fields. CS2 saves entities, not systems, so both reset on reload — and since a
displayed month is one in-game day, over an hour of real play, they spent most of their lives
meaningless.

It is now a ring of 128 per-slice counts of 2048 frames each, tiling the 262144-frame window
exactly, on a serialized singleton with a `DynamicBuffer`. Two details that are easy to get wrong:

- **Bucket indices must be absolute** (`frameIndex / kFramesPerBucket`), not ring positions. The
  distance between two absolute indices is what says how many slices to clear; ring positions
  cannot tell "one slice on" from "one slice short of a full lap".
- **The "never written" sentinel is -1 and must not be treated as a distance of zero.** Doing so
  walks from bucket 0 to the current one — millions of iterations on a mature save — to clear 128
  slices. Both -1 and any gap of a lap or more mean the same thing: clear everything.

The window total is cached in a field and re-derived in `OnGameLoadingComplete`. Two UI systems read
it at interface frequency, and summing 128 entries for each of them would be work done hundreds of
times a second for a number that changes every few seconds.

## The frontend cannot be read, only measured

`F:\CS2Decompiled` holds the C# assemblies only. The interface is TypeScript compiled into packed
`.cok` archives, and the locale strings are packed with it. Neither is greppable. Building the
demand bar took roughly a dozen build-and-look rounds because of this, and most of them were spent
guessing where one log line would have answered it. If a frontend question comes up, log first.

Things that cost a round each, all of which now have one-line answers:

**Class map values are multi-class strings.** `classes.title` is `"title_TAF title_PYv title_bwV"`,
not one name. `"." + value` therefore builds a *descendant* selector matching nothing, silently.
One value even contains a literal `undefined`. Split on whitespace and join with dots — see `sel()`
in `ui/src/mods/tourist-demand.tsx`. Passing the raw string as `className` is fine; only selectors
break.

**The game usually wants data, not markup.** Both surfaces looked like they needed a component
built to match, and neither did. Registering tourism in `DemandType` / `demandColors` /
`demandIcons` made `DemandSection` render the whole thing. `DemandBars` turned out to take its bars
as `items: [{key, color, value}]` — one appended entry. The class map there is geometry
(`maxBarHeight`, `barGap`, `rangeFill`), which correctly indicates the bars are computed into a
fixed SVG; the wrong conclusion was that a matching bar had to be drawn.

**Missing text means an empty element, not a missing one.** For a registered type the game builds
the full structure and leaves the strings blank, because the locale key does not resolve. The
section title is `[class*="title_"]` inside the section; the pane's are `[class*="title_"]` and
`[class*="paragraphs_"]` under `infoColumn`. Fill those. Appending a node instead always looks
wrong however it is anchored, because the real elements are already sitting above it.

**The title locale namespace does not exist.** Sixteen candidate patterns were probed and ten
registered simultaneously with marker letters; none rendered. The game does not build the section
title from a key on the type name, so there is nothing for a locale entry to hook. Writing into the
element is the fix, not a workaround.

**`useCachedLocalization` is per-consumer.** Patching the object it returns does not intercept the
calls native components make. That diagnostic does not work; do not try it again.

**Wrap the content, not the page.** `CityInfoDemand` is the scroll container: a sibling appended
after it renders outside the panel, full-bleed across the toolbar. `DemandSection` is the thing
inside the list. Its props are `{type, demand, factors, onHover, onSelect, className}` — `onHover`
and `onSelect` are **required**; omitting them makes every hover throw inside the native handler,
which an error boundary then catches and re-renders from, producing what looks like a performance
collapse.

**`__Type` must match.** Managed bindings the native UI consumes carry it. Factor rows expect
`Game.UI.InGame.FactorInfo`; ours said `TourismOverhaul.DemandFactor` and rendered nothing.

## The zero-radius origin — the root of almost everything

`TouristFindTargetSystem:99-104` builds its pathfind origin without setting `m_Value2`, the origin
search radius. `CommonPathfindSetup.SetupCurrentLocationJob:83`:

```csharp
if ((FindTargets(entity, entity, 0f, ...) != 0 && flag)
    || ((m_Methods & PathMethod.Flying) == 0 && num <= 0f))
{
    return;
}
```

With `num = 0` and no `Flying` method the job returns before the road fallback (`:101`), the airway
lookups (`:105-128`) and the radius search (`:130`). The only thing that can supply an origin is the
zero-radius `FindTargets` in the condition itself, which succeeds only where a pedestrian lane lies
directly under the household.

Road connections sit on such a lane. Air and sea connections at the map edge do not. Measured: 34%
eviction by road against 94-98% for everything else, unchanged by free rooms, arrival rate, or which
building the arrival was placed in.

Replaced by `TouristTargetSearchSystem` with a 150m radius and three attempts. `NoTarget` went from
20,122 per month to 7.

**Do not "simplify" this back to the native system.** It looks like a pointless copy until you know
what the one changed field does.

## Things that are counted per party, not per person

- **Hotel rooms.** `HotelReserveJob:182-189` decrements `m_FreeRooms` once per household. A family of
  four takes one room. So tourists ÷ occupied rooms ≈ 2.3, which looks wrong and is not.
- `HotelRoomsPerTourist` is measured against headcount while rooms are consumed per party, so the
  "city wants N rooms" figure is roughly 25x overstated. Harmless — only its sign is read — but the
  number is meaningless as displayed.

## Time

- 262,144 frames per in-game day. `GetUpdateInterval` must be a power of two.
- `TimeSettingsPrefab.m_DaysPerYear = 12`, so **one in-game day is one displayed calendar month**.
- `GetCurrentDateTime().Month` is useless: `day = 1 + floor(daysPerYear * normalizedDate) % daysPerYear`
  never exceeds 12, and `CreateDateTime` does `AddDays(day - 1)` from January, so the month is always
  1. Derive months from `normalizedDate * 12` instead.

## Lodging is a commodity, not a price

`LodgingProviderSystem:138-158`. A hotel holds a stock of `Resource.Lodging` and each guest consumes
`m_TouristLodgingConsumePerDay` (ships at 30) per day, paying `consumed x market price`. The nightly
rate is emergent, not authored: 30 x 50 = 1,500.

`LodgingProvider.m_Price` is derived output, not input. To change the room rate, scale consumption —
scaling the market price fights the resource economy.

## Zone membership lives in two fields

- `BuildingSpawnGroupData` (shared) — what may be **built** where. `ZoneSpawnSystem:288`.
- `SpawnableBuildingData.m_ZonePrefab` — where a building may **stand**. `ZoneCheckSystem:337`.

Move only the first and buildings spawn then condemn on the same tick. `BuildingInitializeSystem`
derives the first from the second (`:1024`) and also uses it to widen the zone's height range
(`:1045-1061`).

`CondemnedBuildingSystem` deletes a quarter of condemned buildings every 64 frames, and
`ZoneCheckSystem` only re-examines buildings inside recently changed zoning bounds (`:475-484`) — so
a condemnation during load is never revisited and is fatal within seconds.

## Serialized state that must be restored

`AttractivenessProvider.m_Attractiveness` is serialized. `AttractionCrowdingSystem` scales it, so it
keeps the authored value in a `NativeHashMap` and always writes `base x factor`, never a
read-modify-write. It restores every base value on disable and on destroy. **Any future system that
damps a serialized value must do the same** or it ratchets the save downward permanently.

## A citizen is a record, not a body

`Citizen` entities hold state. The thing that physically moves — walks, boards, drives — is a
separate creature entity, and `CurrentVehicle` lives on that one. `Game.Citizens.CurrentTransport`
is the link between them, the same hop `CommonPathfindSetup:88-95` makes.

Looking for `CurrentVehicle` on the citizen silently finds nothing, which read as "tourists never
use transport" for several rounds. `ResourceBuyer`, by contrast, *is* on the citizen
(`CitizenBehaviorSystem.GoShopping` adds it there). Check which entity a component belongs to
before concluding a behaviour does not happen.

## Money paths worth knowing

- **Lodging** — `LodgingProviderSystem:138-158`. Guest pays, hotel company receives. Continuous,
  every tick, for as long as a room is held.
- **Leisure** — `LeisureSystem:123-129`. Household pays, venue company receives. Price is
  `consumption x market price x GetServicePriceMultiplier(available, max)`, so it surges as a venue
  is drained.
- **Fares** — `ResidentAISystem:3922-3928`. Household pays, **the city budget** receives directly,
  not a company. Residents' and visitors' fares are indistinguishable once paid, so a split has to
  be reconstructed at the point of boarding.
- **Shopping** — settled by `ResourceBuyerSystem`. The citizen carries `ResourceBuyer` only while a
  purchase is outstanding.

## Inferring behaviour from momentary components

`ResourceBuyer` exists only while a purchase is outstanding. Sampling households every few thousand
frames misses most of them, so shopping read as 1% of tourist spending while a quarter of it sat in
"unattributed". The fix was to stop inferring: `TouristShoppingSystem` grants the need, so it marks
the household with `ExpectsPurchase` at that moment and the ledger clears the mark with the drop
that pays for it.

The general rule: if this mod causes the behaviour, record it directly. Only infer what the game
does on its own, and expect to lose most of it if the marker is short-lived.

The same reasoning gave exact figures for lodging (counted in `HotelCapacitySystem` where guests are
billed) and fares (reconstructed from the route on the transition onto a vehicle).

## Serialized components need a version field from day one

`TourismLedger` shipped without one. Adding a fifth field made the reader run past the end of older
saves and throw `ComponentSerializerException: Data size mismatch`. Renaming the type to
`TourismLedgerData` is what made those saves loadable again — the old name no longer resolves, so
stale data is skipped rather than misread.

Write a version int first, always. Add new fields at the end and read them conditionally.

## Publishing

- Verbs are `Publish` (first upload), `NewVersion` (new package), `Update` (metadata only). There is
  no `New`.
- `ModId` is **not** written back by the publisher. Set it by hand. It is 153543.
- `ShortDescription` must be 200 characters or fewer; the upload is rejected, not truncated.
- The `ChangeLog` field is published verbatim per version — put only that version's notes in it.
- The UI and managed halves deploy independently. Webpack can fail while the C# build succeeds,
  shipping a stale `.mjs`. Check both.

## Open

- ~4,000 households per snapshot are emptied of citizens without going through the departure path.
  `HotelRoomReclaimSystem` clears them faster than they appear, so it is not harmful, but the
  mechanism is unknown.
- Arrival counting now happens on conversion (`Components/ArrivalMode`), not dispatch. Average
  wallet held should converge below the starting budget; if it does not, something tops wallets up.
