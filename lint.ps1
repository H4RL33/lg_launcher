#Requires -Version 7.0
[CmdletBinding()]
param(
    [string[]]$ChangedPath = @(),
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$sourceExtensions = @('.cs', '.axaml', '.ps1', '.psm1', '.psd1', '.iss', '.csproj', '.props', '.targets', '.manifest')
$xmlExtensions = @('.axaml', '.csproj', '.props', '.targets', '.manifest')
$scriptExtensions = @('.ps1', '.psm1', '.psd1')
$ignoredDirectories = @('.git', '.build-work', 'build', 'bin', 'obj', '__pycache__')

function Get-LintRepositoryFiles([string]$Directory) {
    foreach ($item in Get-ChildItem -LiteralPath $Directory -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        if ($item.PSIsContainer) {
            if ($item.Name -notin $ignoredDirectories) {
                Get-LintRepositoryFiles -Directory $item.FullName
            }
        } else {
            $item
        }
    }
}

function Write-LintDiagnostic([string]$Path, [int]$Line, [string]$Message) {
    Write-Host "${Path}(${Line}): $Message"
}

function Invoke-LintDotnet([string[]]$CommandArguments) {
    & $dotnet @CommandArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($CommandArguments[0]) failed with exit code $LASTEXITCODE." }
}

Write-Host 'Checking source whitespace, PowerShell syntax, and XML markup...'
$repositoryFiles = @(Get-LintRepositoryFiles -Directory $root)
$files = @($repositoryFiles | Where-Object Extension -In $sourceExtensions)
$relativeFiles = @($files | ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName) })
$xmlSettings = [Xml.XmlReaderSettings]::new()
$xmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
$xmlSettings.XmlResolver = $null
$issues = 0
foreach ($file in $files) {
    $path = [IO.Path]::GetRelativePath($root, $file.FullName)
    $lineNumber = 0
    foreach ($line in [IO.File]::ReadLines($file.FullName)) {
        $lineNumber++
        if ($line -match '^ *\t') {
            Write-LintDiagnostic $path $lineNumber 'Indentation must use spaces.'
            $issues++
        }
        if ($line -match '[ \t]+$') {
            Write-LintDiagnostic $path $lineNumber 'Remove trailing whitespace.'
            $issues++
        }
    }

    if ($file.Extension -in $scriptExtensions) {
        $tokens = $null
        $parseErrors = $null
        [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
        foreach ($parseError in $parseErrors) {
            Write-LintDiagnostic $path $parseError.Extent.StartLineNumber $parseError.Message
            $issues++
        }
    } elseif ($file.Extension -in $xmlExtensions) {
        $reader = [Xml.XmlReader]::Create($file.FullName, $xmlSettings)
        try {
            while ($reader.Read()) { }
        } catch [Xml.XmlException] {
            Write-LintDiagnostic $path $_.Exception.LineNumber $_.Exception.Message
            $issues++
        } finally {
            $reader.Dispose()
        }
    }
}
if ($issues -gt 0) { throw "Found $issues source lint issue(s)." }

Write-Host 'Checking documentation links, source articles, and the UI catalog...'
$documentationIssues = @(& (Join-Path $root 'scripts/Test-DeveloperDocumentation.ps1') -Root $root -RepositoryFiles $repositoryFiles)
foreach ($issue in $documentationIssues) { Write-Host $issue }
if ($documentationIssues.Count -gt 0) { throw "Found $($documentationIssues.Count) documentation lint issue(s)." }

$changedCSharp = @(
    foreach ($path in $ChangedPath) {
        $fullPath = [IO.Path]::GetFullPath($path, $root)
        $relativePath = [IO.Path]::GetRelativePath($root, $fullPath)
        if ($relativePath -notin $relativeFiles) { throw "ChangedPath must name an existing authored source file: $path" }
        if ([IO.Path]::GetExtension($fullPath) -ieq '.cs') { $relativePath }
    }
)

$executable = if ($IsWindows) { 'dotnet.exe' } else { 'dotnet' }
$localSdk = Join-Path $root ".build-work/.avalonia-sdk/$executable"
$previousCliHome = $env:DOTNET_CLI_HOME
$previousNuGetPackages = $env:NUGET_PACKAGES
if (Test-Path -LiteralPath $localSdk -PathType Leaf) {
    $dotnet = $localSdk
    if (!$env:DOTNET_CLI_HOME) { $env:DOTNET_CLI_HOME = Join-Path $root '.build-work/.avalonia-cli' }
    if (!$env:NUGET_PACKAGES) { $env:NUGET_PACKAGES = Join-Path $root '.build-work/.avalonia-nuget' }
} else {
    $dotnet = (Get-Command $executable -CommandType Application -TotalCount 1 -ErrorAction Stop).Source
}

Push-Location -LiteralPath $root
try {
    if ($changedCSharp.Count -gt 0) {
        Write-Host 'Checking C# formatting for changed files with dotnet format...'
        Invoke-LintDotnet (@('format', 'whitespace', '.', '--folder', '--verify-no-changes', '--include') + $changedCSharp)
    }

    Write-Host 'Compiling Avalonia C# and XAML with warnings treated as errors...'
    $restore = @()
    if ($NoRestore) { $restore += '--no-restore' }
    foreach ($project in @('LGLauncher.Desktop', 'LGLauncher.Gallery')) {
        $projectFile = "avalonia/$project/$project.csproj"
        Invoke-LintDotnet (@('build', $projectFile, '--configuration', 'Release', '--nologo', '--warnaserror') + $restore)
    }
} finally {
    Pop-Location
    $env:DOTNET_CLI_HOME = $previousCliHome
    $env:NUGET_PACKAGES = $previousNuGetPackages
}

Write-Host 'Lint checks passed.'
