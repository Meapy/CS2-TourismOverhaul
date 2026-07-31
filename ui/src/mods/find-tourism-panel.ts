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
