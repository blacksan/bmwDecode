# Sets the latency timer of every FTDI USB serial device to 1 ms (INPA/EDIABAS recommendation for K+DCAN cables).
# Applies to cables that have been plugged in at least once; replug the cable afterwards.
$root = 'HKLM:\SYSTEM\CurrentControlSet\Enum\FTDIBUS'
if (-not (Test-Path $root)) { exit 0 }
Get-ChildItem $root -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.PSChildName -eq 'Device Parameters' } |
    ForEach-Object {
        try { Set-ItemProperty -Path $_.PSPath -Name 'LatencyTimer' -Value 1 -Type DWord -ErrorAction Stop } catch { }
    }
exit 0
