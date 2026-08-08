import { ModRegistrar } from "cs2/modding";
import { bindValue, useValue } from "cs2/api";
import {
  logInfoviewModules,
  logSelectedInfoRegistry,
  logSelectedVehicleModules,
} from "mods/find-tourism-panel";
import {
  wrapVehiclePanel,
  VEHICLE_PANEL_PATH,
  VEHICLE_PANEL_EXPORT,
} from "mods/cruise-departure";
import { wrapTourismPanel, TourismFinancePanel } from "mods/tourism-panel";
import {
  wrapDemandSection,
  wrapDemandToolbar,
  wrapDemandFactorDetail,
} from "mods/tourist-demand";

// Zone toolbar icons. These are not rendered by this module — the C# side puts the URL on the zone
// prefab's UIObject and the game's toolbar loads it. They are imported here purely so webpack's
// asset/resource rule emits them into images/, which is what makes coui://ui-mods/images/... resolve.
// The console line below is what keeps TypeScript from eliding these as unused imports, and it
// doubles as a way to confirm the exact URL at runtime if an icon ever fails to appear.
import hotelsIcon from "images/tourism-overhaul-hotels.svg";
import motelsIcon from "images/tourism-overhaul-motels.svg";
import financeIcon from "images/tourism-finance.svg";
import cruiseLineIcon from "images/tourism-overhaul-cruise-line.svg";

/**
 * Module path and export name of the native Tourism info view panel.
 *
 * Confirmed at runtime via ModuleRegistry.find(/tourism/i) — the game's UI bundle ships packed
 * inside .cok archives, so this cannot be read from disk. Re-check after a game update; the
 * guard below turns a moved panel into a log line instead of a missing row.
 */
const TOURISM_PANEL_PATH =
  "game-ui/game/components/infoviews/active-infoview-panel/panels/tourism-infoview-panel.tsx";
const TOURISM_PANEL_EXPORT = "TourismInfoviewPanel";

/**
 * Appends the cruise sailing time to the public transport vehicle panel.
 *
 * Path and export were discovered with ModuleRegistry.find and pinned from UI.log. find rather than
 * get, because get throws when a module is absent and a moved panel should cost a log line rather
 * than the whole interface.
 */
function registerCruiseDeparture(moduleRegistry: any): void {
  const matches = moduleRegistry.find(VEHICLE_PANEL_PATH) ?? [];

  const found = matches.some(
    ([path, ...exports]: [string, ...string[]]) =>
      path === VEHICLE_PANEL_PATH && exports.includes(VEHICLE_PANEL_EXPORT)
  );

  if (!found) {
    console.warn(
      `[TourismOverhaul] "${VEHICLE_PANEL_EXPORT}" not found at "${VEHICLE_PANEL_PATH}". ` +
        `Cruise ships will not show a sailing time. Pick the correct path from the list below.`
    );
    logSelectedVehicleModules(moduleRegistry);
    return;
  }

  moduleRegistry.extend(VEHICLE_PANEL_PATH, VEHICLE_PANEL_EXPORT, wrapVehiclePanel);
  console.log("[TourismOverhaul] Cruise departure row registered.");

  // Wrapping InfoSection, the shared component every section in the panel is built from.
  //
  // Two better-looking targets registered cleanly and were never invoked: the vehicle section's own
  // module, and the panel's content area. The dump below explains both — selected-info-bindings.ts
  // exports topSections, middleSections and bottomSections, so the panel assembles its sections from
  // data C# publishes and resolves each group to a component internally, never reading the exports
  // we were extending.
  //
  // InfoSection is different in the one way that matters: it is a shared component, so whatever
  // renders a section renders it. If a wrapper here is not invoked either, nothing in this panel is
  // reachable from a mod and the row belongs on another surface.
  //
  // Registered and never rendered, so dump the neighbourhood.
  //
  // The wrapper installs cleanly and its component is never invoked — no render line reaches the
  // log at all. That rules out the bindings and the markup and leaves one explanation: whatever
  // draws the selected-info sections does not obtain them from this module's export. The C# side
  // gives each section a `group` string (InfoSectionBase.group, "PublicTransportVehicleSection"
  // here), so the panel most likely resolves group → component through a registry, and extending
  // the export changes something nothing renders.
  //
  // The registry cannot be read from disk — the interface ships packed in .cok archives — so it has
  // to be found by listing what is there. This prints every selected-info module and its exports;
  // the one to pin is whichever exports a map or a lookup rather than a section component.
  logSelectedInfoRegistry(moduleRegistry);
}

const register: ModRegistrar = (moduleRegistry) => {
  console.log(
    `[TourismOverhaul] Icons emitted at ${hotelsIcon}, ${motelsIcon}, ${financeIcon} ` +
      `and ${cruiseLineIcon}.`
  );

  // Note: moduleRegistry.get throws when the module is absent, so existence is checked with
  // find, which returns an empty list instead.
  // Every surface registers independently, and a miss on one must not skip the rest.
  //
  // This used to return outright when the Tourism panel was not found, and the three registrations
  // below it — the cruise departure row, the finance panel, tourist demand — are all after that
  // point. So one moved path took out four features and only complained about one of them. The
  // symptom is the worst kind: a row that never appears, with a log line about something else
  // entirely.
  const matches = moduleRegistry.find(TOURISM_PANEL_PATH) ?? [];

  const found = matches.some(
    ([path, ...exports]) => path === TOURISM_PANEL_PATH && exports.includes(TOURISM_PANEL_EXPORT)
  );

  if (found) {
    moduleRegistry.extend(TOURISM_PANEL_PATH, TOURISM_PANEL_EXPORT, wrapTourismPanel);
    console.log("[TourismOverhaul] Tourism panel extended.");
  } else {
    console.error(
      `[TourismOverhaul] Tourism panel not found at "${TOURISM_PANEL_PATH}" ` +
        `export "${TOURISM_PANEL_EXPORT}". It has probably moved in a game update — ` +
        `pick the correct path from the list below and update src/index.tsx.`
    );
    logInfoviewModules(moduleRegistry);
  }

  registerCruiseDeparture(moduleRegistry);

  registerFinancePanel(moduleRegistry);
  registerDemand(moduleRegistry);
};

/**
 * The two places the game draws demand: the Demand info view page with its arrow bars and factor
 * lists, and the compact pill stack in the toolbar.
 *
 * Neither path can be read from disk, so both are pinned guesses with a guard. On a miss the mod
 * logs every module matching /demand/i and skips that surface, leaving the native UI untouched —
 * the same approach that pinned the Tourism panel.
 */
// The section, not the page. CityInfoDemand is the scroll container: wrapping it rendered our
// section outside the panel, full-bleed across the toolbar. DemandSection renders inside the list,
// so ours goes after the last of them instead.
const DEMAND_PAGE_PATH =
  "game-ui/game/components/city-info-panel/city-info-demand/demand-section/demand-section.tsx";
const DEMAND_PAGE_EXPORT = "DemandSection";

const DEMAND_TOOLBAR_PATH =
  "game-ui/game/components/toolbar/top/city-info-field/demand-bars.tsx";
const DEMAND_TOOLBAR_EXPORT = "DemandBars";

function registerDemand(moduleRegistry: any): void {
  const page = tryExtend(
    moduleRegistry,
    DEMAND_PAGE_PATH,
    DEMAND_PAGE_EXPORT,
    wrapDemandSection,
    "Demand section"
  );

  const toolbar = tryExtend(
    moduleRegistry,
    DEMAND_TOOLBAR_PATH,
    DEMAND_TOOLBAR_EXPORT,
    wrapDemandToolbar,
    "toolbar demand stack"
  );

  // Diagnostic only: reports what the native factor row is handed, which is what will say whether
  // our labels are missing because of a wrong key or a wrong shape.
  tryExtend(
    moduleRegistry,
    "game-ui/game/components/city-info-panel/city-info-demand/demand-section/demand-factors.tsx",
    "DemandFactorDetail",
    wrapDemandFactorDetail,
    "Demand factor row"
  );

  // The right-hand detail pane — the one that describes the hovered section — is not in the
  // /demand/i set, so it lives under a different name. Listed here so it can be pinned rather
  // than guessed at, which is what cost four rounds on the locale keys.
  const paneCandidates = moduleRegistry.find(/city-info/i) ?? [];

  console.log(
    `[TourismOverhaul] ${paneCandidates.length} module(s) matching /city-info/i:`
  );

  for (const [path, ...exports] of paneCandidates) {
    console.log(`[TourismOverhaul]   ${path} → ${exports.join(", ")}`);
  }

  if (!page || !toolbar) {
    logDemandModules(moduleRegistry);
  }
}

/**
 * Extends a native component, and says so clearly if the target is not there.
 *
 * extend rather than append: append relies on the target rendering props.children. Both of these
 * registered cleanly through append and neither rendered, which is what that failure looks like.
 * Wrapping does not depend on the native component cooperating.
 *
 * Returns whether it happened, so the caller can dump candidates once rather than once per miss.
 */
function tryExtend(
  moduleRegistry: any,
  path: string,
  exportName: string,
  wrapper: (native: any) => any,
  label: string
): boolean {
  // find, not get: get throws when the module is absent rather than returning nothing.
  const matches = moduleRegistry.find(path) ?? [];

  const found = matches.some(
    ([p, ...exports]: [string, ...string[]]) => p === path && exports.includes(exportName)
  );

  if (!found) {
    console.warn(
      `[TourismOverhaul] ${label} not found at "${path}" export "${exportName}". ` +
        `Tourist demand will not appear there.`
    );
    return false;
  }

  moduleRegistry.extend(path, exportName, wrapper);
  console.log(`[TourismOverhaul] ${label} extended with tourist demand.`);
  return true;
}

/** Lists every module whose path mentions demand, so a moved one can be pinned rather than guessed. */
function logDemandModules(moduleRegistry: any): void {
  const candidates = moduleRegistry.find(/demand/i) ?? [];

  console.log(
    `[TourismOverhaul] ${candidates.length} module(s) matching /demand/i — pin the right ` +
      `path and export in index.tsx:`
  );

  for (const [path, ...exports] of candidates) {
    console.log(`[TourismOverhaul]   ${path} → ${exports.join(", ")}`);
  }
}

/**
 * The active info view panel picks a component from a registry keyed by info view. Our view is new,
 * so nothing is registered against it and the panel would come up empty.
 *
 * The registry's module path cannot be read from disk — the game's UI ships packed inside .cok
 * archives — so it is discovered the same way the Tourism panel was: try the expected path, and if
 * it has moved, log every infoview module so the correct one can be pinned here.
 */
// The panel body, not the window frame.
//
// ActiveInfoviewPanel is the frame: title bar, close button, and a slot. Appending to it put our
// rows outside the window, floating over the map beside it. InfoviewPanelSpace is the content area
// every view's panel renders into, so appending there lands inside the box.
const INFOVIEW_PANELS_PATH =
  "game-ui/game/components/infoviews/active-infoview-panel/components/infoview-panel-space.tsx";
const INFOVIEW_PANELS_EXPORT = "InfoviewPanelSpace";

/** Must match InfoviewPrefab.name in TourismFinanceViewSystem. */
const FINANCE_VIEW = "TourismOverhaul Finance";

/** Stable marker, so a second registration does not nest the panel inside itself. */
const FINANCE_WRAPPED = Symbol.for("TourismOverhaul.activeInfoviewPanelWrapped");

/**
 * Whether our view is the one showing, published by TourismPanelUISystem.
 *
 * Read from C# rather than from the game's own UI state. The panel component gets only
 * [closeHint, focusKey, onClose] and resolves the active view internally, and two attempts to reach
 * that binding through the module registry failed — first on the call signature, then with
 * "activeInfoview is not defined". ToolSystem.activeInfoview knows the answer on the managed side,
 * so it is simply asked and the result published like any other value.
 */
const financeViewActive$ = bindValue<boolean>("tourismOverhaul", "financeViewActive", false);

function registerFinancePanel(moduleRegistry: any): void {
  const matches = moduleRegistry.find(INFOVIEW_PANELS_PATH) ?? [];

  const found = matches.some(
    ([path, ...exports]: [string, ...string[]]) =>
      path === INFOVIEW_PANELS_PATH && exports.includes(INFOVIEW_PANELS_EXPORT)
  );

  if (!found) {
    console.warn(
      `[TourismOverhaul] "${INFOVIEW_PANELS_EXPORT}" not found at "${INFOVIEW_PANELS_PATH}". ` +
        `The Tourism Finance view will show an empty panel. Pick the correct path and export ` +
        `from the list below and update index.tsx.`
    );
    logInfoviewModules(moduleRegistry);
    return;
  }

  // There is no registry to add to — the module exports only the panel component, so the mapping
  // from info view to panel is internal to it. Wrapping is therefore the only way in: render the
  // native panel unchanged, and append ours when our view is the active one.
  moduleRegistry.extend(INFOVIEW_PANELS_PATH, INFOVIEW_PANELS_EXPORT, wrapActiveInfoviewPanel);
}

/** Set once we have logged the panel's props, so the log is not spammed every render. */
let loggedPanelProps = false;

/**
 * Renders the native active-infoview panel, plus our finance rows when our view is showing.
 *
 * Which view is active has to be read from the props the game passes down, and their shape is not
 * documented — the UI ships packed, so it cannot be read from disk. Several plausible shapes are
 * tried and the prop keys are logged once, so if none matches the correct one can be pinned rather
 * than guessed at again.
 */
function wrapActiveInfoviewPanel(NativePanel: any): any {
  if (NativePanel == null || NativePanel[FINANCE_WRAPPED]) {
    return NativePanel;
  }

  const Wrapped = (props: any) => {
    const isFinanceView = useValue(financeViewActive$);

    if (!loggedPanelProps) {
      loggedPanelProps = true;
      console.log(`[TourismOverhaul] finance view active: ${isFinanceView}.`);
    }

    return (
      <>
        <NativePanel {...props} />
        {isFinanceView ? <TourismFinancePanel /> : null}
      </>
    );
  };

  (Wrapped as any)[FINANCE_WRAPPED] = true;

  console.log(`[TourismOverhaul] active-infoview panel wrapped for "${FINANCE_VIEW}".`);

  return Wrapped;
}

export default register;
