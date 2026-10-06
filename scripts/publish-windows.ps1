# Builds the portable Windows release: a self-contained win-x64 publish zipped as
#   artifacts/release/ArbetsWatch-<version>-win-x64.zip  (+ .sha256)
# The .NET runtime is included, so the zip runs on Windows 10/11 x64 without installing .NET.
#
# Reproducible: locked restore, deterministic compilation with CI path mapping, no debug symbols, and a zip whose
# entries are sorted and stamped with the commit time. The folder carries release.json, the marker that lets a
# copy extracted from a release replace itself with a newer release (in-app updates, see docs/updates.md).
#
#   pwsh scripts/publish-windows.ps1              # restore, test, publish, zip
#   pwsh scripts/publish-windows.ps1 -SkipTests   # when the tests already ran for this commit
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [string]$Output = 'artifacts/release',
    # Overrides <Version> from Directory.Build.props (local update testing only; releases use the props value).
    [string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

function Invoke-Checked([string]$what, [scriptblock]$command) {
    Write-Host "==> $what" -ForegroundColor Cyan
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." }
}

$project = 'src/ArbetsWatch.Desktop/ArbetsWatch.Desktop.csproj'
$runtime = 'win-x64'

$commit = (git rev-parse HEAD).Trim()
# Untracked files count too: the SDK globs would compile a stray *.cs or *.axaml under src/ into the release.
$dirty = [bool](git status --porcelain)
if ($dirty) {
    Write-Warning 'The working tree has uncommitted or untracked files: this build is not reproducible from the commit and must not be released.'
}
$stamp = [DateTimeOffset]::FromUnixTimeSeconds([long](git log -1 --format=%ct HEAD)).UtcDateTime

$versionArgs = @()
if ($Version) {
    $versionArgs = @("-p:Version=$Version")
} else {
    $Version = (dotnet msbuild $project -getProperty:Version).Trim()
}
if (-not $Version) { throw 'Could not read the version from the project.' }
$name = "ArbetsWatch-$Version-$runtime"
$publishDir = Join-Path $root "artifacts/publish/$runtime/ArbetsWatch"
$outDir = Join-Path $root $Output
$zip = Join-Path $outDir "$name.zip"

Invoke-Checked 'Restore (locked)' { dotnet restore ArbetsWatch.slnx --locked-mode }
if (-not $SkipTests) {
    Invoke-Checked 'Build' { dotnet build ArbetsWatch.slnx -c Release --no-restore }
    Invoke-Checked 'Test' { dotnet test ArbetsWatch.slnx -c Release --no-build --no-restore }
}

# Start from empty output folders so files from an earlier publish cannot leak into the zip.
foreach ($dir in (Join-Path $root "artifacts/publish/$runtime"), (Join-Path $root "src/ArbetsWatch.Desktop/obj/Release/net10.0/$runtime")) {
    if (Test-Path $dir) { Remove-Item -Recurse -Force $dir }
}
Invoke-Checked 'Publish' {
    dotnet publish $project -c Release -r $runtime --self-contained -p:RestoreLockedMode=true `
        -p:ContinuousIntegrationBuild=true -p:DebugType=none -p:DebugSymbols=false -p:SatelliteResourceLanguages=en `
        @versionArgs -o $publishDir
}
if (-not (Test-Path (Join-Path $publishDir 'ArbetsWatch.exe'))) { throw 'The publish did not produce ArbetsWatch.exe.' }

# Marks the folder as a release of this version: only such a copy may replace itself with a newer release.
$shipped = [string[]](Get-ChildItem $publishDir -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { [IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/') }) + 'release.json'
[Array]::Sort($shipped, [StringComparer]::Ordinal)
$manifest = [ordered]@{ version = $Version; commit = $commit; runtime = $runtime; files = $shipped }
Set-Content -Path (Join-Path $publishDir 'release.json') -Value ($manifest | ConvertTo-Json -Compress) -Encoding utf8 -NoNewline

Write-Host '==> Zip' -ForegroundColor Cyan
New-Item -ItemType Directory -Force $outDir | Out-Null
if (Test-Path $zip) { Remove-Item -Force $zip }
Add-Type -AssemblyName System.IO.Compression
$files = @(Get-ChildItem $publishDir -Recurse -File | Where-Object { $_.Extension -ne '.pdb' } |
    ForEach-Object { [pscustomobject]@{ File = $_; Entry = 'ArbetsWatch/' + [IO.Path]::GetRelativePath($publishDir, $_.FullName).Replace('\', '/') } })
# Ordinal order, so the entry order cannot depend on the culture or ICU version.
$entries = [string[]]($files | ForEach-Object Entry)
$items = [object[]]$files
[Array]::Sort($entries, $items, [StringComparer]::Ordinal)
$files = $items
$stream = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
try {
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($item in $files) {
            $entry = $archive.CreateEntry($item.Entry, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = $stamp
            $source = $item.File.OpenRead()
            $target = $entry.Open()
            try { $source.CopyTo($target) } finally { $target.Dispose(); $source.Dispose() }
        }
    } finally { $archive.Dispose() }
} finally { $stream.Dispose() }

$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -Path "$zip.sha256" -Value "$hash  $name.zip" -Encoding ascii -NoNewline
$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)

Write-Host ''
Write-Host "Toolchain: .NET SDK $((dotnet --version).Trim()), PowerShell $($PSVersionTable.PSVersion)"
Write-Host "Version : $Version ($($commit.Substring(0, 7))$(if ($dirty) { ', dirty' }))"
Write-Host "Zip     : $zip ($size MB, $($files.Count) files)"
Write-Host "SHA-256 : $hash"
Write-Host "Run     : extract the zip, then start ArbetsWatch\ArbetsWatch.exe"
