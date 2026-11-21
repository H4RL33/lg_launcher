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
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Run installer smoke from an elevated PowerShell session to test Program Files, the shared Start menu, and HKLM.'
    }
    $id = [Guid]::NewGuid().ToString('N')
    $root = (New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot ".build-work\installer-smoke\$id") -Force).FullName
    $appId = "LGLauncher.SetupCheck.$id"
    $group = "LG Launcher Setup Check $id"
    $testSetup = Build-Setup $root @("/DInstallerAppId=$appId", "/DInstallerGroup=$group")
    $programFiles = [Environment]::GetEnvironmentVariable('ProgramW6432')
    if (!$programFiles) { $programFiles = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles) }
    $installDir = Join-Path $programFiles $group
    $log = Join-Path $root 'install.log'
    $install = Start-Process -FilePath $testSetup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',
        ('/LOG="' + $log + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Test setup failed with code $($install.ExitCode). $log" }
    $installed = Join-Path $installDir 'LG-Launcher.exe'
    $marker = Join-Path $installDir 'LG-Launcher.install.ini'
    $uninstaller = Join-Path $installDir 'unins000.exe'
    $uninstallKey = "Software\Microsoft\Windows\CurrentVersion\Uninstall\${appId}_is1"
    $programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::CommonPrograms)
    $shortcut = Join-Path $programs "$group\LG Launcher.lnk"
    $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    try {
        try {
            $registration = $machine.OpenSubKey($uninstallKey)
            try {
                if ($null -eq $registration -or $registration.GetValue('DisplayVersion') -ne $version) {
                    throw 'Apps & Features does not show the correct machine-wide registration.'
                }
            } finally {
                if ($registration) { $registration.Dispose() }
            }
            if (!(Test-Path -LiteralPath $uninstaller) -or !(Test-Path -LiteralPath $marker) -or !(Test-Path -LiteralPath $shortcut)) {
                throw 'Test setup did not install the launcher metadata, shared shortcut, and uninstaller.'
            }
            $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
            if ($link.TargetPath -ne $installed -or $link.WorkingDirectory -ne $installDir) {
                throw 'The shared Start menu shortcut does not point to the Program Files installation.'
            }
            if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $binary.FullName).Hash) {
                throw 'The installed executable differs from the stable build.'
            }
            $testProfile = (New-Item -ItemType Directory -Path (Join-Path $root 'test-profile') -Force).FullName
            $profileFile = Join-Path $testProfile 'library.json'
            [IO.File]::WriteAllText($profileFile, '{"UsageNoticeAccepted":true,"Preferences":{"AutomaticUpdates":true}}')
            $profileHash = (Get-FileHash -LiteralPath $profileFile).Hash
            $preview = Join-Path $root 'installed-launcher.png'
            $launch = Start-Process -FilePath $installed -ArgumentList @('--verify-setup',
                ('"' + $testProfile + '"'), ('"' + $preview + '"')) -WindowStyle Hidden -PassThru
            if (!$launch.WaitForExit(30000)) { $launch.Kill(); $launch.WaitForExit(); throw 'The installed launcher did not finish its smoke test.' }
            if ($launch.ExitCode -ne 0 -or !(Test-Path -LiteralPath $preview)) {
                if (Test-Path -LiteralPath "$preview.error.txt") { Get-Content -LiteralPath "$preview.error.txt" | Out-Host }
                throw 'The installed launcher did not complete the isolated profile and update checks.'
            }
        } finally {
            if (Test-Path -LiteralPath $uninstaller) {
                $uninstall = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART') -WindowStyle Hidden -Wait -PassThru
                if ($uninstall.ExitCode -ne 0) { throw "Test uninstall failed with code $($uninstall.ExitCode)." }
            }
        }
        if ($machine.OpenSubKey($uninstallKey) -or (Test-Path -LiteralPath $installed) -or
            (Test-Path -LiteralPath $marker) -or (Test-Path -LiteralPath $shortcut)) {
            throw 'The test uninstaller did not remove the machine registration, files, and shortcut.'
        }
        if ((Get-FileHash -LiteralPath $profileFile).Hash -ne $profileHash) {
            throw 'The fixture profile changed during launch or uninstall.'
        }
    } finally {
        $machine.Dispose()
    }
    Write-Output 'Program Files installation, shared Start menu shortcut, machine registration, isolated launch, and uninstall passed.'
}

Build-Setup (Join-Path $PSScriptRoot 'build')
