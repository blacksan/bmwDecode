<#
  Makes bmwDecode-data.zip on your Desktop: the merged Ecu folder (INPA first, ISTA on top) and the built-in data
  pack, with a checksum manifest. Import it in bmwDecode on any laptop or Mac (it is offered on first start), and
  that install needs no INPA or ISTA.

    .\installer\make-data-zip.ps1
    .\installer\make-data-zip.ps1 -RebuildPack       (rebuild sgbd.pack from the merged SGBDs + ECU functions export)

  The zip holds BMW's files: keep it on your own machines.
#>
param(
    [string]$EcuPath = 'C:\EDIABAS\Ecu',
    [string]$IstaEcuPath = "$env:ProgramFiles\BMW\ISPI\TRIC\ISTA\Ecu",
    [string]$EcuFunctions = 'C:\Projects\EcuFunctionsOutput\EcuFunctions.zip',
    [string]$Pack = '',
    [string]$Output = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'bmwDecode-data.zip'),
    [switch]$RebuildPack
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path $repo 'artifacts\data-zip'
$staging = Join-Path $repo 'artifacts\ecu-staging'

# 1. Merged Ecu folder: ISTA's copy wins when both have a file (robocopy compares names case-insensitively).
Write-Host 'Merging SGBD files...'
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $staging | Out-Null
foreach ($s in @($EcuPath, $IstaEcuPath) | Where-Object { $_ -and (Test-Path $_) }) {
    robocopy $s $staging *.prg *.grp /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "Copying from $s failed (robocopy $LASTEXITCODE)" }
}
$count = (Get-ChildItem $staging -File).Count
if ($count -eq 0) { throw "No SGBD files found in $EcuPath or $IstaEcuPath" }
Write-Host "  $count SGBD files"

# 2. Data pack.
if (-not $Pack) { $Pack = Join-Path $repo 'artifacts\data\sgbd.pack' }
if ($RebuildPack -or -not (Test-Path $Pack)) {
    Write-Host 'Building the data pack...'
    New-Item -ItemType Directory -Force -Path (Split-Path $Pack) | Out-Null
    $builderArgs = @($Pack, $staging)
    if ($EcuFunctions -and (Test-Path $EcuFunctions)) { $builderArgs += @('--ista', $EcuFunctions) }
    else { Write-Warning "No ECU functions export at '$EcuFunctions': no BMW English fault texts or VIN ranges in the pack." }
    dotnet run --project (Join-Path $repo 'tools\BmwDecode.DataBuilder\BmwDecode.DataBuilder.csproj') -c Release -- @builderArgs
    if ($LASTEXITCODE -ne 0) { throw "Data pack build failed ($LASTEXITCODE)" }
}

# 3. ecu.zip + sgbd.pack + manifest.json, stored (not re-compressed) in the outer zip.
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $work | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
Write-Host 'Zipping the Ecu folder...'
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, (Join-Path $work 'ecu.zip'), [System.IO.Compression.CompressionLevel]::Optimal, $false)
Copy-Item $Pack (Join-Path $work 'sgbd.pack')

$files = foreach ($f in Get-ChildItem $work -File | Sort-Object Name) {
    [ordered]@{ path = $f.Name; size = $f.Length; sha256 = (Get-FileHash $f.FullName -Algorithm SHA256).Hash; extract = $f.Extension -eq '.zip' }
}
$version = Get-Date -Format 'yyyy.MM.dd.HHmm'
$manifest = [ordered]@{ version = $version; built = (Get-Date).ToUniversalTime().ToString('o'); files = @($files) }
[System.IO.File]::WriteAllText((Join-Path $work 'manifest.json'), ($manifest | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))

if (Test-Path $Output) { Remove-Item $Output -Force }
$zip = [System.IO.Compression.ZipFile]::Open($Output, 'Create')
try {
    foreach ($f in Get-ChildItem $work -File) {
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $f.Name, [System.IO.Compression.CompressionLevel]::NoCompression)
    }
} finally { $zip.Dispose() }

$mb = [math]::Round((Get-Item $Output).Length / 1MB, 0)
Write-Host "Done: $Output ($mb MB, version $version). Import it in bmwDecode: Settings > BMW data > Import data file."
