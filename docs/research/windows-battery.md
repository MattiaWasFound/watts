# Windows: battery figures

**Question.** On a Windows laptop running on battery, the battery's discharge rate is the whole
machine's draw. Can it be read without admin, and is it live?

## Two ways to ask

- **`CallNtPowerInformation(SystemBatteryState)`.** The power manager's summary: a
  `SYSTEM_BATTERY_STATE` struct with `Rate` in milliwatts (negative while discharging).
- **The battery driver.** Find the battery devices with SetupAPI (`GUID_DEVCLASS_BATTERY`), open
  each one, get its tag with `IOCTL_BATTERY_QUERY_TAG` (`0x294040`), then
  `IOCTL_BATTERY_QUERY_STATUS` (`0x29404C`) returns `BATTERY_STATUS`: power-state flags (1 =
  online, 2 = discharging) and `Rate` in milliwatts at offset 12.

## ASUS G14, three runs, 2026-10-04

**First run.** `CallNtPowerInformation` held 33.9 W, then 17.9 W, flat through 20 s of all-core
load, while EMI showed the CPU alone drawing 25 W under the same load on the charger. The power
manager's figure was frozen.

**Changes.** Battery reads go to the driver first. Any battery figure that has not changed for
20 s is treated as frozen and dropped (the `FreezeGuard`), so the meter falls back to EMI plus
the model.

**Second run.** The driver's figure changed every 3–10 s (24.6, 20.4, 20.1, 21.5 W). The freeze
guard never had to step in. But under all-core load the whole machine read only 20–21.5 W,
less than at idle.

**Third run, every reading logged.** When the load started, EMI's CPU power went from about 5 to
17 W within a second. The battery figure went from 13.4 to 16.2 W over the next 15 s, and kept
rising after the load ended. **The gauge is a slow average, lagging 30 s or more.**

## Decision

The battery stays the whole-system measurement on battery, and is labelled `Measured`. Its
session total over minutes is sound, but its live figure runs late. A game that shows live watts
on a Windows laptop on battery will see a sudden load show up half a minute late.

## Open

- Could EMI's live CPU power correct the battery's lag (battery for the slow total, EMI for the
  fast changes)? Not tried: it needs EMI, which not every laptop has.
- Linux laptops report `power_now` from the same kind of gauge, and likely lag too. Not yet
  tested on real hardware.
