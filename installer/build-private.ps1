<#
  Builds a private, fully standalone bmwDecode installer on this PC: the app, your SGBD files and the K+DCAN
  (FTDI) driver in one setup.exe. BMW's SGBD files are BMW's property: keep the result for your own machines and
  never upload it (the public GitHub build deliberately contains no BMW data).

  Requirements: .NET 10 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup).

  It also builds sgbd.pack, the app's built-in reference data: every job, table and text of those SGBD files
  (pre-translated to English) plus, with -EcuFunctions, an ECU functions export (BMW's English fault texts,
  labelled live values, type keys and VIN ranges). The installed app then needs nothing else.

  Example:
    .\installer\build-private.ps1 -EcuPath C:\EDIABAS\Ecu -IstaEcuPath "C:\Program Files\BMW\ISPI\TRIC\ISTA\Ecu" `
        -EcuFunctions C:\Projects\EcuFunctionsOutput\EcuFunctions.zip -DriverPath "D:\BMW INPA v5.0.6\Driver_D_CAN_USB\Driver"
#>
param(
    [string]$EcuPath = 'C:\EDIABAS\Ecu',
    [string]$IstaEcuPath = "$env:ProgramFiles\BMW\ISPI\TRIC\ISTA\Ecu",
    [string]$DriverPath = '',
    [string]$EcuFunctions = 'C:\Projects\EcuFunctionsOutput\EcuFunctions.zip',
    [string]$Version = '0.2.0'
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $repo 'publish'
$staging = Join-Path $repo 'artifacts\ecu-staging'

dotnet publish (Join-Path $repo 'src\BmwDecode.App\BmwDecode.App.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Merge INPA's and ISTA's SGBDs (ISTA's newer files win).
Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $staging | Out-Null
foreach ($s in @($EcuPath, $IstaEcuPath) | Where-Object { $_ -and (Test-Path $_) }) {
    robocopy $s $staging *.prg *.grp /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
}

# Built-in reference data from the merged SGBDs (+ the ECU functions export when present).
$dataDir = Join-Path $repo 'artifacts\data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
$builderArgs = @((Join-Path $dataDir 'sgbd.pack'), $staging)
if ($EcuFunctions -and (Test-Path $EcuFunctions)) { $builderArgs += @('--ista', $EcuFunctions) }
else { Write-Warning "No ECU functions export at '$EcuFunctions': fault texts will be machine-translated, no VIN ranges." }
dotnet run --project (Join-Path $repo 'tools\BmwDecode.DataBuilder\BmwDecode.DataBuilder.csproj') -c Release -- @builderArgs
if ($LASTEXITCODE -ne 0) { throw "Data pack build failed ($LASTEXITCODE)" }

$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

$defines = @("/DAppVersion=$Version", "/DPublishDir=$publish", "/DEcuDir=$staging", "/DDataDir=$dataDir", "/DOutputDir=$(Join-Path $repo 'artifacts')")
if ($DriverPath -and (Test-Path $DriverPath)) { $defines += "/DDriverDir=$DriverPath" }
& $iscc @defines (Join-Path $PSScriptRoot 'bmwDecode.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }
Write-Host "Private installer written to $(Join-Path $repo 'artifacts')"
