import { ModRegistrar } from "cs2/modding";
import { logInfoviewModules } from "mods/find-tourism-panel";
import { wrapTourismPanel } from "mods/tourism-panel";

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
};

export default register;
