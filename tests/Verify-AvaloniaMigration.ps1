param(
    [string]$Launcher = (Join-Path $PSScriptRoot '..\build\LG Launcher.exe'),
    [string]$ExistingLibrary,
    [string]$PortraitSample,
    [switch]$ModsOnly,
    [switch]$WebOnly
)

$ErrorActionPreference = 'Stop'
$reports = New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot '..\.build-work\smoke') -Force
$run = Join-Path $reports.FullName ([Guid]::NewGuid().ToString('N'))
$fixture = Join-Path $run 'legacy-library'
$custom = Join-Path $fixture 'artwork\custom'
New-Item -ItemType Directory -Path $custom -Force | Out-Null
$exe = (Resolve-Path -LiteralPath $Launcher).Path
$artworkId = '94144886e22a4c8f8b32b5a782a4b7bc'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\assets\welcome.jpg') -Destination (Join-Path $custom "$artworkId.img")
$audioSamples = New-Item -ItemType Directory -Path (Join-Path $fixture 'theme-audio-samples')
Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'fixtures\theme-audio') -File | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $audioSamples.FullName
}
$portraits = (New-Item -ItemType Directory -Path (Join-Path $fixture 'artwork\portraits')).FullName
if (!$PortraitSample) { $PortraitSample = Join-Path $PSScriptRoot '..\assets\welcome.jpg' }
Copy-Item -LiteralPath $PortraitSample -Destination (Join-Path $portraits '2380050.jpg')
foreach ($appId in @(620, 1623730, 3489700)) {
    $sample = Join-Path (Split-Path -Parent $PortraitSample) "$appId.jpg"
    if (!(Test-Path -LiteralPath $sample)) { $sample = $PortraitSample }
    Copy-Item -LiteralPath $sample -Destination (Join-Path $portraits "$appId.jpg")
}
New-Item -ItemType File -Path (Join-Path $fixture 'New Discovery.exe'), (Join-Path $fixture 'Ignored.exe') | Out-Null
$legacy = @{
    UsageNoticeAccepted = $true
    Preferences = @{ InterfaceFont = 'Segoe UI'; StartMinimised = $true; CloseToTray = $true; CloseWhenGameLaunched = $true; UpdateFeedUrl = 'https://example.net/releases/latest.json' }
    Folder = $fixture
    Collections = @(@{ Id = 'retro'; Name = 'Retro' }, @{ Id = 'racing'; Name = 'Driving'; GenreFilters = @('Racing') })
    IgnoredPaths = @((Join-Path $fixture 'Ignored.exe'))
    Games = @(
        @{ Id = 'manual'; Title = 'Legacy Game'; Path = (Join-Path $fixture 'Legacy.exe'); Arguments = '--windowed'; SteamId = 2380050; PlaySeconds = 4199; LastPlayedUtc = '/Date(1763589600000)/'; CollectionIds = @('retro'); CustomArtworkId = $artworkId; ArtworkPosition = @{ X = 0.71; Y = 0.39; Zoom = 2.4 }; Genres = @('Racing', 'Simulation'); ManualGenres = $true },
        @{ Id = 'prezoom'; Title = 'Before Zoom'; Path = (Join-Path $fixture 'Before.exe'); ArtworkPosition = @{ X = 0.67; Y = 0.29 } }
    )
}
Set-Content -LiteralPath (Join-Path $fixture 'library.json') -Value (ConvertTo-Json -InputObject $legacy -Depth 8) -Encoding Ascii

function Test-Library([string]$Data, [string]$Mode, [string]$Preview) {
    $before = (Get-FileHash -LiteralPath (Join-Path $Data 'library.json')).Hash
    $timeoutSeconds = if ($Mode -eq '--verify-migration') { 120 } else { 45 }
    $report = "$Preview.error.txt"
    Write-Output "Running $Mode (timeout: ${timeoutSeconds}s)."
    $process = Start-Process -FilePath $exe -ArgumentList @($Mode, ('"' + $Data + '"'), ('"' + $Preview + '"')) -PassThru -WindowStyle Hidden
    if (!$process.WaitForExit($timeoutSeconds * 1000)) {
        if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
        if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
        $captures = @(Get-ChildItem -LiteralPath (Split-Path -Parent $Preview) -Filter '*.png' -File |
            Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 5 -ExpandProperty Name)
        throw "Avalonia $Mode timed out after ${timeoutSeconds}s. Recent captures: $($captures -join ', ')."
    }
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
        throw "Avalonia $Mode exited with $($process.ExitCode)."
    }
    if (!(Test-Path -LiteralPath $Preview)) { throw 'The Avalonia launcher did not render a screenshot.' }
    if ($Mode -eq '--verify-existing' -and $before -ne (Get-FileHash -LiteralPath (Join-Path $Data 'library.json')).Hash) {
        throw 'Opening the legacy data changed the library file.'
    }
}

if ($WebOnly) {
    Test-Library $fixture '--verify-web' (Join-Path $run 'web.png')
    Write-Output "Web UI verification passed. $run"
    return
}

if ($ModsOnly) {
    Test-Library $fixture '--verify-mods-ui' (Join-Path $run 'mods.png')
    Write-Output "Mods UI verification passed. $run"
    return
}

$readOnlyPreview = Join-Path $run 'legacy-read-only.png'
Test-Library $fixture '--verify-existing' $readOnlyPreview
$preview = Join-Path $run 'legacy.png'
Test-Library $fixture '--verify-migration' $preview
$saved = Get-Content -LiteralPath (Join-Path $fixture 'library.json') -Raw | ConvertFrom-Json
if (!$saved.Preferences.StartMinimised -or !$saved.Preferences.CloseToTray -or !$saved.Preferences.CloseWhenGameLaunched -or
    'Racing' -notin ($saved.Games | Where-Object Id -EQ 'manual').Genres -or
    'Racing' -notin ($saved.Collections | Where-Object Id -EQ 'racing').GenreFilters) {
    throw 'Game genres, a dynamic collection, or app behaviour settings were lost when the library was saved.'
}
if ($saved.Preferences.PSObject.Properties['UpdateFeedUrl']) {
    throw 'The obsolete custom update feed survived the settings save.'
}
Write-Output "Legacy migration and save round-trip passed. $preview"

if (!$saved.Preferences.DisableHardwareRendering) {
    throw 'The software rendering preference was not saved for the next launch.'
}
$softwarePreview = Join-Path $run 'app-behaviour-software.png'
Test-Library $fixture '--verify-existing' $softwarePreview
Write-Output "Software rendering startup passed. $softwarePreview"

if ($ExistingLibrary) {
    $copy = Join-Path $run 'existing-library'
    New-Item -ItemType Directory -Path $copy | Out-Null
    try {
        foreach ($item in @('library.json', 'library.json.bak', 'artwork')) {
            $source = Join-Path $ExistingLibrary $item
            if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination $copy -Recurse }
        }
        $preview = Join-Path $run 'existing.png'
        Test-Library $copy '--verify-existing' $preview
        Write-Output "Existing library copy opened without a write. $preview"
    } finally {
        $resolvedCopy = (Resolve-Path -LiteralPath $copy).Path
        $resolvedRun = (Resolve-Path -LiteralPath $run).Path
        if ([IO.Path]::GetDirectoryName($resolvedCopy) -eq $resolvedRun) {
            Remove-Item -LiteralPath $resolvedCopy -Recurse -Force
        }
    }
}
