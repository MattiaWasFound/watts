# How it works

This describes version 0.2, a work in progress. The numbers in the model will change as more
hardware is tested.

## Terms

| Term | What it is |
| --- | --- |
| SMC, `PSTR` | Apple's System Management Controller, and its key for the whole machine's power ([note](research/apple-smc-pstr.md)) |
| EMI | Windows' Energy Meter Interface: energy counters that firmware and drivers publish, often the CPU package ([note](research/windows-emi.md)) |
| RAPL | Intel's and AMD's running-average power limit counters for the CPU package, under `/sys/class/powercap` on Linux ([note](research/linux.md)) |
| NVML | NVIDIA's management library, shipped with the driver: board power and busy share |
| amdgpu hwmon | The Linux AMD GPU driver's power and busy-share files under `/sys/class/drm` |
| PDH | Windows' Performance Data Helper, the performance counters Task Manager also reads |
| D3DKMT | Windows' kernel graphics interface: which adapter is which, and per-adapter perf data |
| `host_statistics` | The macOS call that reports CPU time per state, used for CPU load |
| Supply factor | The power supply's efficiency: wall watts = DC watts ÷ factor |

## The loop

`PowerMeter` runs one background thread at below-normal priority. Once a second it asks every
sensor what it can see. Each sensor answers with zero or more readings, each tagged by part:

| Part | Unit | Example source |
| --- | --- | --- |
| `System` | watts, plus the supply factor and whether a screen is included | SMC `PSTR`, battery drain |
| `Cpu` | watts | EMI package channel, RAPL, amdgpu on an APU |
| `Gpu` | watts | NVML, amdgpu |
| `CpuLoad` | busy share 0–1 | `host_statistics`, `GetSystemTimes`, `/proc/stat` |
| `GpuLoad` | busy share 0–1 | PDH GPU Engine counters, NVML, amdgpu, the game's frame timings |

Sensors only report. One function, `PowerMeter.Compose`, decides what the figure is:

1. **A whole-system reading wins outright.** The first `System` reading becomes the figure.
2. **Otherwise the total is built part by part.** Each part uses its measured reading if one
   came in, and `PowerModel` otherwise. The parts are the CPU, the GPU (integrated or discrete),
   a fixed rest of system, and a laptop's built-in screen.
3. **DC becomes wall.** The DC total is divided by the supply factor.
4. **The label.** `Measured` when a `System` reading was used, or when measured parts make up at
   least 75% of the DC total. Otherwise `Estimated`.

The result is published as one immutable `PowerReading`. Reading it is a reference read, so the
game thread never waits on the sampler.

## Failure handling

The meter must never crash or block the game, and must always have a number.

- A sensor whose constructor throws (library missing, no such hardware, permission denied) is
  dropped for the session. Listing a sensor that may not exist costs one failed attempt.
- A sensor that throws while reading three times in a row rests for 60 s, then is tried again.
- A reading outside a plausible range (for example a GPU above 1500 W) counts as a failure.
- On Windows, a battery figure that has not changed for 20 s is treated as frozen and dropped,
  so the part-by-part path takes over.
- Before the first sample lands, the figure is the model's estimate at a 30% CPU load.

## Energy

Watt-hours are integrated by the trapezoid rule between ticks. A gap longer than 5 s (the
machine slept, the process was suspended) is charged as one 1 s interval, not its full length,
so a laptop waking from sleep isn't billed for the hours it slept.
The total is not saved anywhere. `ResetSession()` starts it at zero.

## Sensors by OS, in the order they are tried

| OS | Sensors |
| --- | --- |
| macOS | SMC `PSTR` via the AppleSMC user client (IOKit) · battery via the IORegistry (`AppleSmartBattery`) · CPU load via `host_statistics` |
| Windows | Battery via the battery driver (`IOCTL_BATTERY_QUERY_STATUS`), else `CallNtPowerInformation` · EMI energy meters via SetupAPI and `DeviceIoControl` · NVML (only when the GPU name says NVIDIA) · CPU load via `GetSystemTimes` · GPU load via the PDH "GPU Engine" counters, for the adapter the game renders on |
| Linux | Battery via `/sys/class/power_supply` · RAPL via `/sys/class/powercap` (usually root-only) · amdgpu hwmon (`power1_average` / `power1_input`, `gpu_busy_percent`) · NVML · CPU load via `/proc/stat` |

Every call goes to a library by absolute path or to a file, so nothing has to ship beside the
game. On Windows there is no WMI (Unity's Mono has no `System.Management`).

## The model

`PowerModel` holds every estimated number in one file. It works from the CPU and GPU names and
the busy shares.

**CPU.** A full-load figure by class, taken from the model name. These are typical sustained
power limits, not datasheet peaks:

| Class | Full load (W) |
| --- | --- |
| Apple M / Pro / Max / Ultra | 18 / 35 / 45 / 80 (laptops × 0.7) |
| Intel desktop K, Ryzen x900X | 125–140 |
| Intel and Ryzen desktop, no suffix | 65 |
| Intel H / HX, Ryzen H / HS / HX | 45 / 55, 45 / 35 / 55 |
| Intel and Ryzen U | 20 |
| Server (Xeon, EPYC) | 150, capped at 5 W per vCPU in a VM |

Idle is about 1 W on Apple Silicon, 15% of full load (at least 3 W) on a PC laptop, and 10% (at
least 5 W) on a PC desktop. Between them, power is linear in the busy share.

**GPU.** Integrated GPUs share the CPU's budget: 30% of the CPU figure at full load on a PC, 100%
on Apple Silicon. Discrete cards get a board-power class from the model number (an RTX xx70 is
210 W, an RX 6800 or 7800 260 W). Laptop cards get 45% of that, and at least 35 W. A laptop
card idles at 1 W (hybrid laptops power it off), a desktop card at 6% of its class (at least 8 W).

**Rest of system.** Board, memory, storage and fans: 25 W for a PC desktop, 6 W for a desktop
Mac, 4 W for a PC laptop, 1.5 W for an Apple laptop, 5 W for a VM. Laptops add the screen: 4 W,
or 2.5 W on an Apple laptop.

**Supply.** Wall = DC ÷ factor. 0.90 for a desktop Mac, 0.87 for a laptop adapter. A PC desktop's
ATX supply follows a curve: 0.78 at 30 W, 0.86 at 100 W, 0.89 from 200 W up. A battery's drain is
charged at the adapter's factor too, because that energy comes back through the charger later.

## Hardware detection

`HardwareProfile.Detect` reads the CPU name (`sysctl` on macOS, the registry on Windows,
`/proc/cpuinfo` on Linux), the GPU name (from Unity's `SystemInfo`, else the OS), whether the
machine is a laptop (a battery, a chassis type) and whether it is a virtual machine (the
hypervisor flag, DMI vendor). Windows maps the GPU name to the PDH adapter LUID through D3DKMT,
so GPU load comes from the card the game draws on, not from whichever adapter is busiest.

## Code map

| Path | What |
| --- | --- |
| `unity/Watts/Core/` | Engine-free core. Also compiled by `dotnet/Watts.Core` as netstandard2.1, C# 9, which is how Unity sees it. |
| `unity/Watts/Core/PowerMeter.cs` | The loop, `Compose`, failure handling, integration |
| `unity/Watts/Core/PowerModel.cs` | Every estimate number |
| `unity/Watts/Core/Platform/` | One file per OS, plus `NvmlSensor`. `Platform.SensorsFor` is the try order. |
| `unity/Watts/Unity/WattsMeter.cs` | The static facade and the frame-timing feed |
| `unity/WattsHarness/` | A Unity project that references the package by path: PlayMode tests and player builds |
| `dotnet/Watts.Tests/` | Unit tests, including a fake sysfs tree and the Windows struct and EMI parsers |
| `dotnet/Watts.Probe/` | `watts-probe`, the field test |
| `dotnet/Watts.Cli/` | `watts`, the meter as JSON lines on stdout |
| `dotnet/Watts.Native/`, `include/watts.h` | `libwatts`, the C API, compiled by NativeAOT |
| `examples/` | Using `libwatts` from C and Python |
| `tests/native/` | The C test of `libwatts`, run by `bin/test-native` |
| `eval/` | Measurements behind the model's numbers, with how to rerun them |

## Known limits

- **Linux CPU power** (RAPL) is root-only on most kernels since 5.10. The sensor is listed
  anyway and reports unavailable. See [research/linux.md](research/linux.md).
- **Unity's Linux player** crashed at start on an Ubuntu 26.04 VM even for an empty project, so
  on Linux the core is verified through `watts-probe`, not a player.
- **SMC `PSTR` excludes battery charging,** so a charging Mac is billed for its own draw only. The
  IORegistry battery current lags by tens of seconds. See
  [research/apple-smc-pstr.md](research/apple-smc-pstr.md).
- **Battery gauges lag** a load change by 30 s or more on Windows. See
  [research/windows-battery.md](research/windows-battery.md).
- **Supply factors are assumed.** `PSTR`, EMI, NVML and the model are all DC figures behind the
  supply. No wall meter has checked the factors yet.
