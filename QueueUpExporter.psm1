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

    $export = foreach ($game in $games)
    {
        [PSCustomObject]@{
            Id                = $game.Id.ToString()
            Name              = $game.Name
            # Platforms/Source Name is the user-editable display label; SpecificationId is the
            # stable slug (e.g. "sony_playstation5", "pc_windows") - export both since QueueUp's
            # import will need the stable id, not just the label.
            Platforms         = @($game.Platforms | Where-Object { $_ } | ForEach-Object {
                [PSCustomObject]@{ Name = $_.Name; SpecificationId = $_.SpecificationId }
            })
            Source            = if ($game.Source) { $game.Source.Name } else { $null }
            Roms              = @($game.Roms | Where-Object { $_ } | ForEach-Object { $_.Name })
            Genres            = @($game.Genres | Where-Object { $_ } | ForEach-Object { $_.Name })
            Developers        = @($game.Developers | Where-Object { $_ } | ForEach-Object { $_.Name })
            Publishers        = @($game.Publishers | Where-Object { $_ } | ForEach-Object { $_.Name })
            ReleaseDate       = if ($game.ReleaseDate) { $game.ReleaseDate.ToString() } else { $null }
            Playtime          = $game.Playtime
            LastActivity      = if ($game.LastActivity) { $game.LastActivity.ToString("o") } else { $null }
            IsInstalled       = $game.IsInstalled
            InstallDirectory  = $game.InstallDirectory
            CompletionStatus  = if ($game.CompletionStatus) { $game.CompletionStatus.Name } else { $null }
            Added             = if ($game.Added) { $game.Added.ToString("o") } else { $null }
        }
    }

    $export = @($export)
    $json = $export | ConvertTo-Json -Depth 5

    $savePath = $PlayniteApi.Dialogs.SaveFile("JSON file|*.json")
    if (-not $savePath)
    {
        $savePath = Join-Path $env:TEMP "queueup-library-export.json"
    }

    [System.IO.File]::WriteAllText($savePath, $json)
    $PlayniteApi.Dialogs.ShowMessage("Exported $($export.Count) games to:`n$savePath")
}
