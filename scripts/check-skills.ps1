# Validates agent skills in .agents/skills:
# 1. Frontmatter YAML contains 'name' and 'description'
# 2. Local relative markdown links point to existing files
# 3. Referenced repo scripts exist
$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$skillsDir = Join-Path $repo '.agents\skills'

if (-not (Test-Path -LiteralPath $skillsDir -PathType Container)) {
    throw "Skills directory not found: $skillsDir"
}

$skillDirs = Get-ChildItem -LiteralPath $skillsDir -Directory
if ($skillDirs.Count -eq 0) {
    throw "No skills found in $skillsDir"
}

$checkedCount = 0
foreach ($dir in $skillDirs) {
    $skillMd = Join-Path $dir.FullName 'SKILL.md'
    if (-not (Test-Path -LiteralPath $skillMd -PathType Leaf)) {
        throw "Skill '$($dir.Name)' is missing SKILL.md"
    }

    $content = [IO.File]::ReadAllText($skillMd, [Text.Encoding]::UTF8)
    
    # 1. Check frontmatter
    if ($content -notmatch '(?s)^---\r?\n(.*?)\r?\n---') {
        throw "Skill '$($dir.Name)' SKILL.md has no YAML frontmatter delimiter."
    }
    $frontmatter = $Matches[1]
    if ($frontmatter -notmatch '(?m)^name:\s*([^\r\n]+)') {
        throw "Skill '$($dir.Name)' frontmatter missing 'name'."
    }
    $skillName = $Matches[1].Trim()
    if ($skillName -ne $dir.Name) {
        throw "Skill '$($dir.Name)' frontmatter name '$skillName' does not match directory name '$($dir.Name)'."
    }
    if ($frontmatter -notmatch '(?m)^description:\s*([^\r\n]+)') {
        throw "Skill '$($dir.Name)' frontmatter missing 'description'."
    }

    # 2. Check all markdown links
    $allMdFiles = @(Get-ChildItem -LiteralPath $dir.FullName -Recurse -Filter '*.md')
    foreach ($file in $allMdFiles) {
        $text = [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8)
        $linkMatches = [regex]::Matches($text, '\[(?<label>[^\]]+)\]\((?<target>[^)]+)\)')
        foreach ($m in $linkMatches) {
            $target = $m.Groups['target'].Value.Trim()
            # Ignore web links, anchors, mailto
            if ($target -match '^(https?://|mailto:|#)') { continue }
            # Strip anchor if present
            $pathPart = $target.Split('#')[0]
            if ([string]::IsNullOrWhiteSpace($pathPart)) { continue }
            
            $resolved = [IO.Path]::GetFullPath((Join-Path $file.DirectoryName $pathPart))
            if (-not (Test-Path -LiteralPath $resolved)) {
                throw "Broken link in $($file.FullName): '$target' (resolved: '$resolved')"
            }
        }
    }

    $checkedCount++
}

Write-Output ("PASS agent skills: {0} skills validated (frontmatter, relative links, referenced scripts)." -f $checkedCount)
