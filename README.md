# bmwDecode

Modern Windows 11 diagnostics for **every BMW E-series** car – E30 to E93, K-line (DS2) and D-CAN (BN2000),
including the **E84 X1** – built as a
replacement for the INPA workflow: identify the car, run a quick test of every module, read and clear fault
memory with freeze-frame data, watch live values and run any SGBD job.

It uses your existing INPA/EDIABAS **SGBD files** (`C:\EDIABAS\Ecu`) and an ordinary **K+DCAN USB cable**, but
does not need BMW's 32-bit EDIABAS runtime, INPA scripts or any COM-port juggling beyond picking the port.

![platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4) ![dotnet](https://img.shields.io/badge/.NET-10%20LTS-512BD4) ![license](https://img.shields.io/badge/license-GPLv3-green)

## Features

| Page | What it does |
| --- | --- |
| **Overview** | Connect (K+DCAN cable via its COM port, ENET, or the built-in demo car), read VIN and vehicle order (FA → series via `FA.PRG`), battery and ignition via `UTILITY.PRG`, pick the chassis profile. Warns when a swapped module (e.g. an E90 CAS in an E84) reports a different chassis. |
| **Vehicle / VIN** | RealOEM-style identification from a full or short (last 7) VIN, offline: manufacturer, type key, plant, check digit/model year, plus model, engine, gearbox, body, power and market from the type key tables. With the car connected it adds the vehicle order (build date, paint, upholstery, option codes) and the fitted engine ECU. Buttons for RealOEM and the free NHTSA online decode. |
| **Modules (car-reported)** | After connecting, the car itself reports its modules: one functional `IDENT_FUNKTIONAL` broadcast through the main-series SGBD (`E89X`, `E60`, `E65`, `E70`) returns every ECU with its exact variant SGBD and fault count, like ISTA. DS2 cars are probed module by module. The list is ranked by importance: engine, transmission, brakes, airbag, CAS, cluster, gateway, steering… |
| **Jobs & coding (per module)** | Every job of that module's SGBD, grouped (fault memory, status, identification, actuator tests, adaptations & resets, coding, programming). Translated documentation, arguments, run with results, and *Back up coding*, which runs every coding read job and saves the results. Writing jobs always ask first. |
| **Quick test** | Tries every module slot of the chassis (group files `D_MOTOR`, `D_CAS`, `D_DSC`… from the ISTA-derived catalogue), identifies the fitted variant (`D60M57A0`, `CAS`, `DSC_87`…), reads `IDENT` and `FS_LESEN`. Clear all faults, export HTML/CSV report. |
| **Modules** | Per-module identification, fault memory with environmental conditions (`FS_LESEN_DETAIL`: mileage, frequency, `F_UWn` values), info memory (`IS_LESEN`), clear, and **live data**: tick any argument-free `STATUS_*` jobs and poll them with min/max. |
| **Job explorer** | Tool32-style: browse all 2,000+ SGBDs offline, read job/argument/result documentation, run any job with arguments. Writing jobs (coding, flashing, actuators, clearing) need explicit confirmation. |
| **Log** | Every connection, variant identification and job, exportable. Optional EDIABAS interface trace. |
| **Settings** | EDIABAS Ecu folder (auto-detected), theme (Windows / light / dark, with Mica), defaults. |

The UI is WPF on .NET 10 with the built-in **Windows 11 Fluent theme**: follows the Windows light/dark setting and
accent colour, Mica backdrop, per-monitor DPI.

## English texts

BMW's SGBD texts are German. bmwDecode translates them offline. It checks an exact table of 2,289 common phrases
first (fault types, conditions, frequent locations), then a 6,000-word automotive glossary with compound splitting.
"Kühlmitteltemperatur Signal unplausibel" becomes "Coolant temperature signal implausible", and codes such as
`0x2FC`, `PT-CAN` or `D60M57A0` stay untouched. Put corrections in `%LOCALAPPDATA%\bmwDecode\translations.tsv`
(`German<TAB>English`), or switch translation off in Settings.

## Vehicle identification

On connect the VIN is read from the car (CAS, light module, footwell module, gateway; on DS2 cars the coding key),
and the chassis profile is chosen from it:

1. **ISTA history**: cars ISTA has identified on this PC. Its plain-XML session files (`RG_META_*.xml`,
   `RG_TRANS_*.xml`) hold VIN, type key, series, sales name, engine, gearbox and build date. ISTA's encrypted
   databases are never opened.
2. **Type key** (VIN 4–7) from `FZGIDENT.PRG` or your `typekeys.csv`.
3. **Vehicle order** read from the car, then the main-series SGBD that answers.

You can still type a VIN on the *Vehicle / VIN* page, or change the profile by hand.

## Do I need INPA or ISTA installed?

No. bmwDecode has its own EDIABAS engine, so it needs only:

- the **SGBD files**: the `Ecu` folder of an INPA/EDIABAS package (`*.prg`, `*.grp`). Copy it anywhere. An existing
  `C:\EDIABAS\Ecu` is found automatically. INPA, EDIABAS configuration and the COM1 requirement are not needed.
- the **FTDI driver** for the K+DCAN cable.

ISTA is never used. Without either, the demo car and the offline VIN decoder still work.

## Supported cars

| Generation | Chassis | Protocol / cable setting | Identification |
| --- | --- | --- | --- |
| BN2000 | E60–E64, E65–E68, E70–E72, E81/E82/E84/E87/E88, E89, E90–E93 | D-CAN | VIN + vehicle order from CAS/LM/FRM, decoded with `FA.PRG` |
| DS2 | E36, E38, E39, E46, E52, E53, E83, E85/E86 | K-line | VIN + coding key (`ZCS_*.PRG`: series, model, engine, gearbox…) and build date |
| Classic (generic profile) | E30, E31, E32, E34, E36/7 Z3 | K-line, 20-pin round socket adapter | VIN via DS2 jobs; every DS2 module is probed |

Each chassis profile lists the module addresses and group files from the ISTA-derived catalogue, and the actual
variant fitted is identified on the car. Engine swaps, CAS swaps and retrofits are therefore detected rather than assumed.

### VIN and type keys

The type key (VIN positions 4–7) is looked up in `FZGIDENT.PRG` from your Ecu folder (about 2,200 type keys,
E36–E93) and in an optional `%LOCALAPPDATA%\bmwDecode\typekeys.csv` you can extend
(*Vehicle / VIN → Edit type key list*). Newer models missing from FZGIDENT (e.g. E84) can be added there. The car's
own data (vehicle order, ZCS, engine ECU) always takes precedence.

## Getting started

1. **Download** `bmwDecode-win-x64.zip` from the latest [Actions run](../../actions) (or a [release](../../releases)),
   unzip anywhere and run `bmwDecode.exe`. It is self-contained, so no .NET install is needed.
2. **SGBD files**: point *Settings → EDIABAS ECU folder* at the `Ecu` folder of your INPA install
   (normally `C:\EDIABAS\Ecu`). It is auto-detected when present. bmwDecode never ships BMW files.
3. **Cable** (K+DCAN USB):
   - Install the FTDI driver that came with the cable (`D-CAN.exe` / `OBDSetup.exe` in the INPA package).
   - 2007+ cars (E8x / E9x / **E84**, E6x/E7x) use **D-CAN**: set the cable's switch to D-CAN (pins 7 and 8 *not*
     bridged). Older cars (E36, E38, E39, E46, E53, E83, E85) use **K-line**. Pre-1996 cars with the round 20-pin
     socket need a 20-pin → OBD adapter.
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

## Installer

`bmwDecode-x.y.z-setup.exe` (from CI) installs the self-contained app. If INPA/EDIABAS or ISTA is on the PC, it
offers to copy their SGBD files into `C:\ProgramData\bmwDecode\Ecu`, so bmwDecode keeps working after they are
removed. It also sets the FTDI latency timer to 1 ms.

For a **fully standalone private installer**, run `installer\build-private.ps1` on your own PC (needs the .NET 10
SDK and Inno Setup 6). It also carries your SGBD files and the K+DCAN FTDI driver:

```powershell
.\installer\build-private.ps1 -EcuPath C:\EDIABAS\Ecu -DriverPath "D:\BMW INPA v5.0.6\Driver_D_CAN_USB\Driver"
```

BMW's SGBD files are BMW's property, so a private installer is for your own machines only. Never publish one.

## Private data server (no INPA / ISTA on new laptops)

bmwDecode can fetch the BMW data it needs (the Ecu folder and the built-in data pack) from your own password-protected
server, so a fresh Windows laptop or Mac only needs the app:

1. **Server, once:** on the VPS run `sudo bash installer/server/setup-data-server.sh tinker.ftw.co.ke` and choose the
   password. It adds an HTTPS site (Caddy, own certificate) serving `/srv/bmwdecode-data` to that password only.
2. **Upload, from the laptop with INPA/ISTA:** `.\installer\upload-data.ps1` merges the SGBDs, builds the data pack,
   and copies `ecu.zip`, `sgbd.pack` and `manifest.json` to the server over SSH. Run it again whenever you have newer data.
3. **Any new install:** on first start bmwDecode asks for the password once, downloads everything into
   `%LOCALAPPDATA%\bmwDecode\Data` (Mac: `~/Library/Application Support/bmwDecode/Data`) and works offline from then on.
   Later starts fetch only what changed. Settings → BMW data shows the status and can update on demand.

The password and the data never go into this repository.

## macOS (Apple Silicon and Intel)

bmwDecode also runs on a Mac, with the same pages and the same diagnostics engine as the Windows app.

1. **Get the app.** Download `bmwDecode-…-macos-arm64.dmg` (M1/M2/M3/M4) or `…-macos-x64.dmg` (Intel) from the
   latest build's artifacts or a release, open it and drag **bmwDecode** to Applications.
2. **First start.** The app is not notarised by Apple, so macOS blocks the first launch. Open it once, then go to
   **System Settings → Privacy & Security** and click **Open Anyway** (or run
   `xattr -dr com.apple.quarantine /Applications/bmwDecode.app` in Terminal).
3. **Copy your BMW files from the Windows PC** (they are not bundled):
   - `C:\EDIABAS\Ecu` (or the merged Ecu folder) → `~/Library/Application Support/bmwDecode/Ecu`
   - the data pack `artifacts\data\sgbd.pack` from a private build → `~/Library/Application Support/bmwDecode/Data/sgbd.pack`

   bmwDecode finds both there automatically (Settings shows what it found).
4. **Cable.** macOS has the FTDI driver built in: plug the K+DCAN cable in (USB-C to USB-A adapter on a MacBook) and
   choose the `/dev/cu.usbserial-…` port on the Overview page. Same D-CAN / K-line switch rules as on Windows.
   macOS does not let apps set the FTDI latency timer; if a module times out, scan it again.

To build it yourself on a Mac: install the .NET 10 SDK and run `installer/macos/make-app.sh osx-arm64`.

## Roadmap: F/G series

The engine (EdiabasLib) already speaks ENET/DoIP, ISTA's Ecu folder contains the F/G SGBDs (`G_ZGW`, `ZGW_01`,
`F01`…), and the chassis catalogue includes F/G/I series. Support needs ENET cable handling, BN2020 module
discovery (`STATUS_VCM_GET_ECU_LIST_ALL` on the central gateway) and UDS fault parsing. These are planned once the
E-series side is proven on real cars.

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
