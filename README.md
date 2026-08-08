# QueueUpPlayniteExtension

A Playnite extension that pushes your game library into [QueueUp](https://github.com/trentnbauer/QueueUp).

**Requires Playnite 10.** Playnite 11 dropped script-extension support and needs a different plugin model this
extension hasn't migrated to yet - see [issue #4](https://github.com/trentnbauer/QueueUpPlayniteExtension/issues/4).

## Installing

1. Download `QueueUpExporter_v<version>.pext` from the [latest release](../../releases/latest).
2. Drag the file onto Playnite's open window. Playnite recognizes `.pext` as its own installable-extension format and
   installs it automatically.
3. Restart Playnite, or reload extensions from developer settings, if it doesn't pick it up right away.

If you already have the older PowerShell version installed, remove it first (Playnite's `Add-ons` manager, or delete
`QueueUpExporter.psm1` from its extension folder) - leaving both installed alongside each other is untested.

This extension isn't in Playnite's own Add-ons database, so Playnite can't check for updates on its own - instead it
checks on every startup and shows a Playnite notification if a newer release is available, linking to the download
page. It can't install the update for you; drag the new `.pext` in the same way as above.

### Manual install

Only needed if drag-and-drop doesn't trigger an install prompt for some reason - check
`%AppData%\Playnite\extensions.log` first if that happens.

1. Download `QueueUpExporter.zip` from the [latest release](../../releases/latest) and unzip it - this gives you a
   folder containing `extension.yaml` and `QueueUpExporter.dll`, no build step needed.
2. Copy that folder into Playnite's `Extensions` directory, e.g. `%AppData%\Playnite\Extensions\QueueUpExporter\`.
3. Restart Playnite, or reload extensions from developer settings.

(You can also point Playnite's `For developers` settings at this repo folder directly, but that needs a local
`dotnet build` first so the `.dll` exists - see [Building from source](#building-from-source) below.)

## Using it

Everything lives under `Extensions > QueueUp` in Playnite's menu.

### 1. Connect to QueueUp

**`Connect to QueueUp...`** - paste the connection code from QueueUp's own **Profile Settings** page (the
`Generate Playnite setup code` button). It's a single `qc1_...` string that packs your server's URL and a personal API
key. Run this once per Playnite install, or again if you regenerate the code in QueueUp.

### 2. Push your library

**`Push library to QueueUp`** - sends your whole library to QueueUp in one go:

- Dedupes by title, unioning platforms when the same game is reported under multiple sources.
- Maps each Playnite platform to one of QueueUp's categories (PC, Xbox 360/One/Series, PS3/4/5, Switch/Switch 2) -
  anything without a match (e.g. a VR headset) is dropped, since QueueUp has no matching category for it.
- Shows a progress dialog while QueueUp matches titles to games (via IGDB, on QueueUp's server), then reports how many
  matched, how many need manual review in QueueUp, and how many errored.

Once you've run this manually at least once, Auto-Sync (below) takes over pushing future changes on its own - re-run
it by hand any time you want an immediate push instead of waiting for the next automatic trigger. Confirmed working
end-to-end against a real 1445-game library.

**If something goes wrong:** an error dialog quotes QueueUp's response directly - check it for a wrong server URL, an
expired/revoked connection code, or a network/HTTPS problem. If the push succeeds but the unmatched count looks a lot
higher than expected, that points to QueueUp's title-matching (or the platform mapping above) rather than anything
having crashed.

### 3. Auto-Sync

Once connected, the extension pushes your library to QueueUp on its own - no more manually re-running `Push library
to QueueUp` every time something changes:

- **Triggers:** whenever Playnite finishes refreshing library data (its own periodic Steam/Epic/GOG/etc. syncs, or a
  manual "Update library"), and once at Playnite startup as a safety net in case a change was missed on the previous
  run.
- **Cooldown:** at most once an hour, so a burst of library-updated events (each connected source syncing
  separately, a metadata-only refresh, ...) doesn't hammer QueueUp with repeat pushes.
- **Silent:** no progress dialog and no error dialog - a background trigger you didn't click shouldn't interrupt you.
  A successful auto-sync shows one small dismissible notification; a failed one only goes to Playnite's own log
  (`Add-ons` → view logs), so a transient network hiccup doesn't nag you.
- **`Enable Auto-Sync` / `Disable Auto-Sync`** in the `@QueueUp` menu toggles it - on by default once you've
  connected. With it off, pushing is back to a fully manual action via `Push library to QueueUp`.

### Exporting to a JSON file (diagnostic)

**`Export library to QueueUp (JSON)`** writes your library to a local file instead of pushing it anywhere - useful for
inspecting exactly what QueueUp would see, or for debugging. Enter how many games to export (e.g. `10` for a sample),
or leave blank for the whole library; pick a save location, or cancel to fall back to
`%TEMP%\queueup-library-export.json`. The export includes every field Playnite's `Game` object exposes except
cover/background images and icons - platform (display name + stable `SpecificationId` slug), source, per-source
`GameId` (e.g. Steam AppID), genres, developers/publishers, playtime, install status, and more.

**Tip:** a count-limited sample is taken alphabetically, so it may end up all PC/Steam titles if that's most of your
library. If you specifically want to check console/emulated entries, leave the count blank for a full export instead.

## Building from source

Every push to `main` is built by GitHub Actions. If that push bumped `extension.yaml`'s `Version`, it's also published
as a real, permanent [numbered release](../../releases/latest) (`.pext` and `.zip`) - that's what most people should
download, and what the in-app update check compares against. Every push, bumped or not, also force-moves a
[`latest` (bleeding edge)](../../releases/tag/latest) prerelease to the same build, for testing something not yet in a
numbered release. Most people don't need to build any of this themselves.

To build it yourself, you need the .NET SDK (8.0+ works fine even though this targets net462 - it cross-compiles via
the `Microsoft.NETFramework.ReferenceAssemblies` package, no Windows/.NET Framework install needed):

```
dotnet build
```

Produces `bin/Debug/net462/QueueUpExporter.dll`.

## Status

- It's a C# `GenericPlugin` targeting the Playnite 10 SDK (PlayniteSDK 6.15.0, .NET Framework 4.6.2) - see
  [issue #4](https://github.com/trentnbauer/QueueUpPlayniteExtension/issues/4) for why Playnite 11 isn't supported yet.
- Pushing to QueueUp auto-syncs on library changes/startup (at most once an hour), or run `Push library to QueueUp`
  by hand any time; `Enable Auto-Sync`/`Disable Auto-Sync` toggles the automatic side.
- Exporting or pushing a library where most games have no genres/developers/publishers ([issue #3](https://github.com/trentnbauer/QueueUpPlayniteExtension/issues/3))
  warns that Playnite's "Download Metadata" (Library menu, or right-click a selection) probably hasn't been run yet,
  and lets you cancel or proceed anyway; auto-sync only logs this rather than popping a dialog.
- The update notification compares `extension.yaml`'s `Version` against the same field baked into the filename of
  GitHub's current [latest release](../../releases/latest) - the newest numbered (non-prerelease) release, never the
  bleeding-edge `latest` alias. **Any release-worthy change needs a `Version` bump** in `extension.yaml`, or it won't
  get its own numbered release and existing installs won't see it as an update.
