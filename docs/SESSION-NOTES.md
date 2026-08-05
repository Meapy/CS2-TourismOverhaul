# Working notes

Findings and decisions that are expensive to rediscover. Everything here was established by reading
the decompiled game or by measurement in a live city; nothing is assumed.

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
