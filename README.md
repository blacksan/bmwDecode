# bmwDecode

Modern Windows 11 diagnostics for BMW **E-series** cars (E36 → E93, including the **E84 X1**), built as a
replacement for the INPA workflow: identify the car, run a quick test of every module, read and clear fault
memory with freeze-frame data, watch live values and run any SGBD job.

It uses your existing INPA/EDIABAS **SGBD files** (`C:\EDIABAS\Ecu`) and an ordinary **K+DCAN USB cable**, but
does not need BMW's 32-bit EDIABAS runtime, INPA scripts or any COM-port juggling beyond picking the port.

![platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4) ![dotnet](https://img.shields.io/badge/.NET-10%20LTS-512BD4) ![license](https://img.shields.io/badge/license-GPLv3-green)

## Features

| Page | What it does |
| --- | --- |
| **Overview** | Connect (K+DCAN cable via its COM port, ENET, or the built-in demo car), read VIN and vehicle order (FA → series via `FA.PRG`), battery and ignition via `UTILITY.PRG`, pick the chassis profile. Warns when a swapped module (e.g. an E90 CAS in an E84) reports a different chassis. |
| **Quick test** | Tries every module slot of the chassis (group files `D_MOTOR`, `D_CAS`, `D_DSC`… from the ISTA-derived catalogue), identifies the fitted variant (`D60M57A0`, `CAS`, `DSC_87`…), reads `IDENT` and `FS_LESEN`. Clear all faults, export HTML/CSV report. |
| **Modules** | Per-module identification, fault memory with environmental conditions (`FS_LESEN_DETAIL`: mileage, frequency, `F_UWn` values), info memory (`IS_LESEN`), clear, and **live data**: tick any argument-free `STATUS_*` jobs and poll them with min/max. |
| **Job explorer** | Tool32-style: browse all 2,000+ SGBDs offline, read job/argument/result documentation, run any job with arguments. Writing jobs (coding, flashing, actuators, clearing) need explicit confirmation. |
| **Log** | Every connection, variant identification and job, exportable. Optional EDIABAS interface trace. |
| **Settings** | EDIABAS Ecu folder (auto-detected), theme (Windows / light / dark, with Mica), defaults. |

The UI is WPF on .NET 10 with the built-in **Windows 11 Fluent theme**: follows the Windows light/dark setting and
accent colour, Mica backdrop, per-monitor DPI.

## Getting started

1. **Download** `bmwDecode-win-x64.zip` from the latest [Actions run](../../actions) (or a [release](../../releases)),
   unzip anywhere and run `bmwDecode.exe`. It is self-contained, so no .NET install is needed.
2. **SGBD files**: point *Settings → EDIABAS ECU folder* at the `Ecu` folder of your INPA install
   (normally `C:\EDIABAS\Ecu`). It is auto-detected when present. bmwDecode never ships BMW files.
3. **Cable** (K+DCAN USB):
   - Install the FTDI driver that came with the cable (`D-CAN.exe` / `OBDSetup.exe` in the INPA package).
   - E8x / E9x / **E84** use **D-CAN**: set the cable's switch to D-CAN (pins 7 and 8 *not* bridged).
     Older cars (E46, E39, E53, E83 early) use K-line.
   - Pick **K+DCAN (COM port)** and select the cable's COM port (Device Manager → Ports → USB Serial Port →
     Advanced → *Latency timer 1 ms*). Any COM number works; it does not have to be COM1 as INPA requires.
4. Ignition **on**, engine off, battery charger connected for long sessions → **Connect** → **Run quick test**.

No car handy? Choose **Demo vehicle**: a simulated E84 with an M57 diesel (DDE6), an E90 CAS and a RWD conversion,
complete with plausible faults, freeze frames and live data.

### Notes for the E84 X1 (and other swapped cars)

- The E84 belongs to the **E89X** family (shared with E81/E82/E87/E88/E90–E93), so modules from those cars are
  identified correctly, e.g. an M57 DDE shows up as `D60M57A0` at address `0x12`.
- A CAS from an E90 reports the donor's vehicle order. bmwDecode keeps your chosen **E84** profile within the same
  family and shows a notice instead of silently switching.
- A RWD conversion has no transfer case (`VGSG`); its absence, and the "transfer case message missing" faults that
  other modules store, are expected.

## Building

Requirements: Windows 10/11, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```powershell
git clone --recurse-submodules https://github.com/blacksan/bmwDecode
cd bmwDecode
dotnet test BmwDecode.slnx
dotnet run --project src/BmwDecode.App
# single-file exe:
dotnet publish src/BmwDecode.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish
```

CI (`.github/workflows/build.yml`) builds, tests and publishes the zip on every push; tags `v*` create a release.

## Architecture

```
src/
  BmwDecode.Core      net10.0, no dependencies: result model, chassis catalogue, fault/ident/live parsers,
                      vehicle identification, quick test, live polling, reports, simulated E84
  BmwDecode.Ediabas   EdiabasLib compiled from the submodule + EdiabasEngine (K+DCAN / FTDI / ENET)
                      and SgbdInspector (offline job documentation)
  BmwDecode.App       WPF (.NET 10, Fluent theme), MVVM with CommunityToolkit.Mvvm, Microsoft DI
tests/
  BmwDecode.Core.Tests  xUnit tests against the catalogue, parsers and the simulated vehicle
external/
  ediabaslib          github.com/uholeschak/ediabaslib (pinned submodule)
```

- **Diagnostics engine.** [EdiabasLib](https://github.com/uholeschak/ediabaslib) is an open-source
  re-implementation of EDIABAS that interprets the BMW `.prg`/`.grp` SGBD files directly, so the bytes on the bus are
  exactly what INPA/ISTA would send. Bluetooth adapters are compiled out (`Shims/EdBluetoothInterface.cs`).
- **Chassis catalogue.** EdiabasLib's `VehicleSeries.xml` (derived from ISTA) maps each chassis to diagnostic
  addresses and group files. It is embedded at build time from the submodule, so updating the submodule updates it.
- **Faults.** `F_ORT_NR` is shown in hex like ISTA/INPA; present/stored and warning-lamp state come from the
  numeric fault-type codes (`0x20–0x23`, `0x30/0x31`) with a fallback to the SGBD texts.

INPA's compiled `.IPO` scripts are not executed. bmwDecode talks to the same SGBD jobs those scripts use, so
nothing is lost for diagnosis. Coding (NCS Expert) and programming are out of scope.

## Licence and disclaimer

GPL-3.0 (required by EdiabasLib, which is GPLv3). See [LICENSE.md](LICENSE.md).

BMW, INPA, EDIABAS, ISTA and NCS Expert are trademarks of BMW AG. This project is not affiliated with BMW and
does not distribute BMW software or data. **Do not commit SGBD/INPA files to this repository** (`.gitignore` blocks
`*.prg`, `*.grp`, `*.ipo`).

Use at your own risk: clearing faults and running writing jobs can change how the car behaves.
