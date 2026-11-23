param(
    [Parameter(Mandatory = $true)][string]$Launcher,
    [string]$Compiler,
    [switch]$Smoke
)

$ErrorActionPreference = 'Stop'
$binary = Get-Item -LiteralPath (Resolve-Path -LiteralPath $Launcher).Path
$version = $binary.VersionInfo.ProductVersion
$parsedVersion = $null
if ($binary.Extension -ne '.exe' -or ![Version]::TryParse($version, [ref]$parsedVersion) -or
    $parsedVersion.Build -lt 0 -or $parsedVersion.Revision -eq 0 -or $parsedVersion.Revision -gt 65534 -or
    $version -cne $parsedVersion.ToString()) {
    throw 'Select a packaged LG Launcher Windows executable.'
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
    $testSetup = Build-Setup $root @("/DInstallerAppId=$appId", "/DInstallerGroup=$group", "/DInstallerAppMutex=Local\$appId")
    $localAppData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    $installDir = Join-Path $localAppData "Programs\$group"
    $log = Join-Path $root 'install.log'
    $install = Start-Process -FilePath $testSetup -ArgumentList @('/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART','/SP-',
        ('/LOG="' + $log + '"')) -WindowStyle Hidden -Wait -PassThru
    if ($install.ExitCode -ne 0) { throw "Test setup failed with code $($install.ExitCode). $log" }
    $installed = Join-Path $installDir 'LG-Launcher.exe'
    $marker = Join-Path $installDir 'LG-Launcher.install.ini'
    $uninstaller = Join-Path $installDir 'unins000.exe'
    $uninstallKey = "Software\Microsoft\Windows\CurrentVersion\Uninstall\${appId}_is1"
    $programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
    $shortcut = Join-Path $programs "$group\LG Launcher.lnk"
    $userRegistry = [Microsoft.Win32.RegistryKey]::OpenBaseKey('CurrentUser', 'Registry64')
    try {
        try {
            $registration = $userRegistry.OpenSubKey($uninstallKey)
            try {
                if ($null -eq $registration -or $registration.GetValue('DisplayVersion') -ne $version) {
                    throw 'Apps & Features does not show the correct per-user registration.'
                }
            } finally {
                if ($registration) { $registration.Dispose() }
            }
            if (!(Test-Path -LiteralPath $uninstaller) -or !(Test-Path -LiteralPath $marker) -or !(Test-Path -LiteralPath $shortcut)) {
                throw 'Test setup did not install the launcher metadata, user shortcut, and uninstaller.'
            }
            if (!(Select-String -LiteralPath $marker -Pattern '^Scope=User$' -Quiet)) {
                throw 'Test setup did not mark the launcher as a per-user installation.'
            }
            $link = (New-Object -ComObject WScript.Shell).CreateShortcut($shortcut)
            if ($link.TargetPath -ne $installed -or $link.WorkingDirectory -ne $installDir) {
                throw 'The Start menu shortcut does not point to the per-user installation.'
            }
            if ((Get-FileHash -LiteralPath $installed).Hash -ne (Get-FileHash -LiteralPath $binary.FullName).Hash) {
                throw 'The installed executable differs from the packaged build.'
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
        if ($userRegistry.OpenSubKey($uninstallKey) -or (Test-Path -LiteralPath $installed) -or
            (Test-Path -LiteralPath $marker) -or (Test-Path -LiteralPath $shortcut)) {
            throw 'The test uninstaller did not remove the per-user registration, files, and shortcut.'
        }
        if ((Get-FileHash -LiteralPath $profileFile).Hash -ne $profileHash) {
            throw 'The fixture profile changed during launch or uninstall.'
        }
    } finally {
        $userRegistry.Dispose()
    }
    Write-Output 'Per-user installation, Start menu shortcut, user registration, isolated launch, and uninstall passed.'
}

Build-Setup (Join-Path $PSScriptRoot 'build')
