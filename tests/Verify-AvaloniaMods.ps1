param([Parameter(Mandatory = $true)][string]$Launcher, [string]$Archives, [string]$Catalog)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Launcher).Path
$root = New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot ('..\.build-work\mods-smoke\' + [Guid]::NewGuid().ToString('N'))) -Force
New-Item -ItemType File -Path (Join-Path $root.FullName 'mod-fixture') | Out-Null
if ($Catalog) { Copy-Item -LiteralPath (Resolve-Path -LiteralPath $Catalog).Path -Destination (Join-Path $root.FullName 'upstream-catalog.json') }
$arguments = @('--verify-mods', ('"' + $root.FullName + '"'))
if ($Archives) { $arguments += ('"' + (Resolve-Path -LiteralPath $Archives).Path + '"') }
$process = Start-Process -FilePath $source -ArgumentList $arguments -PassThru
if (!$process.WaitForExit(40000)) { $process.Kill(); throw 'The mod fixture runner timed out.' }
$report = Join-Path $root.FullName 'mod-results.txt'
if (!(Test-Path -LiteralPath $report)) { throw 'The mod fixture runner did not write a report.' }
Get-Content -LiteralPath $report
if ($process.ExitCode -ne 0) { throw 'The mod fixtures failed.' }
