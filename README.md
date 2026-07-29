# QueueUpPlayniteExtension
Push your Playnite library into QueueUp

## Current status

This is a barebones exporter (see [issue #1](https://github.com/trentnbauer/QueueUpPlayniteExtension/issues/1)). It adds an
`Extensions > QueueUp > Export library to QueueUp (JSON)` menu item to Playnite that dumps your library to a JSON file, so the
data shape can be reviewed before wiring up the real push to the QueueUp API. Verified against a real 241-game library.

It's a PowerShell script extension, which only loads on **Playnite 10** (see [issue #4](https://github.com/trentnbauer/QueueUpPlayniteExtension/issues/4)
for why that matters going forward).

## Installing (Playnite desktop, v10)

1. Create a new folder named `QueueUpExporter` inside Playnite's `Extensions` directory, e.g.
   `%AppData%\Playnite\Extensions\QueueUpExporter\`, and copy just `extension.yaml` and `QueueUpExporter.psm1` into it
   (don't copy the whole repo folder — you don't need `.git`, `README.md`, etc.). Alternatively, add this repo folder as a
   developer extension from Playnite's `For developers` settings so you can iterate without copying files.
2. Restart Playnite, or reload extensions from developer settings.
3. Open `Extensions > QueueUp > Export library to QueueUp (JSON)`.
4. Enter how many games to export (e.g. `10` for a sample), or leave blank for the whole library.
5. Pick a save location (or cancel to fall back to `%TEMP%\queueup-library-export.json`). The exported JSON includes each
   game's name, platform(s) (display name + stable `SpecificationId` slug), source, ROM entries, genres,
   developers/publishers, release date, playtime, install status, and completion status.

**Tip:** if you sample with a count (e.g. `10`), games are taken alphabetically, which may all be PC/Steam titles if that's
most of your library. Since knowing the console per game is the actual point of this exporter, re-run with the count left
blank (full library export) if the sample doesn't include any console/emulated entries.

If the menu item doesn't appear or the export fails, check Playnite's extension load log at `%AppData%\Playnite\extensions.log`
(and `playnite.log` for script errors).
