# TourismOverhaul — UI module

Frontend half of the mod. Adds a **Local cims away** row to the game's Tourism info view panel.

The C# side (`TourismPanelUISystem`) publishes the values as bindings in the `tourismOverhaul`
group. This module subscribes to them and renders the rows inside the native panel.

## First-time setup

`mod.json` and `src/` are already written. The rest of the boilerplate — build config, type
definitions and dependencies — is versioned against your installed game, so copy it from the
toolchain rather than checking copies into the repo.

Note that `CSII_TOOLPATH` points at the *installed* toolchain cache under `LocalLow`, which does
not carry the UI scaffolder. The template lives in the game folder instead:

```powershell
cd C:\Users\dkras\OneDrive\Documents\GitHub\CS2-TourismOverhaul\ui

$tpl = "F:\SteamLibrary\steamapps\common\Cities Skylines II\Cities2_Data\Content\Game\.ModdingToolchain\npx-create-csii-ui-mod\template"
Copy-Item "$tpl\package.json","$tpl\tsconfig.json","$tpl\webpack.config.js" -Destination .
Copy-Item "$tpl\types","$tpl\tools" -Destination . -Recurse

npm install
```

`npm run update` afterwards refreshes those files from the toolchain after a game update.

## Building

```powershell
npm run build     # production bundle
npm run dev       # rebuild on change
```

Webpack reads `CSII_USERDATAPATH` and writes the `.mjs` and `.css` straight into
`%LOCALAPPDATA%Low\Colossal Order\Cities Skylines II\Mods\TourismOverhaul`, alongside the managed
DLL. Both halves must be present for the row to appear.

### The deploy folder is shared, and the stock build wipes it

The toolchain's `DeployWIP` target runs `<RemoveDir Directories="$(DeployDir)" />` before copying
the managed output — into the very folder webpack just wrote the UI bundle to. Building the C#
project therefore deletes the frontend, and the failure is silent: no error, the module simply
never registers and the panel rows disappear.

`TourismOverhaul.csproj` overrides `DeployWIP` to copy without the `RemoveDir`, so the two halves
coexist regardless of build order. If you ever rename the assembly, delete the deploy folder by
hand, since stale managed files are no longer cleaned up automatically.

## The panel module path

The game's UI bundle ships packed inside `.cok` archives, so the Tourism panel's module path
cannot be read from disk. It was resolved at runtime with `ModuleRegistry.find(/tourism/i)` and is
pinned in `src/index.tsx`:

```
game-ui/game/components/infoviews/active-infoview-panel/panels/tourism-infoview-panel.tsx
export: TourismInfoviewPanel
```

If a game update moves it, the registrar logs an error plus every `/infoview/i` module it can find,
so the new path can be read straight out of `UI.log` and pinned again.

Note `ModuleRegistry.get()` **throws** when a module is missing rather than returning null, which
is why existence is checked with `find()` instead.

## Verifying

A successful build does not prove the module loaded. Check `UI.log` for:

- `[TourismOverhaul] Tourism panel extended.`
- no `Could not extend the Tourism panel` error
- the module registering without exceptions
