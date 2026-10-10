<#
.SYNOPSIS
  Fails when a DeepL API key (or an obvious key assignment) is about to be committed or is already tracked.

.DESCRIPTION
  -Staged   scans the lines added in the staged diff (used by the pre-commit hook).
  (default) scans every tracked file (used by CI).

  Free-plan DeepL keys end in ":fx" and are matched anywhere. Paid keys look like a plain GUID, so they are only
  matched next to an Authorization header or a key-like name. The all-zero GUID used by the tests is allowed.
  Matches are reported by file and line only; the matched text is never printed.
#>
[CmdletBinding()]
param([switch]$Staged)

$ErrorActionPreference = 'Stop'

$guid = '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}'
$patterns = @(
    "$guid`:fx",
    "DeepL-Auth-Key\s+$guid",
    "(?i)(deepl|api)[_\-]?(auth[_\-]?)?key['""]?\s*[:=]\s*['""]?$guid"
)
$allowed = '^0{8}-0{4}-0{4}-0{4}-0{11}[0-9a-f]'

$findings = New-Object System.Collections.Generic.List[string]

function Test-Line([string]$path, [int]$number, [string]$line) {
    foreach ($pattern in $patterns) {
        foreach ($m in [regex]::Matches($line, $pattern)) {
            $guidOnly = [regex]::Match($m.Value, $guid).Value
            if ($guidOnly -match $allowed) { continue }
            $findings.Add("${path}:${number}: looks like a DeepL API key (value not shown)")
        }
    }
}

if ($Staged) {
    $path = $null
    $number = 0
    $inHunk = $false
    foreach ($line in (git -c core.quotePath=false diff --cached --unified=0 --no-color)) {
        if ($line.StartsWith('diff --git ')) { $inHunk = $false; continue }
        # File headers only count before a file's first hunk; afterwards "+++" is added text.
        if (-not $inHunk -and $line -match '^\+\+\+ b/(.+)$') { $path = $Matches[1]; continue }
        if ($line -match '^@@ .* \+(\d+)') { $number = [int]$Matches[1]; $inHunk = $true; continue }
        if ($inHunk -and $line.StartsWith('+')) {
            Test-Line $path $number $line.Substring(1)
            $number++
        }
    }
    $stagedFiles = git -c core.quotePath=false diff --cached --name-only
    foreach ($f in $stagedFiles) {
        if ($f -match '(^|/)deepl-key\.bin$' -or $f -match '\.key$') { $findings.Add("${f}: key files must never be committed") }
    }
}
else {
    foreach ($file in (git -c core.quotePath=false ls-files)) {
        if ($file -match '(^|/)deepl-key\.bin$' -or $file -match '\.key$') { $findings.Add("${file}: key files must never be committed"); continue }
        if ($file -match '\.(png|ico|zip|db|dll|exe)$' -or -not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        $number = 0
        foreach ($line in [System.IO.File]::ReadLines((Resolve-Path -LiteralPath $file))) {
            $number++
            Test-Line $file $number $line
        }
    }
}

if ($findings.Count -gt 0) {
    $findings | ForEach-Object { Write-Error $_ -ErrorAction Continue }
    Write-Error 'Secret scan failed. Remove the key from the change; if it was ever pushed, revoke it in the DeepL account and create a new one.' -ErrorAction Continue
    exit 1
}

Write-Host 'Secret scan passed.'
