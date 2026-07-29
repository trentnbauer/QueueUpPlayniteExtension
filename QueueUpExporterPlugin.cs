using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
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

            var export = games.Select(ToExportDictionary).ToList();
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
        /// dialog since a big non-Steam library can take a while to resolve server-side.
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

            var entries = BuildImportEntries(PlayniteApi.Database.Games);
            if (entries.Count == 0)
            {
                PlayniteApi.Dialogs.ShowMessage(
                    "No games with a recognized platform (PC/Xbox/PlayStation/Switch) found to push.",
                    "Push library to QueueUp");
                return;
            }

            string resultMessage = null;
            var isError = false;

            PlayniteApi.Dialogs.ActivateGlobalProgress(
                progressArgs =>
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
                                resultMessage = DescribeError(response.StatusCode, responseBody);
                                isError = true;
                                return;
                            }

                            var started = Serialization.FromJson<ImportStartedResponse>(responseBody);
                            progressArgs.IsIndeterminate = true;
                            progressArgs.Text = $"Sent {entries.Count} titles ({started.ConsideredCount} considered). Waiting for QueueUp to finish matching...";

                            ImportProgress progress = null;
                            while (true)
                            {
                                if (progressArgs.CancelToken.IsCancellationRequested)
                                {
                                    resultMessage = "Push canceled locally - QueueUp will keep processing what was already sent; check back in QueueUp later.";
                                    return;
                                }

                                Thread.Sleep(1500);

                                var progressResponse = client.GetAsync("api/v1/library/import-playnite/progress").GetAwaiter().GetResult();
                                var progressBody = progressResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                                if (!progressResponse.IsSuccessStatusCode)
                                {
                                    resultMessage = DescribeError(progressResponse.StatusCode, progressBody);
                                    isError = true;
                                    return;
                                }

                                progress = Serialization.FromJson<ImportProgressResponse>(progressBody)?.Progress;
                                if (progress == null)
                                {
                                    continue;
                                }

                                progressArgs.Text = $"Matched {progress.Matched}, unmatched {progress.Unmatched}, errored {progress.Errored} of {progress.ConsideredCount}...";
                                if (progress.Done)
                                {
                                    break;
                                }
                            }

                            resultMessage =
                                $"Push complete: {progress.Matched} matched, {progress.Unmatched} unresolved (review in QueueUp's Profile Settings), " +
                                $"{progress.Errored} errored, out of {progress.ConsideredCount} considered.";
                        }
                    }
                    catch (Exception ex)
                    {
                        resultMessage = $"Push failed: {ex.Message}";
                        isError = true;
                    }
                },
                new GlobalProgressOptions("Pushing library to QueueUp...", true) { IsIndeterminate = false });

            if (resultMessage == null)
            {
                return;
            }

            if (isError)
            {
                PlayniteApi.Dialogs.ShowErrorMessage(resultMessage, "Push library to QueueUp");
            }
            else
            {
                PlayniteApi.Dialogs.ShowMessage(resultMessage, "Push library to QueueUp");
            }
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
                    () => Process.Start(ReleasePageUrl));

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

        private class QueueUpConnectionSettings
        {
            public string Url { get; set; }

            public string Key { get; set; }
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
