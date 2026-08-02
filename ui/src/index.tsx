import { ModRegistrar } from "cs2/modding";
import { bindValue, useValue } from "cs2/api";
import { logInfoviewModules } from "mods/find-tourism-panel";
import { wrapTourismPanel, TourismFinancePanel } from "mods/tourism-panel";

// Zone toolbar icons. These are not rendered by this module — the C# side puts the URL on the zone
// prefab's UIObject and the game's toolbar loads it. They are imported here purely so webpack's
// asset/resource rule emits them into images/, which is what makes coui://ui-mods/images/... resolve.
// The console line below is what keeps TypeScript from eliding these as unused imports, and it
// doubles as a way to confirm the exact URL at runtime if an icon ever fails to appear.
import hotelsIcon from "images/tourism-overhaul-hotels.svg";
import motelsIcon from "images/tourism-overhaul-motels.svg";
import financeIcon from "images/tourism-finance.svg";

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

const register: ModRegistrar = (moduleRegistry) => {
  console.log(
    `[TourismOverhaul] Icons emitted at ${hotelsIcon}, ${motelsIcon} and ${financeIcon}.`
  );

  // Note: moduleRegistry.get throws when the module is absent, so existence is checked with
  // find, which returns an empty list instead.
  const matches = moduleRegistry.find(TOURISM_PANEL_PATH) ?? [];

  const found = matches.some(
    ([path, ...exports]) => path === TOURISM_PANEL_PATH && exports.includes(TOURISM_PANEL_EXPORT)
  );

  if (!found) {
    console.error(
      `[TourismOverhaul] Tourism panel not found at "${TOURISM_PANEL_PATH}" ` +
        `export "${TOURISM_PANEL_EXPORT}". It has probably moved in a game update — ` +
        `pick the correct path from the list below and update src/index.tsx.`
    );
    logInfoviewModules(moduleRegistry);
    return;
  }

  moduleRegistry.extend(TOURISM_PANEL_PATH, TOURISM_PANEL_EXPORT, wrapTourismPanel);
  console.log("[TourismOverhaul] Tourism panel extended.");

  registerFinancePanel(moduleRegistry);
};

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
