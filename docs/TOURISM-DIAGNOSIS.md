# Cities: Skylines II — Tourism Simulation Diagnosis

All findings traced from the decompiled managed assemblies of the installed game
(`Cities2_Data/Managed/Game.dll`, decompiled to `F:\CS2Decompiled`). Every claim below
cites the file and line it came from. Nothing here is inferred from community wikis.

Timing note used throughout: **262144 simulation frames = 1 in-game day**
(see any `GetUpdateInterval` implementation, e.g. `AirPollutionSystem.cs:86` returning
`262144 / kUpdatesPerDay`).

---

## The chain, end to end

| Stage | System | Interval | Per day |
|---|---|---|---|
| Compute city attractiveness + UI values | `TourismSystem` | 32768 | 8 |
| Decide whether a tourist arrives | `TouristSpawnSystem` | 16 | 16,384 |
| Give the household citizens | `HouseholdInitializeSystem` | on creation | — |
| Find a hotel / attraction | `TouristFindTargetSystem` | 16 | 16,384 |
| Re-seek lodging | `TouristHouseholdBehaviorSystem` | 64 | 4,096 |
| Evict / send home | `TouristLeaveSystem` | 512 | 512 |

Throughput is **not** the bottleneck — the game is willing to attempt 16,384 arrivals per
day. The bottlenecks are all downstream of that attempt.

---

## Finding 1 — City attractiveness is logistic-capped at 100

`TourismSystem.cs:95-107`

```csharp
foreach (AttractivenessProvider p in providers)
    num += (float)(p.m_Attractiveness * p.m_Attractiveness) / 10000f;

num = 200f / (1f + math.exp(-0.3f * num)) - 100f;
CityUtils.ApplyModifier(ref num, modifiers, CityModifierType.Attractiveness);
value.m_Attractiveness = Mathf.RoundToInt(num);
```

The logistic maps any non-negative raw sum into `(-100, 100)`. It saturates fast: a raw sum
of 30 already yields **99.98**. Since each provider contributes `attractiveness² / 10000`,
a handful of decent attractions pins the city at 100 permanently.

**Consequence:** past a fairly early point, every additional park, landmark and signature
building contributes *exactly nothing*. The displayed "100" is not an achievement, it is a
ceiling you hit and then keep pushing against.

Only `CityModifierType.Attractiveness` modifiers (policies, signature buildings) are applied
*after* the logistic (`CityUtils.cs:17-25`, additive then multiplicative), so values above
100 are possible — but see Finding 2 for how little that buys.

---

## Finding 2 — Target tourist population is effectively hard-capped at ~1,500

`TourismSystem.cs:171-180`

```csharp
public static int GetTargetTourists(int attractiveness)
{
    if (attractiveness <= 100)
        return attractiveness * 15;          // → 1500 at the cap

    float num = attractiveness - 100;
    return 1500 + Mathf.RoundToInt(100f * Mathf.Log10(1f + num));
}
```

Combined with Finding 1, the practical target is **1,500 tourist citizens**, and the growth
beyond that is base-10 logarithmic:

| Attractiveness | Target tourists |
|---:|---:|
| 100 | 1,500 |
| 200 | 1,700 |
| 500 | 1,860 |
| 1,100 | 1,900 |

Doubling attractiveness from 100 to 200 — which requires stacking modifiers on an already
saturated city — buys **200 extra tourists**. In a 200,000-citizen city, tourism tops out
below 1% of the population no matter what is built. This is the single biggest reason
tourism feels inert at scale.

---

## Finding 3 — 80% of tourist arrivals are silently discarded

This is the most severe defect, and it is the direct cause of "planes and ferries never
bring anyone."

`DemandPrefab.cs:79-80`

```csharp
[Tooltip("Percentage that tourist will spawn at OCs x:Road y:Train z:Air w:Ship")]
public float4 m_TouristOCSpawnParameters = new float4(0.1f, 0.1f, 0.5f, 0.3f);
```

Road 10%, Train 10%, **Air 50%, Ship 30%**.

`BuildingUtils.cs:396-440` — one roll picks a single transfer type, then filters outside
connections to *exactly* that type:

```csharp
float num = random.NextFloat(1f);
if (num < p.x)                    ocTransferType = Road;
else if (num < p.x+p.y)           ocTransferType = Train;
else if (num < p.x+p.y+p.z)       ocTransferType = Air;
else if (num < p.x+p.y+p.z+p.w)   ocTransferType = Ship;

return GetRandomOutsideConnectionByTransferType(..., ocTransferType, out result);
// returns false when no OC of that type exists — no fallback, no retry
```

`TouristSpawnSystem.cs:107-114` — when that returns `false`, the household is **still
created**, just without `CurrentBuilding`:

```csharp
if (m_OutsideConnectionEntities.Length > 0 && BuildingUtils.GetRandomOutsideConnectionByParameters(...))
{
    m_CommandBuffer.AddComponent(e, new CurrentBuilding { m_CurrentBuilding = result });
}
// else: household exists, but has no location
```

`HouseholdInitializeSystem.cs:344` — the query that populates a household with citizens
**requires** `CurrentBuilding`:

```csharp
m_Additions = GetEntityQuery(
    ComponentType.ReadWrite<Household>(),
    ComponentType.ReadOnly<PrefabRef>(),
    ComponentType.ReadWrite<HouseholdCitizen>(),
    ComponentType.ReadOnly<CurrentBuilding>(),   // ← never matches
    ComponentType.ReadWrite<Resources>(),
    ComponentType.Exclude<Temp>());
```

So the household never receives citizens. It is picked up later by
`TouristFindTargetSystem` (whose query at `TouristFindTargetSystem.cs:278` requires neither
`CurrentBuilding` nor citizens), where the pathfinding origin resolves to `Entity.Null`
because the citizen buffer is empty (`TouristFindTargetSystem.cs:92-98`). The path fails,
and the household is disposed of via
`HouseholdMoveAway(..., MoveAwayReason.TouristNoTarget)`.

**Net effect on a city with no airport and no harbour: 80% of every tourist arrival attempt
is converted into a wasted pathfinding request instead of a tourist.** At 16,384 attempts
per day that is roughly 13,000 dead households per in-game day — a correctness bug and a
performance cost at the same time.

Even a city *with* an airport but no harbour throws away 30% of arrivals.

---

## Finding 4 — Length of stay was never implemented

`TouristHousehold.cs:10` declares `m_LeavingTime`. It is:

- written as `0u` in `TouristSpawnSystem.cs:105` and `HouseholdInitializeSystem.cs:235`
- serialized and deserialized (`TouristHousehold.cs:16,24`)
- **never read by any system**

`TourismSystem.cs:161-164` defines `GetTouristRandomStay()` returning `262144` (exactly one
day) — and **nothing calls it**. Both are unfinished feature stubs.

The only departure logic is `TouristLeaveSystem.cs:60-73`:

```csharp
MoveAwayReason reason =
    (!hasHotel && m_Time > 0.8f) ? MoveAwayReason.TouristNoHotel :
    (money < hotelPrice && m_Time > 0.7f) ? MoveAwayReason.TouristNoMoney :
    MoveAwayReason.None;
```

**Consequence:** a tourist who has not secured a hotel room is evicted every evening at
~80% through the day. Tourism cannot accumulate across days without hotel capacity, and
there is no concept of a multi-day visit, a day-tripper, or a stay length that varies with
how much there is to do.

---

## Finding 5 — The tourist count under-reports, and it feeds back

`CountHouseholdDataSystem.cs:479-482`

```csharp
if (flag && chunk.Has<Target>())
    value.m_TouristCitizenCount += length;
```

Tourists are only counted while they hold a `Target`. Any tourist between targets — which
is exactly the `LodgingSeeker` state every arrival passes through — is invisible.

That undercount is what `TouristSpawnSystem` feeds into `GetSpawnProbability` as
`currentTourists`, which keeps spawn probability pinned at 1.0 for longer than it should,
compounding the wasted spawns from Finding 3.

---

## Finding 6 — The UI number and the simulation disagree by ~8x

`TourismSystem.cs:108`

```csharp
value.m_AverageTourists = Mathf.RoundToInt(
    2f * GetTouristProbability(...) * 100000f / 16f);
```

That is `12500 × probability`. At probability 1.0 the panel reports **12,500 average
tourists** while `GetTargetTourists` will never let the city exceed **1,500**.

This is very likely the proximate reason tourism "doesn't seem to do anything": the game
displays a number roughly eight times larger than anything the simulation will produce, so
the feature reads as broken even in the cases where it is working as designed.

---

## Summary of root causes

| # | Root cause | Severity | Fixable without Harmony? |
|---|---|---|---|
| 3 | 80% of arrivals discarded when Air/Ship OCs are absent | **Critical** | **Yes** — `m_TouristOCSpawnParameters` is a writable singleton component |
| 2 | Target population capped at ~1,500 regardless of city size | **Critical** | Partly — requires replacing the spawn decision |
| 1 | Attractiveness saturates at 100, further building is inert | High | Partly — same path as #2 |
| 4 | No length-of-stay; nightly eviction without hotels | High | Yes — new system using the existing unused field |
| 6 | UI reports ~8x the achievable number | Medium | Yes — write `Tourism.m_AverageTourists` after the native system |
| 5 | Tourist count under-reports while seeking lodging | Low | Yes — recompute in our own system |

---

## Proposed intervention (smallest change that fixes each cause)

Ordered by value-per-risk. Nothing here reproduces native machinery — arrivals still use
real outside connections, real pathfinding, real hotels and the real economy.

### A. Adaptive outside-connection routing — fixes #3

Read the outside connections that actually exist in the city, then rewrite the
`DemandParameterData.m_TouristOCSpawnParameters` singleton so the probability mass is
distributed only across transfer types that are present, renormalised to sum to 1.

This is a plain component write on a prefab singleton — no patching, no reflection. It
preserves the designers' intent (air and sea are the *preferred* tourist modes) while
removing the failure case. A city with an airport and a harbour keeps the 50/30 split; a
city with neither routes tourists over road and rail instead of throwing them away.

Directly serves the goal of tourists arriving by plane and ferry: once an airport or
harbour exists, that connection immediately carries its intended majority share, and the
arrivals are real agents using real vehicles.

### B. Population-scaled tourist target — fixes #2 and #1

Disable `TouristSpawnSystem` and run a replacement that keeps the native spawn mechanics
(same archetype, same `Household` flags, same OC selection, same downstream systems) but
computes the target as a function of both attractiveness **and** city population, so a
large attractive city can support a proportionate tourist population instead of a flat
1,500. Configurable multiplier, defaulting to something conservative.

Also lets the spawn decision emit more than one household per update, which the native
one-Bernoulli-trial-per-update design cannot.

### C. Real length of stay — fixes #4

Populate the existing, already-serialized `m_LeavingTime` on arrival with a stay length
derived from city attractiveness and lodging availability, and depart when it elapses.
Soften the nightly no-hotel eviction into a day-tripper path so a city without hotels still
gets same-day visitors rather than a hard reset every evening.

### D. Honest reporting — fixes #6 and #5

Recompute `Tourism.m_AverageTourists` and the tourist count from actual tourist households
after the native systems run, so the info panel reflects the simulation.

---

## Open design questions

1. Should B scale with population, with attractiveness, or with both multiplicatively?
2. Should A be strictly renormalising (preserve intent), or should it let you bias the
   split yourself from a settings panel?
3. Does C need a UI surface, or is a settings slider enough?
