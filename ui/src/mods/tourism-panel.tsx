import { Component, ReactNode } from "react";
import { bindValue, useValue } from "cs2/api";
import * as CS2UI from "cs2/ui";

/**
 * Adds a "Local cims away" row to the native Tourism info view panel.
 *
 * The value comes from TourismPanelUISystem on the C# side, which reads
 * ResidentTravelSystem.CitizensAway — citizens whose TravelPurpose.m_Purpose is
 * Purpose.Traveling, i.e. residents currently out of town on holiday.
 *
 * Bindings live in their own "tourismOverhaul" group so nothing collides with the native
 * "tourismInfo" group the panel already uses.
 */

const LOG_PREFIX = "[TourismOverhaul]";

const citizensAway$ = bindValue<number>("tourismOverhaul", "citizensAway", 0);
const currentTourists$ = bindValue<number>("tourismOverhaul", "currentTourists", 0);
const targetTourists$ = bindValue<number>("tourismOverhaul", "targetTourists", 0);
const hotelRoomsFree$ = bindValue<number>("tourismOverhaul", "hotelRoomsFree", 0);
const hotelRoomsTotal$ = bindValue<number>("tourismOverhaul", "hotelRoomsTotal", 0);

const arrivalsRoad$ = bindValue<number>("tourismOverhaul", "arrivalsRoad", 0);
const arrivalsTrain$ = bindValue<number>("tourismOverhaul", "arrivalsTrain", 0);
const arrivalsAir$ = bindValue<number>("tourismOverhaul", "arrivalsAir", 0);
const arrivalsShip$ = bindValue<number>("tourismOverhaul", "arrivalsShip", 0);

const spentLodging$ = bindValue<number>("tourismOverhaul", "spentLodging", 0);
const spentGoods$ = bindValue<number>("tourismOverhaul", "spentGoods", 0);
const spentFares$ = bindValue<number>("tourismOverhaul", "spentFares", 0);
const spentLeisure$ = bindValue<number>("tourismOverhaul", "spentLeisure", 0);
const spentOther$ = bindValue<number>("tourismOverhaul", "spentOther", 0);

/**
 * cs2/ui is resolved at runtime as window["cs2/ui"], and its surface does not necessarily match
 * the type definitions — ui.d.ts re-exports InfoRow/InfoSection as PanelSectionRow/PanelSection,
 * and the runtime may only carry one set of names. Importing the missing name yields undefined,
 * which React reports as the very unhelpful minified error #130.
 *
 * So resolve by preference and fall back to plain markup rather than trusting either name.
 */
const ui = CS2UI as any;

const FallbackSection = ({ children }: { children?: ReactNode }) => (
  <div className="infoSection_RXJ item_hyi">{children}</div>
);

const FallbackRow = ({ left, right }: { left?: ReactNode; right?: ReactNode }) => (
  <div className="infoRow_QQ9 left_Cmg" style={{ display: "flex", justifyContent: "space-between" }}>
    <div className="left_Cmg">{left}</div>
    <div className="right_ZUb">{right}</div>
  </div>
);

const Section: any = ui?.InfoSection ?? ui?.PanelSection ?? FallbackSection;
const Row: any = ui?.InfoRow ?? ui?.PanelSectionRow ?? FallbackRow;

if (!ui?.InfoSection && !ui?.PanelSection) {
  console.warn(`${LOG_PREFIX} cs2/ui section component not found; using fallback markup.`);
}
if (!ui?.InfoRow && !ui?.PanelSectionRow) {
  console.warn(`${LOG_PREFIX} cs2/ui row component not found; using fallback markup.`);
}

/**
 * Contains any failure inside our rows.
 *
 * Without this, anything that throws while rendering — an undefined component, a binding that is
 * not registered because the managed DLL is out of step with this bundle — propagates up and
 * unmounts the entire game UI, leaving the player with a bare map and no way out.
 * A missing row is an acceptable failure; a dead UI is not.
 */
class RowErrorBoundary extends Component<{ children?: ReactNode }, { failed: boolean }> {
  constructor(props: { children?: ReactNode }) {
    super(props);
    this.state = { failed: false };
  }

  static getDerivedStateFromError() {
    return { failed: true };
  }

  componentDidCatch(error: any) {
    console.error(`${LOG_PREFIX} row rendering failed; rows hidden.`, error);
  }

  render() {
    return this.state.failed ? null : this.props.children;
  }
}

/** Rows this mod contributes, rendered inside the native panel. */
export const TourismOverhaulRows = () => {
  const citizensAway = useValue(citizensAway$);
  const currentTourists = useValue(currentTourists$);
  const targetTourists = useValue(targetTourists$);
  const hotelRoomsFree = useValue(hotelRoomsFree$);
  const hotelRoomsTotal = useValue(hotelRoomsTotal$);

  const byRoad = useValue(arrivalsRoad$);
  const byTrain = useValue(arrivalsTrain$);
  const byAir = useValue(arrivalsAir$);
  const bySea = useValue(arrivalsShip$);

  const occupancy =
    hotelRoomsTotal > 0
      ? Math.round(((hotelRoomsTotal - hotelRoomsFree) / hotelRoomsTotal) * 100)
      : 0;

  // Share of the month's arrivals, so the split is readable without dividing four numbers by eye.
  const arrivalsTotal = byRoad + byTrain + byAir + bySea;
  const share = (n: number) =>
    arrivalsTotal > 0 ? ` (${Math.round((n / arrivalsTotal) * 100)}%)` : "";


  return (
    <>
      <Row
        left="Tourists in city"
        right={`${currentTourists.toLocaleString()} / ${targetTourists.toLocaleString()}`}
        tooltip="Tourists currently in the city, against the number it can sustain at this
                 attractiveness and population."
      />
      <Row
        left="Hotel rooms free"
        right={`${hotelRoomsFree.toLocaleString()} / ${hotelRoomsTotal.toLocaleString()}`}
        subRow
        tooltip="Free rooms against total rooms. When this reaches zero, arrivals with no room
                 leave the same evening."
      />
      <Row
        left="Occupancy"
        right={`${occupancy}%`}
        subRow
        subRowDimmed
        tooltip="How full your hotels are. Sustained high occupancy means lodging is the limit on
                 tourist numbers."
      />
      <Row
        left="Local cims away"
        right={citizensAway.toLocaleString()}
        tooltip="Your own residents currently out of town on holiday."
      />
      <Row
        left="Arrivals by mode"
        right={`${arrivalsTotal.toLocaleString()} /mo.`}
        tooltip="Visitors sent through each outside connection type over the last full month,
                 counted in cims rather than travel parties. This is a flow, not a population: it
                 counts everyone who came through the door across a whole month, where Tourists in
                 city counts who is here right now. The two differ by the average length of stay."
      />
      <Row left="Road" right={`${byRoad.toLocaleString()}${share(byRoad)}`} subRow subRowDimmed />
      <Row left="Train" right={`${byTrain.toLocaleString()}${share(byTrain)}`} subRow subRowDimmed />
      <Row left="Plane" right={`${byAir.toLocaleString()}${share(byAir)}`} subRow subRowDimmed />
      <Row left="Sea" right={`${bySea.toLocaleString()}${share(bySea)}`} subRow subRowDimmed />
    </>
  );
};

/**
 * The Tourism Finance view's own rows: where visitor money went, over the last full month.
 *
 * Kept apart from the tourism rows because they belong to a different view. The values come from
 * TouristSpendingLedgerSystem, which samples wallets and attributes each fall to whatever the
 * household was doing at the time.
 */
export const TourismFinanceRows = () => {
  const onLodging = useValue(spentLodging$);
  const onGoods = useValue(spentGoods$);
  const onFares = useValue(spentFares$);
  const onLeisure = useValue(spentLeisure$);
  const onOther = useValue(spentOther$);

  const total = onLodging + onGoods + onFares + onLeisure + onOther;
  const money = (n: number) => `¢${n.toLocaleString()}`;
  const share = (n: number) =>
    total > 0 ? ` (${Math.round((n / total) * 100)}%)` : "";

  return (
    <>
      <Row
        left="Tourist spending"
        right={`${money(total)} /mo.`}
        tooltip="What visitors spent in your city over the last full month, and on what.
                 Totals are exact — every coin that leaves a wallet is counted — but the split
                 between categories is inferred from what each visitor was doing at the time, so
                 read it as a strong indication rather than an audit."
      />
      <Row left="Hotels" right={`${money(onLodging)}${share(onLodging)}`} subRow subRowDimmed />
      <Row left="Shops" right={`${money(onGoods)}${share(onGoods)}`} subRow subRowDimmed />
      <Row left="Fares" right={`${money(onFares)}${share(onFares)}`} subRow subRowDimmed />
      <Row left="Leisure" right={`${money(onLeisure)}${share(onLeisure)}`} subRow subRowDimmed />
      <Row
        left="Unattributed"
        right={`${money(onOther)}${share(onOther)}`}
        subRow
        subRowDimmed
        tooltip="Money that left visitor wallets by a route this mod does not recognise. A small
                 figure is normal; a large one means there is a spending path still to find."
      />
    </>
  );
};

/**
 * The panel the game shows when the Tourism Finance view is selected.
 *
 * Self-contained rather than wrapping anything, because this view is ours and has no native panel
 * to extend.
 */
export const TourismFinancePanel = () => (
  <RowErrorBoundary>
    <Section>
      <TourismFinanceRows />
    </Section>
  </RowErrorBoundary>
);

/**
 * Wraps the native panel component, rendering it unchanged and appending our rows.
 *
 * Idempotent via a stable global marker: if the wrapper is registered twice (two copies of the
 * mod, or a hot reload) the second call returns the existing wrapper rather than nesting the
 * panel inside itself.
 */
const WRAPPED = Symbol.for("TourismOverhaul.tourismPanelWrapped");

export function wrapTourismPanel(NativePanel: any): any {
  if (NativePanel == null || NativePanel[WRAPPED]) {
    return NativePanel;
  }

  const Wrapped = (props: any) => (
    <>
      <NativePanel {...props} />
      <RowErrorBoundary>
        <Section>
          <TourismOverhaulRows />
        </Section>
      </RowErrorBoundary>
    </>
  );

  (Wrapped as any)[WRAPPED] = true;

  return Wrapped;
}
