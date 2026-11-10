# Writes NAMESPACES.md for the Arktis root and for every top-level folder that is its own repository
param([string]$Root)
$Root = (Resolve-Path $Root).Path.TrimEnd('\') + '\'
$nested = @(Get-ChildItem $Root -Directory | Where-Object { Test-Path (Join-Path $_.FullName '.git') } | ForEach-Object { $_.FullName.TrimEnd('\') + '\' })

function Write-Index([string]$dir, [string[]]$skip)
{
    Get-ChildItem -Path $dir -Recurse -Filter '*.cs' |
        Where-Object { $p = $_.FullName; $p -notmatch '\\.claude\\' -and -not ($skip | Where-Object { $p.StartsWith($_) }) } |
        Select-String '^namespace ' |
        ForEach-Object { ($_.Line.Trim() -replace '^namespace ','') + ' -> ' + ($_.Path.Substring($dir.Length) -replace '\\','/') } |
        Sort-Object -Unique | Out-File (Join-Path $dir 'NAMESPACES.md') -Encoding UTF8
}

Write-Index $Root $nested
foreach ($n in $nested) { Write-Index $n @() }
