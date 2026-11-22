<#
  Uploads the BMW data (merged Ecu folder + data pack) to your private data server, where bmwDecode downloads it
  on first start. Run on the Windows laptop that has INPA / ISTA.

    .\installer\upload-data.ps1                              (defaults below)
    .\installer\upload-data.ps1 -Server root@162.35.47.106

  Steps: merges the SGBDs (INPA first, ISTA on top), builds sgbd.pack if it is missing (with the ECU functions
  export when found), zips the Ecu folder, writes manifest.json, copies everything over SSH (Windows' built-in
  OpenSSH) into a staging folder, then swaps it in, manifest last, so a laptop downloading at that moment never
  sees a half-finished set. The files are BMW's: this goes only to your password-protected server.
#>
param(
    [string]$Server = 'root@162.35.47.106',
    [string]$RemoteDir = '/srv/bmwdecode-data',
    [string]$EcuPath = 'C:\EDIABAS\Ecu',
    [string]$IstaEcuPath = "$env:ProgramFiles\BMW\ISPI\TRIC\ISTA\Ecu",
    [string]$EcuFunctions = 'C:\Projects\EcuFunctionsOutput\EcuFunctions.zip',
    [string]$Pack = '',
    [switch]$RebuildPack
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$work = Join-Path $repo 'artifacts\upload'
$staging = Join-Path $repo 'artifacts\ecu-staging'

foreach ($tool in 'ssh', 'scp') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool not found: Settings > System > Optional features > add 'OpenSSH Client'."
    }
}

# 1. Merged Ecu folder.
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

# 3. Upload set: ecu.zip, sgbd.pack, manifest.json.
Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $work | Out-Null
Write-Host 'Zipping the Ecu folder...'
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($staging, (Join-Path $work 'ecu.zip'), [System.IO.Compression.CompressionLevel]::Optimal, $false)
Copy-Item $Pack (Join-Path $work 'sgbd.pack')

$files = foreach ($f in Get-ChildItem $work -File | Sort-Object Name) {
    [ordered]@{
        path    = $f.Name
        size    = $f.Length
        sha256  = (Get-FileHash $f.FullName -Algorithm SHA256).Hash
        extract = $f.Extension -eq '.zip'
    }
}
$version = Get-Date -Format 'yyyy.MM.dd.HHmm'
$manifest = [ordered]@{ version = $version; built = (Get-Date).ToUniversalTime().ToString('o'); files = @($files) }
# Written without a BOM (Windows PowerShell 5.1 has no utf8NoBOM encoding name).
[System.IO.File]::WriteAllText((Join-Path $work 'manifest.json'), ($manifest | ConvertTo-Json -Depth 4), (New-Object System.Text.UTF8Encoding($false)))
$mb = [math]::Round(((Get-ChildItem $work -File | Measure-Object Length -Sum).Sum / 1MB), 0)
Write-Host "  version $version, $mb MB"

# 4. Copy to a staging folder on the server, then swap in (manifest last).
$incoming = "$RemoteDir/.incoming"
Write-Host "Uploading to ${Server}:$RemoteDir ..."
ssh $Server "mkdir -p '$incoming' && rm -f '$incoming'/*"
if ($LASTEXITCODE -ne 0) { throw 'ssh failed' }
scp (Join-Path $work 'ecu.zip') (Join-Path $work 'sgbd.pack') (Join-Path $work 'manifest.json') "${Server}:$incoming/"
if ($LASTEXITCODE -ne 0) { throw 'scp failed' }
ssh $Server "cd '$incoming' && chmod 644 ecu.zip sgbd.pack manifest.json && mv -f ecu.zip sgbd.pack '$RemoteDir/' && mv -f manifest.json '$RemoteDir/'"
if ($LASTEXITCODE -ne 0) { throw 'Moving the files into place failed' }

Write-Host "Done. bmwDecode installs will download version $version on their next start."
