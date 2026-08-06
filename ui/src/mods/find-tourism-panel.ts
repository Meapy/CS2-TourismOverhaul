import { ModuleRegistry } from "cs2/modding";

/**
 * The game's UI bundle ships packed inside .cok archives, so the module path and export name of
 * the Tourism info view panel cannot be read from disk. ModuleRegistry.find lets us discover them
 * at runtime and log them, so the correct values can be pinned in index.tsx.
 *
 * Launch the game once with this module installed and check UI.log for "[TourismOverhaul]".
 */

const LOG_PREFIX = "[TourismOverhaul]";

/** Logs every registry entry whose path mentions tourism, with its exports. */
export function logTourismModules(moduleRegistry: ModuleRegistry): void {
  try {
    const matches = moduleRegistry.find(/tourism/i);

    if (!matches || matches.length === 0) {
      console.warn(
        `${LOG_PREFIX} no module path matching /tourism/i was found. ` +
          `The panel may be named differently on this game version — ` +
          `try searching for /infoview/i instead.`
      );
      return;
    }

    console.log(`${LOG_PREFIX} candidate tourism modules:`);

    for (const [path, ...exports] of matches) {
      console.log(`${LOG_PREFIX}   ${path}  ->  [${exports.join(", ")}]`);
    }
  } catch (error) {
    console.warn(`${LOG_PREFIX} failed to search the module registry:`, error);
  }
}

/**
 * Logs the modules that build the selected-vehicle panel.
 *
 * Needed for the cruise ship's sailing time row. The C# side already publishes the value
 * (CruiseDepartureUISystem); what is missing is the module path and export name of the panel that
 * shows a vehicle's Origin, Destination and Line, which cannot be read from disk.
 *
 * Two patterns are searched rather than one, because it is not known which the panel is filed
 * under — the section may be a generic selected-info section rather than anything named for
 * vehicles. Both lists go to UI.log; pin whichever entry carries the passenger/line rows.
 */
export function logSelectedVehicleModules(moduleRegistry: ModuleRegistry): void {
  try {
    // Both broad sweeps came back far too large to read — 148 selected-info modules and 23
    // vehicle ones. The panel wanted is the section drawing a public transport vehicle's Origin,
    // Destination and Line, so filter to paths that mention a vehicle or a transport line *and*
    // look like a selected-info section. That intersection is small enough to act on.
    const interesting =
      /(public-?transport|transport-?line|vehicle).*(section|panel)|(section|panel).*(public-?transport|transport-?line|vehicle)/i;

    const matches = moduleRegistry.find(interesting) ?? [];

    console.log(
      `${LOG_PREFIX} candidate vehicle panel sections (${matches.length}):`
    );

    for (const [path, ...exports] of matches) {
      console.log(`${LOG_PREFIX}   ${path}  ->  [${exports.join(", ")}]`);
    }

    if (matches.length === 0) {
      console.warn(
        `${LOG_PREFIX} nothing matched. Fall back to listing every selected-info module ` +
          `and look for one whose exports mention Vehicle or Line.`
      );
    }
  } catch (error) {
    console.warn(`${LOG_PREFIX} failed to search the module registry:`, error);
  }
}

/** Broader sweep, for when the tourism search comes back empty. */
export function logInfoviewModules(moduleRegistry: ModuleRegistry): void {
  try {
    const matches = moduleRegistry.find(/infoview/i);
    console.log(`${LOG_PREFIX} infoview modules (${matches?.length ?? 0}):`);

    for (const [path, ...exports] of matches ?? []) {
      console.log(`${LOG_PREFIX}   ${path}  ->  [${exports.join(", ")}]`);
    }
  } catch (error) {
    console.warn(`${LOG_PREFIX} failed to search the module registry:`, error);
  }
}
