# G-Diag — BMW F/G series diagnostics in plain English

An ImGui diagnostic app for BMW F, G (and early U) series cars through a **Tactrix OpenPort 2.0** (or any J2534
adapter) or an **ENET cable**, for macOS and Windows.
It is the BMW counterpart of [Diogenes-ImGui](https://github.com/Unblonded/Diogenes-ImGui/tree/macos), which does
the same job for Mercedes-Benz. The idea is the same: talk to the car's control units directly and explain what
comes back in plain English instead of German abbreviations and hex.

![Vehicle tab](docs/vehicle.png)

## What's different from the Mercedes version

| | Diogenes-ImGui (Mercedes) | G-Diag (BMW) |
|---|---|---|
| Adapter | J2534 pass-through (OpenPort, Macchina) | The same J2534 adapters (OpenPort 2.0), or an ENET cable |
| Protocol | KWP2000 / UDS over CAN, ISO-TP in the driver | UDS over BMW D-CAN (ISO-TP with extended addressing, done in the app) or over HSFZ for ENET |
| Data files | One CBF per control unit, loaded by the user | Not needed to read modules, faults and live data |
| German in | CBF names and texts | Coding (FDL) parameter names, explained in the Coding tab |

## Features

- **OpenPort 2.0 / J2534**: finds installed drivers (registry on Windows; Homebrew `openport-j2534` and
  `~/.passthru/*.json` on macOS) and talks BMW D-CAN on the OBD port. Or **ENET**: finds the car on the network.
- **Vehicle**: VIN decode, every control unit that answers with what it does, part numbers, and its
  SVK software list (hardware, bootloader, software and the CAFD coding file) with each part explained.
- **Fault codes**: read every module at once, status spelled out ("Active now", "Stored"), clear per module or all.
  BMW codes are maker-specific, so descriptions come from a `code;text` list you load (File > Load fault code texts).
- **Live data**: instrument-cluster style gauges for engine values (DME, standard OBD values via UDS `22 F4xx`;
  on the OpenPort, plain OBD-II mode 01 on `7E0`/`7E8` when the DME refuses), history sparklines, and your own DIDs.
- **Vehicle order (FA)**: open an E-Sys/ISTA FA (XML or `G020#0719*5U31$205...` text) and see the series,
  build date, paint and every option code with its name.
- **Coding explainer**: paste lines from E-Sys or a coding guide
  (`BDC_BODY > 3002 KomfortSchliessen > SPIEGEL_ANKLAPPEN_FB = aktiv`) and get
  `BDC_BODY (Body domain controller) > 3002 close comfort > fold mirror remote key = active`.
  It only reads text, nothing goes to the car.
- **Console** (View > Advanced mode): raw UDS to any module. Reads go straight out; anything that could change the car
  asks twice, with the "yes" button locked for 3 seconds.
- **Demo car**: a simulated G20 to try everything without a car (`--demo`, or File > Connect to the demo car).
  With OpenPort selected it runs over a simulated D-CAN bus, so the same CAN code is exercised.

![Live data](docs/live.png)
![Fault codes](docs/faults.png)
![Coding explainer](docs/coding.png)

## Connecting to a car

### Tactrix OpenPort 2.0 (J2534)

1. Install the driver.
   - **macOS** (Apple silicon and Intel): `brew install bisak/tap/openport-j2534`
     ([openport-j2534](https://github.com/bisak/openport-j2534), no kernel extension needed).
   - **Windows**: Tactrix's "OpenPort 2.0 J2534" driver from tactrix.com. It is 32-bit, which is why the Windows build is x86.
   - Other J2534 drivers: listed automatically on Windows; on macOS put a `~/.passthru/<name>.json` with `NAME` and
     `FUNCTION_LIB` (add `"LONG_SIZE": 8` for a driver built with C `unsigned long`), or press **Add** and pick the library.
2. Plug the OpenPort into the OBD port, ignition on (Start without the brake), battery charger for long sessions.
3. Step 1: **OpenPort 2.0 / J2534**, pick the driver. Step 2: **Connect**. The scan starts automatically.

How it talks: BMW D-CAN on OBD pins 6/14 at 500 kbit/s. Requests go out on CAN id `0x6F1` with the module address as
the first byte; modules answer on `0x600 + address` with `0xF1` first. ISO-TP is done in the app over a raw CAN
channel, so one channel reaches every module and the functional address `0xDF`.

### ENET cable

1. An ENET cable. G series cars need the activation resistor between OBD pins 8 and 16, which nearly every ENET cable has.
2. Leave the Ethernet adapter on "automatic" (DHCP). With no DHCP server, the computer and the car both pick a
   `169.254.x.x` address after a few seconds. On macOS allow Local Network access when asked.
3. Step 1: **ENET cable**, **Find**. Step 2: **Connect**.

## Command line

```
GDiag --demo                         start connected to the simulated car
GDiag --sim-server [port]            serve the simulated car over real HSFZ on 127.0.0.1 (default 6801)
GDiag --drivers                      list installed J2534 drivers
GDiag --scan j2534[:N]               list modules, CAFDs and fault codes through J2534 driver N (default 0)
GDiag --scan sim-dcan                the same against the demo car on a simulated D-CAN bus
GDiag --discover                     list cars on the ENET network
GDiag --scan 169.254.12.34[:port]    the same over ENET
GDiag --translate "SPIEGEL_ANKLAPPEN_FB"
GDiag --demo --screenshot out.png --tab live    render one frame to a PNG and quit
```

## Building

Needs the .NET 8 SDK.

```
dotnet run --project GDiag -- --demo     # run
dotnet test                              # 27 tests: D-CAN and HSFZ against the simulator, parsers, translator
mac/build-app.sh                         # G-Diag.app for Apple silicon (osx-x64 for Intel)
```

GitHub Actions runs the tests, builds the Mac and Windows zips and publishes them as a release on every push to `main` (`v1.0.<run number>`). Push a tag like `v2.0.0` to release under that name instead.

## Layout

- `GDiag/Net`: J2534 binding and driver lookup, D-CAN ISO-TP transport, HSFZ client and discovery, the simulated car
  (in-process, on a simulated CAN bus, and as an HSFZ server)
- `GDiag/Uds`: UDS requests (response-pending handling, negative response texts) and fault code parsing
- `GDiag/Bmw`: module addresses, SVK parsing, FA parsing and option names, VIN decode, live value definitions
- `GDiag/Translation`: the German→English translator from Diogenes-ImGui plus BMW coding vocabulary
- `GDiag/UI`: the ImGui tabs (theme, fonts and widgets are from Diogenes-ImGui)

## Status and limits — read before using on a car

- **Not yet tested on a real car or a real OpenPort.** The D-CAN and HSFZ code is tested end to end against the
  built-in simulator, which follows the protocols as documented by the open-source BMW tooling community. The J2534
  binding follows the J2534 v04.04 API and the driver handling of Diogenes-ImGui, but has not run against a physical
  adapter yet. Real gateways may differ in details, such as which DIDs each module answers.
- **G series and D-CAN:** BMW's own tools use ENET on G series. How much the G series gateway routes from the OBD CAN
  pins varies by model and software level: some modules may not answer over D-CAN that do over ENET. Standard OBD-II
  (the engine gauges' fallback) is required by law and always works.
- The module list is the usual F/G series addresses. Variants differ; "Scan every address" in Settings finds the rest.
- The SVK parser assumes the common layout (count, then 8 bytes per entry). Units that report differently show no parts list.
- Many DMEs do not answer the standard OBD values over ENET; those gauges then show "-".
- **No coding writes and no flashing.** Changing coding safely needs the CAFD files and E-Sys' checks. This app explains
  coding; use E-Sys or BimmerCode to change it.
- DoIP (port 13400, used by the newest U/G series gateways) is not implemented yet; those cars still answer HSFZ on most models.
- K-line (pins 7/8, pre-2008 cars) is not supported; F and G series don't need it.

Use at your own risk. Clearing fault codes or sending raw requests to a car can have consequences.
Not affiliated with BMW AG. BMW, E-Sys and ISTA are trademarks of their owners.

## License

MIT. Parts come from Diogenes-ImGui / CaesarSuite (MIT, © 2020 JinGen Lim), see [LICENSE](LICENSE).
Material Icons font: Apache 2.0, see `GDiag/Fonts/MaterialIcons-LICENSE.txt`.
