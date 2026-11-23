param([Parameter(Mandatory = $true)][string]$Launcher)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Launcher).Path
$root = New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot ('..\.build-work\update-smoke\' + [Guid]::NewGuid().ToString('N'))) -Force

foreach ($case in @('install', 'rollback', 'activation', 'timeout', 'missing-message')) {
    $rollback = $case -in 'rollback', 'activation', 'timeout'
    $folder = New-Item -ItemType Directory -Path (Join-Path $root.FullName $case)
    $target = Join-Path $folder.FullName 'update-test.exe'
    $fixture = Join-Path $folder.FullName 'fixture.exe'
    $report = Join-Path $folder.FullName "$case.txt"
    Copy-Item -LiteralPath $source -Destination $target
    Copy-Item -LiteralPath $source -Destination $fixture
    # Alter the copy's hash without changing the Windows PE or bundled application.
    $stream = [IO.File]::Open($fixture, [IO.FileMode]::Append)
    try { $stream.WriteByte(92) } finally { $stream.Dispose() }
    $expected = if ($rollback) { $source } else { $fixture }
    $process = Start-Process -FilePath $target -ArgumentList @('--verify-update-install', ('"' + $fixture + '"'), ('"' + $report + '"')) -PassThru
    if (!$process.WaitForExit(25000)) { $process.Kill(); throw "The update $case handoff timed out." }
    if ($process.ExitCode -ne 0) {
        if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
        throw "The update $case handoff failed."
    }
    $deadline = [DateTime]::UtcNow.AddSeconds(50)
    while (!(Test-Path -LiteralPath $report) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    if (!(Test-Path -LiteralPath $report)) { throw "The update $case process did not relaunch." }
    $actualHash = (Get-FileHash -LiteralPath $target).Hash
    if ($actualHash -ne (Get-FileHash -LiteralPath $expected).Hash) { throw "The update $case left the wrong executable installed." }
    $message = Get-Content -LiteralPath $report -Raw
    $prefix = if ($rollback) { 'The update could not be installed.' }
        elseif ($case -eq 'missing-message') { 'Restart was acknowledged without a result message.' }
        else { 'LG Launcher updated to ' }
    if (!$message.StartsWith($prefix)) { throw "The update $case did not report its outcome. $message" }
    if (Get-ChildItem -LiteralPath $folder.FullName -Force -Directory -Filter '.lg-launcher-update-*') {
        throw "The update $case did not clean up the staging folder."
    }
    Write-Output "Windows update $case and relaunch passed."
}
