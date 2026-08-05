# Tourist demand UI

A seventh demand bar, light red, in both places the game shows demand: the Demand info view page
and the toolbar pill stack.

## What the game does

Traced from `Game.UI.InGame/CityInfoUISystem.cs` and the three demand systems.

Every native bar is the same three pieces:

1. A demand system exposes `buildingDemand` as an **int 0-100** (`CommercialDemandSystem.cs:347`)
   and a `NativeArray<int>` of signed factor weights indexed by `DemandFactor`
   (`CommercialDemandSystem.cs:364`). The array is length 19 and mostly zero.
2. `CityInfoUISystem` smooths that int into a 0-1 float every frame
   (`CityInfoUISystem.cs:197-203`) and binds it under the `cityInfo` group.
3. `FactorInfo.FromFactorArray` drops zero weights, sorts by `|weight|` descending
   (`FactorInfo.cs:22-30, 62-74`), and writes `{factor, weight}` pairs for the +/- list.

Two details carry most of the native feel:

**The smoothing is asymmetric.** `AdvanceSmoothDemand` (`CityInfoUISystem.cs:225-228`):

```csharp
math.clamp(target / 100f, current - 0.000625f * delta, current + 0.000125f * delta)
```

The bar falls five times faster than it rises. Copying these constants exactly is the difference
between a bar that belongs and one that does not.

**Demand and factors update at different rates.** Demand advances every frame from the frame
delta; the factor lists refresh on `UIUpdateState.Create(world, 256)` (`CityInfoUISystem.cs:117`),
so the text is stable while the bar moves.

### The unfinished piece

`DemandFactor.TouristDemand` is declared at `Game.Simulation/DemandFactor.cs:14` and **nothing in
the game ever writes it**. A search across the decompiled source returns the declaration and
nothing else. It sits alongside the unread departure timer and the uncalled stay-length function
as a third piece of tourism that was specified and never wired up.

We do not reuse the enum. Its name would render through the game's own localisation as a label we
do not control, and only one of our factors has a native counterpart. Our factors carry our own
keys, translated through the mod's existing locale table.

## What the bar means

One bar, with the factor list saying what is actually holding tourism back.

```
appetite = max(0, IntrinsicTarget - CurrentTourists)   // would come, before lodging is considered
unhoused = max(0, appetite - roomsFree)                // would come, nowhere to sleep
demand   = IntrinsicTarget > 0 ? clamp(appetite * 100 / IntrinsicTarget, 0, 100) : 0
```

`IntrinsicTarget` rather than `TargetTourists` on purpose: `TargetTourists` is already capped by
lodging, so using it would make the bar fall to zero the moment hotels filled — reporting no demand
at exactly the point the player most needs to build. The bar answers "how much more tourism could
this city carry", and the factors answer "and what is stopping it".

Factors, at most five, sorted by the native rule:

| Factor | Sign | Weight |
| --- | --- | --- |
| Not enough hotel rooms | + | `unhoused * 100 / max(1, appetite)` |
| Attractiveness | + | share of appetite attributable to headroom |
| Empty hotel rooms | − | `(roomsFree - appetite) * 100 / max(1, roomsTotal)` |
| City at its visitor ceiling | − | how close `CurrentTourists` sits to `IntrinsicTarget` |
| Few ways into the city | +/− | monthly arrivals against the deficit they need to close |

These are indicative shares, not an exact decomposition of the demand figure — the same caveat the
spending split carries, and worth stating rather than implying a precision that is not there.

## Structure

**`TouristDemandUISystem`** — new, modelled on `CityInfoUISystem` rather than folded into
`TourismPanelUISystem`. That system runs at interval 512, roughly eight seconds of real play, which
would make the bar visibly step. The new one takes the default UI interval, advances the smoothed
value from the frame delta with the native constants, and refreshes factors on a 256-tick
`UIUpdateState`.

Bindings, group `tourismOverhaul`: `touristDemand` (float 0-1) and `touristDemandFactors`
(`{factor, weight}[]`).

Deliberate deviation: native serializes the smoothed value so the bar does not animate up from zero
on load. We snap to target on the first update after load instead. That avoids adding a serialized
field to a mod whose save format has already broken once, and the visible difference is one frame.

**Frontend** — `ui/src/mods/tourist-demand.tsx` exporting the panel section and the toolbar pill,
both wrapped in an error boundary. The toolbar is always on screen and an undefined component there
takes the whole interface down, so the boundary degrades to the vanilla widget rather than to a
blank map.

Module paths for the demand page and the pill stack cannot be read from disk. Both are pinned with
a best guess and guarded: on a miss, log every module matching `/demand/i` and skip the extension,
exactly as the Tourism panel and infoview registry already do.

**Colour** — light red, `#e8756b`, chosen to sit beside the existing zone colours without reading as
the red the game uses for warnings and negative factors.
