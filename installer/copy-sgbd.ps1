# Copies SGBD files (*.prg, *.grp) from existing INPA/EDIABAS and ISTA installs into bmwDecode's own folder.
# INPA's set first (covers older cars), then ISTA's newer files on top.
param([Parameter(Mandatory = $true)][string]$Target)

$sources = @(
    'C:\EDIABAS\Ecu',
    "$env:ProgramFiles\BMW\ISPI\TRIC\ISTA\Ecu",
    'C:\EC-APPS\ISTA\Ecu',
    'C:\Rheingold\Ecu'
) | Where-Object { Test-Path $_ }

New-Item -ItemType Directory -Force -Path $Target | Out-Null
foreach ($s in $sources) {
    # /XO is not used on purpose: a later (newer) source replaces files from an earlier one.
    robocopy $s $Target *.prg *.grp /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
}
"Copied SGBD files from: $($sources -join ', ')" | Out-File -FilePath (Join-Path $Target 'source.txt') -Encoding utf8
exit 0
