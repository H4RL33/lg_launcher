param([string]$Compiler, [switch]$PreviewPromotion)

$ErrorActionPreference = 'Stop'
$repository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$work = Join-Path $repository '.build-work'
$id = [Guid]::NewGuid().ToString('N')
$appId = "LGLauncher.SetupUpgrade.$id"
$root = (New-Item -ItemType Directory -Path (Join-Path $work "setup-update-smoke/$id") -Force).FullName
$programDirectory = (New-Item -ItemType Directory -Path (Join-Path $root 'Custom Install')).FullName
$tempDirectory = (New-Item -ItemType Directory -Path (Join-Path $root 'temp')).FullName
$target = Join-Path $programDirectory 'LG-Launcher.exe'
$uninstaller = Join-Path $programDirectory 'unins000.exe'
$uninstallKey = "Software\Microsoft\Windows\CurrentVersion\Uninstall\${appId}_is1"
$project = Join-Path $repository 'avalonia/LGLauncher.Desktop/LGLauncher.Desktop.csproj'
$installer = Join-Path $repository 'installer/LGLauncher.iss'
$localSdk = Join-Path $work '.avalonia-sdk/dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $localSdk) { $localSdk } else { 'dotnet' }
if (!$Compiler) {
    $localCompiler = Join-Path $work 'inno-toolchain/compiler/ISCC.exe'
    $Compiler = if (Test-Path -LiteralPath $localCompiler) { $localCompiler } else { (Get-Command ISCC.exe -ErrorAction Stop).Source }
}

function New-SetupFixture([string]$Version) {
    $output = Join-Path $root $Version
    $artifacts = Join-Path $root 'artifacts'
    $fileVersion = ([Version]$Version).ToString(3) + '.0'
    & $dotnet publish $project --configuration Release --runtime win-x64 --self-contained false --artifacts-path $artifacts `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true "-p:Version=$Version" `
        "-p:FileVersion=$fileVersion" "-p:SetupFixtureAppId=$appId" `
        -p:IncludeSourceRevisionInInformationalVersion=false -o $output | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not build the $Version setup fixture." }
    $binary = Join-Path $output 'LG Launcher.exe'
    & $Compiler "/DLauncherBinary=$binary" "/DReleaseVersion=$Version" "/DInstallerAppId=$appId" `
        "/DInstallerGroup=LG Launcher Setup Upgrade $id" "/DInstallerAppMutex=Local\$appId" "/O$output" $installer | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Could not compile the $Version setup fixture." }
    return [PSCustomObject]@{ Launcher = $binary; Setup = (Join-Path $output "LG-Launcher-Setup-$Version.exe") }
}

function Invoke-UpdateFixture([string]$Directory, [int]$ExpectedExit) {
    $launcher = Join-Path $Directory 'LG-Launcher.exe'
    $package = Join-Path $Directory 'fixture.exe'
    $report = Join-Path $Directory 'setup.txt'
    Copy-Item -LiteralPath $next.Setup -Destination $package
    $process = Start-Process -FilePath $launcher -ArgumentList @('--verify-update-install', ('"' + $package + '"'),
        ('"' + $report + '"')) -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(25000)) { $process.Kill(); throw 'The Setup worker handoff timed out.' }
    if ($process.ExitCode -ne $ExpectedExit) {
        if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report | Out-Host }
        throw "Unexpected Setup handoff exit code $($process.ExitCode)."
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(65)
    while (!(Test-Path -LiteralPath $report) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path -LiteralPath $report)) { throw 'The Setup worker did not report its result.' }
    Get-Content -LiteralPath $report -Raw
}

$previousCliHome = $env:DOTNET_CLI_HOME
$previousPackages = $env:NUGET_PACKAGES
$previousTemp = $env:TEMP
$previousTmp = $env:TMP
try {
    if ($dotnet -eq $localSdk) {
        if (!$env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $work '.avalonia-cli' }
        if (!$env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $work '.avalonia-nuget' }
    }
    $previous = New-SetupFixture $(if ($PreviewPromotion) { '0.0.2.17' } else { '0.0.1' })
    $next = New-SetupFixture '0.0.2'
    $env:TEMP = $env:TMP = $tempDirectory
    $quietSetupArguments = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-')
    $installLog = Join-Path $root 'install.log'
    $installArguments = $quietSetupArguments + @('/DIR="' + $programDirectory + '"', '/LOG="' + $installLog + '"')
    $install = Start-Process -FilePath $previous.Setup -ArgumentList $installArguments `
        -WindowStyle Hidden -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "The initial Setup fixture exited with code $($install.ExitCode). $installLog" }

    $marker = 'LG-Launcher.install.ini'
    $copy = (New-Item -ItemType Directory -Path (Join-Path $root 'Copied Install')).FullName
    Copy-Item -LiteralPath $target, (Join-Path $programDirectory $marker) -Destination $copy
    $before = (Get-FileHash -LiteralPath $target).Hash
    $failure = Invoke-UpdateFixture $copy 1
    if (!$failure.Contains('registered Windows Setup folder') -or (Get-FileHash -LiteralPath $target).Hash -ne $before) {
        throw 'Updating a copied installation did not preserve the registered launcher.'
    }

    $result = Invoke-UpdateFixture $programDirectory 0
    if ($result -ne 'LG Launcher updated to 0.0.2.' -or
        (Get-FileHash -LiteralPath $target).Hash -ne (Get-FileHash -LiteralPath $next.Launcher).Hash -or
        (Get-ChildItem -LiteralPath $tempDirectory -Directory -Filter 'lg-launcher-setup-update-*')) {
        throw "Setup did not update and restart the registered installation. $result"
    }
    $registry = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
    try {
        $registration = $registry.OpenSubKey($uninstallKey)
        try {
            if (!$registration -or $registration.GetValue('DisplayVersion') -ne '0.0.2' -or
                $registration.GetValue('InstallLocation').TrimEnd('\') -ne $programDirectory) {
                throw 'Setup did not preserve the registered directory and update its version.'
            }
        } finally { if ($registration) { $registration.Dispose() } }
    } finally { $registry.Dispose() }
} finally {
    try {
        if (Test-Path -LiteralPath $uninstaller) {
            $remove = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') `
                -WindowStyle Hidden -Wait -PassThru
            if ($remove.ExitCode -ne 0) { throw "The Setup fixture uninstaller exited with code $($remove.ExitCode)." }
        }
    } finally {
        $env:DOTNET_CLI_HOME = $previousCliHome
        $env:NUGET_PACKAGES = $previousPackages
        $env:TEMP = $previousTemp
        $env:TMP = $previousTmp
    }
}
$shortcutFolder = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) "LG Launcher Setup Upgrade $id"
if ((Test-Path -LiteralPath "HKCU:\$uninstallKey") -or (Test-Path -LiteralPath (Join-Path $shortcutFolder 'LG Launcher.lnk')) -or
    (Test-Path -LiteralPath $target)) {
    throw 'The Setup fixture left its registration, shortcut, or executable installed.'
}
Write-Output "Windows Setup custom-directory upgrade, restart, and copied-install rejection passed. $root"
