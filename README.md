# QueueUpPlayniteExtension
Push your Playnite library into QueueUp

## Current status

This is a barebones exporter (see [issue #1](https://github.com/trentnbauer/QueueUpPlayniteExtension/issues/1)). It adds an
`Extensions > QueueUp > Export library to QueueUp (JSON)` menu item to Playnite that dumps your library to a JSON file, so the
data shape can be reviewed before wiring up the real push to the QueueUp API. Verified against a real 1107-game library.

It's a C# `GenericPlugin` targeting the **Playnite 10 SDK** (.NET Framework 4.6.2) - see
[issue #4](https://github.com/trentnbauer/QueueUpPlayniteExtension/issues/4) for why (Playnite 11 uses an incompatible SDK/plugin
model; migrating to it is separate future work, not done here).

`QueueUpExporter.psm1` (the old PowerShell script version) is still in this repo but is no longer referenced by
`extension.yaml` - it's kept only as a known-working fallback until the new DLL has been confirmed to load and export
correctly inside a real Playnite install. It'll be deleted for good in a follow-up once that's confirmed.

## Building

Requires the .NET SDK (8.0+ works fine even though this targets net462 - it cross-compiles via the
`Microsoft.NETFramework.ReferenceAssemblies` package, no Windows/.NET Framework install needed to build).

```
dotnet build
```

Produces `bin/Debug/net462/QueueUpExporter.dll`.

## Installing (Playnite desktop, v10)

1. Create a new folder named `QueueUpExporter` inside Playnite's `Extensions` directory, e.g.
   `%AppData%\Playnite\Extensions\QueueUpExporter\`, and copy `extension.yaml` and the built `QueueUpExporter.dll` into it.
   Alternatively, add this repo folder as a developer extension from Playnite's `For developers` settings so you can iterate
   without copying files each time (still needs a `dotnet build` first so the `.dll` exists). **If you already have the older
   PowerShell version installed**, delete `QueueUpExporter.psm1` from that folder first - `extension.yaml` now points at the
   DLL, but leaving the old script sitting alongside it is untested and best avoided.
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

**Verification note:** this compiles cleanly against the real Playnite 10 SDK (`PlayniteSDK` 6.15.0) and its reflection-based
export logic has been runtime-tested directly (via `mono`, outside Playnite) against a real `Game` instance - but it hasn't
been run inside an actual Playnite process, since that's a Windows desktop app with no way to launch/test it in this
extension's dev environment. If the menu item doesn't appear or the export fails, check Playnite's extension load log at
`%AppData%\Playnite\extensions.log` (and `playnite.log` for other errors).

On first real run, specifically check:
- **Does the `Extensions > QueueUp > Export library to QueueUp (JSON)` menu item appear at all** - this confirms the plugin
  loaded and the GUID/manifest are valid.
- **Do date fields (`Added`, `Modified`, `LastActivity`, etc.) come out as normal ISO-style strings**, not something like
  `/Date(1234567890)/`. The old PowerShell version's `ConvertTo-Json` had exactly that bug; whether Playnite's own SDK
  serializer (`Serialization.ToJson`, used here) has the same issue is untested - it couldn't be exercised outside a live
  Playnite host in this sandbox (it throws `NullReferenceException` standalone, since it needs a serializer injected by the
  running app).

If either of those looks wrong, `QueueUpExporter.psm1` (see above) is the fallback until it's fixed.
