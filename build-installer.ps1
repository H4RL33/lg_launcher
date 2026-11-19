param(
    [Parameter(Mandatory = $true)][string]$Launcher,
    [string]$Compiler,
    [switch]$Smoke
)

$ErrorActionPreference = 'Stop'
$binary = Get-Item -LiteralPath (Resolve-Path -LiteralPath $Launcher).Path
$version = $binary.VersionInfo.ProductVersion
if ($binary.Extension -ne '.exe' -or $version -notmatch '^\d+\.\d+\.\d+$') {
    throw 'Select a stable LG Launcher Windows executable.'
}
if (!$Compiler) {
    $portable = Join-Path $PSScriptRoot '.build-work\inno-toolchain\compiler\ISCC.exe'
    $Compiler = if (Test-Path -LiteralPath $portable) { $portable } else { (Get-Command ISCC.exe -ErrorAction Stop).Source }
}
$compilerPath = (Resolve-Path -LiteralPath $Compiler).Path
$scriptPath = Join-Path $PSScriptRoot 'installer\LGLauncher.iss'
$filename = "LG-Launcher-Setup-$version.exe"

function Build-Setup([string]$Directory, [string[]]$Definitions = @()) {
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    & $compilerPath "/DLauncherBinary=$($binary.FullName)" "/DReleaseVersion=$version" "/O$Directory" @Definitions $scriptPath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compiler exited with code $LASTEXITCODE." }
    (Get-Item -LiteralPath (Join-Path $Directory $filename)).FullName
}

if ($Smoke) {
    $id = [Guid]::NewGuid().ToString('N')
    $root = (New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot ".build-work\installer-smoke\$id") -Force).FullName
    $appId = "LGLauncher.SetupCheck.$id"
    $group = "LG Launcher Setup Check $id"
    $testSetup = Build-Setup $root @("/DInstallerAppId=$appId", "/DInstallerGroup=$group")
    $installDir = Join-Path $root 'installed'
    $log = Join-Path $root 'install.log'
    $install = Start-Process -FilePath $testSetup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',
        ('/DIR="' + $installDir + '"'), ('/LOG="' + $log + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Test setup failed with code $($install.ExitCode). $log" }
    $installed = Join-Path $installDir 'LG-Launcher.exe'
    $uninstaller = Join-Path $installDir 'unins000.exe'
    $uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${appId}_is1"
    $programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
    $shortcut = Join-Path $programs "$group\LG Launcher.lnk"
    if (!(Test-Path -LiteralPath $uninstaller) -or !(Test-Path -LiteralPath $uninstallKey) -or !(Test-Path -LiteralPath $shortcut)) {
        throw 'Test setup did not register the app, shortcut, and uninstaller.'
    }
    if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $binary.FullName).Hash) {
        throw 'The installed executable differs from the stable build.'
    }
    if ((Get-ItemPropertyValue -LiteralPath $uninstallKey -Name DisplayVersion) -ne $version) { throw 'Apps & Features shows the wrong version.' }
    $testProfile = (New-Item -ItemType Directory -Path (Join-Path $root 'test-profile') -Force).FullName
    [IO.File]::WriteAllText((Join-Path $testProfile 'library.json'), '{"UsageNoticeAccepted":true}')
    $preview = Join-Path $root 'installed-launcher.png'
    $launch = Start-Process -FilePath $installed -ArgumentList @('--verify-existing',
        ('"' + $testProfile + '"'), ('"' + $preview + '"')) -WindowStyle Hidden -PassThru
    if (!$launch.WaitForExit(30000)) { $launch.Kill(); throw 'The installed launcher did not finish its smoke test.' }
    if ($launch.ExitCode -ne 0 -or !(Test-Path -LiteralPath $preview)) { throw 'The installed launcher did not open successfully.' }
    $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
    if ($uninstall.ExitCode -ne 0 -or (Test-Path -LiteralPath $uninstallKey) -or (Test-Path -LiteralPath $installed) -or
        (Test-Path -LiteralPath $shortcut)) { throw 'The test uninstaller did not remove the app registration and files.' }
    Write-Output 'Install, Start menu shortcut, Apps & Features registration, stable executable hash, launch, and uninstall passed.'
}

Build-Setup (Join-Path $PSScriptRoot 'build')
