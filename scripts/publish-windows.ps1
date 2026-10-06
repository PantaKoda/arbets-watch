# Builds the portable Windows release: a self-contained win-x64 folder, zipped, with a SHA-256 file.
# Run from the repository root:  pwsh scripts/publish-windows.ps1 [-SkipTests]
# Output: artifacts/release/ArbetsWatch-<version>-win-x64.zip and .sha256
param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'

[xml]$props = Get-Content Directory.Build.props
$version = $props.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw 'Version not found in Directory.Build.props' }

dotnet restore ArbetsWatch.slnx --locked-mode
if ($LASTEXITCODE) { throw 'restore failed' }
if (-not $SkipTests) {
    dotnet build ArbetsWatch.slnx --configuration Release --no-restore
    if ($LASTEXITCODE) { throw 'build failed' }
    dotnet test ArbetsWatch.slnx --configuration Release --no-build --no-restore
    if ($LASTEXITCODE) { throw 'tests failed' }
}

$publish = 'artifacts/publish/win-x64'
$release = 'artifacts/release'
Remove-Item -Recurse -Force $publish, $release -ErrorAction SilentlyContinue
dotnet publish src/ArbetsWatch.Desktop/ArbetsWatch.Desktop.csproj --configuration Release --runtime win-x64 --self-contained true `
    -p:RestoreLockedMode=true -p:DebugType=none --output "$publish/ArbetsWatch"
if ($LASTEXITCODE) { throw 'publish failed' }

New-Item -ItemType Directory -Force $release | Out-Null
$zip = Join-Path $release "ArbetsWatch-$version-win-x64.zip"
Compress-Archive -Path "$publish/ArbetsWatch" -DestinationPath $zip -CompressionLevel Optimal
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path $zip -Leaf)" | Set-Content -Encoding ascii "$zip.sha256"

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
"Wrote $zip ($size MB)"
"SHA-256 $hash"
