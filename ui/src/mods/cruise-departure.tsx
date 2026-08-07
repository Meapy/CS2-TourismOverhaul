import { ReactNode } from "react";
import { bindValue, useValue } from "cs2/api";
import { useLocalization } from "cs2/l10n";
import * as CS2UI from "cs2/ui";

/**
 * Adds a sailing time to the native public transport vehicle panel, for a docked cruise ship.
 *
 * The panel shows a vehicle's state as "Boarding" and nothing more, which for a cruise ship is
 * close to useless — it is boarding for twelve in-game hours and there is no way to tell whether
 * that is nearly over or barely begun.
 *
 * Values come from CruiseDepartureUISystem, which watches the selected entity and publishes only
 * when it is a ship with an open CruiseCall. So the row appears exactly while a cruise ship is in
 * port and is absent for every other vehicle, without this component knowing anything about what
 * is selected.
 *
 * The module path below was found with ModuleRegistry.find and pinned from UI.log — the interface
 * ships packed inside .cok archives, so it cannot be read from disk. Re-check it after a game
 * update; the guard in index.tsx turns a moved panel into a log line rather than a missing row.
 */

const LOG_PREFIX = "[TourismOverhaul]";

/**
 * The selected-info panel's content area, not the vehicle section.
 *
 * Wrapping the section itself registered cleanly and was never invoked — not once, no render line at
 * all. The reason is in the module dump: `selected-info-bindings.ts` exports `topSections`,
 * `middleSections`, `bottomSections` and a `SectionType` enum, so the panel builds its sections from
 * *data* published by C# and resolves each one's group to a component internally. Extending the
 * section's export therefore changes a module the panel never reads from.
 *
 * PanelSpace is the content container, and the same shape already works elsewhere in this mod:
 * InfoviewPanelSpace is what the Tourism Finance view appends into, for exactly the same reason.
 * Wrapping the frame instead would render our row outside the window, floating over the map — the
 * mistake the notes record for CityInfoDemand.
 */
export const VEHICLE_PANEL_PATH =
  "game-ui/game/components/selected-info-panel/shared-components/info-section/info-section.tsx";

export const VEHICLE_PANEL_EXPORT = "InfoSection";

const cruiseDocked$ = bindValue<boolean>("tourismOverhaul", "cruiseDocked", false);
const cruiseDeparture$ = bindValue<string>("tourismOverhaul", "cruiseDeparture", "");
const cruiseMinutesLeft$ = bindValue<number>("tourismOverhaul", "cruiseMinutesLeft", 0);
const cruiseAshore$ = bindValue<number>("tourismOverhaul", "cruiseAshore", 0);

/**
 * cs2/ui is resolved at runtime as window["cs2/ui"] and its surface does not necessarily match the
 * type definitions, so resolve by preference and fall back to plain markup. Importing a name that
 * is undefined yields React's minified error #130, which says nothing useful.
 */
const ui = CS2UI as any;

const FallbackRow = ({ left, right }: { left?: ReactNode; right?: ReactNode }) => (
  <div
    style={{
      display: "flex",
      justifyContent: "space-between",
      alignItems: "center",
      padding: "4rem 12rem",
    }}
  >
    <div>{left}</div>
    <div>{right}</div>
  </div>
);

/**
 * Plain markup, deliberately, rather than cs2/ui's InfoRow.
 *
 * InfoRow is a native component whose prop shape is not documented and cannot be read from disk, so
 * handing it {left, right, subRow} is a guess — and a component that receives props it does not
 * understand renders nothing rather than complaining. That is indistinguishable from the row not
 * being there, which is exactly the symptom this has had.
 *
 * The fallback is styled with inline flex rather than the game's class names for the same reason:
 * the class map values are multi-class hashed strings that change between builds, and a stale one
 * silently styles nothing. Plain markup is uglier and it appears.
 */
const Row: any = FallbackRow;

const useRowText = () => {
  const { translate } = useLocalization();
  return (key: string, english: string) =>
    translate(`TourismOverhaul.PANEL[${key}]`, english) ?? english;
};

/** Last reported value signature, so the log is written on change rather than every render. */
let loggedRender = "";

const CruiseDepartureRow = () => {
  const docked = useValue(cruiseDocked$);
  const departure = useValue(cruiseDeparture$);
  const minutesLeft = useValue(cruiseMinutesLeft$);
  const ashore = useValue(cruiseAshore$);
  const text = useRowText();

  // Logged whenever the values change, not once.
  //
  // Once was not enough: the first render happens the first time any selection panel opens, which
  // is almost never a docked cruise ship, so it reported docked=false and said nothing about the
  // case in question. Keyed on the values themselves, this stays quiet while nothing moves and
  // speaks exactly when a selection changes what the row is handed.
  const signature = `${docked}|${departure}|${ashore}`;

  if (signature !== loggedRender) {
    loggedRender = signature;
    console.log(
      `${LOG_PREFIX} cruise row: docked=${docked}, departure="${departure}", ashore=${ashore}.`
    );
  }

  if (!docked || !departure) {
    return null;
  }

  // Hours read better than a four-digit minute count. At normal speed an in-game day is over an
  // hour of real play, so "720 minutes" is true and unhelpful.
  const hoursLeft = Math.max(0, Math.round(minutesLeft / 60));

  return (
    <>
      {/* The native PASSENGERS bar counts the ship's own buffer, which is zero while everyone is
          out in the city — so it reads as an empty vessel exactly when the interesting number is
          how many are still ashore and due back. */}
      <Row
        left={text("CruiseAshore", "Passengers ashore")}
        right={ashore.toLocaleString()}
        uppercase={false}
        subRow={true}
      />
      <Row
        left={text("CruiseDeparture", "Departs")}
        right={`${departure}  (${hoursLeft}h)`}
        uppercase={false}
        subRow={true}
      />
    </>
  );
};

/**
 * Renders the native section unchanged and appends the row when a cruise ship is selected.
 *
 * Wrapping rather than replacing, so every other public transport vehicle is untouched and a
 * future change to the native section carries through.
 */
/** Stable marker, so a second registration cannot nest the component inside itself. */
const WRAPPED = Symbol.for("TourismOverhaul.infoSectionWrapped");

/**
 * True while a render pass has already placed our row, so only one section carries it.
 *
 * InfoSection is the shared component every section in the panel is built from, which is why it is
 * the wrap target — it is certainly rendered, where the vehicle section's own module and the panel's
 * content area both turned out not to be. The cost of that certainty is that it renders many times
 * per panel, so appending unconditionally would repeat the row once per section.
 *
 * React renders a panel's children synchronously, so a flag set during the first section and cleared
 * on the next microtask covers exactly one pass. It cannot leak between panels: the clear is queued
 * before anything else can run.
 */
let placedThisPass = false;

const claimPass = (): boolean => {
  if (placedThisPass) {
    return false;
  }

  placedThisPass = true;
  Promise.resolve().then(() => {
    placedThisPass = false;
  });

  return true;
};

/** Logged once, so the props InfoSection receives can be read rather than guessed at. */
let loggedSectionProps = false;

export const wrapVehiclePanel = (Native: any) => {
  if (Native == null || Native[WRAPPED]) {
    return Native;
  }

  const Wrapped = (props: any) => {
    try {
      if (!loggedSectionProps) {
        loggedSectionProps = true;
        console.log(
          `${LOG_PREFIX} InfoSection props: [${Object.keys(props ?? {}).join(", ")}].`
        );
      }

      return (
        <>
          <Native {...props} />
          {claimPass() ? <CruiseDepartureRow /> : null}
        </>
      );
    } catch (error) {
      console.warn(`${LOG_PREFIX} cruise departure row failed to render:`, error);
      return <Native {...props} />;
    }
  };

  (Wrapped as any)[WRAPPED] = true;

  return Wrapped;
};
