using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace QueueUpExporter
{
    /// <summary>
    /// C# port of the original PowerShell exporter (issue #4) - Playnite 11 drops script extension
    /// support entirely, so anything meant to survive that upgrade has to be a compiled plugin.
    /// Targets the Playnite 10 SDK (.NET Framework 4.6.2) for now; migrating to the Playnite 11 SDK
    /// (.NET 10, a separate/incompatible plugin model) is a distinct future task, not done here.
    /// </summary>
    public class QueueUpExporterPlugin : GenericPlugin
    {
        // Binary/media file references (icon, cover art, background image) - excluded from the
        // export dump below. Everything else on Game is included generically via reflection, same
        // approach and reasoning as the PowerShell version's Select-Object -Property *
        // -ExcludeProperty: a full view of what's available to match against QueueUp, not a
        // hand-picked subset that has to be revisited every time a new field turns out to matter.
        private static readonly HashSet<string> MediaFields = new HashSet<string>
        {
            "Icon",
            "CoverImage",
            "BackgroundImage",
        };

        // Wire format from QueueUp's connectionCode.ts: `qc1_` + base64(JSON({url, key})). The `1`
        // is a version marker on QueueUp's side - a future breaking change bumps to `qc2_`, which
        // this extension doesn't understand and should reject rather than silently misparse.
        private const string ConnectionCodePrefix = "qc1_";

        // Keyword -> QueueUp RoomPlatform (packages/shared/src/types.ts). Order matters: more
        // specific keywords ("switch 2", "xbox series") must be checked before substrings of them
        // ("switch", implicitly via distinct "xbox" not being listed at all). Deliberately no bare
        // "xbox" or "playstation" fallback - guessing the wrong console generation is worse than
        // leaving a title's platform unmapped (it just won't count toward ownership on that system
        // until QueueUp resolves it some other way).
        private static readonly KeyValuePair<string, string>[] PlatformKeywordMap =
        {
            new KeyValuePair<string, string>("switch 2", "switch2"),
            new KeyValuePair<string, string>("switch2", "switch2"),
            new KeyValuePair<string, string>("switch", "switch"),
            new KeyValuePair<string, string>("xbox series", "xbox_series"),
            new KeyValuePair<string, string>("xbox 360", "xbox_360"),
            new KeyValuePair<string, string>("xbox one", "xbox_one"),
            new KeyValuePair<string, string>("playstation 5", "ps5"),
            new KeyValuePair<string, string>("ps5", "ps5"),
            new KeyValuePair<string, string>("playstation 4", "ps4"),
            new KeyValuePair<string, string>("ps4", "ps4"),
            new KeyValuePair<string, string>("playstation 3", "ps3"),
            new KeyValuePair<string, string>("ps3", "ps3"),
            new KeyValuePair<string, string>("windows", "pc"),
            new KeyValuePair<string, string>("linux", "pc"),
            new KeyValuePair<string, string>("mac", "pc"),
            new KeyValuePair<string, string>("pc", "pc"),
        };

        public override Guid Id { get; } = Guid.Parse("ffbe3993-60a1-49e6-a228-4cf7a130ce44");

        public QueueUpExporterPlugin(IPlayniteAPI api) : base(api)
        {
        }

        /// <summary>
        /// This extension isn't in Playnite's own Add-ons database (it's distributed manually via
        /// GitHub releases - see README), so Playnite has no way to know a new version exists on its
        /// own. Checks GitHub on every startup instead and surfaces a notification if the published
        /// release is ahead of what's installed - the user still has to grab and drag in the new
        /// `.pext` themselves, since there's no API for a plugin to replace its own files while
        /// Playnite has it loaded.
        /// </summary>
        public override void OnApplicationStarted(OnApplicationStartedEventArgs args)
        {
            // Backgrounded so a slow/unreachable GitHub API can never delay Playnite's own startup -
            // this is a nice-to-know check, not something worth blocking on.
            Task.Run(() => CheckForUpdate());
            // Startup safety net (issue #570 follow-up) - covers the case where OnLibraryUpdated
            // didn't fire on the previous run (extension update, crash) but the library moved on
            // regardless. TryAutoSync itself is a fast, mostly-synchronous set of guard checks before
            // it backgrounds the actual push, so this doesn't delay startup either.
            TryAutoSync();
        }

        /// <summary>
        /// Fires after Playnite finishes refreshing library data - its own periodic connected-source
        /// syncs (Steam/Epic/GOG/...) or a manual "Update library" - which is the real "something
        /// worth pushing might have changed" signal (issue #570 follow-up: there was no automated
        /// sync at all before this). TryAutoSync's own cooldown absorbs this firing more often than
        /// actually useful (e.g. a metadata-only refresh with no new titles).
        /// </summary>
        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            TryAutoSync();
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                Description = "Export library to QueueUp (JSON)",
                MenuSection = "@QueueUp",
                Action = ExportLibrary,
            };

            yield return new MainMenuItem
            {
                Description = "Connect to QueueUp...",
                MenuSection = "@QueueUp",
                Action = ConnectToQueueUp,
            };

            yield return new MainMenuItem
            {
                Description = "Push library to QueueUp",
                MenuSection = "@QueueUp",
                Action = PushLibrary,
            };

            // Reflects current state in its own label (re-evaluated by Playnite each time the menu
            // opens, same as every other item here) rather than a separate checkbox control - the
            // SDK's plain MainMenuItem has no built-in checked/toggle state to bind to.
            var autoSyncEnabled = (LoadPluginSettings<QueueUpConnectionSettings>() ?? new QueueUpConnectionSettings()).AutoSyncEnabled;
            yield return new MainMenuItem
            {
                Description = autoSyncEnabled ? "Disable Auto-Sync" : "Enable Auto-Sync",
                MenuSection = "@QueueUp",
                Action = ToggleAutoSync,
            };
        }

        private void ExportLibrary(MainMenuItemActionArgs args)
        {
            var countInput = PlayniteApi.Dialogs.SelectString(
                "How many games to export? Leave blank to export the full library.",
                "QueueUp Export",
                "10");

            if (!countInput.Result)
            {
                return;
            }

            IEnumerable<Game> games = PlayniteApi.Database.Games.OrderBy(g => g.Name);

            var limitText = (countInput.SelectedString ?? string.Empty).Trim();
            if (limitText.Length > 0 && int.TryParse(limitText, out var limit) && limit > 0)
            {
                games = games.Take(limit);
            }

            var gameList = games.ToList();
            if (LooksLikeMetadataMissing(gameList))
            {
                var proceed = PlayniteApi.Dialogs.ShowMessage(
                    MissingMetadataMessage,
                    "QueueUp Export",
                    System.Windows.MessageBoxButton.YesNo);
                if (proceed != System.Windows.MessageBoxResult.Yes)
                {
                    return;
                }
            }

            var export = gameList.Select(ToExportDictionary).ToList();
            var json = Serialization.ToJson(export, true);

            var savePath = PlayniteApi.Dialogs.SaveFile("JSON file|*.json");
            if (string.IsNullOrEmpty(savePath))
            {
                savePath = Path.Combine(Path.GetTempPath(), "queueup-library-export.json");
            }

            File.WriteAllText(savePath, json);
            PlayniteApi.Dialogs.ShowMessage($"Exported {export.Count} games to:\n{savePath}");
        }

        /// <summary>
        /// Decodes a pasted `qc1_...` connection code (from QueueUp's Profile Settings > "Generate
        /// Playnite setup code") and persists it via Playnite's own plugin-settings storage (the
        /// plugin's user-data folder, not plaintext next to the extension's code - see #7).
        /// </summary>
        private void ConnectToQueueUp(MainMenuItemActionArgs args)
        {
            var input = PlayniteApi.Dialogs.SelectString(
                "Paste the connection code from QueueUp's Profile Settings (\"Generate Playnite setup code\").",
                "Connect to QueueUp",
                string.Empty);

            if (!input.Result || string.IsNullOrWhiteSpace(input.SelectedString))
            {
                return;
            }

            var code = input.SelectedString.Trim();
            if (!code.StartsWith(ConnectionCodePrefix, StringComparison.Ordinal))
            {
                PlayniteApi.Dialogs.ShowErrorMessage(
                    $"That doesn't look like a QueueUp connection code (should start with \"{ConnectionCodePrefix}\").",
                    "Connect to QueueUp");
                return;
            }

            ConnectionCode decoded = null;
            try
            {
                var json = Encoding.UTF8.GetString(Convert.FromBase64String(code.Substring(ConnectionCodePrefix.Length)));
                decoded = Serialization.FromJson<ConnectionCode>(json);
            }
            catch
            {
                // decoded stays null - handled by the validity check below.
            }

            if (decoded == null || string.IsNullOrWhiteSpace(decoded.Url) || string.IsNullOrWhiteSpace(decoded.Key))
            {
                PlayniteApi.Dialogs.ShowErrorMessage("Couldn't parse that connection code.", "Connect to QueueUp");
                return;
            }

            SavePluginSettings(new QueueUpConnectionSettings { Url = decoded.Url.TrimEnd('/'), Key = decoded.Key });
            PlayniteApi.Dialogs.ShowMessage($"Connected to QueueUp at {decoded.Url}.", "Connect to QueueUp");
        }

        /// <summary>
        /// Builds the deduped/platform-mapped payload and pushes it to QueueUp's bulk import
        /// endpoint (QueueUp#450/#453), then polls for completion. Runs inside a global-progress
        /// dialog since a big non-Steam library can take a while to resolve server-side. The actual
        /// push/poll work lives in PushLibraryCore below, shared with TryAutoSync's silent
        /// background variant (#570 follow-up) - this method owns only the dialog/UX around it.
        /// </summary>
        private void PushLibrary(MainMenuItemActionArgs args)
        {
            var settings = LoadPluginSettings<QueueUpConnectionSettings>();
            if (settings == null || string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.Key))
            {
                PlayniteApi.Dialogs.ShowErrorMessage(
                    "Not connected to QueueUp yet - use \"Connect to QueueUp...\" first to paste a connection code from Profile Settings.",
                    "Push library to QueueUp");
                return;
            }

            var games = PlayniteApi.Database.Games;
            var entries = BuildImportEntries(games);
            if (entries.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "No games with a recognized platform (PC/Xbox/PlayStation/Switch) found to push.",
                    "Push library to QueueUp");
                return;
            }

            if (LooksLikeMetadataMissing(games))
            {
                var proceed = PlayniteApi.Dialogs.ShowMessage(
                    MissingMetadataMessage,
                    "Push library to QueueUp",
                    System.Windows.MessageBoxButton.YesNo);
                if (proceed != System.Windows.MessageBoxResult.Yes)
                {
                    return;
                }
            }

            PushOutcome outcome = null;

            PlayniteApi.Dialogs.ActivateGlobalProgress(
                progressArgs => outcome = PushLibraryCore(settings, entries, text => progressArgs.Text = text, progressArgs.CancelToken),
                new GlobalProgressOptions("Pushing library to QueueUp...", true) { IsIndeterminate = false });

            if (outcome == null)
            {
                return;
            }

            // A manual push is itself real, fresh evidence QueueUp is up to date - recording it
            // here means an auto-sync trigger (OnLibraryUpdated/OnApplicationStarted) firing right
            // after doesn't immediately re-push the same library over again. LastPushedHash is
            // recorded too (issue #18) so that same follow-up trigger's unchanged-library check
            // also has something current to compare against, rather than only the cooldown
            // protecting against a redundant re-push.
            if (!outcome.IsError)
            {
                settings.LastAutoSyncAt = DateTime.UtcNow;
                settings.LastPushedHash = ComputeEntriesHash(entries);
                SavePluginSettings(settings);
            }

            if (outcome.IsError)
            {
                PlayniteApi.Dialogs.ShowErrorMessage(outcome.Message, "Push library to QueueUp");
            }
            else
            {
                PlayniteApi.Dialogs.ShowMessage(outcome.Message, "Push library to QueueUp");
            }
        }

        /// <summary>
        /// The push-and-poll request/response cycle itself, with no Dialogs/UI dependency - callable
        /// from PushLibrary's progress-dialog callback (reportProgress wired to progressArgs.Text)
        /// and from TryAutoSync's background thread (reportProgress a no-op, cancelToken
        /// CancellationToken.None - a silent auto-sync isn't user-cancelable). Never throws; any
        /// failure comes back as an IsError outcome instead.
        /// </summary>
        private static PushOutcome PushLibraryCore(
            QueueUpConnectionSettings settings,
            List<ImportEntry> entries,
            Action<string> reportProgress,
            CancellationToken cancelToken)
        {
            try
            {
                using (var client = CreateHttpClient(settings))
                {
                    var requestJson = Serialization.ToJson(new ImportRequestBody { Entries = entries });
                    var response = client
                        .PostAsync("api/v1/library/import-playnite", new StringContent(requestJson, Encoding.UTF8, "application/json"))
                        .GetAwaiter()
                        .GetResult();
                    var responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

                    if (!response.IsSuccessStatusCode)
                    {
                        return new PushOutcome { IsError = true, Message = DescribeError(response.StatusCode, responseBody) };
                    }

                    var started = Serialization.FromJson<ImportStartedResponse>(responseBody);
                    reportProgress($"Sent {entries.Count} titles ({started.ConsideredCount} considered). Waiting for QueueUp to finish matching...");

                    ImportProgress progress;
                    while (true)
                    {
                        if (cancelToken.IsCancellationRequested)
                        {
                            return new PushOutcome
                            {
                                IsError = false,
                                Message = "Push canceled locally - QueueUp will keep processing what was already sent; check back in QueueUp later.",
                            };
                        }

                        Thread.Sleep(1500);

                        var progressResponse = client.GetAsync("api/v1/library/import-playnite/progress").GetAwaiter().GetResult();
                        var progressBody = progressResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                        if (!progressResponse.IsSuccessStatusCode)
                        {
                            return new PushOutcome { IsError = true, Message = DescribeError(progressResponse.StatusCode, progressBody) };
                        }

                        progress = Serialization.FromJson<ImportProgressResponse>(progressBody)?.Progress;
                        if (progress == null)
                        {
                            continue;
                        }

                        reportProgress($"Matched {progress.Matched}, unmatched {progress.Unmatched}, errored {progress.Errored} of {progress.ConsideredCount}...");
                        if (progress.Done)
                        {
                            break;
                        }
                    }

                    return new PushOutcome
                    {
                        IsError = false,
                        Message =
                            $"Push complete: {progress.Matched} matched, {progress.Unmatched} unresolved (review in QueueUp's Profile Settings), " +
                            $"{progress.Errored} errored, out of {progress.ConsideredCount} considered.",
                    };
                }
            }
            catch (Exception ex)
            {
                return new PushOutcome { IsError = true, Message = $"Push failed: {ex.Message}" };
            }
        }

        private const double AutoSyncCooldownHours = 1.0;

        /// <summary>
        /// Silent counterpart to the "Push library to QueueUp" menu action (issue #570 follow-up:
        /// Playnite sync wasn't automated at all before this) - fires from OnLibraryUpdated (a real
        /// library change) and OnApplicationStarted (a startup safety net in case a change event was
        /// missed), gated by AutoSyncEnabled, an unchanged-library check (issue #18), and a cooldown
        /// so a burst of library-updated events (one per connected source syncing, metadata
        /// refreshes, ...) can't hammer QueueUp's import endpoint. No progress dialog, no error
        /// dialog - a background trigger the user didn't click shouldn't interrupt them; success
        /// gets one small dismissible notification (same style as the update-check's), failure only
        /// goes to Playnite's own log. Always runs off the calling thread's continuation via
        /// Task.Run - both call sites are event handlers Playnite expects to return promptly.
        /// </summary>
        private void TryAutoSync()
        {
            var settings = LoadPluginSettings<QueueUpConnectionSettings>();
            if (settings == null || string.IsNullOrWhiteSpace(settings.Url) || string.IsNullOrWhiteSpace(settings.Key))
            {
                return;
            }

            if (!settings.AutoSyncEnabled)
            {
                return;
            }

            // Building entries and hashing them is local/synchronous, no network round-trip -
            // cheap enough to do up front, before the cooldown check, so a library that's
            // byte-for-byte identical to what was last successfully pushed (metadata-only refresh,
            // cover-art re-scan, a restart with nothing changed) skips entirely regardless of
            // cooldown state (issue #18) instead of spending a full push + server-side matching
            // pass on nothing new.
            var games = PlayniteApi.Database.Games;
            var entries = BuildImportEntries(games);
            if (entries.Count == 0)
            {
                return;
            }

            var currentHash = ComputeEntriesHash(entries);
            if (currentHash == settings.LastPushedHash)
            {
                LogManager.GetLogger().Debug("QueueUp auto-sync skipped: library unchanged since last push.");
                return;
            }

            if (settings.LastAutoSyncAt.HasValue && DateTime.UtcNow - settings.LastAutoSyncAt.Value < TimeSpan.FromHours(AutoSyncCooldownHours))
            {
                return;
            }

            // Auto-sync is a background trigger the user didn't click, so - same "no dialog"
            // reasoning as the rest of this method - a missing-metadata warning (issue #3) can only
            // be logged here, not asked as a blocking Yes/No like PushLibrary does. Doesn't skip the
            // push itself: sparse metadata doesn't make the sync wrong, just less useful, and
            // skipping would silently stop auto-syncing altogether until the user happens to notice
            // and run Download Metadata on their own.
            if (LooksLikeMetadataMissing(games))
            {
                LogManager.GetLogger().Warn(
                    "QueueUp auto-sync: most games have no genres/developers/publishers - " +
                    "Playnite's Download Metadata may not have been run for this library yet.");
            }

            Task.Run(() =>
            {
                try
                {
                    var outcome = PushLibraryCore(settings, entries, _ => { }, CancellationToken.None);
                    if (outcome.IsError)
                    {
                        LogManager.GetLogger().Warn($"QueueUp auto-sync failed: {outcome.Message}");
                        return;
                    }

                    settings.LastAutoSyncAt = DateTime.UtcNow;
                    settings.LastPushedHash = currentHash;
                    SavePluginSettings(settings);

                    PlayniteApi.Notifications.Add(new NotificationMessage(
                        "queueup_exporter_auto_sync",
                        $"QueueUp: library synced automatically ({entries.Count} titles).",
                        NotificationType.Info));
                }
                catch (Exception ex)
                {
                    LogManager.GetLogger().Warn(ex, "QueueUp auto-sync failed.");
                }
            });
        }

        private void ToggleAutoSync(MainMenuItemActionArgs args)
        {
            var settings = LoadPluginSettings<QueueUpConnectionSettings>() ?? new QueueUpConnectionSettings();
            settings.AutoSyncEnabled = !settings.AutoSyncEnabled;
            SavePluginSettings(settings);

            PlayniteApi.Dialogs.ShowMessage(
                settings.AutoSyncEnabled
                    ? "Auto-Sync enabled - QueueUp will be pushed automatically (at most once an hour) whenever your library changes or Playnite starts."
                    : "Auto-Sync disabled - use \"Push library to QueueUp\" to sync manually.",
                "QueueUp Auto-Sync");
        }

        private const string UpdateNotificationId = "queueup_exporter_update_available";
        private const string ReleaseTagApiUrl = "https://api.github.com/repos/trentnbauer/QueueUpPlayniteExtension/releases/tags/latest";
        private const string ReleasePageUrl = "https://github.com/trentnbauer/QueueUpPlayniteExtension/releases/tag/latest";

        private void CheckForUpdate()
        {
            try
            {
                var localVersion = ReadLocalVersion();
                var remoteVersion = localVersion == null ? null : FetchLatestReleaseVersion();
                if (remoteVersion == null || remoteVersion <= localVersion)
                {
                    return;
                }

                var notification = new NotificationMessage(
                    UpdateNotificationId,
                    $"A new QueueUp Library Exporter version is available ({remoteVersion}, you have {localVersion}). Click to open the download page.",
                    NotificationType.Info,
                    OpenReleasePage);

                // This runs on the Task.Run background thread OnApplicationStarted kicked off, but
                // INotificationsAPI.Messages is an ObservableCollection WPF's UI binds to directly -
                // mutating it off the dispatcher thread throws. Application.Current is null under a
                // non-UI host (never true inside real Playnite, but cheap to guard).
                Action addNotification = () =>
                {
                    // Remove any notification left over from an earlier startup before adding a
                    // fresh one - Add doesn't dedupe by Id itself, and without this a version bump
                    // the user hasn't acted on yet would pile up a duplicate on every launch.
                    PlayniteApi.Notifications.Remove(UpdateNotificationId);
                    PlayniteApi.Notifications.Add(notification);
                };

                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess())
                {
                    addNotification();
                }
                else
                {
                    dispatcher.Invoke(addNotification);
                }
            }
            catch (Exception ex)
            {
                // Never surface an error dialog over a background version check - the user didn't
                // ask for this, so a failure (offline, GitHub down, rate-limited) should just be
                // silent, same "quiet failure" reasoning as the export's own per-property try/catch.
                LogManager.GetLogger().Warn(ex, "QueueUp update check failed.");
            }
        }

        /// <summary>
        /// The update notification's click action - runs later, on Playnite's UI thread when the
        /// user actually clicks it, well outside CheckForUpdate's own try/catch. Process.Start on a
        /// bare URL throws Win32Exception if there's no registered browser/URL handler; same "don't
        /// surface an error dialog over something the user didn't explicitly ask to run" reasoning
        /// as CheckForUpdate itself.
        /// </summary>
        private static void OpenReleasePage()
        {
            try
            {
                Process.Start(ReleasePageUrl);
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn(ex, "Could not open the QueueUp Library Exporter release page.");
            }
        }

        /// <summary>
        /// Reads Version straight from this install's own extension.yaml, sitting next to the
        /// compiled DLL - single source of truth rather than a second hardcoded copy, same file
        /// CI's build reads to name the release asset (see .github/workflows/build.yml).
        ///
        /// This is also what CheckForUpdate compares against the published release, which is named
        /// from that same field - a PR that ships a user-visible change without bumping
        /// extension.yaml's Version republishes the exact same asset name, so every existing install
        /// computes "no update available" and the notification never fires. Bump Version on any
        /// release-worthy change.
        /// </summary>
        private static Version ReadLocalVersion()
        {
            var pluginDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var manifestPath = Path.Combine(pluginDirectory ?? string.Empty, "extension.yaml");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            foreach (var line in File.ReadAllLines(manifestPath))
            {
                if (line.StartsWith("Version:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = line.Substring("Version:".Length).Trim();
                    return Version.TryParse(value, out var version) ? version : null;
                }
            }

            return null;
        }

        /// <summary>
        /// GitHub's own "latest release" API endpoint excludes prereleases, and this repo's only
        /// release (tag "latest", rebuilt on every push to main) is marked prerelease - fetches it
        /// by tag instead. Neither the release name nor the tag carries a version of its own (both
        /// are static "Latest build"/"latest"); the only place a version shows up is the `.pext`
        /// asset's filename, named from extension.yaml's Version field by the same build.
        /// </summary>
        private static Version FetchLatestReleaseVersion()
        {
            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
            {
                // GitHub's API rejects requests with no User-Agent header. TryAddWithoutValidation
                // rather than Add - Add parses/validates the value into a typed header and throws
                // FormatException on anything it doesn't like; neither header here needs that.
                client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "QueueUpExporter-Playnite-Plugin");
                client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "application/vnd.github+json");

                var json = client.GetStringAsync(ReleaseTagApiUrl).GetAwaiter().GetResult();
                var release = Serialization.FromJson<GitHubRelease>(json);
                var pextAsset = release?.Assets?.FirstOrDefault(a =>
                    a.Name != null &&
                    a.Name.StartsWith("QueueUpExporter_v", StringComparison.OrdinalIgnoreCase) &&
                    a.Name.EndsWith(".pext", StringComparison.OrdinalIgnoreCase));
                if (pextAsset == null)
                {
                    return null;
                }

                var versionText = pextAsset.Name.Substring(
                    "QueueUpExporter_v".Length,
                    pextAsset.Name.Length - "QueueUpExporter_v".Length - ".pext".Length);
                return Version.TryParse(versionText, out var version) ? version : null;
            }
        }

        private static HttpClient CreateHttpClient(QueueUpConnectionSettings settings)
        {
            var client = new HttpClient { BaseAddress = new Uri(settings.Url.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.Key);
            return client;
        }

        /// <summary>
        /// QueueUp's error responses are always `{"error": "..."}` (server/src/plugins/auth.ts'
        /// global error handler) below 500, and a generic message at/above it - surface whichever
        /// text is available rather than a bare status code.
        /// </summary>
        private static string DescribeError(HttpStatusCode statusCode, string body)
        {
            string message = null;
            try
            {
                message = Serialization.FromJson<ErrorResponse>(body)?.Error;
            }
            catch
            {
                // Fall through to the raw body below.
            }

            return $"QueueUp returned {(int)statusCode} {statusCode}: {message ?? body}";
        }

        private static string MapPlatformName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return null;
            }

            var lower = name.ToLowerInvariant();
            foreach (var mapping in PlatformKeywordMap)
            {
                if (lower.Contains(mapping.Key))
                {
                    return mapping.Value;
                }
            }

            return null;
        }

        // Above this proportion of games in the export having no genres, developers, or publishers
        // at all is treated as "Download Metadata was never run" rather than "this library just
        // happens to be sparse" (issue #3) - picked from the real-world case that motivated this
        // check (241 games, no metadata download run first: genres empty for 240/241, developers/
        // publishers empty for ~90%), which sits nowhere near this threshold by accident.
        private const double MissingMetadataThreshold = 0.5;

        /// <summary>
        /// Playnite's SDK has no way to trigger "Download Metadata" itself (confirmed by the
        /// maintainer - issue #3) - it stays a manual step the user runs from Playnite's Library
        /// menu (or right-click a selection) before exporting. A game missing genres, developers,
        /// AND publishers all at once is a strong signal metadata was never downloaded for it,
        /// rather than the title genuinely having none of the three (checking all three together,
        /// not any one alone, keeps a game that's merely light on one field from tripping this).
        /// Flags the whole batch once more than MissingMetadataThreshold of the games being
        /// exported look that way.
        /// </summary>
        private static bool LooksLikeMetadataMissing(ICollection<Game> games)
        {
            if (games.Count == 0)
            {
                return false;
            }

            var missingCount = games.Count(g =>
                (g.Genres == null || g.Genres.Count == 0) &&
                (g.Developers == null || g.Developers.Count == 0) &&
                (g.Publishers == null || g.Publishers.Count == 0));

            return (double)missingCount / games.Count > MissingMetadataThreshold;
        }

        private const string MissingMetadataMessage =
            "Most of these games have no genres, developers, or publishers - looks like Playnite's " +
            "\"Download Metadata\" hasn't been run for this library yet (right-click a selection, or " +
            "the Library menu, in Playnite itself). Exporting now will produce a misleadingly sparse " +
            "result.\n\nExport anyway? Choose \"No\" to cancel, run Download Metadata in Playnite, " +
            "then come back and try again.";

        /// <summary>
        /// Dedupes/unions by title (QueueUp's own dedupeImportEntries is only a defensive backstop -
        /// see its doc comment referencing this issue), and drops any title that maps to zero
        /// recognized platforms rather than sending an ownership claim with nothing behind it.
        /// </summary>
        private static List<ImportEntry> BuildImportEntries(IEnumerable<Game> games)
        {
            var byTitle = new Dictionary<string, HashSet<string>>();
            foreach (var game in games)
            {
                if (string.IsNullOrWhiteSpace(game.Name))
                {
                    continue;
                }

                var title = game.Name.Trim();
                if (!byTitle.TryGetValue(title, out var platforms))
                {
                    platforms = new HashSet<string>();
                    byTitle[title] = platforms;
                }

                if (game.Platforms == null)
                {
                    continue;
                }

                foreach (var platform in game.Platforms)
                {
                    var mapped = MapPlatformName(platform?.Name);
                    if (mapped != null)
                    {
                        platforms.Add(mapped);
                    }
                }
            }

            return byTitle
                .Where(kv => kv.Value.Count > 0)
                .Select(kv => new ImportEntry { Title = kv.Key, Platforms = kv.Value.ToList() })
                .ToList();
        }

        /// <summary>
        /// Deterministic fingerprint of a built entries list, for TryAutoSync's unchanged-library
        /// skip (issue #18). Sorts titles and, within each entry, platforms before hashing rather
        /// than trusting BuildImportEntries' own Dictionary/HashSet enumeration order - that order
        /// is incidental (insertion-order-ish in practice, not a documented guarantee), so two
        /// builds over identical underlying library data could otherwise hash differently and defeat
        /// the whole point of comparing hashes across runs.
        /// </summary>
        private static string ComputeEntriesHash(List<ImportEntry> entries)
        {
            var canonical = string.Join("\n", entries
                .OrderBy(e => e.Title, StringComparer.Ordinal)
                .Select(e => e.Title + "|" + string.Join(",", e.Platforms.OrderBy(p => p, StringComparer.Ordinal))));

            using (var sha256 = SHA256.Create())
            {
                var hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical));
                return Convert.ToBase64String(hashBytes);
            }
        }

        private class QueueUpConnectionSettings
        {
            public string Url { get; set; }

            public string Key { get; set; }

            // Defaults true via this initializer, not just "true unless the JSON says otherwise" -
            // Playnite's settings deserializer only overwrites properties actually present in a
            // saved settings blob, so an existing connection saved before Auto-Sync existed still
            // loads as enabled rather than silently defaulting to bool's own false.
            public bool AutoSyncEnabled { get; set; } = true;

            // Null means "never auto-synced yet" - TryAutoSync treats that as no cooldown to wait
            // out, so a freshly-connected user's first OnLibraryUpdated/OnApplicationStarted fires
            // right away instead of waiting a full cooldown window with nothing pushed yet.
            public DateTime? LastAutoSyncAt { get; set; }

            // Hash of the entries built from the library as of the last successful push (manual or
            // auto), from ComputeEntriesHash - issue #18. Null means "nothing pushed yet" (or a
            // settings blob saved before this field existed), which never equals a real hash, so
            // TryAutoSync's unchanged-library check simply falls through to the cooldown/push path
            // the first time, same "no prior state to compare against" reasoning as LastAutoSyncAt.
            public string LastPushedHash { get; set; }
        }

        private class PushOutcome
        {
            public bool IsError { get; set; }

            public string Message { get; set; }
        }

        private class ConnectionCode
        {
            [SerializationPropertyName("url")]
            public string Url { get; set; }

            [SerializationPropertyName("key")]
            public string Key { get; set; }
        }

        private class ImportEntry
        {
            [SerializationPropertyName("title")]
            public string Title { get; set; }

            [SerializationPropertyName("platforms")]
            public List<string> Platforms { get; set; }
        }

        private class ImportRequestBody
        {
            [SerializationPropertyName("entries")]
            public List<ImportEntry> Entries { get; set; }
        }

        private class ImportStartedResponse
        {
            [SerializationPropertyName("consideredCount")]
            public int ConsideredCount { get; set; }
        }

        private class ImportProgressResponse
        {
            [SerializationPropertyName("progress")]
            public ImportProgress Progress { get; set; }
        }

        private class ImportProgress
        {
            [SerializationPropertyName("consideredCount")]
            public int ConsideredCount { get; set; }

            [SerializationPropertyName("matched")]
            public int Matched { get; set; }

            [SerializationPropertyName("unmatched")]
            public int Unmatched { get; set; }

            [SerializationPropertyName("errored")]
            public int Errored { get; set; }

            [SerializationPropertyName("done")]
            public bool Done { get; set; }
        }

        private class ErrorResponse
        {
            [SerializationPropertyName("error")]
            public string Error { get; set; }
        }

        private class GitHubRelease
        {
            [SerializationPropertyName("assets")]
            public List<GitHubReleaseAsset> Assets { get; set; }
        }

        private class GitHubReleaseAsset
        {
            [SerializationPropertyName("name")]
            public string Name { get; set; }
        }

        /// <summary>
        /// Every public property Game exposes, minus MediaFields, keyed by property name - built
        /// via reflection rather than named field-by-field so this automatically tracks whatever
        /// the SDK exposes, including anything added to Game after this was written.
        /// </summary>
        private static Dictionary<string, object> ToExportDictionary(Game game)
        {
            var result = new Dictionary<string, object>();
            foreach (var property in typeof(Game).GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (MediaFields.Contains(property.Name) || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                object value;
                try
                {
                    value = property.GetValue(game);
                }
                catch
                {
                    // A property that throws on read (shouldn't happen for Game, but reflecting
                    // over every property defensively) shouldn't take the whole export down over
                    // one field - same "one bad entry doesn't abort the batch" reasoning used
                    // throughout the QueueUp import work this export feeds into.
                    value = null;
                }

                result[property.Name] = value;
            }

            return result;
        }
    }
}
