# Prints one version's section of CHANGELOG.md (without its heading): the GitHub release notes, which the
# app shows in its update window. Fails when the version has no section, so a release can't go out without notes.
#   pwsh scripts/release-notes.ps1 -Version 0.1.0 [-OutFile release-notes.md]
# Use -OutFile for a file: it is written as UTF-8 directly (redirecting a separate pwsh process goes through the
# console code page and garbles characters such as "å", "ä", "ö").
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version, [string]$OutFile)
$ErrorActionPreference = 'Stop'
$lines = Get-Content -Encoding utf8 (Join-Path (Split-Path $PSScriptRoot -Parent) 'CHANGELOG.md')
$heading = $lines | Select-String -Pattern ('^## \[' + [regex]::Escape($Version) + '\]') | Select-Object -First 1
if (-not $heading) { throw "CHANGELOG.md has no '## [$Version]' section. Add one before releasing." }
$body = foreach ($line in $lines[$heading.LineNumber..($lines.Count - 1)]) {
    if ($line -match '^## \[') { break }
    $line
}
$text = ($body -join "`n").Trim()
if (-not $text) { throw "The '## [$Version]' section of CHANGELOG.md is empty." }
if ($OutFile) {
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutFile), $text + "`n", [System.Text.UTF8Encoding]::new($false))
} else {
    $text
}
