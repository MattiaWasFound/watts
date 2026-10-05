# Windows: the Energy Meter Interface

**Question.** Is there any CPU power reading on Windows that a normal user can open?

**Background.** Windows 10 added the Energy Meter Interface (EMI): a device interface
(`{45BD8344-7ED6-49CF-A440-C276C933B053}`) that firmware and drivers use to publish energy
counters. A program finds the devices with SetupAPI, opens each one, and asks it with
`DeviceIoControl`:

| IOCTL | Returns |
| --- | --- |
| `0x226000` `IOCTL_EMI_GET_VERSION` | 1 or 2 |
| `0x226004` `IOCTL_EMI_GET_METADATA_SIZE` | size of the metadata block |
| `0x226008` `IOCTL_EMI_GET_METADATA` | channel names, units |
| `0x22600C` `IOCTL_EMI_GET_MEASUREMENT` | per channel: energy in picowatt-hours, time in 100 ns |

Power is the energy difference over the time difference between two reads.

## ASUS ROG Zephyrus G14 (Ryzen 9 6900HS, Radeon RX 6800S), 2026-10-04

**EMI opened without admin** and exposed AMD's SMU channels: `VDDCR_VDD Power`,
`VDDCR_SOC Power`, `Current Socket Power` and `Apu Power`. The socket channel read 8–10 W at 1%
busy and 25 W at all-core load, and followed a load step within a second.

**Pitfalls.** Socket power already contains the SoC rail, so adding every CPU-looking channel
counts the SoC twice (about 4 W high on this machine). Names must be matched as whole words:
"Socket" is not "SoC", and a DRAM rail is not the package. The rule (`PackageWatts`):

1. A channel whose name has the whole word PKG, PACKAGE or SOCKET is the package. It wins.
2. Otherwise add up the rails named CPU, SOC, VDD or CORES.
3. Otherwise there is no CPU figure from EMI.

## Intel desktop (i7-13700KF), 2026-10-04

An EMI device is present, but `IOCTL_EMI_GET_VERSION` fails with error 50 (not supported). The
CPU stays modelled. This is probably the usual case on Intel desktops, where the CPU's energy
counters are model-specific registers only a kernel driver can read.

## Open

- Intel laptops, AMD desktops and Snapdragon laptops have not been tried. Intel laptops often
  publish EMI channels through the Intel power engine plug-in. If they do, those laptops become
  CPU-measured too.
