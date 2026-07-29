# QueueUpPlayniteExtension
Push your Playnite library into QueueUp

## Current status

The JSON export ([issue #1](https://github.com/trentnbauer/QueueUpPlayniteExtension/issues/1)) is confirmed working end-to-end
inside a real Playnite install: `Extensions > QueueUp > Export library to QueueUp (JSON)` dumps the library to a file, and a
real run has verified both that the plugin loads correctly and that date fields serialize as normal ISO strings (not the old
PowerShell version's `/Date(...)/` bug).

The real push ([issue #7](https://github.com/trentnbauer/QueueUpPlayniteExtension/issues/7)) is now wired up too, via two more
menu items - see "Connecting and pushing to QueueUp" below. This part is new and **not yet verified against a live QueueUp
server** (see that section's verification note).

It's a C# `GenericPlugin` targeting the **Playnite 10 SDK** (.NET Framework 4.6.2) - see
[issue #4](https://github.com/trentnbauer/QueueUpPlayniteExtension/issues/4) for why (Playnite 11 uses an incompatible SDK/plugin
model; migrating to it is separate future work, not done here).

`QueueUpExporter.psm1` (the old PowerShell script version) is still in this repo but is no longer referenced by
`extension.yaml` - it's kept only as a known-working fallback until the new DLL has been confirmed to load and export
correctly inside a real Playnite install. It'll be deleted for good in a follow-up once that's confirmed.

## Building

**Don't have the .NET SDK installed? You don't need it.** Every push to `main` is built by GitHub Actions, which publishes a
ready-to-use `QueueUpExporter.zip` to the [`latest` release](../../releases/tag/latest) - just download it, unzip, and skip
straight to step 2 of Installing below.

To build it yourself instead, this requires the .NET SDK (8.0+ works fine even though this targets net462 - it
cross-compiles via the `Microsoft.NETFramework.ReferenceAssemblies` package, no Windows/.NET Framework install needed to
build).

```
dotnet build
```

Produces `bin/Debug/net462/QueueUpExporter.dll`.

## Installing (Playnite desktop, v10)

1. Get `extension.yaml` and `QueueUpExporter.dll` into a folder together, either by:
   - **Downloading the [latest release](../../releases/tag/latest)** (`QueueUpExporter.zip`) and unzipping it - no build step
     needed, this is the easiest option; or
   - Building locally (see above) and copying `extension.yaml` + the built `.dll` into a new folder yourself; or
   - Adding this repo folder as a developer extension from Playnite's `For developers` settings (still needs a local
     `dotnet build` first so the `.dll` exists - this option does *not* use the prebuilt release).

   Put that folder inside Playnite's `Extensions` directory, e.g. `%AppData%\Playnite\Extensions\QueueUpExporter\`.
   **If you already have the older PowerShell version installed**, delete `QueueUpExporter.psm1` from that folder first -
   `extension.yaml` now points at the DLL, but leaving the old script sitting alongside it is untested and best avoided.
2. Restart Playnite, or reload extensions from developer settings.
3. Open `Extensions > QueueUp > Export library to QueueUp (JSON)`.
4. Enter how many games to export (e.g. `10` for a sample), or leave blank for the whole library.
5. Pick a save location (or cancel to fall back to `%TEMP%\queueup-library-export.json`). The exported JSON includes every
   field Playnite's `Game` object exposes except cover/background images and icons - platform(s) (display name + stable
   `SpecificationId` slug), source, per-source `GameId` (e.g. Steam AppID), genres, developers/publishers, playtime, install
   status, and everything else, for a full view of what's available to match against QueueUp.

**Tip:** if you sample with a count (e.g. `10`), games are taken alphabetically, which may all be PC/Steam titles if that's
most of your library. Since knowing the console per game is the actual point of this exporter, re-run with the count left
blank (full library export) if the sample doesn't include any console/emulated entries.

**Verified:** confirmed against a real 1107-game library - the menu item loads, and dates come out as normal ISO strings
(e.g. `2022-12-09T13:09:29.195+11:00`), not the old PowerShell version's `/Date(1234567890)/` bug. If a future Playnite/SDK
update breaks either of those, check Playnite's extension load log at `%AppData%\Playnite\extensions.log` (and
`playnite.log` for other errors) - `QueueUpExporter.psm1` (see above) is the fallback until it's fixed.

## Connecting and pushing to QueueUp

Two more menu items under `Extensions > QueueUp` do the real push described in
[issue #7](https://github.com/trentnbauer/QueueUpPlayniteExtension/issues/7):

- **`Connect to QueueUp...`** - paste the connection code from QueueUp's own Profile Settings (`Generate Playnite setup
  code` button - QueueUp issues #441/#448). It's a single `qc1_...` string that packs your server's URL and a personal API
  key; this decodes it and saves the result to this plugin's own data folder (not plaintext next to the extension code).
  Run this once per install (or again if you regenerate the code in QueueUp).
- **`Push library to QueueUp`** - dedupes your library by title (unioning platforms for the same title reported under
  multiple sources/entries), maps each Playnite platform name to one of QueueUp's platform categories (PC, Xbox 360/One/
  Series, PS3/4/5, Switch/Switch 2 - anything else, like a VR headset, is dropped since QueueUp has no matching category),
  and submits it to QueueUp's bulk import endpoint. A progress dialog polls until QueueUp finishes matching titles to games
  (via IGDB, server-side - this extension never resolves that itself), then reports how many matched, how many need manual
  review in QueueUp (unresolved titles), and how many errored.

This is a manual menu action for now, not an automatic sync - re-run it whenever you want to push library changes.

**Verification note:** this compiles cleanly against the real `PlayniteSDK` 6.15.0 types and the platform-mapping/dedupe
logic has been tested in isolation (all keyword-mapping and dedupe/trim/drop cases pass), but the actual HTTP push has
**not** been run against a live QueueUp server - there's no QueueUp instance reachable from this extension's dev
environment. On first real use, specifically check:
- **`Connect to QueueUp...` accepts a real connection code** and doesn't error decoding it.
- **`Push library to QueueUp` actually reaches your server** - a wrong URL, an expired/revoked key, or a firewall/HTTPS
  issue would surface as an error dialog quoting QueueUp's response; a successful push should report matched/unmatched/
  errored counts that roughly add up to your library size (minus anything with no recognized platform, which no PC/Xbox/
  PlayStation/Switch platform).
- **The unmatched count isn't way higher than expected** - that would suggest the title-matching on QueueUp's side (or the
  platform mapping here) needs work, not that anything crashed.
