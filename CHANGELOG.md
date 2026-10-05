# Changelog

## 0.2.0 (2026-10-05)

First public release.

- **`watts` command.** The meter as a program that prints one JSON line per second, for any
  language that can start a process.
- **NuGet package `Watts`.** The same core for any .NET host.
- **`libwatts`.** A native library with a C API (`include/watts.h`) for C, C++, Rust, Python and
  other engines, built with NativeAOT for six targets. Examples in C and Python.
- **Windows diagnostics in `watts-probe`.** Each row logs the CPU clock against nominal (turbo on
  or off), the Energy and Power Meter counter sets, and each GPU's driver-reported power share,
  temperature and engine clock (D3DKMT perf data).
- **Reports carry no machine name.**
- Documentation: how it works, research notes, field test guide.

## Before 0.2.0 (2026-10-04, never released)

Development builds, field-tested before the first release. Probe runs on an M4 Mac mini, an M2
MacBook Air, an ASUS G14 under Windows, an Intel and NVIDIA Windows desktop, and a Linux VM, led to:

- SMC `PSTR` as the whole-system sensor on Apple Silicon, and the finding that it excludes
  battery charging.
- EMI CPU package power on Windows, with a rule that never adds the package to its own rails.
- Battery reads from the Windows battery driver, with a guard against frozen figures.
- GPU load from the adapter the game renders on, not the busiest one.
- Model fixes: Apple idle, Apple laptops, PC laptop CPU idle, idle laptop GPUs, VMs.

The details are in [docs/research](docs/research/README.md) and
[eval/model-vs-measured](eval/model-vs-measured/README.md).
