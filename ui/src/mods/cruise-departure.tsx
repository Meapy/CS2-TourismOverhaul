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

export const VEHICLE_PANEL_PATH =
  "game-ui/game/components/selected-info-panel/selected-info-sections/vehicle-sections/public-transport-vehicle-section.tsx";

export const VEHICLE_PANEL_EXPORT = "PublicTransportVehicleSection";

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
    className="infoRow_QQ9 left_Cmg"
    style={{ display: "flex", justifyContent: "space-between" }}
  >
    <div className="left_Cmg">{left}</div>
    <div className="right_ZUb">{right}</div>
  </div>
);

const Row: any = ui?.InfoRow ?? ui?.PanelSectionRow ?? FallbackRow;

const useRowText = () => {
  const { translate } = useLocalization();
  return (key: string, english: string) =>
    translate(`TourismOverhaul.PANEL[${key}]`, english) ?? english;
};

const CruiseDepartureRow = () => {
  const docked = useValue(cruiseDocked$);
  const departure = useValue(cruiseDeparture$);
  const minutesLeft = useValue(cruiseMinutesLeft$);
  const ashore = useValue(cruiseAshore$);
  const text = useRowText();

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
export const wrapVehiclePanel = (Native: any) => (props: any) => {
  try {
    return (
      <>
        <Native {...props} />
        <CruiseDepartureRow />
      </>
    );
  } catch (error) {
    console.warn(`${LOG_PREFIX} cruise departure row failed to render:`, error);
    return <Native {...props} />;
  }
};
