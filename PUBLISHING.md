# Publishing to Paradox Mods

Publishing is an external write. Build and verify first, then publish deliberately.

## 1. Outstanding before the first upload

- [ ] **`GameVersion`** in `TourismOverhaul/Properties/PublishConfiguration.xml` is `TODO`.
      Read the version from the bottom of the game's main menu and set it. Paradox rejects
      packages whose declared game version doesn't match.
- [ ] **`Thumbnail.png`** does not exist yet. Open
      `TourismOverhaul/Properties/make-thumbnail.html` in a browser, pick a size, click
      **Download PNG**, and save it as `TourismOverhaul/Properties/Thumbnail.png`.
- [ ] **Screenshots** — drop them in `TourismOverhaul/Properties/Screenshots/` and uncomment the
      matching `<Screenshot>` lines in `PublishConfiguration.xml`.
- [ ] **Tags** — `Code Mod` is set. Validate against the live service rather than trusting it;
      accepted tags are server-side data and change.
- [ ] Decide whether `ForumLink` should point anywhere.

## 2. Build a clean release

```powershell
cd C:\Users\dkras\OneDrive\Documents\GitHub\CS2-TourismOverhaul

# UI half first
cd ui
npm run build
cd ..

# managed half, staged outside the live Mods folder
dotnet build .\TourismOverhaul\TourismOverhaul.csproj -c Release `
  -p:LocalModsPath='C:\Temp\CS2ModStage'
```

Require **0 errors**. Address any new warnings.

Note the UI build always writes to the live Mods folder (webpack reads `CSII_USERDATAPATH`), so a
staged managed build and the UI bundle end up in different places. For a release package, build the
managed half **without** `-p:LocalModsPath` so both land together, then verify the folder contents.

## 3. Verify the package

The deploy folder must contain all of:

- `TourismOverhaul.dll`
- `TourismOverhaul_win_x86_64.dll`, `_mac_x86_64.bundle`, `_linux_x86_64.so`
- `TourismOverhaul.mjs`
- any `.css` emitted by webpack

Record SHA-256 hashes of the exact folder you intend to upload:

```powershell
Get-FileHash "$env:LOCALAPPDATA\..\LocalLow\Colossal Order\Cities Skylines II\Mods\TourismOverhaul\*" |
  Format-Table Hash, Path -AutoSize
```

## 4. Test in game before publishing

A successful build does not prove anything ran. Confirm each of these:

- [ ] `Logs\TourismOverhaul.log` shows `OnLoad` and `systems registered`
- [ ] `Logs\UI.log` shows `[TourismOverhaul] Tourism panel extended.` and no JS error
- [ ] Tourism info view shows **Tourists in city**, **Hotel rooms free**, **Local cims away**
- [ ] Arrival split line appears in the log and names your map's available connection types
- [ ] Settings page renders, every slider moves, disabled states behave
- [ ] Save, reload, and confirm figures persist sensibly
- [ ] Toggle the hotel multiplier off and confirm `Native LodgingProviderSystem restored.`
- [ ] Remove the mod from a save and confirm the city still loads

## 5. Publish

Only after the checks above pass.

```powershell
cd TourismOverhaul

& "$env:CSII_TOOLPATH\ModPublisher\ModPublisher.exe" NewVersion `
  'Properties\PublishConfiguration.xml' `
  -c '<verified-folder>' -v
```

- Use `NewVersion` for a new package version, `Update` for metadata-only changes.
- Run from the project directory — the publisher resolves media paths relative to its working
  directory.
- Upload the exact folder whose hashes were recorded.
- The publisher writes the assigned `ModId` back into `PublishConfiguration.xml`; commit that.

## 6. After publishing

- [ ] Record the returned mod ID and version
- [ ] Open the public page and confirm it loads, with the right description, thumbnail and links
- [ ] Confirm the GitHub link resolves: https://github.com/Meapy/CS2-TourismOverhaul
- [ ] Tag the release in git and push
- [ ] Confirm the working tree is clean

## Keeping things aligned

`PublishConfiguration.xml`'s long description, `README.md` and the changelog should agree. When
bumping `ModVersion`, add a changelog entry in the same commit.

Never redistribute game assemblies, the toolchain, or Paradox credentials. `.gitignore` excludes
build output; keep it that way.
