# Watts

How much power is this computer drawing right now, at the wall socket? Watts gives a running
**estimate**, in watts, plus the session's total in watt-hours, from inside a game or any other
program. It works on macOS, Windows and Linux, with no admin rights.

> [!WARNING]
> **Work in progress, and an estimate, not a meter.** Watts is young (0.2) and still being
> tested on more hardware, so its numbers and its API may change. Expect the figure to be off by
> 10–15% on an Apple Silicon Mac and by 20–40% on a Windows or Linux desktop. It leaves out any
> external monitor. Each figure comes labelled **measured** (built mostly from the machine's own
> power sensors) or **estimated** (built mostly from a model of the hardware), and even a
> measured figure adds the power supply's losses as an assumption. Nothing has been checked
> against a wall meter yet. If you need watts you can bill or publish, put a meter at the socket.
> See [Status and what's next](#status-and-whats-next).

<p align="center"><img src="https://raw.githubusercontent.com/MattiaWasFound/watts/main/docs/images/watts-chart.svg" width="760" alt="A line chart of wall watts measured once a second on a MacBook Air: about 6 W idle, rising to about 14 W within two seconds when every core is busy, and back to about 6 W."></p>

It was made for an art game that charges the player for the electricity their own computer uses
while they play. Anything that wants an honest "this session used about X Wh" can use it: an
energy readout in a game, a sustainability study, a benchmark tool.

- **Unity package, NuGet package, a C library, or a command-line program** that prints JSON,
  so it works from any language or engine.
- **No admin rights, no driver, no permission prompt, no network.**
- **Always returns a number.** When a sensor is missing or fails, it is skipped quietly and the
  model fills in. The label says which happened.
- **Pure C#.** Sensors are reached by P/Invoke into the operating system's own libraries and by
  reading sysfs, so the Unity package ships no native binary. It works on Mono and IL2CPP.
- **Cheap.** One background thread samples once a second. Reading the value never blocks.

## Unity

### Install

In Unity (2021.3 or newer), open *Window → Package Manager → + → Add package from git URL*
and enter:

```
https://github.com/MattiaWasFound/watts.git?path=unity/Watts
```

To pin a version, add a tag: `…?path=unity/Watts#v0.2.0`.

### Use

The meter starts itself before the first scene loads. There is nothing to add to a scene.

```csharp
using UnityEngine;
using Watts;

double watts = WattsMeter.Watts;                // wall power right now
double wattHours = WattsMeter.SessionWattHours; // since the game started
bool measured = WattsMeter.Confidence == Confidence.Measured;
bool screen = WattsMeter.DisplayIncluded;       // true only for a laptop's built-in panel

Debug.Log(WattsMeter.Hardware);                 // what it detected: CPU, GPU, laptop or desktop
Debug.Log(WattsMeter.Current);                  // everything, plus what was measured and what modelled
WattsMeter.ResetSession();                      // start the watt-hour total again at zero
```

To turn energy into money, multiply: `cost = WattsMeter.SessionWattHours / 1000 * pricePerKWh`.
Prices are out of scope on purpose. They depend on where the player is and when.

`WattsMeter.Current` is one immutable `PowerReading` (`Watts`, `SessionWattHours`, `Confidence`,
`DisplayIncluded`, `Source`), so read it once if you show several fields together. `Source` is
for logs. It reads like `CPU 17.1 W (model, 4 % busy) + GPU 12.1 W (NVML card 0) + rest 25 W
(model), ÷0.81 for adapter/PSU losses`.

**What the figure covers.** The whole computer, every app included, as a meter at the wall
socket would see it. Supply losses are added in. An external monitor is never included, because
nothing inside the computer can see its draw. A laptop's built-in screen is included, and
`DisplayIncluded` says so.

**GPU load.** Where no system counter reports GPU load (macOS, and Linux without an AMD or NVIDIA
card), the model assumes the game keeps the GPU half busy. Turn on *Frame Timing Stats* in
Player Settings and the meter uses the game's own GPU frame time instead.

**Platforms.** The meter runs on desktop players and in the Editor. On other platforms (mobile,
WebGL, consoles) it returns the model's first estimate, labelled `Estimated`.

## Other ways to use it

### .NET (Godot C#, Stride, desktop apps, tools)

```
dotnet add package Watts
```

```csharp
using Watts;

using var meter = new PowerMeter();   // pass new GpuInfo("<GPU name>", memoryMB) if you know it
meter.Start();                        // samples once a second on a background thread
// ...
PowerReading r = meter.Current;       // Watts, SessionWattHours, Confidence, DisplayIncluded, Source
meter.SetGameGpuLoad(0.6);            // optional: how busy you keep the GPU, if you know it
```

Targets netstandard2.1, so .NET Core 3.0 and later, Mono and Unity all load it.

### Any language: the `watts` command

Download `watts-<os>-<arch>` for your machine from the
[latest release](https://github.com/MattiaWasFound/watts/releases/latest) (for example
`watts-win-x64.exe`, `watts-osx-arm64`, `watts-linux-x64`), rename it to `watts` and put it on
your PATH. It is one file with the .NET runtime inside, so nothing else needs installing. The
files are not signed: on macOS and Linux run `chmod +x watts`, on macOS also
`xattr -d com.apple.quarantine watts`, and on Windows choose *More info*, *Run anyway*. It prints
one JSON line per second until stopped:

```
$ watts
{"time":"2026-10-05T23:32:19.018+02:00","watts":33.36,"wh":0.00948,"confidence":"measured","display":false,"source":"SMC PSTR (whole system): 30.0 W DC, ÷0.90 for adapter/PSU losses"}
```

`watts --once` waits two seconds (load counters need two samples) and prints a single line.
`--gpu "<name>"` names the GPU when the OS can't tell which one you use. From Python:

```python
import json, subprocess
with subprocess.Popen(["watts"], stdout=subprocess.PIPE, text=True) as p:
    for line in p.stdout:
        print(json.loads(line)["watts"])
```

The command assumes nothing is rendering. In a game, prefer a library, which can be told the
GPU load.

### C, C++, Rust, Python, other engines: `libwatts`

A native shared library with a small C API, built ahead of time with .NET NativeAOT, so nothing
else needs installing. Download `libwatts-<os>-<arch>.zip` from the
[latest release](https://github.com/MattiaWasFound/watts/releases/latest). It holds the library
(`libwatts.dylib`, `libwatts.so`, or `libwatts.dll` with its import library `libwatts.lib`), the
header [`include/watts.h`](https://github.com/MattiaWasFound/watts/blob/main/include/watts.h) and
examples in C and Python. The build commands for each OS are at the top of
`examples/watts_example.c`.

```c
#include "watts.h"

watts_start(NULL);                   /* or the name of the GPU you render on */
/* ... */
watts_reading r = { sizeof r };      /* struct_size first, so the struct can grow */
watts_read(&r);
printf("%.1f W, %.4f Wh, %s\n", r.watts, r.session_wh, r.measured ? "measured" : "estimated");
watts_set_gpu_load(0.6);             /* optional: how busy you keep the GPU */
watts_stop();
```

All functions are safe from any thread and never block. One meter per process. Every function
is checked against the header on macOS, Linux and Windows in CI (`bin/test-native`). The library is
about 3 MB. The macOS build is ad-hoc signed: an app you notarize signs it with your own key, as
with any library you embed.

## How good is the number?

On top of whatever a sensor measures, the wall figure divides by the supply's efficiency. A
reading is `Measured` when a whole-system sensor gives it, or when measured parts make up at least
75% of the total. The aim is the right ballpark, honestly labelled, not lab precision: within
about 30% is good.

| Platform and hardware | Measured | Modelled | Label | Likely error at the wall |
| --- | --- | --- | --- | --- |
| Mac, Apple Silicon (desktop or laptop) | Whole system: SMC key `PSTR` | Supply losses | Measured | ±10–15% |
| Mac, Intel, with `PSTR` | Whole system: SMC `PSTR` | Supply losses | Measured | ±15% (no Intel Mac tested yet) |
| Mac, Intel, no `PSTR`, on battery | Whole system: battery drain | Charger losses | Measured | ±15–20% |
| Mac, Intel, no `PSTR`, on the charger | CPU load | Everything | Estimated | ±40% |
| Windows or Linux laptop on battery | Whole system: battery drain rate | Charger losses | Measured | ±15–20%. Gauges lag 30 s or more; the total over minutes is sound. |
| Windows or Linux laptop on the charger | CPU package power (EMI on Windows, where the firmware offers it), NVIDIA GPU power, CPU and GPU load | Other GPUs, the rest | Estimated, or Measured when sensors cover 75% | ±25–40% |
| Windows desktop, NVIDIA GPU | GPU power (NVML), CPU and GPU load | CPU, board, supply | Estimated | ±25–40%. The CPU is modelled, and turbo on or off moves its full-load draw by 2×. |
| Windows desktop, AMD or Intel GPU | CPU and GPU load | Everything | Estimated | Worse than ±40% for a big card (see below) |
| Linux desktop, AMD GPU | GPU power and load (amdgpu), CPU load | CPU, board, supply | Estimated | ±25% |
| Linux desktop, NVIDIA GPU | GPU power and load (NVML), CPU load | CPU, board, supply | Estimated | ±25% |
| Linux desktop, AMD APU only | SoC power (amdgpu), CPU load | Board, supply | Measured when the SoC is most of the draw | ±20% |
| Linux desktop, Intel graphics only | CPU load | Everything | Estimated | ±40% |
| Virtual machine | CPU load | Everything, scaled to its vCPUs | Estimated | Not meaningful: the host's draw is hidden |

The sensor names (SMC, EMI, NVML, RAPL…) are explained in
[how it works](https://github.com/MattiaWasFound/watts/blob/main/docs/how-it-works.md#terms).

**Strongest:** Apple Silicon, where the Mac's own whole-system sensor reads without any
privileges. **Weakest:** Windows and Linux desktops, where nothing a normal user can read reports
CPU power. A big AMD or Intel graphics card on Windows is the worst case: an idle card can show
50% busy at its lowest clock while drawing 5% of its power limit, and the model charges that as
half its power.

None of these totals has yet been checked against a wall meter. The error figures come from
cross-checks between sensors and the model on the machines listed in
[eval/model-vs-measured](https://github.com/MattiaWasFound/watts/blob/main/eval/model-vs-measured/README.md). The reasoning behind each number is
in [docs/research](https://github.com/MattiaWasFound/watts/blob/main/docs/research/README.md).

## What elevated rights would buy

The package never asks for them. If you control the machine (an installation, a lab), these
help:

| Platform | One-time privileged step | What it gains |
| --- | --- | --- |
| Linux | Make RAPL readable: `sudo chmod a+r /sys/class/powercap/intel-rapl:*/energy_uj` (a udev rule makes it survive reboots) | Measured CPU package power on Intel and AMD. A desktop with an AMD or NVIDIA card becomes Measured, about ±15–20%. This reopens a side channel the kernel closed on purpose ([Platypus, 2020](https://platypusattack.com/)). |
| Windows | A kernel driver that reads the CPU's model-specific registers (LibreHardwareMonitor, WinRing0) | Measured CPU package power on every desktop. Not recommended: Defender flags WinRing0, and anti-cheat software objects to it. |
| macOS, Intel without `PSTR` | `sudo powermetrics` | CPU and GPU power. Few machines, and a game cannot ask for sudo. |
| Any | A smart plug or wall meter with a local API | True wall power, monitor included. Needs a network call, which this package does not make. |

Apple Silicon needs nothing extra.

## Status and what's next

Tested so far on an M4 Mac mini, an M2 MacBook Air, an ASUS ROG Zephyrus G14 under Windows, one
Intel and NVIDIA Windows desktop, and a Linux virtual machine. The figures above come from those
runs ([eval/model-vs-measured](https://github.com/MattiaWasFound/watts/blob/main/eval/model-vs-measured/README.md)).
Being worked on, roughly in order:

- **Checks against a wall meter.** The supply factors and the 25 W "rest of system" on a PC
  desktop are typical values, not measured ones.
- **GPU power on Windows without NVIDIA.** An AMD or Intel card's busy share says little about
  its power. The probe now logs what the Windows driver itself reports (power share, engine
  clock), to see whether that can replace it.
- **A CPU model that knows the clock.** Turbo on or off changes a desktop CPU's full-load draw by
  about 2×. The probe now logs the CPU clock against nominal.
- **More hardware:** Intel Windows laptops, Linux laptops and desktops with real sensors, Intel
  Macs, AMD desktops.
- **Later:** mobile (iOS, Android), and a "this game's share" figure beside the whole-machine one.

Until 1.0, minor versions may change the API. Pin a version (a git tag in Unity, a version on
NuGet) if that matters to you.

## Help it get better

The fastest way to improve the numbers is more machines. `watts-probe` is a small program that
runs the same code outside Unity. It lists what each sensor sees, then logs the meter beside the
model alone through 15 s idle, 20 s of full CPU load and 15 s idle again. Download it from the
[latest release](https://github.com/MattiaWasFound/watts/releases/latest), run it, and
attach the report to a [field report issue](https://github.com/MattiaWasFound/watts/issues/new?template=field-report.yml).
Reports with a wall meter or smart plug reading alongside are worth the most. See
[docs/field-test.md](https://github.com/MattiaWasFound/watts/blob/main/docs/field-test.md) and [CONTRIBUTING.md](https://github.com/MattiaWasFound/watts/blob/main/CONTRIBUTING.md).

## More

- [docs/how-it-works.md](https://github.com/MattiaWasFound/watts/blob/main/docs/how-it-works.md): the sensors on each OS, how readings combine,
  the model, and how energy is integrated.
- [docs/research](https://github.com/MattiaWasFound/watts/blob/main/docs/research/README.md): what was found while building it, with the evidence.
- [eval](https://github.com/MattiaWasFound/watts/tree/main/eval): the measurements, raw reports and scripts to rerun them.
- [CHANGELOG.md](https://github.com/MattiaWasFound/watts/blob/main/CHANGELOG.md)

MIT licensed. See [LICENSE](https://github.com/MattiaWasFound/watts/blob/main/LICENSE).
