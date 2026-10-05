# Design constraints

The requirements the meter was built to, and what each of them ruled in or out.

## Requirements

- Report wall watts (supply losses included), and say whether a screen is included.
- Report the session's watt-hours.
- Label the figure measured or estimated. Only watts and watt-hours leave the component.
- Always return a number.
- No admin rights, no driver install, no permission prompt, no network.
- About once a second, at a cost nobody notices. Never crash, never block the game.
- macOS, Windows and Linux, and say where each is weaker.
- Within about 30% is good. An honestly labelled estimate beats a confidently wrong
  measurement.
- Out of scope: prices, UI.

## What followed from it

**Pure C#, no native plugin.** Every sensor here is either a C function in a library the OS
already ships (IOKit, powrprof, setupapi, pdh, gdi32, NVML) or a file (sysfs). P/Invoke reaches
all of them by absolute path, so the Unity package has nothing to compile, sign or ship per
platform, and it runs the same on Mono and IL2CPP. The cost is struct layouts written out by
hand, which is why the parsers have unit tests against the documented layouts.

**No WMI.** WMI is the usual way to read battery state on Windows, but Unity's Mono has no
`System.Management`, and WMI queries can take hundreds of milliseconds. The battery driver,
`CallNtPowerInformation`, PDH and the registry give the same information through plain calls.

**No admin, no driver.** This rules out the best CPU power source on Windows (the CPU's
model-specific registers, read through a kernel driver such as WinRing0) and on Linux (RAPL,
root-only since 5.10). The README's "What elevated rights would buy" table lists what each
would add, for people who control their machines.

**No network.** This rules out a smart plug, the only source that sees an external monitor.

**Always a number.** Every sensor is optional. The model can price any machine from its CPU and
GPU names and the load counters, and every machine has load counters. A sensor that cannot be
created is dropped for the session. One that keeps failing rests for a minute. The label is
how the meter stays honest about it.

**Measured or estimated, by share.** A figure is `Measured` when a whole-system sensor gives it,
or when measured parts make up at least 75% of the total. Below that the model dominates, and
calling it measured would overstate it. An NVIDIA desktop with a measured GPU and a modelled CPU
is `Estimated`, even though one part is real.

**One background thread at 1 Hz.** Every sensor read is a few system calls. The meter thread runs
below normal priority and publishes one immutable snapshot, so a reader never takes a lock.

**Whole machine, not the game's share.** A wall socket bills the whole machine, background apps
included, and that is the figure an electricity bill follows. A per-process share (CPU time, the
game's GPU time) could be added as a second figure. It would be a model on top of a model.
