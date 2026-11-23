#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Root,
    [Parameter(Mandatory)][System.IO.FileInfo[]]$RepositoryFiles
)

function Get-MarkdownAnchors([string]$Html, [System.Text.RegularExpressions.MatchCollection]$Headings) {
    $anchors = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($heading in $Headings) {
        $text = [Net.WebUtility]::HtmlDecode(($heading.Groups['text'].Value -replace '<[^>]+>', '')).Trim()
        # GitHub's heading fragments omit punctuation such as dots in source file headings.
        $slug = $text.ToLowerInvariant() -replace '[^\p{L}\p{M}\p{N}_ -]', '' -replace ' ', '-'
        if (!$slug) { continue }
        $id = $slug
        $suffix = 0
        while (!$anchors.Add($id)) {
            $suffix++
            $id = "$slug-$suffix"
        }
    }
    foreach ($anchor in [regex]::Matches($Html, '(?i)<a(?=\s)[^>]*?\s(?:id|name)\s*=\s*(?<quote>["''])(?<id>[^>]*?)\k<quote>')) {
        [void]$anchors.Add([Net.WebUtility]::HtmlDecode($anchor.Groups['id'].Value))
    }
    return ,$anchors
}

$files = [System.Collections.Generic.Dictionary[string, System.IO.FileInfo]]::new([StringComparer]::Ordinal)
$targets = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
[void]$targets.Add('.')
foreach ($file in $RepositoryFiles) {
    $path = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
    $files.Add($path, $file)
    [void]$targets.Add($path)
    while (($slash = $path.LastIndexOf('/')) -ge 0) {
        $path = $path.Substring(0, $slash)
        if (!$targets.Add($path)) { break }
    }
}

$articles = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$markdown = [System.Collections.Generic.Dictionary[string, string]]::new([StringComparer]::Ordinal)
$anchorsByPath = [System.Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
foreach ($path in $files.Keys | Where-Object { $files[$_].Extension -ieq '.md' } | Sort-Object) {
    $html = (ConvertFrom-Markdown -LiteralPath $files[$path].FullName).Html
    $markdown.Add($path, $html)
    $headings = [regex]::Matches($html, '(?s)<h(?<level>[1-6])\b[^>]*>(?<text>.*?)</h\k<level>>')
    $anchorsByPath.Add($path, (Get-MarkdownAnchors $html $headings))
    if (!$path.StartsWith('docs/source/', [StringComparison]::Ordinal)) { continue }
    foreach ($heading in $headings) {
        if ($heading.Groups['level'].Value -ne '2' -or $heading.Groups['text'].Value -cnotmatch '^<code>(.+)</code>$') { continue }
        $source = [Net.WebUtility]::HtmlDecode($Matches[1])
        if (!$files.ContainsKey($source)) {
            "${path}: Stale source article for '$source'."
        } elseif (!$articles.Add($source)) {
            "${path}: Duplicate source article for '$source'."
        }
    }
}

foreach ($path in $files.Keys | Sort-Object) {
    $sourceTree = $path -cmatch '^(?:avalonia|src|tests|scripts|installer|assets|\.github/workflows)/'
    $sourceFile = $sourceTree -and ($files[$path].Extension -ine '.md' -or $path.StartsWith('tests/fixtures/'))
    $rootFile = $path -match '^[^/]+\.(?:ps1|psm1|psd1|py|sh|cmd|bat|cs)$' -or $path -cin @('.editorconfig', '.gitignore')
    if (($sourceFile -or $rootFile) -and !$articles.Contains($path)) {
        "${path}: Missing source article under docs/source/."
    }
}

foreach ($path in $markdown.Keys | Sort-Object) {
    if ($path.StartsWith('tests/fixtures/', [StringComparison]::Ordinal)) { continue }
    $links = [regex]::Matches($markdown[$path], '(?i)<(?:a|img)(?=\s)[^>]*?\s(?:href|src)\s*=\s*(?<quote>["''])(?<address>[^>]*?)\k<quote>')
    foreach ($link in $links) {
        $address = [Net.WebUtility]::HtmlDecode($link.Groups['address'].Value)
        if (!$address -or $address -match '^(?:[a-z][a-z0-9+.-]*:|//)') { continue }
        $parts = $address.Split('#', 2)
        $destination = [Uri]::UnescapeDataString($parts[0].Split('?', 2)[0])
        $target = $path
        if ($destination) {
            $base = $files[$path].DirectoryName
            if ($destination.StartsWith('/')) {
                $base = $Root
                $destination = ".$destination"
            }
            try {
                $absolute = [IO.Path]::GetFullPath($destination, $base)
                $target = [IO.Path]::GetRelativePath($Root, [IO.Path]::TrimEndingDirectorySeparator($absolute)).Replace('\', '/')
            } catch [ArgumentException] {
                "${path}: Invalid local link '$address'."
                continue
            }
        }
        if (!$targets.Contains($target)) {
            "${path}: Broken local link '$address'."
        } elseif ($parts.Count -eq 2 -and $parts[1] -and $anchorsByPath.ContainsKey($target) -and
            !$anchorsByPath[$target].Contains([Uri]::UnescapeDataString($parts[1]))) {
            "${path}: Broken Markdown anchor in '$address'."
        }
    }
}

$catalogPath = 'docs/ui/catalog.json'
if (!$files.ContainsKey($catalogPath)) {
    "${catalogPath}: Missing UI catalog."
    return
}
try {
    $catalog = Get-Content -LiteralPath $files[$catalogPath].FullName -Raw | ConvertFrom-Json -AsHashtable
} catch {
    "${catalogPath}: Invalid JSON. $($_.Exception.Message)"
    return
}
if ($catalog -isnot [System.Collections.IDictionary] -or $catalog.schemaVersion -ne 1 -or $catalog.entries -isnot [array]) {
    "${catalogPath}: Expected schemaVersion 1 and an entries array."
    return
}

$ids = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$entries = [System.Collections.Generic.List[System.Collections.IDictionary]]::new()
foreach ($entry in $catalog.entries) {
    if ($entry -isnot [System.Collections.IDictionary] -or $entry.id -isnot [string] -or [String]::IsNullOrWhiteSpace($entry.id)) {
        "${catalogPath}: Every UI catalog entry needs an ID."
        continue
    }
    if (!$ids.Add($entry.id)) { "${catalogPath}: Duplicate UI catalog ID '$($entry.id)'." }
    $entries.Add($entry)
}

$uiSource = ($files.Keys | Where-Object {
    $_ -cmatch '^avalonia/' -and $files[$_].Extension -in @('.cs', '.axaml') -and
        $files[$_].Name -cnotmatch '\.Verification(?:\.|$)'
} | ForEach-Object { [IO.File]::ReadAllText($files[$_].FullName) }) -join "`n"
$fields = 'symbols', 'resourceKey', 'resourceKeys', 'classes', 'selector', 'selectors', 'tokens'
foreach ($entry in $entries) {
    $label = "$catalogPath [$($entry.id)]"
    if ($entry.source -isnot [string] -or !$files.ContainsKey($entry.source)) {
        "${label}: Unknown source file '$($entry.source)'."
    }
    foreach ($consumer in $entry.usedBy) {
        if ($consumer -isnot [string] -or !$files.ContainsKey($consumer)) {
            "${label}: Unknown usedBy file '$consumer'."
        }
    }
    foreach ($related in $entry.related) {
        if ($related -isnot [string] -or !$ids.Contains($related)) {
            "${label}: Unknown related entry '$related'."
        }
    }
    foreach ($field in $fields) {
        foreach ($value in $entry[$field]) {
            if ($value -isnot [string] -or [String]::IsNullOrWhiteSpace($value) -or
                $uiSource -cnotmatch ('(?<![\w.-])' + [regex]::Escape($value) + '(?![\w.-])')) {
                "${label}: Unknown $field reference '$value'."
            }
        }
    }
}

$guidePath = 'docs/ui/README.md'
if ($markdown.ContainsKey($guidePath)) {
    $guideIds = [regex]::Matches($markdown[$guidePath], '<code>((?:theme|style|pattern|component|helper|state|icon|token)\.[a-z0-9.-]+)</code>')
    foreach ($reference in $guideIds) {
        $id = $reference.Groups[1].Value
        if (!$ids.Contains($id)) { "${guidePath}: Unknown UI catalog entry '$id'." }
    }
}
