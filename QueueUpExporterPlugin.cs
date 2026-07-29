using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Playnite.SDK;
using Playnite.SDK.Data;
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

        public override Guid Id { get; } = Guid.Parse("ffbe3993-60a1-49e6-a228-4cf7a130ce44");

        public QueueUpExporterPlugin(IPlayniteAPI api) : base(api)
        {
        }

        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            yield return new MainMenuItem
            {
                Description = "Export library to QueueUp (JSON)",
                MenuSection = "@QueueUp",
                Action = ExportLibrary,
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
