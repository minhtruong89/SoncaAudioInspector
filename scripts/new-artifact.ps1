param(
    [Parameter(Mandatory)][string]$Description,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]+(?:-[a-z0-9]+)*$')][string]$Slug,
    [string]$OutputRoot = 'artifacts'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$root = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputRoot))
if (-not $root.StartsWith($projectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Artifact root must be inside the repository.'
}
# Reject junctions in all existing parents, including the repository itself.
$parent = $root
while ($parent) {
    if ((Test-Path -LiteralPath $parent) -and ((Get-Item -LiteralPath $parent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Reparse point is not allowed: $parent"
    }
    $parent = Split-Path -Parent $parent
}
$created = [DateTimeOffset]::UtcNow
$path = Join-Path $root ($created.ToString("yyyyMMdd-HHmmss-fff'Z'") + '-' + $Slug)
if (Test-Path -LiteralPath $path) { throw "Artifact already exists: $path" }
New-Item -ItemType Directory -Path $path -Force | Out-Null
$sha = & git -C $projectRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine source commit.' }
$dirty = [bool](& git -C $projectRoot status --porcelain --untracked-files=normal)
$metadata = [ordered]@{ createdUtc = $created.ToString('o'); description = $Description; sourceCommit = "$sha"; workingTreeDirty = $dirty }
$metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $path 'artifact.json') -Encoding utf8
@("# $Description", '', "Created UTC: $($metadata.createdUtc)", "Source commit: $sha", "Working tree dirty: $dirty", '', 'Validation: pending. Record commands, results, package hashes and hardware limits here.') |
    Set-Content -LiteralPath (Join-Path $path 'ARTIFACT.md') -Encoding utf8
$path
