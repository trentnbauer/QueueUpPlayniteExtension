function GetMainMenuItems
{
    param(
        $getMainMenuItemsArgs
    )

    $menuItem = New-Object Playnite.SDK.Plugins.ScriptMainMenuItem
    $menuItem.Description = "Export library to QueueUp (JSON)"
    $menuItem.FunctionName = "ExportQueueUpLibrary"
    $menuItem.MenuSection = "@QueueUp"
    return $menuItem
}

function ExportQueueUpLibrary
{
    param(
        $scriptMainMenuItemActionArgs
    )

    $countInput = $PlayniteApi.Dialogs.SelectString(
        "How many games to export? Leave blank to export the full library.",
        "QueueUp Export",
        "10")

    if (-not $countInput.Result)
    {
        return
    }

    $games = $PlayniteApi.Database.Games | Sort-Object Name

    $limitText = "$($countInput.SelectedString)".Trim()
    if ($limitText)
    {
        $limit = 0
        if ([int]::TryParse($limitText, [ref]$limit) -and $limit -gt 0)
        {
            $games = $games | Select-Object -First $limit
        }
    }

    # Every property Playnite's Game object exposes, except binary/media file references (icon,
    # cover art, background image) - the point of this export is a full view of what QueueUp could
    # match against, not a curated subset. Using reflection (Select-Object -Property *) rather than
    # naming fields one by one means this automatically picks up whatever the SDK exposes, including
    # anything added after this script was written.
    $MEDIA_FIELDS = @('Icon', 'CoverImage', 'BackgroundImage')
    $DATE_FIELDS = @('Added', 'Modified', 'LastActivity', 'LastSizeScanDate')
    $export = foreach ($game in $games)
    {
        $obj = $game | Select-Object -Property * -ExcludeProperty $MEDIA_FIELDS
        # PowerShell 5.1's ConvertTo-Json renders [DateTime] as the legacy ASP.NET "/Date(ms)/"
        # wire format, not ISO 8601 - confirmed against real export output, not a guess. Force these
        # back to a plain parseable string. ReleaseDate isn't a plain DateTime (it's a
        # Playnite.SDK.Models.ReleaseDate struct) - its Day/Month/Year already come through as clean
        # ints, which is fine as-is, so it's left alone here.
        foreach ($dateField in $DATE_FIELDS)
        {
            if ($obj.$dateField)
            {
                $obj.$dateField = $obj.$dateField.ToString("o")
            }
        }
        $obj
    }
    $export = @($export)
    $json = $export | ConvertTo-Json -Depth 8

    $savePath = $PlayniteApi.Dialogs.SaveFile("JSON file|*.json")
    if (-not $savePath)
    {
        $savePath = Join-Path $env:TEMP "queueup-library-export.json"
    }

    [System.IO.File]::WriteAllText($savePath, $json)
    $PlayniteApi.Dialogs.ShowMessage("Exported $($export.Count) games to:`n$savePath")
}
