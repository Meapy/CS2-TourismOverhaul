# Working notes

Findings and decisions that are expensive to rediscover. Everything here was established by reading
the decompiled game or by measurement in a live city; nothing is assumed.

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
