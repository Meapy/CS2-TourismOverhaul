# Publishing to Paradox Mods

Publishing is an external write. Build and verify first, then publish deliberately.

## 1. Outstanding before the first upload

- [ ] **`GameVersion`** in `TourismOverhaul/Properties/PublishConfiguration.xml` is `TODO`.
      Read the version from the bottom of the game's main menu and set it. Paradox rejects
      packages whose declared game version doesn't match.
- [ ] **`Thumbnail.png`** — generate it with:

      ```powershell
      .\TourismOverhaul\Properties\Render-Thumbnail.ps1
      ```

      That draws the artwork with GDI+ and needs no browser, so it can run as part of an unattended
      publish. `make-thumbnail.html` renders the same composition in a browser if you would rather
      preview it interactively; it just needs a person to click the button.

      Three files hold the same artwork — `Thumbnail.svg`, `Render-Thumbnail.ps1` and
      `make-thumbnail.html`. Keep them in step when it changes.
- [ ] **Screenshots** — `PublishConfiguration.xml` lists ten, in the order they should
      appear (the demand page leads; it is captured separately from the nine copied below). The captures live in `mod-page-images/` under names like `image copy 4.png`; this
      copies and renames them into place:

      ```powershell
      $src = "C:\Users\dkras\OneDrive\Documents\GitHub\CS2-TourismOverhaul\mod-page-images"
      $dest = "C:\Users\dkras\OneDrive\Documents\GitHub\CS2-TourismOverhaul\TourismOverhaul\Properties\Screenshots"
      New-Item -ItemType Directory -Force -Path $dest | Out-Null

      Copy-Item "$src\image copy 5.png" "$dest\01-hotel-zone.png" -Force
      Copy-Item "$src\image copy 6.png" "$dest\02-motel-zone.png" -Force
      Copy-Item "$src\image.png"        "$dest\03-tourism-panel.png" -Force
      Copy-Item "$src\image copy.png"   "$dest\04-tourist-markers.png" -Force
      Copy-Item "$src\image copy 7.png" "$dest\05-transport-usage.png" -Force
      Copy-Item "$src\image copy 4.png" "$dest\06-tourism-overlay.png" -Force
      Copy-Item "$src\image copy 2.png" "$dest\07-settings-arrivals.png" -Force
      Copy-Item "$src\image copy 3.png" "$dest\08-settings-hotels.png" -Force
      Copy-Item "$src\image copy 8.png" "$dest\09-hotel-detail.png" -Force
      ```

      Written out rather than looped over a hashtable: `[ordered]@{...}.GetEnumerator()` is a parse
      error, because `[ordered]` may only be applied where a hash literal is assigned.

      Then convert them, because Paradox rejects any image over 2.1 MB and full-resolution captures
      are well past it:

      ```powershell
      .\TourismOverhaul\Properties\Optimize-Screenshots.ps1
      ```

      That downscales to 1920 wide and re-encodes as JPEG, stepping quality down until each file
      fits. `PublishConfiguration.xml` references the `.jpg` names it produces.

      Two to check before uploading:

      - `06-tourism-overlay.png` and `09-hotel-detail.png` both carry a hardware monitoring overlay
        in the top-right corner. Recapture without it, or drop those entries.
      - `09-hotel-detail.png` shows a hotel at 2,130 of 6,840 rooms rented with a profit of
        -¢3,648,366/mo and a negative bank balance. It is a striking shot, but it advertises a hotel
        losing money on the listing for a mod that claims to keep hotels viable. Prefer a capture of
        a profitable hotel.
- [ ] **`ShortDescription`** must be 200 characters or fewer. The publisher rejects the upload
      with "Must be a string between 1 and 200 length" rather than truncating.
- [ ] **Tags** — `Code Mod` is set. Validate against the live service rather than trusting it;
      accepted tags are server-side data and change.
- [x] `ForumLink` points at the discussion thread:
      https://forum.paradoxplaza.com/forum/threads/mod-tourism-overhaul-fix-tourism.1937099/

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
- `images\tourism-overhaul-hotels.svg` and `images\tourism-overhaul-motels.svg` — the zone icons.
  Their absence is silent: `HotelZoneSystem` falls back to the stock commercial zone icon, so the
  zones still work and you only notice on the published mod.

No `.css` is emitted, because nothing under `ui/src` imports a stylesheet. Do not pass `-RequireUI`
to the verification script below — it requires a `.css` and would fail on a correct package.

```powershell
$stage = "$env:CSII_USERDATAPATH\Mods\TourismOverhaul"

& 'C:\Users\dkras\.codex\skills\build-cities-skylines-2-mods\scripts\verify-release.ps1' `
  -ReleaseFolder $stage -AssemblyName TourismOverhaul

Get-ChildItem -Recurse -File $stage | Get-FileHash -Algorithm SHA256 |
  Format-Table Hash, Path -AutoSize
```

Keep that hash list. The folder you upload must be the one you hashed.

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

Run the official publisher directly rather than going through `dotnet publish`. Both end in the same
executable, but the direct call uploads exactly the folder you verified, instead of whatever the
build happens to have left in the deploy directory.

`cd` into the managed project first: the media paths in `PublishConfiguration.xml` are relative
(`Properties/Thumbnail.png`), and the publisher resolves them against the working directory.

Use `$env:CSII_MODPUBLISHERPATH`, which points at the executable itself. It is **not** under
`CSII_TOOLPATH` — that variable points at the cached MSBuild toolchain, which contains no publisher.

```powershell
cd C:\Users\dkras\OneDrive\Documents\GitHub\CS2-TourismOverhaul\TourismOverhaul

& "$env:CSII_MODPUBLISHERPATH" Publish `
  'Properties\PublishConfiguration.xml' `
  -c "$env:CSII_USERDATAPATH\Mods\TourismOverhaul" -v
```

The publisher accepts exactly three commands, confirmed from `ModPublisher.exe --help`:

- `Publish` — creates the listing. Use this for the first upload, while `ModId` is still empty.
- `NewVersion` — uploads a new package version to an existing listing. Requires `ModId`, and fails
  with "ModId must be set in configuration" without it.
- `Update` — metadata only: descriptions, screenshots, tags, links. No package is uploaded, so it
  cannot fix a bad build.

There is no `New`. After the first `Publish`, every subsequent release uses `NewVersion`.

`Update` will not revise metadata for a version that is already published. Run it against a
`ModVersion` that exists and it fails with:

```
Could not update mod metadata: User version already exists for this mod.
```

This matters because screenshots, descriptions, tags and links are all metadata and are **not**
refreshed by `NewVersion` — that uploads the package only. So a screenshot corrected after the
release has gone out cannot be fixed with either verb at the same version number. The choices are
to publish a new version, or to edit the listing on the website.

The practical consequence: get screenshots and descriptions right *before* running `NewVersion`,
because afterwards they are expensive to change. Check the staged files exist and are under 2.1 MB
first — an image that is missing or oversized fails quietly, which looks identical to a partial
update.

Authentication comes from the publisher's own Paradox session. Never pass credentials on the command
line or store them in the repository.

`ModId` is empty for the first upload only. The publisher prints the assigned id on success —

```
Mod published with Id=153543
```

— but does **not** write it back into `PublishConfiguration.xml`. Set it by hand, or the next
`NewVersion` fails with "ModId must be set in configuration". It is now `153543`.

Public page: https://mods.paradoxplaza.com/mods/153543

- Use `NewVersion` for a new package version, `Update` for metadata-only changes.
- Run from the project directory — the publisher resolves media paths relative to its working
  directory.
- Upload the exact folder whose hashes were recorded.
- The publisher does **not** write `ModId` back — see above. It is set by hand and already committed.

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
