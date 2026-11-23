[CmdletBinding()]
param([switch]$Test, [switch]$Smoke, [switch]$NoRestore)

$ErrorActionPreference = 'Stop'
$output = Join-Path $PSScriptRoot 'build'
$work = Join-Path $PSScriptRoot '.build-work'
New-Item -ItemType Directory -Path $work -Force | Out-Null

$localSdk = Join-Path $work '.avalonia-sdk\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
if ($dotnet -eq $localSdk) {
    if (!$env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $work '.avalonia-cli' }
    if (!$env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $work '.avalonia-nuget' }
}
$restore = @()
if ($NoRestore) { $restore = @('--no-restore') }

if ($Test) {
    $project = Join-Path $PSScriptRoot 'tests\LGLauncher.CoreTests.csproj'
    $artifacts = Join-Path $work 'core-tests'
    & $dotnet run --project $project --configuration Release --artifacts-path $artifacts @restore
    exit $LASTEXITCODE
}

$project = Join-Path $PSScriptRoot 'avalonia\LGLauncher.Desktop\LGLauncher.Desktop.csproj'
$stage = Join-Path $work 'publish'
& $dotnet publish $project -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded -o $stage @restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$candidate = Join-Path $stage 'LG Launcher.exe'
if ($Smoke) {
    & (Join-Path $PSScriptRoot 'tests\Verify-AvaloniaMigration.ps1') -Launcher $candidate
    & (Join-Path $PSScriptRoot 'tests\Verify-AvaloniaUpdate.ps1') -Launcher $candidate
}
New-Item -ItemType Directory -Path $output -Force | Out-Null
$exe = Join-Path $output 'LG Launcher.exe'
if (Test-Path -LiteralPath $exe) {
    [IO.File]::Replace($candidate, $exe, (Join-Path $stage 'previous.exe'))
} else {
    [IO.File]::Move($candidate, $exe)
}
Write-Output $exe
